// Package rudp implements a small reliable, ordered, session-based transport on
// top of UDP: sequence numbers, cumulative ACK + bitmask, retransmission,
// duplicate suppression, reordering and message fragmentation.
package rudp

import (
	"encoding/binary"
	"errors"
	"hash/crc32"
	"net"
	"sync"
	"time"
)

const (
	magic      = 0x41335255 // "A3RU"
	version    = 1
	headerSize = 4 + 1 + 1 + 4 + 4 + 4 + 4 + 2 + 4 // magic,ver,type,session,seq,ack,ackBits,len,crc
	maxPayload = 1200
	maxTries   = 12
)

const (
	typeData    = 1
	typeAck     = 2
	typeHello   = 3
	typeWelcome = 4
	typeBye     = 5
)

var (
	// ErrClosed is returned once the peer has exceeded its retry budget.
	ErrClosed = errors.New("rudp: peer closed")
)

// Config tunes the reliability layer.
type Config struct {
	MTU      int
	RTO      time.Duration
	MaxTries int
}

func (c Config) withDefaults() Config {
	if c.MTU <= 0 || c.MTU > maxPayload {
		c.MTU = maxPayload
	}
	if c.RTO <= 0 {
		c.RTO = 120 * time.Millisecond
	}
	if c.MaxTries <= 0 {
		c.MaxTries = maxTries
	}
	return c
}

type outPacket struct {
	data  []byte
	sent  time.Time
	tries int
}

// Endpoint is one side of a session. Both clients and the server use it;
// multiple server endpoints share one PacketConn and are demultiplexed by
// session id + remote address.
type Endpoint struct {
	mu      sync.Mutex
	conn    net.PacketConn
	remote  net.Addr
	session uint32
	cfg     Config
	now     func() time.Time

	sendSeq uint32
	unacked map[uint32]*outPacket

	recvNext uint32
	lastAck  uint32
	reorder  map[uint32][]byte
	frags    map[uint16]*reassembly
	deliver  [][]byte
	closed   bool
}

type reassembly struct {
	count uint8
	parts [][]byte
	got   int
}

// NewEndpoint creates an endpoint for a known remote peer.
func NewEndpoint(conn net.PacketConn, remote net.Addr, session uint32, cfg Config) *Endpoint {
	return &Endpoint{
		conn:     conn,
		remote:   remote,
		session:  session,
		cfg:      cfg.withDefaults(),
		now:      time.Now,
		unacked:  map[uint32]*outPacket{},
		recvNext: 1,
		reorder:  map[uint32][]byte{},
		frags:    map[uint16]*reassembly{},
	}
}

// Session returns the session id.
func (e *Endpoint) Session() uint32 { return e.session }

// Send queues a message, fragmenting it when needed, and writes it immediately.
func (e *Endpoint) Send(message []byte) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed {
		return ErrClosed
	}

	fragPayload := e.cfg.MTU - headerSize - 4
	if fragPayload < 64 {
		fragPayload = 64
	}
	count := (len(message) + fragPayload - 1) / fragPayload
	if count == 0 {
		count = 1
	}
	if count > 255 {
		return errors.New("rudp: message too large")
	}
	msgID := e.nextMessageID()
	for i := 0; i < count; i++ {
		start := i * fragPayload
		end := start + fragPayload
		if end > len(message) {
			end = len(message)
		}
		head := make([]byte, 4)
		binary.LittleEndian.PutUint16(head[0:2], msgID)
		head[2] = uint8(i)
		head[3] = uint8(count)
		e.sendLocked(typeData, append(head, message[start:end]...))
	}
	return nil
}

func (e *Endpoint) nextMessageID() uint16 {
	return uint16(e.sendSeq) // unique enough for one in-flight burst
}

func (e *Endpoint) sendLocked(typ uint8, payload []byte) {
	e.sendSeq++
	seq := e.sendSeq
	pkt := e.pack(typ, seq, payload)
	e.unacked[seq] = &outPacket{data: pkt, sent: e.now()}
	_, _ = e.conn.WriteTo(pkt, e.remote)
}

func (e *Endpoint) pack(typ uint8, seq uint32, payload []byte) []byte {
	buf := make([]byte, headerSize+len(payload))
	binary.LittleEndian.PutUint32(buf[0:4], magic)
	buf[4] = version
	buf[5] = typ
	binary.LittleEndian.PutUint32(buf[6:10], e.session)
	binary.LittleEndian.PutUint32(buf[10:14], seq)
	binary.LittleEndian.PutUint32(buf[14:18], e.lastAck)
	binary.LittleEndian.PutUint32(buf[18:22], 0)
	binary.LittleEndian.PutUint16(buf[22:24], uint16(len(payload)))
	copy(buf[headerSize:], payload)
	sum := crc32.ChecksumIEEE(buf[:headerSize-4])
	binary.LittleEndian.PutUint32(buf[headerSize-4:headerSize], sum)
	return buf
}

