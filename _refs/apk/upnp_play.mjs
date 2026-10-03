// 目标②实验：用标准 UPnP AVTransport 把 PC 上的音频推到 Gravity 播放。
//   本地 HTTP 服务（支持 Range，DLNA 必需）→ SetAVTransportURI(+DIDL) → Play → 轮询状态
// 用法: node upnp_play.mjs [--file <路径>] [--vol 8] [--mime audio/wav] [--keep]
import http from 'node:http';
import fs from 'node:fs';
import dgram from 'node:dgram';
import { spawn } from 'node:child_process';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : d; };
const VOL = parseInt(arg('vol', '8'), 10);
const MIME = arg('mime', 'audio/wav');
const FILE = arg('file', '');

// 没给文件就现生成一段 6 秒的 PCM WAV（音阶，一听就知道是我们的流）
function makeWav(path) {
  const rate = 44100, secs = 6, n = rate * secs;
  const notes = [523.25, 587.33, 659.25, 698.46, 783.99, 880, 987.77, 1046.5];
  const data = Buffer.alloc(n * 2 * 2);                 // 16-bit stereo
  for (let i = 0; i < n; i++) {
    const t = i / rate;
    const f = notes[Math.floor(t / 0.75) % notes.length];
    const env = Math.min(1, (t % 0.75) * 12) * Math.exp(-1.6 * (t % 0.75));
    const s = Math.sin(2 * Math.PI * f * t) * 0.32 + Math.sin(2 * Math.PI * f * 2 * t) * 0.08;
    const v = Math.max(-1, Math.min(1, s * env)) * 32767 | 0;
    data.writeInt16LE(v, i * 4); data.writeInt16LE(v, i * 4 + 2);
  }
  const h = Buffer.alloc(44);
  h.write('RIFF', 0); h.writeUInt32LE(36 + data.length, 4); h.write('WAVE', 8);
  h.write('fmt ', 12); h.writeUInt32LE(16, 16); h.writeUInt16LE(1, 20); h.writeUInt16LE(2, 22);
  h.writeUInt32LE(rate, 24); h.writeUInt32LE(rate * 4, 28); h.writeUInt16LE(4, 32); h.writeUInt16LE(16, 34);
  h.write('data', 36); h.writeUInt32LE(data.length, 40);
  fs.writeFileSync(path, Buffer.concat([h, data]));
}

const file = FILE || '_probe_tone.wav';
if (!fs.existsSync(file)) { makeWav(file); console.log(`(生成测试音频 ${file}, ${(fs.statSync(file).size / 1024).toFixed(0)}KB)`); }
const body = fs.readFileSync(file);

// 本机 WLAN IPv4（dgram connect 是异步的，address() 拿不到，直接用 PowerShell 问系统）
import { execSync } from 'node:child_process';
function lanIP() {
  try {
    const o = execSync('powershell -NoProfile -Command "(Get-NetIPAddress -InterfaceAlias WLAN -AddressFamily IPv4 -ErrorAction SilentlyContinue).IPAddress"', { encoding: 'utf8' });
    const ip = o.trim().split(/\s+/).find(x => /^\d+\.\d+\.\d+\.\d+$/.test(x));
    if (ip) return ip;
  } catch { }
  const s = dgram.createSocket('udp4');
  s.connect(53, '192.168.1.1');
  const ip = s.address().address; s.close(); return ip;
}
const IP = lanIP(), PORT = parseInt(arg('port', '8123'), 10);
let hits = 0;
const srv = http.createServer((req, res) => {
  hits++;
  const range = /bytes=(\d*)-(\d*)/.exec(req.headers.range || '');
  console.log(`  HTTP #${hits} ${req.method} ${req.url} range=${req.headers.range || '-'}`);
  if (req.method === 'HEAD') { res.writeHead(200, { 'Content-Length': body.length, 'Content-Type': MIME, 'Accept-Ranges': 'bytes', 'DLNA.DMR': 'yes' }); return res.end(); }
  if (range) {
    const a = range[1] ? parseInt(range[1], 10) : 0;
    const b = range[2] ? parseInt(range[2], 10) : body.length - 1;
    res.writeHead(206, { 'Content-Range': `bytes ${a}-${b}/${body.length}`, 'Content-Length': b - a + 1, 'Content-Type': MIME, 'Accept-Ranges': 'bytes' });
    return res.end(body.subarray(a, b + 1));
  }
  res.writeHead(200, { 'Content-Length': body.length, 'Content-Type': MIME, 'Accept-Ranges': 'bytes' });
  res.end(body);
});
srv.listen(PORT, '0.0.0.0', () => console.log(`本地 HTTP: http://${IP}:${PORT}/${file}  (${body.length}B)`));

