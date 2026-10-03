// 在一条真实 RAOP 会话里试一种 SET_PARAMETER 元数据/封面格式，看设备日志认不认。
// 用法: node raop_meta.mjs <plist|plistart|jpeg|progress|note> [音响IP] [本机IP]
import net from 'node:net';
import dgram from 'node:dgram';
import fs from 'node:fs';
import { encode } from './bplist.mjs';

const WHAT = process.argv[2] || 'plist';
const HOST = process.argv[3] || '192.168.1.10';
const MYIP = process.argv[4] || '192.168.1.20';
const RATE = 44100, FRAME = 256, TIMING_PORT = 10001;

let cseq = 0;
const sock = net.connect({ host: HOST, port: 5000, timeout: 8000 });
let buf = '', waiters = [];
sock.on('data', d => {
  buf += d.toString('latin1');
  for (;;) {
    const i = buf.indexOf('\r\n\r\n');
    if (i < 0) return;
    const cl = parseInt((buf.slice(0, i).match(/Content-Length: *(\d+)/i) || [, '0'])[1], 10);
    if (buf.length - i - 4 < cl) return;
    const msg = buf.slice(0, i + 4 + cl); buf = buf.slice(i + 4 + cl);
    const w = waiters.shift(); if (w) w(msg);
  }
});
sock.on('error', e => console.log('[sock err]', e.code));
const brief = t => t.replace(/\r\n/g, ' | ').slice(0, 200);
function rtsp(method, uri, headers = {}, body = '', ms = 4000) {
  if (typeof body === 'string') body = Buffer.from(body, 'binary');
  return new Promise(res => {
    const to = setTimeout(() => { res('<<TIMEOUT 无回包>>'); }, ms);
    waiters.push(m => { clearTimeout(to); res(m); });
    const h = { CSeq: String(++cseq), User: 'gravity-pc.local', ...headers };
    sock.write(Buffer.concat([
      Buffer.from(method + ' ' + uri + ' RTSP/1.0\r\n' +
        Object.entries(h).map(([k, v]) => k + ': ' + v).join('\r\n') + '\r\n\r\n', 'ascii'), body]));
  });
}

const sdp = ['v=0', 'o=- 0 0 IN IP4 127.0.0.1', 's=gravity-pc', 'c=IN IP4 ' + MYIP, 't=0 0',
  'm=audio 0 RTP/AVP 10', 'a=rtpmap:10 L16/44100/2', 'a=control:streamid=0', ''].join('\r\n');
console.log('OPTIONS  ' + brief(await rtsp('OPTIONS', '*')));
console.log('ANNOUNCE ' + brief(await rtsp('ANNOUNCE', '/audio/1.mp4?sessionid=1',
  { 'Content-Type': 'application/sdp', 'Content-Length': Buffer.byteLength(sdp) }, sdp)));
const setup = await rtsp('SETUP', '/audio/1.mp4?sessionid=1',
  { Transport: `RTP/AVP/UDP;unicast;client_port=10000-${TIMING_PORT}` });
console.log('SETUP    ' + brief(setup));
const serverPort = parseInt((setup.match(/server_port=(\d+)/) || [, '0'])[1], 10);
const rt0 = (Date.now() * RATE / 1000) >>> 0;
console.log('RECORD   ' + brief(await rtsp('RECORD', '/audio/1.mp4?sessionid=1',
  { Transport: `RTP/AVP/UDP;unicast;client_port=10000-${TIMING_PORT}`, 'RTP-Info': `seq=1;rtptime=${rt0}` })));

const udp = dgram.createSocket('udp4'), timing = dgram.createSocket('udp4');
timing.bind(TIMING_PORT, () => timing.on('message', (m, r) => {
  if (m[1] === 0xd4) { const b = Buffer.from(m); b[1] = 0xd5; timing.send(b, r.port, r.address); }
}));
let seq = 1, n = 0;
const tick = setInterval(() => {
  const p = Buffer.alloc(FRAME * 4);
  for (let f = 0; f < FRAME; f++) {
    const v = Math.round(Math.sin(2 * Math.PI * 330 * (n * FRAME + f) / RATE) * 8000);
    p.writeInt16LE(v, f * 4); p.writeInt16LE(v, f * 4 + 2);
  }
  const h = Buffer.alloc(12); h[1] = 0x20; h.writeUInt16BE(seq & 0xffff, 2);
  h.writeUInt32BE((rt0 + n * FRAME) >>> 0, 4);
  udp.send(Buffer.concat([h, p]), serverPort, HOST); seq++; n++;
}, FRAME / RATE * 1000);

