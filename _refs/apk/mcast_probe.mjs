// 判定「路由器有没有把未知组播转发到空口」——Airkiss 的校验包(239.254.x.x)全押在这上面。
// 手法：发送端关掉 IP_MULTICAST_LOOP（本机回环被抑制），于是"收到"只可能是从线上回来的。
// Node 的 dgram 拿不到目的地址，所以给每个目的地址分配一个专属长度来归因：
//   137 → 239.254.84.80(A相通路对照)   251 → 239.254.84.80   252 → 239.246.0.0   253 → 广播
import dgram from 'node:dgram';

const IF = process.argv[2] || '192.168.1.20';
const PORT = 1503;
const G_DATA = '239.254.84.80';
const G_BEACON = '239.246.0.0';
const BCAST = '255.255.255.255';
const sleep = ms => new Promise(r => setTimeout(r, ms));

const rx = dgram.createSocket({ type: 'udp4', reuseAddr: true });
const got = [];
rx.on('message', (m, r) => got.push({ len: m.length, src: r.address }));
await new Promise(r => rx.bind(PORT, '0.0.0.0', r));
for (const g of [G_DATA, G_BEACON]) {
  try { rx.addMembership(g, IF); console.log(`已加组 ${g} @ ${IF}`); } catch (e) { console.log(`加组 ${g} 失败 ${e.code}`); }
}

function mkTx(loop) {
  const s = dgram.createSocket('udp4');
  return new Promise(res => s.bind(0, IF, () => { s.setBroadcast(true); s.setMulticastLoopback(loop); res(s); }));
}
async function burst(tx, list, times, gap) {
  for (let k = 0; k < times; k++) for (const [ip, len] of list) {
    await new Promise(r => tx.send(Buffer.alloc(len, 0x71), 0, len, PORT, ip, r));
    await sleep(gap);
  }
}
const count = len => got.filter(g => g.len === len).length;

// A 相：回环开，只发 1 个组一个长度 —— 证明"加组+收包"这套本身是好的
let tx = await mkTx(true);
await burst(tx, [[G_DATA, 137]], 4, 150);
await sleep(700);
const A = count(137);
console.log(`\n[A相 loopback=开] 137B 收到 ${A} 个 → ${A ? '通路正常' : '通路异常，下面结论作废（先修仪器）'}`);

// B 相：回环关，三个目的各占一个长度
got.length = 0;
tx.close();
tx = await mkTx(false);
await burst(tx, [[G_DATA, 251], [G_BEACON, 252], [BCAST, 253]], 8, 150);
await sleep(2500);
const D = count(251), B = count(252), C = count(253);
console.log(`[B相 loopback=关] 组播${G_DATA}:${D}  组播${G_BEACON}:${B}  广播:${C}`);
console.log('  (注意：Windows 对自己发出的广播仍可能本地回环，广播那列只作弱旁证；组播两列才是硬判据)');
if (D > 0 || B > 0) console.log('\n结论：未知组播被 AP 发上了空口 → 校验通道通，问题不在路由器。');
else if (C > 0) console.log('\n结论：广播能收到、未知组播一个都没回来 → 路由器/网卡把未知组播吃了，音响看不到 239.254.x.x 校验包。');
else console.log('\n结论：什么都没回来 → 自证法在这台机器上不成立，改用路由器客户端页/日志作判据。');
rx.close(); tx.close();
process.exit(0);
