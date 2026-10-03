// MEIZU Gravity 配网包构造器 —— 复刻 com.yunos.ckcaptivewifi.softaplogic.UdpBroadcastPacket
// 协议来源：gravity_a8.apk 反编译（DES/ECB/NoPadding + Android Base64.DEFAULT + UDP 40012 广播）
// 用法: node --openssl-legacy-provider provision.mjs --ssid MyWiFi-2.4G --pass <pwd> [--bcast 192.168.1.20] [--port 40012]
//                                     [--n 30] [--interval 1000] [--dry] [--no-wrap]
// Node 17+ 的 OpenSSL 3 默认禁用单 DES，必须带 --openssl-legacy-provider。
import dgram from 'node:dgram';
import crypto from 'node:crypto';

try {
  crypto.createCipheriv('des-ecb', Buffer.from('yun!@idc'), null);
} catch {
  console.error('需要以 legacy provider 运行：node --openssl-legacy-provider provision.mjs ...');
  process.exit(1);
}

const DES_KEY = 'yun!@idc';
const PORT = 40012;

function arg(name, dflt) {
  const i = process.argv.indexOf('--' + name);
  return i > 0 && process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : dflt;
}
const has = (name) => process.argv.includes('--' + name);

// Android Base64.encode(bytes, Base64.DEFAULT): 每 76 字符一个 \n，结尾也有 \n
function androidB64(buf) {
  const s = buf.toString('base64');
  const out = [];
  for (let i = 0; i < s.length; i += 76) out.push(s.slice(i, i + 76));
  return out.join('\n') + '\n';
}

export function buildPacket(ssid, password, wrap = true) {
  // JSONObject.put 顺序：tag, ssid, password, extra, version
  const json = '{"tag":"captivenetwork","ssid":' + JSON.stringify(ssid)
    + ',"password":' + JSON.stringify(password)
    + ',"extra":{},"version":"1.0"}';
  const raw = Buffer.from(json, 'utf8');
  const pad = (8 - (raw.length % 8)) % 8;
  const aligned = Buffer.concat([raw, Buffer.alloc(pad, 0)]);
  const c = crypto.createCipheriv('des-ecb', Buffer.from(DES_KEY, 'utf8'), null);
  c.setAutoPadding(false);
  const ct = Buffer.concat([c.update(aligned), c.final()]);
  return { json, payload: wrap ? androidB64(ct) : ct.toString('base64') };
}

function decryptPacket(b64, wrap = true) {
  const d = crypto.createDecipheriv('des-ecb', Buffer.from(DES_KEY, 'utf8'), null);
  d.setAutoPadding(false);
  const ct = Buffer.from(b64.replace(/\s+/g, ''), 'base64');
  const pt = Buffer.concat([d.update(ct), d.final()]);
  return pt.toString('utf8').replace(/\0+$/, '');
}

// 自校验：加密->解密必须拿回原 JSON
function selftest() {
  const { json, payload } = buildPacket('MyWiFi-2.4G', 'p@ss w0rd 中文');
  const back = decryptPacket(payload);
  const ok = back === json;
  console.log(`[selftest] round-trip ${ok ? 'OK' : 'FAIL'}`);
  console.log(`[selftest] json    = ${json}`);
  console.log(`[selftest] payload = ${JSON.stringify(payload)}`);
  console.log(`[selftest] decoded = ${back}`);
  if (!ok) process.exit(1);
}

if (has('selftest')) { selftest(); process.exit(0); }

const ssid = arg('ssid'), pass = arg('pass');
if (!ssid || !pass) {
  console.log('缺少参数。示例：\n  node provision.mjs --selftest\n  node provision.mjs --ssid MyWiFi-2.4G --pass "你的密码" --bcast 192.168.1.20');
  process.exit(1);
}
const bcast = arg('bcast', null);
const port = parseInt(arg('port', String(PORT)), 10);
const n = parseInt(arg('n', '30'), 10);
const interval = parseInt(arg('interval', '1000'), 10);
const wrap = !has('no-wrap');

if (!bcast) { console.log('必须给 --bcast <子网广播地址>（Windows 拿不到 DHCP broadcast 字段）'); process.exit(1); }

const { json, payload } = buildPacket(ssid, pass, wrap);
console.log('明文 JSON : ' + json);
console.log('载荷长度  : ' + payload.length + ' 字节  (Base64 ' + (wrap ? 'DEFAULT 换行' : '无换行') + ')');
console.log('目的      : ' + bcast + ':' + port + '  x' + n + ' @' + interval + 'ms');
if (has('dry')) process.exit(0);

const sock = dgram.createSocket({ type: 'udp4', reuseAddr: true });
sock.on('message', (m, r) => console.log(`\n*** 收到音响回包 ${r.address}:${r.port}: ${m.toString('utf8').slice(0, 200)}`));
sock.bind(0, () => {
  sock.setBroadcast(true);   // Windows: 必须 bind 之后再设，否则 EBADF
  let i = 0;
  const t = setInterval(() => {
    if (i++ >= n) { clearInterval(t); console.log('\n发送完毕。'); setTimeout(() => process.exit(0), 3000); return; }
    const buf = Buffer.from(payload, 'utf8');
    sock.send(buf, 0, buf.length, port, bcast, (e) => {
      process.stdout.write(e ? `\n  发送失败: ${e.message}` : `  [${i}/${n}] sent ${buf.length}B -> ${bcast}:${port}\n`);
    });
  }, interval);
});
sock.on('message', (m, r) => console.log(`\n*** 收到音响回包 ${r.address}:${r.port}: ${m.toString('utf8').slice(0, 200)}`));
