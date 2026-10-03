// Cooee(bcomlogic) 分支扫射器：按 send_cooee 的输出形态构造，端口 1503。
// 已知：TLV 正文 = 00|L|SSID|03|P|PASS|02|04|u32LE；18 字节头 [0]=0x20 [1]=总长；
//       目的 = 255.255.255.255 / 239.246.0.0 / 239.254.digest[i].digest[i+1]；包长有 +20 / +byte+180 两种变体。
// 用法: node --openssl-legacy-provider cooee_send.mjs --ssid MyWiFi-2.4G --pass <pwd> [--bcast 192.168.1.20] [--rounds 40] [--interval 300]
import dgram from 'node:dgram';
import crypto from 'node:crypto';

const PORT = 1503;
function arg(n, d) { const i = process.argv.indexOf('--' + n); return i > 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : d; }

const ssid = arg('ssid'), pass = arg('pass'), bcast = arg('bcast', '192.168.1.20');
const rounds = parseInt(arg('rounds', '40'), 10), interval = parseInt(arg('interval', '300'), 10);
if (!ssid || !pass) { console.log('用法: node cooee_send.mjs --ssid <SSID> --pass <密码> [--bcast 192.168.1.20]'); process.exit(1); }

function tlv(intervalMs) {
  const s = Buffer.from(ssid, 'utf8').subarray(0, 0x20);
  const p = Buffer.from(pass, 'utf8').subarray(0, 0x40);
  const u = Buffer.alloc(4); u.writeUInt32LE(intervalMs >>> 0);
  return Buffer.concat([Buffer.from([0x00, s.length]), s, Buffer.from([0x03, p.length]), p, Buffer.from([0x02, 0x04]), u]);
}
// 18 字节头：[0]=0x20，[1]=总长(body+18)，其余按 send_cooee 里 strncpy("nonce"/"header") 的痕迹填
function frame(body) {
  const h = Buffer.alloc(18, 0);
  h[0] = 0x20; h[1] = (body.length + 18) & 0xff;
  Buffer.from('nonce', 'utf8').copy(h, 2);
  Buffer.from('header', 'utf8').copy(h, 10);
  return Buffer.concat([h, body]);
}

const digest = crypto.createHash('md5').update(ssid, 'utf8').digest();
const groups = [];
for (let i = 0; i + 1 < digest.length; i += 2) groups.push(`239.254.${digest[i]}.${digest[i + 1]}`);
console.log(`SSID="${ssid}" MD5=${digest.toString('hex')}`);
console.log(`组播组(逐对): ${groups.join('  ')}`);
console.log(`另加: 239.246.0.0  ${bcast}   端口 ${PORT}`);

const body = tlv(interval);
const fr = frame(body);
const sock = dgram.createSocket({ type: 'udp4', reuseAddr: true });
sock.on('message', (m, r) => console.log(`\n*** 回包 ${r.address}:${r.port} (${m.length}B): ${m.subarray(0, 40).toString('hex')}`));

function padTo(buf, n) {                       // 用长度承载信息：把包撑到指定字节数
  if (buf.length >= n) return buf.subarray(0, n);
  return Buffer.concat([buf, Buffer.alloc(n - buf.length, 0x5a)]);
}

sock.bind(0, () => {
  sock.setBroadcast(true);
  const dests = [bcast, '239.246.0.0', ...groups];
  let k = 0;
  const t = setInterval(() => {
    if (k++ >= rounds) { clearInterval(t); console.log('\n扫射完毕。'); setTimeout(() => process.exit(0), 4000); return; }
    const gi = ((k - 1) * 2) % digest.length;
    const gpair = `239.254.${digest[gi]}.${digest[(gi + 1) % digest.length]}`;
    const targets = [bcast, '239.246.0.0', gpair];
    for (const d of targets) {
      for (const pkt of [fr, padTo(fr, fr.length + 20), padTo(fr, 180 + fr.length + (digest[(gi + k) % digest.length] & 0x1f)), body]) {
        sock.send(pkt, 0, pkt.length, PORT, d, () => {});
      }
    }
    process.stdout.write(`\r[round ${k}/${rounds}] group=${gpair} ${targets.length}目的 x 4长度变体   `);
  }, interval);
});
