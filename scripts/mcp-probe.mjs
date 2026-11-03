// Minimal MCP stdio probe: spawn a server, initialize, list tools, exit.
// Usage: node scripts/mcp-probe.mjs <command> [args...]
import { spawn } from 'node:child_process';

const [command, ...rawArgs] = process.argv.slice(2);
if (!command) {
  console.error('usage: node scripts/mcp-probe.mjs <command> [args...] [--hold=seconds]');
  process.exit(2);
}
const holdArg = rawArgs.find((value) => value.startsWith('--hold='));
const holdSeconds = holdArg ? Number(holdArg.slice('--hold='.length)) : 0;
const args = rawArgs.filter((value) => !value.startsWith('--hold='));

const child = spawn(command, args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
let buffer = '';
let nextId = 1;
const pending = new Map();

function send(message) {
  child.stdin.write(JSON.stringify(message) + '\n');
}

function request(method, params) {
  const id = nextId++;
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject });
    send({ jsonrpc: '2.0', id, method, params });
  });
}

child.stdout.on('data', (chunk) => {
  buffer += chunk.toString('utf8');
  let index;
  while ((index = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, index).trim();
    buffer = buffer.slice(index + 1);
    if (!line) continue;
    let message;
    try {
      message = JSON.parse(line);
    } catch {
      console.log('[non-json]', line.slice(0, 200));
      continue;
    }
    if (message.id !== undefined && pending.has(message.id)) {
      const { resolve, reject } = pending.get(message.id);
      pending.delete(message.id);
      if (message.error) reject(new Error(JSON.stringify(message.error)));
      else resolve(message.result);
    }
  }
});

child.stderr.on('data', (chunk) => {
  const text = chunk.toString('utf8').trim();
  if (text) console.log('[stderr]', text.split('\n').slice(0, 3).join(' | '));
});

const timer = setTimeout(() => {
  console.error('TIMEOUT after 120s');
  child.kill();
  process.exit(1);
}, 120000);

try {
  const init = await request('initialize', {
    protocolVersion: '2024-11-05',
    capabilities: {},
    clientInfo: { name: 'dsh-probe', version: '0.0.1' },
  });
  console.log('server:', init.serverInfo?.name, init.serverInfo?.version);
  send({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} });

  const tools = await request('tools/list', {});
  const list = tools.tools ?? [];
  console.log('tools:', list.length);
  for (const tool of list.slice(0, 40)) {
    console.log(' -', tool.name);
  }
  if (holdSeconds > 0) {
    console.log(`holding for ${holdSeconds}s`);
    await new Promise((resolve) => setTimeout(resolve, holdSeconds * 1000));
  }
  clearTimeout(timer);
  child.stdin.end();
  child.kill();
  process.exit(0);
} catch (error) {
  console.error('ERROR', error.message);
  clearTimeout(timer);
  child.kill();
  process.exit(1);
}
