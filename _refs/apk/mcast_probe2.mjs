// 硬判据：AP 到底把不把 未知组播 发到空口。
// 仪器 = netstat -e 的 "Non-unicast packets / Received"（组播+广播的进向帧数）。
// 本机协议栈回环不经过网卡，所以这个计数只认真正从空口进来的帧。
// 对照：广播一定被 AP 上空口 —— 用它先证明这个计数器确实会动，再比组播。
import { exec } from 'node:child_process';
import dgram from 'node:dgram';

const IF = process.argv[2] || '192.168.1.20';
const PORT = 1503, G = '239.254.84.80', BC = '255.255.255.255', N = 40;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const run = cmd => new Promise(r => exec(cmd, { windowsHide: true }, (e, o) => r(o || '')));

async function netstat() {
  const rows = (await run('netstat -e')).split(/\r?\n/)
    .map(l => (l.match(/\d+/g) || []).map(Number)).filter(a => a.length >= 2);
  if (rows.length < 3) throw new Error('netstat -e 解析失败');
  return { bytes: rows[0][0], uni: rows[1][0], nonuni: rows[2][0] };   // 第3行 = 非单播
}

const tx = dgram.createSocket('udp4');
await new Promise(r => tx.bind(0, IF, r));
tx.setBroadcast(true);
tx.setMulticastLoopback(false);           // 关掉栈回环，避免"自己收到自己"

async function flood(ip, len) {
  for (let k = 0; k < N; k++) { await new Promise(r => tx.send(Buffer.alloc(len, 0x71), 0, len, PORT, ip, r)); await sleep(70); }
  await sleep(1200);
}

const rx = dgram.createSocket({ type: 'udp4', reuseAddr: true });
let stackGot = 0;
rx.on('message', () => stackGot++);
await new Promise(r => rx.bind(PORT, '0.0.0.0', r));

let a = await netstat();
await flood(BC, 251);                     // 对照：广播
let b = await netstat();
console.log(`[对照 广播 x${N}] 网卡非单播RX +${b.nonuni - a.nonuni}   协议栈收到 ${stackGot}`);

stackGot = 0;
await sleep(4000);                        // 静默期
let c = await netstat();
console.log(`[静默 4s 噪声]     网卡非单播RX +${c.nonuni - b.nonuni}`);

stackGot = 0;
rx.addMembership(G, IF);                  // 先加组：问"AP 有没有能力把组播送上空口"
await flood(G, 252);                      // 测试：未知组播
let d = await netstat();
const mcDelta = d.nonuni - c.nonuni - (c.nonuni - b.nonuni);
console.log(`[测试 组播 x${N}]   网卡非单播RX +${d.nonuni - c.nonuni}（扣噪声≈${mcDelta}）   协议栈收到 ${stackGot}`);

if (b.nonuni - a.nonuni < N * 0.5) console.log('\n仪器不灵（广播都没让计数动），本轮作废，别下结论。');
else if (mcDelta > N * 0.5) console.log(`\n结论：这台 AP 会把 ${G} 送上空口（加组之后）。组播通道本身可用。`);
else console.log('\n结论：广播能上空口、未知组播不能 → 音响看不到 239.246.0.0 信标和 239.254.x.x 校验包，配网不可能成。');
rx.close(); tx.close();
process.exit(0);
