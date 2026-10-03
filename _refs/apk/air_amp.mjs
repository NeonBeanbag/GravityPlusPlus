// 大振幅版：广播(必被AP上空口)做对照，未知组播做被测，静默做噪声底；仪器=netstat -e 非单播收包数
import { exec } from 'node:child_process';
import dgram from 'node:dgram';
const IF = '192.168.1.20', PORT = 1503, N = 300;
const run = c => new Promise(r => exec(c, { windowsHide: true }, (e, o) => r(o || '')));
const nonuni = async () => {
  const rows = (await run('netstat -e')).split(/\r?\n/).map(l => (l.match(/\d+/g) || []).map(Number)).filter(a => a.length >= 2);
  return rows[2][0];
};
const sleep = ms => new Promise(r => setTimeout(r, ms));
const tx = dgram.createSocket('udp4');
await new Promise(r => tx.bind(0, IF, r));
tx.setBroadcast(true); tx.setMulticastLoopback(false);
async function burst(ip, len) { for (let k = 0; k < N; k++) { await new Promise(r => tx.send(Buffer.alloc(len, 0x71), 0, len, PORT, ip, r)); await sleep(10); } await sleep(1200); }
const a0 = await nonuni(); await sleep(4000); const a1 = await nonuni();
console.log(`噪声底(4s静默): +${a1 - a0}`);
const b0 = await nonuni(); await burst('192.168.1.255', 251); const b1 = await nonuni();
console.log(`对照 广播 x${N}: 网卡非单播RX +${b1 - b0}`);
await sleep(2000); const m0 = await nonuni();
tx.addMembership('239.254.84.80', IF);
await burst('239.254.84.80', 252);
const m1 = await nonuni();
console.log(`被测 组播 x${N}: 网卡非单播RX +${m1 - m0}`);
tx.close(); process.exit(0);
