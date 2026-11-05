package gateway

import (
	"net/http"
)

// dashboardHTML is a tiny self-contained status page: it polls the JSON
// endpoints (/demo, /world) and renders them as tables. No build step and no
// external assets, so it works on the demo machine offline.
const dashboardHTML = `<!doctype html>
<html lang="zh">
<head>
<meta charset="utf-8">
<title>AI003 · 演示状态</title>
<style>
  :root { color-scheme: dark; }
  body { margin: 0; font: 14px/1.5 "Segoe UI", system-ui, sans-serif; background: #14171c; color: #d7dde5; }
  header { padding: 16px 24px; background: #1c2129; border-bottom: 1px solid #2b323c; }
  h1 { margin: 0; font-size: 18px; font-weight: 600; }
  main { padding: 20px 24px; display: grid; gap: 20px; grid-template-columns: repeat(auto-fit, minmax(420px, 1fr)); }
  section { background: #1c2129; border: 1px solid #2b323c; border-radius: 8px; padding: 14px 16px; }
  h2 { margin: 0 0 10px; font-size: 15px; color: #8fb7ff; }
  table { width: 100%; border-collapse: collapse; }
  th, td { text-align: left; padding: 4px 6px; border-bottom: 1px solid #262d37; font-variant-numeric: tabular-nums; }
  th { color: #93a1b3; font-weight: 500; width: 46%; }
  .ok { color: #6fd48b; } .warn { color: #e6c04d; } .bad { color: #ef7d7d; }
  .muted { color: #7c8899; font-size: 12px; }
  pre { margin: 8px 0 0; padding: 10px; background: #12161b; border-radius: 6px; max-height: 240px; overflow: auto; font-size: 12px; }
</style>
</head>
<body>
<header>
  <h1>AI003 · 演示状态 <span class="muted" id="stamp">加载中…</span></h1>
</header>
<main>
  <section>
    <h2>服务器 /demo</h2>
    <table id="demo-table"><tbody><tr><td colspan="2" class="muted">等待数据…</td></tr></tbody></table>
  </section>
  <section>
    <h2>世界 /world</h2>
    <table id="world-table"><tbody><tr><td colspan="2" class="muted">等待数据…</td></tr></tbody></table>
  </section>
  <section style="grid-column: 1 / -1;">
    <h2>原始 JSON</h2>
    <pre id="raw">…</pre>
  </section>
</main>
<script>
const rows = (obj, skip = []) => {
  const out = [];
  const walk = (value, prefix) => {
    if (value === null || value === undefined) return;
    if (Array.isArray(value)) { out.push([prefix, value.length + " 项"]); return; }
    if (typeof value === "object") {
      for (const [k, v] of Object.entries(value)) walk(v, prefix ? prefix + "." + k : k);
      return;
    }
    if (skip.some(s => prefix.endsWith(s))) return;
    out.push([prefix, String(value)]);
  };
  walk(obj, "");
  return out;
};
const render = (table, obj, skip) => {
  const body = table.querySelector("tbody");
  body.innerHTML = "";
  for (const [k, v] of rows(obj, skip)) {
    const tr = document.createElement("tr");
    const kd = document.createElement("td"); kd.textContent = k;
    const vd = document.createElement("td"); vd.textContent = v;
    tr.append(kd, vd); body.append(tr);
  }
};
async function refresh() {
  const stamp = document.getElementById("stamp");
  try {
    const [demo, world] = await Promise.all([
      fetch("/demo").then(r => r.json()).catch(() => ({ error: "不可用" })),
      fetch("/world").then(r => r.json()).catch(() => ({ error: "不可用" })),
    ]);
    render(document.getElementById("demo-table"), demo, ["worldSnapshots"]);
    render(document.getElementById("world-table"), world);
    document.getElementById("raw").textContent = JSON.stringify({ demo, world }, null, 2);
    stamp.textContent = "更新于 " + new Date().toLocaleTimeString();
    stamp.className = "muted";
  } catch (e) {
    stamp.textContent = "连接失败: " + e;
    stamp.className = "bad";
  }
}
refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
`

// handleDashboard serves the status page.
func (s *Server) handleDashboard(w http.ResponseWriter, _ *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write([]byte(dashboardHTML))
}