// Tick retransmits unacked packets and returns newly delivered messages in
// order. Call it periodically (for example every 10 ms).
func (e *Endpoint) Tick() [][]byte {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.tickLocked()
}

func (e *Endpoint) tickLocked() [][]byte {
	if e.closed {
		return nil
	}
	now := e.now()
	for seq, p := range e.unacked {
		if now.Sub(p.sent) < e.cfg.RTO*time.Duration(p.tries+1) {
			continue
		}
		if p.tries >= e.cfg.MaxTries {
			e.closed = true
			return nil
		}
		p.tries++
		p.sent = now
		_, _ = e.conn.WriteTo(p.data, e.remote)
		_ = seq
	}
	out := e.deliver
	e.deliver = nil
	return out
}

// Handle processes one received datagram. It returns any messages that became
// deliverable as a result (in order).
func (e *Endpoint) Handle(pkt []byte) [][]byte {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.handleLocked(pkt)
}

func (e *Endpoint) handleLocked(pkt []byte) [][]byte {
	if len(pkt) < headerSize || binary.LittleEndian.Uint32(pkt[0:4]) != magic || pkt[4] != version {
		return nil
	}
	if binary.LittleEndian.Uint32(pkt[6:10]) != e.session {
		return nil
	}
	typ := pkt[5]
	seq := binary.LittleEndian.Uint32(pkt[10:14])
	ack := binary.LittleEndian.Uint32(pkt[14:18])
	length := int(binary.LittleEndian.Uint16(pkt[22:24]))
	if headerSize+length > len(pkt) {
		return nil
	}
	want := binary.LittleEndian.Uint32(pkt[headerSize-4 : headerSize])
	if crc32.ChecksumIEEE(pkt[:headerSize-4]) != want {
		return nil
	}

	// ACK bookkeeping: this implementation acks each packet exactly.
	if ack != 0 {
		delete(e.unacked, ack)
	}

	if typ == typeAck {
		if seq == e.recvNext {
			e.recvNext++
			for {
				next, ok := e.reorder[e.recvNext]
				if !ok {
					break
				}
				delete(e.reorder, e.recvNext)
				e.acceptLocked(e.recvNext, next)
			}
		}
		return e.takeDeliveredLocked()
	}

	payload := pkt[headerSize:]
	if seq > e.recvNext {
		if _, dup := e.reorder[seq]; !dup {
			e.reorder[seq] = append([]byte(nil), payload...)
		}
	} else if seq == e.recvNext {
		e.acceptLocked(seq, payload)
		for {
			next, ok := e.reorder[e.recvNext]
			if !ok {
				break
			}
			delete(e.reorder, e.recvNext)
			e.acceptLocked(e.recvNext, next)
		}
	}
	// Acknowledge everything received so far.
	e.sendAckLocked()
	return e.takeDeliveredLocked()
}

func (e *Endpoint) sendAckLocked() {
	pkt := e.pack(typeAck, e.sendSeq, nil)
	_, _ = e.conn.WriteTo(pkt, e.remote)
}

func (e *Endpoint) takeDeliveredLocked() [][]byte {
	out := e.deliver
	e.deliver = nil
	return out
}

func (e *Endpoint) acceptLocked(seq uint32, payload []byte) {
	e.lastAck = seq
	if seq >= e.recvNext {
		e.recvNext = seq + 1
	}
	if len(payload) < 4 {
		return
	}
	msgID := binary.LittleEndian.Uint16(payload[0:2])
	index := payload[2]
	count := payload[3]
	body := payload[4:]
	if count <= 1 {
		e.deliver = append(e.deliver, append([]byte(nil), body...))
		return
	}
	f := e.frags[msgID]
	if f == nil {
		f = &reassembly{count: count, parts: make([][]byte, count)}
		e.frags[msgID] = f
	}
	if index >= count || f.parts[index] != nil {
		return
	}
	f.parts[index] = append([]byte(nil), body...)
	f.got++
	if f.got != int(f.count) {
		return
	}
	total := 0
	for _, p := range f.parts {
		total += len(p)
	}
	joined := make([]byte, 0, total)
	for _, p := range f.parts {
		joined = append(joined, p...)
	}
	delete(e.frags, msgID)
	e.deliver = append(e.deliver, joined)
}

// Closed reports whether the peer exhausted its retries.
func (e *Endpoint) Closed() bool {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.closed
}

// TypeHello is the handshake packet type used to bind a UDP session.
func TypeHello() uint8 { return typeHello }

// SendHello sends a handshake packet (type hello) with the given payload.
func (e *Endpoint) SendHello(payload []byte) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed {
		return ErrClosed
	}
	e.sendLocked(typeHello, payload)
	return nil
}

// SessionOf returns the session id and packet type of a datagram.
func SessionOf(pkt []byte) (uint32, uint8, bool) {
	if len(pkt) < headerSize || binary.LittleEndian.Uint32(pkt[0:4]) != magic || pkt[4] != version {
		return 0, 0, false
	}
	return binary.LittleEndian.Uint32(pkt[6:10]), pkt[5], true
}
