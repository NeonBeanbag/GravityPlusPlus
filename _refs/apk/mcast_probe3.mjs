// 把幅度拉大再测：AP 会不会把"未知组播"发到空口。仪器 = netstat -e 的非单播收包计数。
// 本机栈回环不计入网卡，所以只有真空口回来的帧才让计数动。
// 广播做对照（AP 一定转发广播），静默期做噪声底，三者同尺度比较。
import { exec } from 'node:child_process';
import dgram from 'node:dgram';

const IF = process.argv[2] || '192.168.1.20';
const PORT = 1503, G = '239.254.84.80', BC = '255.255.255.255', N = 300, GAP = 10;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const run = cmd => new Promise(r => exec(cmd, { windowsHide: true }, (e, o) => r(o || '')));
const nonuni = async () => {
  const rows = (await run('netstat -e')).split(/\r?\n/).map(l => (l.match(/\d+/g) || []).map(Number)).filter(a => a.length >= 2);
  return rows[2][0];
};

const tx = dgram.createSocket('udp4');
await new Promise(r => tx.bind(0, IF, r));
tx.setBroadcast(true);
tx.setMulticastLoopback(false);
async function flood(ip, len) {
  for (let k = 0; k < N; k++) { await new Promise(r => tx.send(Buffer.alloc(len, 0x71), 0, len, PORT, ip, r)); await sleep(GAP); }
  await sleep(1000);
}

const rx = dgram.createSocket({ type: 'udp4', reuseAddr: true });
let stackGot = 0; rx.on('message', () => stackGot++);
await new Promise(r => rx.bind(PORT, '0.0.0.0', r));

const w0 = await nonuni(); await sleep(4000); const w1 = await nonuni();
const noise = w1 - w0;
console.log(`噪声底：静默4s 非单播RX +${noise}`);

stackGot = 0; const b0 = await nonuni(); await flood(BC, 251); const b1 = await nonuni();
console.log(`对照 广播 x${N}：网卡RX +${b1 - b0}（扣噪声 ${b1 - b0 - noise}）  协议栈收到 ${stackGot}`);

await sleep(2000); const m0 = await nonuni();
stackGot = 0;
rx.addMembership(G, IF);
await flood(G, 252);
const m1 = await nonuni();
const net = m1 - m0 - noise;
console.log(`测试 组播 x${N}：网卡RX +${m1 - m0}（扣噪声 ${net}）  协议栈收到 ${stackGot}`);
const ctrl = b1 - b0 - noise;
console.log(`\n判据：广播净增 ${ctrl} vs 组播净增 ${net}`);
if (ctrl < N * 0.4) console.log('仪器不灵（对照都没动），本轮作废。');
else if (net > ctrl * 0.6) console.log('→ 组播和广播一样被送上空口：AP 转发未知组播，校验通道通，问题不在路由器。');
else if (net < ctrl * 0.15) console.log('→ 广播上得到空口、组播上不到：音响看不到 239.246.0.0 信标 + 239.254.x.x 校验包。');
else console.log('→ 组播只过了一部分（可能被限速/丢弃），需要看路由器组播速率设置。');
rx.close(); tx.close(); process.exit(0);
