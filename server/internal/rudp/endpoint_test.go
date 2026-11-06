package rudp

import (
	"net"
	"testing"
	"time"
)

type testAddr string

func (a testAddr) Network() string { return "sim" }
func (a testAddr) String() string  { return string(a) }

type simNet struct {
	queues map[string][][]byte
}

func newSim() *simNet { return &simNet{queues: map[string][][]byte{}} }

func (s *simNet) conn(name string) net.PacketConn {
	return &simConn{net: s, name: name}
}

func (s *simNet) push(to string, pkt []byte) {
	s.queues[to] = append(s.queues[to], append([]byte(nil), pkt...))
}

func (s *simNet) pop(from string) []byte {
	q := s.queues[from]
	if len(q) == 0 {
		return nil
	}
	pkt := q[0]
	s.queues[from] = q[1:]
	return pkt
}

func (s *simNet) len(from string) int { return len(s.queues[from]) }

type simConn struct {
	net  *simNet
	name string
}

func (c *simConn) ReadFrom(p []byte) (int, net.Addr, error) { return 0, nil, nil }
func (c *simConn) WriteTo(p []byte, addr net.Addr) (int, error) {
	c.net.push(addr.String(), p)
	return len(p), nil
}
func (c *simConn) Close() error                     { return nil }
func (c *simConn) LocalAddr() net.Addr              { return testAddr(c.name) }
func (c *simConn) SetDeadline(time.Time) error      { return nil }
func (c *simConn) SetReadDeadline(time.Time) error  { return nil }
func (c *simConn) SetWriteDeadline(time.Time) error { return nil }

type harness struct {
	net      *simNet
	a, b     *Endpoint
	recv     [][]byte
	packetNo int
}

func newHarness(t *testing.T) *harness {
	h := &harness{net: newSim()}
	cfgA := Config{RTO: 20 * time.Millisecond, MaxTries: 20}
	cfgB := Config{RTO: 20 * time.Millisecond, MaxTries: 20}
	h.a = NewEndpoint(h.net.conn("a"), testAddr("b"), 7, cfgA)
	h.b = NewEndpoint(h.net.conn("b"), testAddr("a"), 7, cfgB)
	return h
}

// pump delivers queued packets with deterministic loss/duplication/reordering.
func (h *harness) pump() {
	for {
		pkt := h.net.pop("b")
		if pkt == nil {
			break
		}
		h.packetNo++
		if h.packetNo%3 == 0 {
			continue // drop
		}
		h.recv = append(h.recv, h.b.Handle(pkt)...)
		if h.packetNo%5 == 0 {
			h.recv = append(h.recv, h.b.Handle(pkt)...) // duplicate
		}
	}
	for {
		pkt := h.net.pop("a")
		if pkt == nil {
			break
		}
		h.a.Handle(pkt)
	}
	h.recv = append(h.recv, h.a.Tick()...)
	h.recv = append(h.recv, h.b.Tick()...)
}

func (h *harness) run(t *testing.T, want int) {
	t.Helper()
	deadline := time.Now().Add(2 * time.Second)
	for len(h.recv) < want && time.Now().Before(deadline) {
		h.pump()
		time.Sleep(time.Millisecond)
	}
	if len(h.recv) < want {
		t.Fatalf("received %d/%d messages", len(h.recv), want)
	}
}

func TestReliableDeliveryUnderLossAndDuplication(t *testing.T) {
	h := newHarness(t)
	const n = 40
	for i := 0; i < n; i++ {
		payload := []byte{byte(i), byte(i >> 8), 0xAB}
		if err := h.a.Send(payload); err != nil {
			t.Fatalf("send %d: %v", i, err)
		}
	}
	h.run(t, n)
	for i := 0; i < n; i++ {
		got := h.recv[i]
		want := []byte{byte(i), byte(i >> 8), 0xAB}
		if len(got) != len(want) || got[0] != want[0] || got[1] != want[1] || got[2] != want[2] {
			t.Fatalf("message %d = %v, want %v", i, got, want)
		}
	}
}

func TestFragmentedMessageIsReassembled(t *testing.T) {
	h := newHarness(t)
	big := make([]byte, 6000)
	for i := range big {
		big[i] = byte(i * 7)
	}
	if err := h.a.Send(big); err != nil {
		t.Fatalf("send big: %v", err)
	}
	h.run(t, 1)
	if len(h.recv[0]) != len(big) {
		t.Fatalf("reassembled length = %d, want %d", len(h.recv[0]), len(big))
	}
	for i := range big {
		if h.recv[0][i] != big[i] {
			t.Fatalf("byte %d differs", i)
		}
	}
}

func TestOutOfOrderIsDeliveredInOrder(t *testing.T) {
	h := newHarness(t)
	// Interleave three messages, then deliver the resulting packets reversed.
	for i := 0; i < 3; i++ {
		if err := h.a.Send([]byte{byte(i)}); err != nil {
			t.Fatalf("send: %v", err)
		}
	}
	var pkts [][]byte
	for h.net.len("b") > 0 {
		pkts = append(pkts, h.net.pop("b"))
	}
	var got [][]byte
	for i := len(pkts) - 1; i >= 0; i-- {
		got = append(got, h.b.Handle(pkts[i])...)
	}
	got = append(got, h.b.Tick()...)
	if len(got) != 3 {
		t.Fatalf("delivered %d, want 3", len(got))
	}
	for i := 0; i < 3; i++ {
		if got[i][0] != byte(i) {
			t.Fatalf("order = %v", got)
		}
	}
}