// ---- SSDP 发现（UUID 每次开机都变，不能写死）----
const found = await new Promise(res => {
  const s = dgram.createSocket('udp4'), hits2 = new Map();
  s.on('message', m => { const l = (/LOCATION: (\S+)/i.exec(m.toString('latin1')) || [])[1]; if (l) hits2.set(l, 1); });
  s.on('error', () => { });
  s.bind(0, () => { const q = ['M-SEARCH * HTTP/1.1', 'HOST: 239.255.255.250:1900', 'MAN: "ssdp:discover"', 'MX: 1', 'ST: ssdp:all', '', ''].join('\r\n'); s.send(q, 0, q.length, 1900, '239.255.255.250'); });
  setTimeout(() => { try { s.close(); } catch { } res([...hits2.keys()]); }, 2500);
});
const loc = found.find(l => /MediaRenderer|desc\.xml/i.test(l)) || found[0];
if (!loc) { console.log('没发现 UPnP 设备'); process.exit(1); }
console.log('LOCATION:', loc);
const base = new URL(loc), abs = p => new URL(p, base).href;
const desc = await (await fetch(loc)).text();
const urlOf = t => { const m = new RegExp('<serviceType>urn:schemas-upnp-org:service:' + t + ':1</serviceType>[\\s\\S]*?<controlURL>([^<]+)</controlURL>').exec(desc); return m ? abs(m[1]) : ''; };
const av = urlOf('AVTransport'), rc = urlOf('RenderingControl');
console.log(`AVTransport=${av}\nRenderingControl=${rc}`);

const soap = (url, svc, action, inner) => new Promise(res => {
  const xml = `<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><u:${action} xmlns:u="${svc}">${inner}</u:${action}></s:Body></s:Envelope>`;
  const u = new URL(url);
  const req = http.request({ host: u.hostname, port: u.port, path: u.pathname, method: 'POST', headers: { 'Content-Type': 'text/xml; charset="utf-8"', SOAPAction: `"${svc}#${action}"`, 'Content-Length': Buffer.byteLength(xml) } },
    r => { let b = ''; r.on('data', d => b += d); r.on('end', () => res({ code: r.statusCode, body: b })); });
  req.on('error', e => res({ err: e.code })); req.write(xml); req.end();
});
const val = (b, tag) => (new RegExp('<' + tag + '(?:[^>]*)>([^<]*)<').exec(b || '') || [])[1];

// ---- 先把音量打开 ----
if (rc) {
  console.log('SetMute=0 →', (await soap(rc, 'urn:schemas-upnp-org:service:RenderingControl:1', 'SetMute', '<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredMute>0</DesiredMute>')).code);
  console.log(`SetVolume=${VOL} →`, (await soap(rc, 'urn:schemas-upnp-org:service:RenderingControl:1', 'SetVolume', `<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>${VOL}</DesiredVolume>`)).code);
}
// ---- 推流 ----
const didl = `<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/"><item id="0" parent="-1" restricted="0"><dc:title>Gravity++ PC 推流测试</dc:title><upnp:class>object.item.audioItem.musicTrack</upnp:class><upnp:genre>Test</upnp:genre><res protocolInfo="http-get:*:${MIME}:*" size="${body.length}">${`http://${IP}:${PORT}/${file}`}</res></item></DIDL-Lite>`;
const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const streamUrl = `http://${IP}:${PORT}/${file}`;
const r1 = await soap(av, 'urn:schemas-upnp-org:service:AVTransport:1', 'SetAVTransportURI', `<InstanceID>0</InstanceID><CurrentURI>${streamUrl}</CurrentURI><CurrentURIMetaData>${esc(didl)}</CurrentURIMetaData>`);
console.log(`SetAVTransportURI → HTTP ${r1.code || r1.err}  errCode=${val(r1.body, 'errorCode') || '-'} ${val(r1.body, 'errorDescription') || ''}`);
const r2 = await soap(av, 'urn:schemas-upnp-org:service:AVTransport:1', 'Play', '<InstanceID>0</InstanceID><Speed>1</Speed>');
console.log(`Play → HTTP ${r2.code || r2.err}  errCode=${val(r2.body, 'errorCode') || '-'} ${val(r2.body, 'errorDescription') || ''}`);

const WATCH = parseInt(arg('watch', '24'), 10);
for (let i = 1; i <= WATCH; i++) {
  await new Promise(r => setTimeout(r, 1500));
  const t = await soap(av, 'urn:schemas-upnp-org:service:AVTransport:1', 'GetTransportInfo', '<InstanceID>0</InstanceID>');
  const p = await soap(av, 'urn:schemas-upnp-org:service:AVTransport:1', 'GetPositionInfo', '<InstanceID>0</InstanceID>');
  const m = await soap(av, 'urn:schemas-upnp-org:service:AVTransport:1', 'GetMediaInfo', '<InstanceID>0</InstanceID>');
  console.log(`  [${i}] Transport=${val(t.body, 'CurrentTransportState') || t.err || '?'} Status=${val(t.body, 'CurrentTransportStatus')} Pos=${val(p.body, 'RelTime')} 剩余拉流次数=${hits} NrTracks=${val(m.body, 'NrTracks')}`);
}
const st = await (await fetch('http://' + base.hostname + ':7766/Status').catch(() => null))?.text().catch(() => '') ;
console.log('7766/Status:', (st || '(取不到)').slice(0, 260));
console.log(`\n本地 HTTP 共被请求 ${hits} 次 —— >0 就说明是**音响主动来拉流**（不是我们本地回环）`);
srv.close();
process.exit(0);
