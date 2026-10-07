// 只读探测：音响的 AirPlay 发现（mDNS/DNS-SD）到底稳不稳。
// 1) 静听 5353 一段秒，看它有没有在广播 _raop._tcp
// 2) 主动发 N 次 PTR 查询，数回包 —— 丢多少就是"投放键刷不出来"的直接读数
// 用法: node mdns_probe.mjs [秒数=12] [查询次数=10] [unicast] [音响IP]
//   unicast = 从本机随机端口直接单播问音响的 5353，绕开 Windows 自己占着 5353 的问题
import dgram from 'node:dgram';

const SECS = parseInt(process.argv[2] || '12', 10);
const QUERIES = parseInt(process.argv[3] || '10', 10);
const UNICAST = process.argv[4] === 'unicast';
const DEV = process.argv[5] || '192.168.1.10';
const QNAME = process.argv[6] || '_raop._tcp.local';

function query(name) {
  const labels = name.split('.');
  const qname = Buffer.concat(labels.map(l => Buffer.concat([Buffer.from([l.length]), Buffer.from(l, 'ascii')])));
  const head = Buffer.alloc(12);                    // ID=0 flags=0 QDCOUNT=1 其余 0
  const q = Buffer.concat([head, qname, Buffer.from([0, 0, 0x0C, 0, 0, 0x01])]);   // PTR / IN
  q.writeUInt16BE(1, 4);
  return q;
}

const s = dgram.createSocket({ type: 'udp4', reuseAddr: true });
let heard = 0, raop = 0, answered = 0, t0 = Date.now();
const names = new Map();
s.on('message', (m, r) => {
  heard++;
  const txt = m.toString('latin1');
  const isRaop = txt.includes(QNAME.split('._')[0]);
  if (isRaop) {
    raop++;
    const srv = (txt.match(/\x08[a-zA-Z0-9-]{2,32}\x04local/) || [])[0];
    const key = `${r.address} ${srv ? srv.replace(/\x08/g, '.').replace(/\x04local/, '') : '?'}`;
    names.set(key, (names.get(key) || 0) + 1);
    if (UNICAST) {
      const printable = txt.replace(/[^\x20-\x7e]/g, '·').slice(12, 220);
      console.log(`  应答 ${r.address}:${r.port} → ${printable}`);
    }
  }
  if (!UNICAST && r.port === 5353 && m.length > 12 && (m[2] & 0x80)) answered++;
  if (UNICAST && isRaop) answered++;
});

function pump(port) {
  console.log(UNICAST ? `单播问 ${DEV}:${port} 共 ${QUERIES} 次（每次 2 包，间隔 1s）`
                      : `静听 + 主动查 ${QUERIES} 次（间隔 1s），共 ${SECS}s`);
  let sent = 0;
  const iv = setInterval(() => {
    if (sent++ >= QUERIES) { clearInterval(iv); return; }
    const q = query(QNAME);
    if (UNICAST) s.send(q, port, DEV);
    else { s.send(q, 5353, '224.0.0.251'); s.send(q, 5353, '224.0.0.251'); }
  }, 1000);
  setTimeout(() => {
    console.log(`发出 ${sent} 次查询；收到总包=${heard} 含_raop的包=${raop} 有效应答=${answered}`);
    for (const [k, n] of names) console.log(`  ${n} 次  ${k}`);
    console.log(`应答率 = ${answered}/${sent} = ${(answered / Math.max(1, sent) * 100).toFixed(0)}%`);
    process.exit(0);
  }, SECS * 1000);
}

if (UNICAST) s.bind(0, '0.0.0.0', () => pump(5353));
else s.bind(5353, '0.0.0.0', () => {
  try { s.addMembership('224.0.0.251'); } catch (e) { console.log('加组失败:', e.code); }
  try { s.addMembership('224.0.0.252'); } catch { }
  pump(5353);
});
