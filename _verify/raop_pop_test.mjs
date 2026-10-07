// 破音定位：同一段正弦，两种停法对照。
//   --mode hard  正弦停在任意相位（数据末端非零）
//   --mode fade  150ms 线性淡出到 0，再补 400ms 纯零数据，然后 TEARDOWN
// 淡出停法下如果还听得到响，那一声就不可能来自"波形被截断"。
// 用法: node raop_pop_test.mjs [音响IP] [本机IP] [--mode hard|fade]
import net from 'node:net';
import dgram from 'node:dgram';

const argv = process.argv.slice(2);
const HOST = argv[0] || '192.168.1.10';
const MYIP = argv[1] || '192.168.1.20';
const MODE = (argv.find((a, i) => argv[i] === '--mode') ? argv[argv.indexOf('--mode') + 1] : '') || 'fade';
const SECS = parseFloat((argv.find((a, i) => argv[i] === '--secs') ? argv[argv.indexOf('--secs') + 1] : '') || '2');
const RATE = 44100, CH = 2, FRAME = 256, FREQ = 440, AMP = 9000;
const CTRL_PORT = 10000, TIMING_PORT = 10001;
const T_SINE = SECS, T_FADE = 0.15, T_TAIL = 0.4;
const T_STOP = MODE === 'fade' ? T_SINE + T_FADE + T_TAIL : T_SINE;

const t0 = Date.now();
const at = () => ((Date.now() - t0) / 1000).toFixed(3) + 's';
const gainAt = t => MODE !== 'fade' ? 1 : t < T_SINE ? 1 : t < T_SINE + T_FADE ? 1 - (t - T_SINE) / T_FADE : 0;

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
    if (w) w(msg); else console.log(at() + ' unsolicited: ' + msg.replace(/\r\n/g, ' | ').slice(0, 140));
  }
});
sock.on('error', e => console.log(at(), '[sock err]', e.code));
const brief = t => t.replace(/\r\n/g, ' | ').slice(0, 200);
function rtsp(method, headers = {}, body = '', ms = 5000) {
  return new Promise(res => {
    const to = setTimeout(() => res('<<TIMEOUT>>'), ms);
    waiters.push(m => { clearTimeout(to); res(m); });
    const h = { User: 'gravity-pc.local', CSeq: ++cseq, ...headers };
    sock.write(method + ' /audio/1.mp4?sessionid=1 RTSP/1.0\r\n' +
      Object.entries(h).map(([k, v]) => k + ': ' + v).join('\r\n') + '\r\n\r\n' + body);
  });
}

console.log(at() + ` OPTIONS  ` + brief(await rtsp('OPTIONS')));
const sdp = ['v=0', 'o=- 0 0 IN IP4 127.0.0.1', 's=gravity-pc', 'c=IN IP4 ' + MYIP, 't=0 0',
  'm=audio 0 RTP/AVP 10', 'a=rtpmap:10 L16/44100/2', 'a=tool:Gravity++ 0.1', 'a=control:streamid=0', ''].join('\r\n');
console.log(at() + ` ANNOUNCE ` + brief(await rtsp('ANNOUNCE',
  { 'Content-Type': 'application/sdp', 'Content-Length': Buffer.byteLength(sdp) }, sdp)));
const setup = await rtsp('SETUP', { Transport: `RTP/AVP/UDP;unicast;client_port=${CTRL_PORT}-${TIMING_PORT}` });
const serverPort = parseInt((setup.match(/server_port=(\d+)/) || [, '0'])[1], 10);
if (!serverPort) { console.log('拿不到 server_port，停'); process.exit(1); }
console.log(at() + ` SETUP 原文:\n` + setup.replace(/^/gm, '    ') + `\n`);
const vol = 'volume: -20.000000\r\n';
console.log(at() + ` VOLUME   ` + brief(await rtsp('SET_PARAMETER',
  { 'Content-Type': 'text/parameters', 'Content-Length': vol.length }, vol)));

const rt0 = (Date.now() * RATE / 1000) >>> 0;
console.log(at() + ` RECORD   ` + brief(await rtsp('RECORD',
  { Transport: `RTP/AVP/UDP;unicast;client_port=${CTRL_PORT}-${TIMING_PORT}`, 'RTP-Info': `seq=12345;rtptime=${rt0}` })));

