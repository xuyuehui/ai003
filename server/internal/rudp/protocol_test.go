package rudp

import (
	"bytes"
	"encoding/binary"
	"errors"
	"hash/crc32"
	"net"
	"testing"
	"time"
)

// captureConn records every datagram an endpoint writes so tests can inspect
// the wire format instead of trusting the encoder.
type captureConn struct {
	sent [][]byte
}

func (c *captureConn) ReadFrom(p []byte) (int, net.Addr, error) { return 0, nil, nil }
func (c *captureConn) WriteTo(p []byte, addr net.Addr) (int, error) {
	c.sent = append(c.sent, append([]byte(nil), p...))
	return len(p), nil
}
func (c *captureConn) Close() error                     { return nil }
func (c *captureConn) LocalAddr() net.Addr              { return testAddr("capture") }
func (c *captureConn) SetDeadline(time.Time) error      { return nil }
func (c *captureConn) SetReadDeadline(time.Time) error  { return nil }
func (c *captureConn) SetWriteDeadline(time.Time) error { return nil }

func (c *captureConn) last(t *testing.T) []byte {
	t.Helper()
	if len(c.sent) == 0 {
		t.Fatal("no datagram was written")
	}
	return c.sent[len(c.sent)-1]
}

func newCaptureEndpoint(session uint32, cfg Config) (*Endpoint, *captureConn) {
	conn := &captureConn{}
	return NewEndpoint(conn, testAddr("peer"), session, cfg), conn
}

// wirePacket is an independent encoder for the documented header layout. Tests
// compare it against the production packer so a silent layout change fails.
func wirePacket(session uint32, typ uint8, seq, ack uint32, payload []byte) []byte {
	const hdr = 28
	buf := make([]byte, hdr+len(payload))
	binary.LittleEndian.PutUint32(buf[0:4], 0x41335255) // "A3RU"
	buf[4] = 1                                          // version
	buf[5] = typ
	binary.LittleEndian.PutUint32(buf[6:10], session)
	binary.LittleEndian.PutUint32(buf[10:14], seq)
	binary.LittleEndian.PutUint32(buf[14:18], ack)
	binary.LittleEndian.PutUint32(buf[18:22], 0) // reserved, must stay zero
	binary.LittleEndian.PutUint16(buf[22:24], uint16(len(payload)))
	copy(buf[hdr:], payload)
	binary.LittleEndian.PutUint32(buf[24:28], crc32.ChecksumIEEE(buf[:24]))
	return buf
}

func TestHeaderIsTwentyEightBytesOfLittleEndian(t *testing.T) {
	if headerSize != 28 {
		t.Fatalf("headerSize = %d, want 28 (clients hardcode the strip offset)", headerSize)
	}

	ep, conn := newCaptureEndpoint(0x01020304, Config{})
	if err := ep.SendHello([]byte(`{"token":"abc"}`)); err != nil {
		t.Fatalf("SendHello: %v", err)
	}
	got := conn.last(t)

	want := wirePacket(0x01020304, typeHello, 1, 0, []byte(`{"token":"abc"}`))
	if !bytes.Equal(got, want) {
		t.Fatalf("hello datagram mismatch:\n got %x\nwant %x", got, want)
	}

	// Spot-check the fields the clients depend on.
	if magic := binary.LittleEndian.Uint32(got[0:4]); magic != 0x41335255 {
		t.Fatalf("magic = %#x, want 0x41335255", magic)
	}
	if got[4] != 1 {
		t.Fatalf("version = %d, want 1", got[4])
	}
	if typ := got[5]; typ != typeHello {
		t.Fatalf("type = %d, want typeHello(%d)", typ, typeHello)
	}
	if n := binary.LittleEndian.Uint16(got[22:24]); int(n) != len(got)-28 {
		t.Fatalf("length field = %d, payload bytes = %d", n, len(got)-28)
	}
	if !bytes.Equal(got[28:], []byte(`{"token":"abc"}`)) {
		t.Fatalf("payload = %q", got[28:])
	}
	if crc := binary.LittleEndian.Uint32(got[24:28]); crc != crc32.ChecksumIEEE(got[:24]) {
		t.Fatalf("crc = %#x, want %#x", crc, crc32.ChecksumIEEE(got[:24]))
	}
}

func TestDataTypeConstantsAreStable(t *testing.T) {
	// The numbers are on the wire; changing them breaks deployed clients.
	for _, tc := range []struct {
		name string
		got  uint8
		want uint8
	}{
		{"data", typeData, 1},
		{"ack", typeAck, 2},
		{"hello", typeHello, 3},
		{"welcome", typeWelcome, 4},
		{"bye", typeBye, 5},
	} {
		if tc.got != tc.want {
			t.Errorf("%s packet type = %d, want %d", tc.name, tc.got, tc.want)
		}
	}
	if TypeHello() != typeHello {
		t.Errorf("TypeHello() = %d, want %d", TypeHello(), typeHello)
	}
	if maxPayload != 1200 {
		t.Errorf("maxPayload = %d, want 1200 (MTU budget shared with clients)", maxPayload)
	}
}

