// 尖峰实验：音响能不能播"无限长的直播流"（决定"把 PC 当音源"这条路成不成立）。
// 做法：HTTP 200 + Content-Type audio/wav，RIFF 头里写一个超长 data 长度，然后不断喂 PCM。
// 若它能持续 PLAYING、位置一直涨 → 可以做音源桥；若很快 STOPPED/NO_MEDIA → 只能走"一首一首切片"的路。
// 用法: node live_stream.mjs [--port 8125] [--secs 60]，然后另开一条：
//   curl -X POST http://127.0.0.1:8788/api/stream/play -H "Content-Type: application/json" -d "{\"url\":\"http://192.168.1.20:8125/live.wav\"}"
import http from 'node:http';

const PORT = parseInt((process.argv.find((a, i) => process.argv[i - 1] === '--port') || '8125'), 10);
const RATE = 44100, CH = 2, SECS = 3600;               // 谎称 1 小时
let conns = 0, bytesSent = 0, live = false;

function wavHeader(dataLen) {
  const h = Buffer.alloc(44);
  h.write('RIFF', 0); h.writeUInt32LE(36 + dataLen, 4); h.write('WAVE', 8);
  h.write('fmt ', 12); h.writeUInt32LE(16, 16); h.writeUInt16LE(1, 20); h.writeUInt16LE(CH, 22);
  h.writeUInt32LE(RATE, 24); h.writeUInt32LE(RATE * CH * 2, 28); h.writeUInt16LE(CH * 2, 32); h.writeUInt16LE(16, 34);
  h.write('data', 36); h.writeUInt32LE(dataLen, 40);
  return h;
}
// 每 0.2 秒换一个音高，方便凭耳朵判断"它拿到的是不是一直在推进的直播流"
function chunk(t0, t1) {
  const n = Math.floor(RATE * (t1 - t0));
  const b = Buffer.alloc(n * CH * 2);
  for (let i = 0; i < n; i++) {
    const t = t0 + i / RATE;
    const f = 330 + 110 * Math.sin(t * 1.7) + (Math.floor(t / 0.2) % 5) * 60;
    const env = 0.35 * (0.6 + 0.4 * Math.sin(t * 6));
    const v = Math.max(-1, Math.min(1, env * Math.sin(2 * Math.PI * f * t))) * 32767 | 0;
    b.writeInt16LE(v, i * 4); b.writeInt16LE(v, i * 4 + 2);
  }
  return b;
}

const srv = http.createServer(async (req, res) => {
  conns++;
  console.log(`\n[${new Date().toISOString().slice(11, 19)}] 第 ${conns} 个连接：${req.method} ${req.url} range=${req.headers.range || '-'}`);
  const dl = SECS * RATE * CH * 2;                       // 1 小时的数据量
  res.writeHead(200, {
    'Content-Type': 'audio/wav',
    'Content-Length': String(44 + dl),                    // 变体B：给确定超长长度
    'Accept-Ranges': 'bytes',
    'Server': 'GravityLive/1.0',
  });
  res.write(wavHeader(dl));
  let t = 0;
  const iv = setInterval(() => {
    if (!res.writable) return;
    const a = t; t += 0.2;
    const c = chunk(a, t); bytesSent += c.length;
    res.write(c);
  }, 120);
  res.on('close', () => { clearInterval(iv); console.log(`  连接关闭（累计发出 ${(bytesSent / 1024 / 1024).toFixed(2)}MB）`); });
});
srv.listen(PORT, '0.0.0.0', () => console.log(`直播测试流：http://192.168.1.20:${PORT}/live.wav  （推这个 URL 给音响）`));
setInterval(() => console.log(`  …已喂 ${(bytesSent / 1024).toFixed(0)}KB 音频（约 ${(bytesSent / (RATE * 4)).toFixed(1)} 秒），连接数 ${conns}`), 5000);
setTimeout(() => { console.log('测试窗口结束'); process.exit(0); }, (parseInt((process.argv.find((a, i) => process.argv[i - 1] === '--secs') || '60'), 10)) * 1000);