function notify(body) {                                  // 只发不等：应答顺序不参与我们的逻辑，等反而会把队列搅乱
  const h = { User: 'gravity-pc.local', CSeq: ++cseq, 'Content-Type': 'text/parameters',
              'Content-Length': Buffer.byteLength(body) };
  sock.write('SET_PARAMETER /audio/1.mp4?sessionid=1 RTSP/1.0\r\n' +
    Object.entries(h).map(([k, v]) => k + ': ' + v).join('\r\n') + '\r\n\r\n' + body);
}
// 真 AirPlay 发送端每秒都在报进度；不报的话设备侧可能有看门狗（我们观测到音轨固定 21 秒被关）
setInterval(() => notify(`progress: 0.000000,0.000000,${((rt0 + n * FRAME) >>> 0)}\r\n`), 1000);

const udp = dgram.createSocket('udp4');
const timing = dgram.createSocket('udp4');
// 控制端口必须绑上：fireair 的重传请求(RTP 0x57)发到这里，不接就等于告诉设备"发送端死了"
udp.bind(CTRL_PORT, () => {
  udp.on('message', (m, r) => {
    const type = m[1] & 0x7f;
    console.log(at() + ` 收到 ${r.address}:${r.port} 类型=0x${type.toString(16)} ${type === 0x57 ? '(重传请求 seq=' + m.readUInt16BE(4) + ' ' + m.readUInt16BE(6) + ')' : ''}`);
    if (type === 0x57) {                       // 按 RAOP 语义回一个同类型应答，别让设备白等
      const back = Buffer.from(m.subarray(0, 8));
      udp.send(back, r.port, r.address);
    }
  });
});
timing.bind(TIMING_PORT, () => {
  timing.on('message', (m, rinfo) => {
    if (m[1] === 0xd4) { const back = Buffer.from(m); back[1] = 0xd5; timing.send(back, rinfo.port, rinfo.address); }
  });
});
let seq = 12345, n = 0, zeroed = false;
const PACKET_MS = FRAME / RATE * 1000;
const sendAt = t0 + 200;                       // 第一包的绝对期限，只留 200ms 前置量
const yieldNow = () => new Promise(r => setImmediate(r));
(async () => {
  while (true) {
    const t = n * FRAME / RATE;
    if (t >= T_STOP) break;
    const deadline = sendAt + n * PACKET_MS;
    while (Date.now() < deadline) await yieldNow();   // setInterval 在 Windows 上是 15.6ms 粒度，会把流拖成欠载
    const g = gainAt(t);
    const payload = Buffer.alloc(FRAME * CH * 2);
    if (g > 0) for (let f = 0; f < FRAME; f++) {
      const v = Math.round(Math.sin(2 * Math.PI * FREQ * (t + f / RATE)) * AMP * g);
      payload.writeInt16LE(v, f * 4); payload.writeInt16LE(v, f * 4 + 2);
    }
    const h = Buffer.alloc(12);
    h[0] = 0x00; h[1] = 0x20; h.writeUInt16BE(seq & 0xffff, 2);
    h.writeUInt32BE(((rt0 + n * FRAME) >>> 0), 4); h.writeUInt32BE(0, 8);
    udp.send(Buffer.concat([h, payload]), serverPort, HOST);
    seq++; n++;
    if (!zeroed && g === 0) { zeroed = true; console.log(at() + ' 数据已归零（之后的响与内容无关）'); }
    if (n % 100 === 0) await yieldNow();               // 让 RTSP/timing 的收发跑一下
  }
  const drift = ((Date.now() - t0) / 1000 - T_STOP).toFixed(2);
  console.log(at() + ` 发完 ${n} 包（模式=${MODE}，末端增益=${gainAt(T_STOP - 0.001).toFixed(2)}，实时偏差=${drift}s），停流`);
  await new Promise(r => setTimeout(r, 250));
  console.log(at() + ` TEARDOWN ` + brief(await rtsp('TEARDOWN', {}, '', 1500)));
  process.exit(0);
})();