func TestSessionOfRoundTripsAndRejectsGarbage(t *testing.T) {
	valid := wirePacket(0xAABBCCDD, typeHello, 7, 0, []byte("x"))

	tests := []struct {
		name        string
		pkt         []byte
		wantSession uint32
		wantType    uint8
		wantOK      bool
	}{
		{"valid hello", valid, 0xAABBCCDD, typeHello, true},
		{"valid ack empty payload", wirePacket(1, typeAck, 3, 1, nil), 1, typeAck, true},
		{"empty", nil, 0, 0, false},
		{"short by one byte", valid[:len(valid)-1-1], 0, 0, false},
		{"header only, no payload", valid[:28], 0xAABBCCDD, typeHello, true},
		{"bad magic", func() []byte {
			p := append([]byte(nil), valid...)
			p[0] ^= 0xFF
			return p
		}(), 0, 0, false},
		{"bad version", func() []byte {
			p := append([]byte(nil), valid...)
			p[4] = 2
			return p
		}(), 0, 0, false},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			session, typ, ok := SessionOf(tc.pkt)
			if ok != tc.wantOK {
				t.Fatalf("ok = %v, want %v", ok, tc.wantOK)
			}
			if !ok {
				return
			}
			if session != tc.wantSession || typ != tc.wantType {
				t.Fatalf("SessionOf = (%d,%d), want (%d,%d)", session, typ, tc.wantSession, tc.wantType)
			}
		})
	}
}

func TestHelloPayloadIsRawJSONAfterTheHeader(t *testing.T) {
	// Handshake datagrams (hello/welcome/bye) carry the JSON body directly
	// after the fixed header; only data packets carry the 4-byte fragment
	// header. The gateway strips the header itself (payloadOf) for this reason.
	sender, conn := newCaptureEndpoint(4242, Config{})
	body := []byte(`{"token":"0123456789abcdef"}`)
	if err := sender.SendHello(body); err != nil {
		t.Fatalf("SendHello: %v", err)
	}
	pkt := conn.last(t)

	if got := pkt[headerSize:]; !bytes.Equal(got, body) {
		t.Fatalf("hello payload after header = %q, want %q", got, body)
	}

	// Feeding the hello to the peer seeds its receive sequence, so the first
	// data packet (seq 2) is then accepted in order.
	receiver := NewEndpoint(&captureConn{}, testAddr("sender"), 4242, Config{})
	receiver.Handle(pkt)
	if receiver.recvNext != 2 {
		t.Fatalf("recvNext after hello = %d, want 2", receiver.recvNext)
	}

	dataPayload := []byte{0x01, 0x00, 0x00, 0x01, 'p', 'i', 'n', 'g'}
	got := receiver.Handle(wirePacket(4242, typeData, 2, 0, dataPayload))
	if len(got) != 1 {
		t.Fatalf("data packet after hello delivered %d messages, want 1", len(got))
	}
	if string(got[0]) != "ping" {
		t.Fatalf("delivered %q, want %q", got[0], "ping")
	}

	// Same packet addressing another session must be dropped.
	other := NewEndpoint(&captureConn{}, testAddr("sender"), 4243, Config{})
	if msgs := other.Handle(pkt); len(msgs) != 0 {
		t.Fatalf("wrong-session packet delivered %d messages", len(msgs))
	}
}

func TestDataPayloadCarriesFourByteFragmentHeader(t *testing.T) {
	ep, conn := newCaptureEndpoint(3, Config{})
	if err := ep.Send([]byte("ab")); err != nil {
		t.Fatalf("send: %v", err)
	}
	pkt := conn.last(t)
	payload := pkt[headerSize:]
	if len(payload) != 4+2 {
		t.Fatalf("payload = %d bytes, want 4-byte header + 2 body", len(payload))
	}
	if n := binary.LittleEndian.Uint16(payload[0:2]); n != 0 {
		// msgID is the sequence number before sendLocked bumps it, so the
		// first message on a fresh endpoint is 0.
		t.Fatalf("msgID = %d, want 0 for the first message", n)
	}
	if payload[2] != 0 || payload[3] != 1 {
		t.Fatalf("index/count = %d/%d, want 0/1", payload[2], payload[3])
	}
	if string(payload[4:]) != "ab" {
		t.Fatalf("body = %q", payload[4:])
	}
}