const jpg = fs.readFileSync(new URL('./test_cover.jpg', import.meta.url));
const META = { Title: 'RAOP 自研测试', Artist: 'Gravity++', Album: '没有 Apple Music' };
const xml = n => `<?xml version="1.0" encoding="UTF-8"?>\n<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">\n<plist version="1.0">\n<dict>\n${n}</dict>\n</plist>\n`;
const xmlBody = xml(Object.entries(META).map(([k, v]) => `  <key>${k}</key>\n  <string>${v}</string>\n`).join(''));
const rec = (type, s) => {
  const p = Buffer.from(s, 'utf8');
  const body = Buffer.concat([Buffer.from([0x01]), Buffer.from(type, 'ascii'), Buffer.from([0x00, 0x00]),
    Buffer.from([p.length >> 8, p.length & 0xff]), p]);
  return Buffer.concat([Buffer.from([(body.length + 2) >> 8, (body.length + 2) & 0xff]), body]);
};
const db = items => {
  const body = Buffer.concat(items);
  const head = Buffer.alloc(12);
  head.writeUInt32BE(body.length + 12, 0); head.writeUInt32BE(0, 4); head.writeUInt32BE(items.length, 8);
  return Buffer.concat([head, body]);
};
const items = [rec('minm', 'RAOP 自研测试'), rec('asar', 'Gravity++'), rec('asal', '没有 Apple Music')];
const mdst = () => {                                  // 直接往 server_port+1 丢 mdst 包
  const body = db(items);
  const h = Buffer.alloc(16);
  h.writeUInt32BE(body.length + 4, 0); h.write('mdst', 4, 'ascii');
  h[8] = 1; h[9] = 0xa9; h.writeUInt32BE(0, 10); h.writeUInt32BE(3, 14);
  udp.send(Buffer.concat([h, body.subarray(12)]), serverPort + 1, HOST);
  console.log(`[mdst] 已发到 ${HOST}:${serverPort + 1}`);
};
const cases = { plist: 1, xmlplist: 1, plistart: 1, jpeg: 1, xdb: 1, progress: 1, note: 1 }[WHAT]
  ? { plist: ['application/x-apple-binary-plist', encode(META)],
      xmlplist: ['text/x-apple-plist+xml', xmlBody],
      plistart: ['application/x-apple-binary-plist', encode({ ...META, artwork: jpg })],
      jpeg: ['image/jpeg', jpg],
      xdb: ['application/x-database', db(items)],
      progress: ['text/parameters', 'progress: 1000/44100/44100\r\n'],
      note: ['text/parameters', 'note: hello\r\n'] }
  : null;
if (!cases && WHAT !== 'mdstudp' && WHAT !== 'xdbmulti') { console.log('未知变体'); process.exit(1); }
await new Promise(r => setTimeout(r, 1500));
if (WHAT === 'xdbmulti') {
  // 一次发好几种封帧，每种把标题写成 V1/V2/...，日志里 ~~~~meta : V?;; 出现的那条就是对的封帧
  const mk = (variant, tag) => {
    const one = (type, s) => {
      const p = Buffer.from(s, 'utf8');
      const r = Buffer.concat([Buffer.from([0x01]), Buffer.from(type, 'ascii'), Buffer.from([0x00, 0x00]),
        Buffer.from([p.length >> 8, p.length & 0xff]), p]);
      const len = variant === 2 ? r.length : r.length + 2;         // 记录长度含/不含自身 2 字节
      return Buffer.concat([Buffer.from([len >> 8, len & 0xff]), r]);
    };
    const body = Buffer.concat([one('minm', tag), one('asar', 'A' + tag), one('asal', 'B' + tag)]);
    const h = Buffer.alloc(12);
    h.writeUInt32BE(variant === 3 ? body.length : body.length + 12, 0);   // 库长度含/不含头
    h.writeUInt32BE(0, 4); h.writeUInt32BE(3, 8);
    return Buffer.concat([h, body]);
  };
  for (const [i, tag] of [[1, 'V1'], [2, 'V2'], [3, 'V3'], [1, 'V4']]) {
    const b = mk(i, tag);
    const r = await rtsp('SET_PARAMETER', '/audio/1.mp4?sessionid=1',
      { 'Content-Type': 'application/x-database', 'Content-Length': b.length }, b, 2000);
    console.log(`x-database ${tag} (变体${i}) -> ${brief(r)}`);
    await new Promise(r2 => setTimeout(r2, 900));
  }
} else if (WHAT === 'mdstudp') mdst();
else console.log(`SET_PARAMETER[${WHAT}] -> ` + brief(await rtsp('SET_PARAMETER', '/audio/1.mp4?sessionid=1',
  { 'Content-Type': cases[WHAT][0], 'Content-Length': cases[WHAT][1].length }, cases[WHAT][1])));
await new Promise(r => setTimeout(r, 2500));
clearInterval(tick);
console.log('OPTIONS(复查服务还活着吗) ' + brief(await rtsp('OPTIONS', '*', {}, '', 2500)));
process.exit(0);
