package gateway

import (
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

func TestWorldEndpoint(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "world.json"), []byte(`{
		"version": 1,
		"name": "test-world",
		"seed": 7,
		"sizeMeters": 4096,
		"resolution": 1025,
		"cellSize": 4,
		"seaLevel": 0,
		"spawn": {"x": 1, "y": 2, "z": 3},
		"files": {"heightmap": "heightmap.f32"}
	}`), 0o644); err != nil {
		t.Fatalf("write world.json: %v", err)
	}
	if err := os.WriteFile(filepath.Join(dir, "heightmap.f32"), make([]byte, 16), 0o644); err != nil {
		t.Fatalf("write heightmap: %v", err)
	}

	server := NewWithWorld(slog.New(slog.NewTextHandler(io.Discard, nil)), dir)
	defer server.Close()
	httpServer := httptest.NewServer(server.Handler())
	defer httpServer.Close()

	resp, err := http.Get(httpServer.URL + "/world")
	if err != nil {
		t.Fatalf("GET /world: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("status = %d, want 200", resp.StatusCode)
	}

	var body struct {
		World struct {
			Name          string  `json:"name"`
			SizeMeters    float64 `json:"sizeMeters"`
			ChunkMeters   float64 `json:"chunkMeters"`
			ChunksPerAxis int     `json:"chunksPerAxis"`
			WorldFiles    []struct {
				Name  string `json:"name"`
				Bytes int64  `json:"bytes"`
			} `json:"worldFiles"`
		} `json:"world"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		t.Fatalf("decode: %v", err)
	}

	if body.World.Name != "test-world" || body.World.SizeMeters != 4096 {
		t.Fatalf("world = %+v", body.World)
	}
	if body.World.ChunksPerAxis != 32 {
		t.Fatalf("chunksPerAxis = %d, want 32", body.World.ChunksPerAxis)
	}
	if len(body.World.WorldFiles) != 1 || body.World.WorldFiles[0].Bytes != 16 {
		t.Fatalf("worldFiles = %+v", body.World.WorldFiles)
	}
}
