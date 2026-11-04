package gateway

import (
	"encoding/json"
	"net/http"
	"runtime"
	"sync/atomic"
	"time"

	"ai003/server/internal/room"
)

// metricsState holds the counters exposed by GET /demo. They exist so a
// technical demo can prove what the server actually did (ticks, traffic,
// combat events) instead of only showing a screenshot.
type metricsState struct {
	startedAt time.Time

	messagesOut   atomic.Uint64
	bytesOut      atomic.Uint64
	worldSnaps    atomic.Uint64
	roomFrames    atomic.Uint64
	attackEvents  atomic.Uint64
	hitEvents     atomic.Uint64
	deathEvents   atomic.Uint64
	respawnEvents atomic.Uint64
}

func newMetrics() *metricsState {
	return &metricsState{startedAt: time.Now()}
}

// noteMessage accounts for one envelope written to a client.
func (m *metricsState) noteMessage(msg Message) {
	m.messagesOut.Add(1)
	m.bytesOut.Add(uint64(len(msg.Body)))
}

// noteRoomFrame accounts for one broadcast frame and its combat events.
func (m *metricsState) noteRoomFrame(frame room.Frame) {
	m.roomFrames.Add(1)
	for _, event := range frame.Events {
		switch event.Type {
		case "attack":
			m.attackEvents.Add(1)
		case "hit":
			m.hitEvents.Add(1)
		case "death":
			m.deathEvents.Add(1)
		case "respawn":
			m.respawnEvents.Add(1)
		}
	}
}

// noteWorldSnapshot accounts for one world-state broadcast.
func (m *metricsState) noteWorldSnapshot() {
	m.worldSnaps.Add(1)
}

type demoStats struct {
	MessagesOut   uint64 `json:"messagesOut"`
	BytesOut      uint64 `json:"bytesOut"`
	WorldSnaps    uint64 `json:"worldSnapshots"`
	RoomFrames    uint64 `json:"roomFrames"`
	AttackEvents  uint64 `json:"attackEvents"`
	HitEvents     uint64 `json:"hitEvents"`
	DeathEvents   uint64 `json:"deathEvents"`
	RespawnEvents uint64 `json:"respawnEvents"`
}

type demoResponse struct {
	Service         string     `json:"service"`
	Version         string     `json:"version"`
	ProtocolVersion int        `json:"protocolVersion"`
	UptimeSeconds   float64    `json:"uptimeSeconds"`
	GoVersion       string     `json:"goVersion"`
	Goroutines      int        `json:"goroutines"`
	WorldPlayers    int        `json:"worldPlayers"`
	Rooms           []roomInfo `json:"rooms"`
	Stats           demoStats  `json:"stats"`
}

// roomInfo mirrors room.Info but keeps the gateway independent of its JSON tags.
type roomInfo struct {
	ID       string `json:"id"`
	Players  int    `json:"players"`
	Capacity int    `json:"capacity"`
	Tick     uint64 `json:"tick"`
}

func (s *Server) handleDemo(w http.ResponseWriter, _ *http.Request) {
	stats := s.metrics
	rooms := s.rooms.List()
	info := make([]roomInfo, 0, len(rooms))
	for _, room := range rooms {
		info = append(info, roomInfo{
			ID:       room.ID,
			Players:  room.Players,
			Capacity: room.Capacity,
			Tick:     room.Tick,
		})
	}

	response := demoResponse{
		Service:         "ai003-server",
		Version:         Version,
		ProtocolVersion: ProtocolVersion,
		UptimeSeconds:   time.Since(stats.startedAt).Seconds(),
		GoVersion:       runtime.Version(),
		Goroutines:      runtime.NumGoroutine(),
		WorldPlayers:    s.hub.Count(),
		Rooms:           info,
		Stats: demoStats{
			MessagesOut:   stats.messagesOut.Load(),
			BytesOut:      stats.bytesOut.Load(),
			WorldSnaps:    stats.worldSnaps.Load(),
			RoomFrames:    stats.roomFrames.Load(),
			AttackEvents:  stats.attackEvents.Load(),
			HitEvents:     stats.hitEvents.Load(),
			DeathEvents:   stats.deathEvents.Load(),
			RespawnEvents: stats.respawnEvents.Load(),
		},
	}

	w.Header().Set("Content-Type", "application/json")
	_ = json.NewEncoder(w).Encode(response)
}
