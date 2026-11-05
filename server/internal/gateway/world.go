package gateway

import (
	"encoding/json"
	"math"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"time"

	"ai003/server/internal/worldgen"
)

// ChunkMeters is the terrain chunk edge used by the clients for streaming.
const ChunkMeters = 128.0

// WorldInfo describes the shared world served to both engines.
type WorldInfo struct {
	worldgen.Metadata
	ChunkMeters   float64     `json:"chunkMeters"`
	ChunksPerAxis int         `json:"chunksPerAxis"`
	WorldFiles    []WorldFile `json:"worldFiles,omitempty"`
}

// WorldFile is one generated array on disk (heightmap, colours, biomes).
type WorldFile struct {
	Name  string `json:"name"`
	Bytes int64  `json:"bytes"`
}

// LoadWorldInfo reads world.json (and the generated array sizes) from dir.
func LoadWorldInfo(dir string) (*WorldInfo, error) {
	raw, err := os.ReadFile(filepath.Join(dir, "world.json"))
	if err != nil {
		return nil, err
	}

	var meta worldgen.Metadata
	if err := json.Unmarshal(raw, &meta); err != nil {
		return nil, err
	}

	info := &WorldInfo{
		Metadata:      meta,
		ChunkMeters:   ChunkMeters,
		ChunksPerAxis: int(math.Ceil(meta.SizeMeters / ChunkMeters)),
	}
	for _, name := range meta.Files {
		if stat, statErr := os.Stat(filepath.Join(dir, name)); statErr == nil {
			info.WorldFiles = append(info.WorldFiles, WorldFile{Name: name, Bytes: stat.Size()})
		}
	}
	return info, nil
}

// handleWorld serves the world metadata plus cheap process metrics.
func (s *Server) handleWorld(w http.ResponseWriter, _ *http.Request) {
	s.worldMu.RLock()
	info := s.world
	s.worldMu.RUnlock()

	if info == nil {
		writeJSON(w, http.StatusServiceUnavailable, map[string]any{
			"error": "world metadata not loaded (start with -world and generate world/)",
		})
		return
	}

	var mem runtime.MemStats
	runtime.ReadMemStats(&mem)

	writeJSON(w, http.StatusOK, map[string]any{
		"world": info,
		"server": map[string]any{
			"uptimeSeconds": time.Since(s.started).Seconds(),
			"memoryMB":      float64(mem.Alloc) / (1024 * 1024),
			"goroutines":    runtime.NumGoroutine(),
		},
	})
}

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}