func TestCorruptDatagramsAreDropped(t *testing.T) {
	good := wirePacket(77, typeData, 1, 0, []byte{0, 0, 0, 1, 'a'})

	mutate := func(f func(p []byte)) []byte {
		p := append([]byte(nil), good...)
		f(p)
		return p
	}

	tests := []struct {
		name string
		pkt  []byte
	}{
		{"truncated header", good[:20]},
		{"truncated payload", good[:len(good)-1]},
		// NOTE: the CRC covers the 24-byte header only, so a flipped payload
		// byte is not detected; that gap is reported, not asserted here.
		{"bit flip in crc", mutate(func(p []byte) { p[25] ^= 0x01 })},
		{"length field too large", mutate(func(p []byte) { binary.LittleEndian.PutUint16(p[22:24], 400) })},
		{"wrong magic", mutate(func(p []byte) { p[3] = 0x00 })},
		{"wrong version", mutate(func(p []byte) { p[4] = 9 })},
		{"wrong session", mutate(func(p []byte) { binary.LittleEndian.PutUint32(p[6:10], 78) })},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			ep := NewEndpoint(&captureConn{}, testAddr("peer"), 77, Config{})
			if msgs := ep.Handle(tc.pkt); len(msgs) != 0 {
				t.Fatalf("corrupt datagram delivered %d messages", len(msgs))
			}
			if ep.Send([]byte("still alive")) != nil {
				t.Fatal("endpoint died after a corrupt datagram")
			}
		})
	}

	// Control: the unmodified datagram is accepted.
	ep := NewEndpoint(&captureConn{}, testAddr("peer"), 77, Config{})
	if msgs := ep.Handle(good); len(msgs) != 1 || string(msgs[0]) != "a" {
		t.Fatalf("valid datagram delivered %q, want %q", msgs, "a")
	}
}

func TestAckReleasesExactlyTheAckedSequence(t *testing.T) {
	ep, _ := newCaptureEndpoint(5, Config{})

	// Payloads with count<=1 deliver immediately, so three sends are three
	// unacked datagrams with seq 1,2,3.
	for i := 0; i < 3; i++ {
		if err := ep.Send([]byte{byte(i)}); err != nil {
			t.Fatalf("send %d: %v", i, err)
		}
	}
	if n := len(ep.unacked); n != 3 {
		t.Fatalf("unacked = %d, want 3", n)
	}

	// A peer ack carrying seq=2 must release only packet 2.
	ep.Handle(wirePacket(5, typeAck, 2, 2, nil))
	if _, still := ep.unacked[2]; still {
		t.Fatal("acked sequence 2 was not released")
	}
	for _, seq := range []uint32{1, 3} {
		if _, ok := ep.unacked[seq]; !ok {
			t.Fatalf("sequence %d released without an ack", seq)
		}
	}

	// Acks for sequences we never sent are harmless.
	ep.Handle(wirePacket(5, typeAck, 99, 99, nil))
	if n := len(ep.unacked); n != 2 {
		t.Fatalf("unacked after bogus ack = %d, want 2", n)
	}
}

func TestRetryBudgetClosesEndpoint(t *testing.T) {
	ep, _ := newCaptureEndpoint(9, Config{RTO: time.Millisecond, MaxTries: 2})
	now := time.Now()
	ep.now = func() time.Time { return now }

	if err := ep.Send([]byte("retry me")); err != nil {
		t.Fatalf("send: %v", err)
	}
	now = now.Add(time.Second)

	for i := 0; i < 2; i++ {
		now = now.Add(time.Second)
		ep.Tick() // retransmit #1, #2
		if ep.Closed() {
			t.Fatalf("closed after %d retries, budget was 2", i+1)
		}
	}
	now = now.Add(time.Second)
	ep.Tick() // exhausted
	if !ep.Closed() {
		t.Fatal("endpoint still open after exhausting the retry budget")
	}
	if got := ep.Tick(); got != nil {
		t.Fatalf("closed endpoint delivered %d messages", len(got))
	}
	if err := ep.Send([]byte("x")); !errors.Is(err, ErrClosed) {
		t.Fatalf("Send on closed endpoint = %v, want ErrClosed", err)
	}
	if err := ep.SendHello(nil); !errors.Is(err, ErrClosed) {
		t.Fatalf("SendHello on closed endpoint = %v, want ErrClosed", err)
	}
}

func TestSessionAccessorAndEmptyMessage(t *testing.T) {
	ep, _ := newCaptureEndpoint(0xFEEDFACE, Config{})
	if got := ep.Session(); got != 0xFEEDFACE {
		t.Fatalf("Session() = %#x, want 0xFEEDFACE", got)
	}

	// A zero-length message is still one datagram and one delivery.
	if err := ep.Send(nil); err != nil {
		t.Fatalf("send empty: %v", err)
	}
	receiver := NewEndpoint(&captureConn{}, testAddr("peer"), 0xFEEDFACE, Config{})
	got := receiver.Handle(ep.conn.(*captureConn).last(t))
	if len(got) != 1 || len(got[0]) != 0 {
		t.Fatalf("empty message delivered %v", got)
	}
}

func TestMessageLargerThanFragmentsIsRejected(t *testing.T) {
	ep, _ := newCaptureEndpoint(1, Config{})
	// 255 fragments of fragPayload bytes is the ceiling.
	fragPayload := ep.cfg.MTU - headerSize - 4
	if fragPayload < 64 {
		fragPayload = 64
	}
	tooBig := make([]byte, fragPayload*256+1)
	if err := ep.Send(tooBig); err == nil {
		t.Fatal("Send accepted a message needing more than 255 fragments")
	}
	atLimit := make([]byte, fragPayload*2)
	if err := ep.Send(atLimit); err != nil {
		t.Fatalf("Send rejected a 2-fragment message: %v", err)
	}
}
