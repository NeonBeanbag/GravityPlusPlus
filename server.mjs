// Gravity 音响 Windows 控制面板后端（零依赖，Node >= 18）
// 用法: node server.mjs  →  浏览器打开 http://127.0.0.1:8788
//
// 音响侧接口（来自 MeizuGravity 项目逆向）:
//   http://<ip>:7766  GET  /Status /Info /Play /Pause /Next /Prev
//                     POST /SetVolume {"CurrentVolume":n}  /SetEQMode {"EQMode":0-5}
//   tcp  <ip>:7788    网络 adb（触屏/按键/任意 shell，需要本机 adb）

import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import fs from 'node:fs';
import path from 'node:path';
import { spawnSync, spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { pipeline } from 'node:stream/promises';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PORT = 8788;
const SPEAKER_API_PORT = 7766;
const ADB_PORT = 7788;

// ---------- adb 定位：优先项目内 platform-tools，其次 PATH ----------
let adbBin = null;
function findAdb() {
  const local = path.join(__dirname, 'platform-tools', 'adb.exe');
  if (fs.existsSync(local)) return local;
  const r = spawnSync('adb', ['version'], { windowsHide: true });
  if (!r.error) return 'adb';
  return null;
}
adbBin = findAdb();

function runAdb(args, timeoutMs = 15000) {
  if (!adbBin) return { ok: false, error: 'adb 不可用' };
  const r = spawnSync(adbBin, args, { windowsHide: true, timeout: timeoutMs, encoding: 'utf8' });
  if (r.error) return { ok: false, error: String(r.error.message || r.error) };
  return { ok: r.status === 0, out: (r.stdout || '') + (r.stderr || ''), code: r.status };
}

// ---------- 小工具 ----------
function json(res, code, obj) {
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8' });
  res.end(JSON.stringify(obj));
}
async function readBody(req) {
  let data = '';
  for await (const chunk of req) data += chunk;
  return data ? JSON.parse(data) : {};
}
async function speakerGet(ip, endpoint) {
  const r = await fetch(`http://${ip}:${SPEAKER_API_PORT}/${endpoint}`, { signal: AbortSignal.timeout(5000) });
  return r.json();
}
async function speakerPost(ip, endpoint, payload) {
  const r = await fetch(`http://${ip}:${SPEAKER_API_PORT}/${endpoint}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
    signal: AbortSignal.timeout(5000),
  });
  return r.text();
}

// ---------- 局域网扫描：探测 7766 端口 ----------
function probe(ip, port, timeoutMs = 350) {
  return new Promise(resolve => {
    const s = net.connect({ host: ip, port, timeout: timeoutMs });
    const done = ok => { s.destroy(); resolve(ok ? ip : null); };
    s.on('connect', () => done(true));
    s.on('timeout', () => done(false));
    s.on('error', () => done(false));
  });
}
async function scanSubnets(customBase) {
  const bases = new Set();
  if (customBase) {
    bases.add(customBase);
  } else {
    for (const addrs of Object.values(os.networkInterfaces()))
      for (const a of addrs || [])
        if (a.family === 'IPv4' && !a.internal) bases.add(a.address.split('.').slice(0, 3).join('.'));
  }
  const tasks = [];
  for (const base of bases)
    for (let i = 1; i <= 254; i++) tasks.push(probe(`${base}.${i}`, SPEAKER_API_PORT));
  const found = [];
  // 并发 64，避免瞬间打满
  for (let i = 0; i < tasks.length; i += 64) {
    const slice = await Promise.all(tasks.slice(i, i + 64));
    for (const ip of slice) if (ip) found.push(ip);
  }
  return found;
}

// ---------- platform-tools 自动下载（Google 官方源） ----------
async function downloadPlatformTools(res) {
  const zipUrl = 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip';
  const zipPath = path.join(__dirname, 'platform-tools.zip');
  try {
    const r = await fetch(zipUrl, { signal: AbortSignal.timeout(120000) });
    if (!r.ok) return { ok: false, error: `下载失败 HTTP ${r.status}` };
    await pipeline(r.body, fs.createWriteStream(zipPath));
    const u = spawnSync('powershell', ['-NoProfile', '-Command',
      `Expand-Archive -Force '${zipPath}' '${__dirname}'; Remove-Item '${zipPath}'`], { windowsHide: true, timeout: 120000 });
    if (u.status !== 0) return { ok: false, error: '解压失败: ' + u.stderr };
    adbBin = findAdb();
    return { ok: !!adbBin };
  } catch (e) {
    if (fs.existsSync(zipPath)) fs.unlinkSync(zipPath);
    return { ok: false, error: String(e.message || e) };
  }
}

// ---------- HTTP 路由 ----------
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://127.0.0.1:${PORT}`);
  const p = url.pathname;
  try {
    if (p === '/' || p === '/index.html') {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      return res.end(fs.readFileSync(path.join(__dirname, 'index.html')));
    }

    // --- 状态检查 ---
    if (p === '/api/ping') {
      const ip = url.searchParams.get('ip');
      if (!ip) return json(res, 400, { ok: false });
      try { await speakerGet(ip, 'Info'); return json(res, 200, { ok: true }); }
      catch { return json(res, 200, { ok: false }); }
    }
    if (p === '/api/scan' && req.method === 'GET') {
      const found = await scanSubnets(url.searchParams.get('base') || '');
      return json(res, 200, { found });
    }

    // --- adb 通道（触屏/按键/shell）--- 先于 ip 守卫处理
    if (p === '/api/adb/env') return json(res, 200, { available: !!adbBin, bin: adbBin });
    if (p === '/api/adb/setup' && req.method === 'POST') return json(res, 200, await downloadPlatformTools(res));
    if (p === '/api/adb/connect' && req.method === 'POST') {
      const { ip } = await readBody(req);
      return json(res, 200, runAdb(['connect', `${ip}:${ADB_PORT}`], 10000));
    }
    if (p === '/api/adb/shell' && req.method === 'POST') {
      const { ip, cmd } = await readBody(req);
      // 只允许经过白名单字符的 shell 命令，且不经过本机 shell
      if (!/^[\w @.:;/'"=<>&%|+\-,[\](){}*!?#]*$/.test(cmd)) return json(res, 400, { ok: false, error: '命令含不允许字符' });
      return json(res, 200, runAdb(['-s', `${ip}:${ADB_PORT}`, 'shell', cmd], 20000));
    }

    // --- 封面代理（不需 ip） ---
    if (p === '/api/cover' && req.method === 'GET') {
      const cu = url.searchParams.get('url');
      if (!cu || !/^https?:\/\//.test(cu)) return json(res, 400, { ok: false, error: 'bad url' });
      const r = await fetch(cu, { signal: AbortSignal.timeout(8000) });
      res.writeHead(200, { 'Content-Type': r.headers.get('content-type') || 'image/jpeg', 'Cache-Control': 'no-store' });
      return res.end(Buffer.from(await r.arrayBuffer()));
    }

    // --- 7766 HTTP API 代理（需 ip） ---
    if (p.startsWith('/api/')) {
      const ip = url.searchParams.get('ip');
      if (!ip || !/^[\w.:-]+$/.test(ip)) return json(res, 400, { ok: false, error: 'bad ip' });
      const cmdMap = { '/api/status': 'Status', '/api/info': 'Info', '/api/play': 'Play', '/api/pause': 'Pause', '/api/next': 'Next', '/api/prev': 'Prev' };
      if (cmdMap[p] && req.method === 'GET') return json(res, 200, await speakerGet(ip, cmdMap[p]));
      if (p === '/api/set-volume' && req.method === 'POST')
        return json(res, 200, { raw: await speakerPost(ip, 'SetVolume', { CurrentVolume: Number((await readBody(req)).value) }) });
      if (p === '/api/set-eq' && req.method === 'POST')
        return json(res, 200, { raw: await speakerPost(ip, 'SetEQMode', { EQMode: Number((await readBody(req)).value) }) });
    }

    json(res, 404, { ok: false, error: 'not found' });
  } catch (e) {
    json(res, 502, { ok: false, error: String(e.message || e) });
  }
});

server.listen(PORT, '127.0.0.1', () => console.log(`Gravity PC 面板: http://127.0.0.1:${PORT}`));
