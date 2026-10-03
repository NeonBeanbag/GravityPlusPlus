// 我们自己当 RAOP 发送端：把一段正弦音推给音响，验证"L16 明文 + 12 字节头"这条路走得通。
// 用法: node raop_send.mjs [音响IP] [本机IP] [秒数=6] [频率=440]
import net from 'node:net';
import dgram from 'node:dgram';

const HOST = process.argv[2] || '192.168.1.10';
const MYIP = process.argv[3] || '192.168.1.20';
const SECS = parseFloat(process.argv[4] || '6');
const FREQ = parseFloat(process.argv[5] || '440');
const RATE = 44100, CH = 2, FRAME = 256;            // 每包 256 帧 = 1024 B 载荷，不过 MTU
const CTRL_PORT = 10000, TIMING_PORT = 10001;

let cseq = 0, sock = net.connect({ host: HOST, port: 5000, timeout: 8000 });
let buf = '', waiters = [];
sock.on('data', d => {
  buf += d.toString('latin1');
  for (;;) {
    const i = buf.indexOf('\r\n\r\n');
    if (i < 0) return;
    const head = buf.slice(0, i);
    const cl = parseInt((head.match(/Content-Length: *(\d+)/i) || [, '0'])[1], 10);
    if (buf.length - i - 4 < cl) return;
    const msg = buf.slice(0, i + 4 + cl);
    buf = buf.slice(i + 4 + cl);
    const w = waiters.shift();
    if (w) w(msg); else console.log('unsolicited: ' + msg.replace(/\r\n/g, ' | ').slice(0, 160));
  }
});
sock.on('error', e => console.log('[sock err]', e.code));
const brief = t => t.replace(/\r\n/g, ' | ').slice(0, 300);
function rtsp(method, uri, headers = {}, body = '', ms = 5000) {
  return new Promise(res => {
    const to = setTimeout(() => { res('<<TIMEOUT>>'); }, ms);
    waiters.push(m => { clearTimeout(to); res(m); });
    const h = { User: 'gravity-pc.local', ...headers };
    sock.write(method + ' ' + uri + ' RTSP/1.0\r\n' +
      Object.entries(h).map(([k, v]) => k + ': ' + v).join('\r\n') + '\r\n\r\n' + body);
  });
}

// --- 1) 握手 ---
console.log('OPTIONS  ' + brief(await rtsp('OPTIONS', '*')));
const sdp = ['v=0', 'o=- 0 0 IN IP4 127.0.0.1', 's=gravity-pc',
  'c=IN IP4 ' + MYIP, 't=0 0',
  'm=audio 0 RTP/AVP 10', 'a=rtpmap:10 L16/44100/2',
  'a=tool:Gravity++ 0.1', 'a=control:streamid=0', ''].join('\r\n');
const ann = await rtsp('ANNOUNCE', '/audio/1.mp4?sessionid=1',
  { 'Content-Type': 'application/sdp', 'Content-Length': Buffer.byteLength(sdp) }, sdp);
console.log('ANNOUNCE ' + brief(ann));
const setup = await rtsp('SETUP', '/audio/1.mp4?sessionid=1',
  { Transport: `RTP/AVP/UDP;unicast;client_port=${CTRL_PORT}-${TIMING_PORT}` });
console.log('SETUP    ' + brief(setup));
const serverPort = parseInt((setup.match(/server_port=(\d+)/) || [, '0'])[1], 10);
const session = (setup.match(/Session: *([^;\r\n]+)/i) || [, '1'])[1].trim();
if (!serverPort) { console.log('拿不到 server_port，停'); process.exit(1); }
console.log(`server_port=${serverPort} session=${session}`);

// --- 2) 音量（SET_PARAMETER，text/parameters 只认 volume；元数据格式另测）---
const vol = 'volume: -25.000000\r\n';
console.log('VOLUME   ' + brief(await rtsp('SET_PARAMETER', '/audio/1.mp4?sessionid=1',
  { 'Content-Type': 'text/parameters', 'Content-Length': vol.length }, vol)));
if (process.env.META) {
  const meta = process.env.META;
  const ct = process.env.META_CT || 'text/parameters';
  console.log('META[' + ct + '] ' + brief(await rtsp('SET_PARAMETER', '/audio/1.mp4?sessionid=1',
    { 'Content-Type': ct, 'Content-Length': Buffer.byteLength(meta) }, meta)));
}

// --- 3) RECORD + 持续发音频 ---
const rt0 = (Date.now() * RATE / 1000) >>> 0;
const rec = await rtsp('RECORD', '/audio/1.mp4?sessionid=1',
  { Transport: `RTP/AVP/UDP;unicast;client_port=${CTRL_PORT}-${TIMING_PORT}`,
    'RTP-Info': `seq=12345;rtptime=${rt0}` });
console.log('RECORD   ' + brief(rec));

const udp = dgram.createSocket('udp4');
const timing = dgram.createSocket('udp4');
// 音响会往我们的 timing_port 发 RTPS，回一个 RTPC（把它的时钟原样还回去）
timing.bind(TIMING_PORT, () => {
  timing.on('message', (m, rinfo) => {
    if (m[1] === 0xd4) {                       // RTPS -> RTPC
      const back = Buffer.from(m); back[1] = 0xd5;
      timing.send(back, rinfo.port, rinfo.address);
    }
  });
});
let seq = 12345, n = 0;
const sendPackets = () => {
  const payload = Buffer.alloc(FRAME * CH * 2);
  for (let f = 0; f < FRAME; f++) {
    const t = (n * FRAME + f) / RATE;
    const v = Math.round(Math.sin(2 * Math.PI * FREQ * t) * 9000);
    payload.writeInt16LE(v, (f * 2) * 2);
    payload.writeInt16LE(v, (f * 2 + 1) * 2);
  }
  const h = Buffer.alloc(12);
  h[0] = 0x00; h[1] = 0x20;                    // type = audio，未加密
  h.writeUInt16BE(seq & 0xffff, 2);
  h.writeUInt32BE(((rt0 + n * FRAME) >>> 0), 4);
  h.writeUInt32BE(0, 8);
  udp.send(Buffer.concat([h, payload]), serverPort, HOST);
  seq++; n++;
};
setInterval(sendPackets, FRAME / RATE * 1000);
console.log(`推 ${SECS}s ${FREQ}Hz 到 ${HOST}:${serverPort}（每包 ${FRAME} 帧 / ${(FRAME / RATE * 1000).toFixed(1)}ms）`);
setTimeout(async () => {
  console.log('TEARDOWN ' + brief(await rtsp('TEARDOWN', '/audio/1.mp4?sessionid=1')));
  process.exit(0);
}, SECS * 1000);
