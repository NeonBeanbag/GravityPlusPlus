// 模拟接收端：只从「包长度」把帧还原出来，再按 lib 的参数解密校验。
// 这一步证明：长度通道里确实携带了自洽、可解密的帧（若失败一定是编码错，不是空口问题）。
// 用法: node sim_rx.mjs --ssid MyWiFi-A --pass <pwd> --ip 192.168.1.20 [--channel sneeze|group|both]
import crypto from 'node:crypto';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : d; };
const ssid = arg('ssid', ''), pass = arg('pass', ''), ip = arg('ip', '192.168.1.20');
const KEY = Buffer.from('abcdabcdabcdabcd', 'ascii');
const NONCE = Buffer.concat([Buffer.alloc(8, 0), Buffer.from('wiced', 'ascii')]);
const HEAD_OFF = 20, DATA_OFF = 180, PORT = 1503;

// 发送端实际会发出的三种包（严格照 send_cooee 的汇编）
function plan() {
  const s = Buffer.from(ssid, 'utf8'), p = Buffer.from(pass, 'utf8');
  const q = ip.split('.').map(x => parseInt(x, 10) & 0xff);
  const tlv = Buffer.concat([Buffer.from([0, s.length]), s, Buffer.from([3, p.length]), p, Buffer.from([2, 4]), Buffer.from(q)]);
  const fp = tlv.length + 18;
  const h = Buffer.alloc(10);
  h[0] = 0x20; h[1] = fp; NONCE.copy(h, 2, 0, 8);
  const e = crypto.createCipheriv('aes-128-ccm', KEY, NONCE, { authTagLength: 8 });
  e.setAAD(h, { plaintextLength: tlv.length });
  const frame = Buffer.concat([h, Buffer.concat([e.update(tlv), e.final()]), e.getAuthTag()]);
  if (frame.length !== fp) throw new Error(`帧长 ${frame.length} != 期望 ${fp}`);
  const rounds = [];
  let G = 0;
  for (let i = 0; i < fp; i++) {
    rounds.push({ i, group: `239.254.${frame[G]}.${G + 1 < fp ? frame[G + 1] : 0}`, lenG: G, ramp: i + HEAD_OFF, sneeze: frame[i] + DATA_OFF });
    G += 2; if (G >= fp) G = 0;
  }
  return { frame, tlv, fp, rounds };
}

// 接收端：从 sneeze 长度还原帧
function decodeSneeze(rounds, fp) {
  const f = Buffer.alloc(fp);
  for (const r of rounds) f[r.i] = r.sneeze - DATA_OFF;
  return f;
}
// 接收端：从组播目的地址的最后两字节还原（每轮给 frame[G],frame[G+1]，G 步进 2）
function decodeGroup(rounds, fp) {
  const f = Buffer.alloc(fp);
  rounds.forEach((r, k) => {
    const parts = r.group.split('.');
    f[(k * 2) % fp] = parseInt(parts[2], 10);
    if ((k * 2 + 1) < fp) f[(k * 2 + 1) % fp] = parseInt(parts[3], 10);
  });
  return f;
}
// 接收端：用 ramp 通道做自洽检查（第 i 轮长度必须 = i+20）
function checkRamp(rounds) { return rounds.every((r, k) => r.ramp === k + HEAD_OFF); }

function decrypt(f) {
  const fp = f.length, tlvLen = fp - 18;
  if (f[0] !== 0x20) return { ok: false, why: `帧首字节 0x${f[0].toString(16)} != 0x20` };
  if (f[1] !== fp) return { ok: false, why: `长度字节 ${f[1]} != 帧长 ${fp}` };
  const d = crypto.createDecipheriv('aes-128-ccm', KEY, NONCE, { authTagLength: 8 });
  d.setAAD(f.subarray(0, 10), { plaintextLength: tlvLen });
  d.setAuthTag(f.subarray(fp - 8));
  try {
    const pt = Buffer.concat([d.update(f.subarray(10, fp - 8)), d.final()]);
    const t = pt.toString('latin1');
    return { ok: true, pt, tagOK: true, tail: t };
  } catch (e) { return { ok: false, why: 'CCM tag 校验失败: ' + e.message }; }
}

const { frame, tlv, fp, rounds } = plan();
console.log(`TLV(${tlv.length}B)=${tlv.toString('hex')}`);
console.log(`帧(${fp}B)=${frame.toString('hex')}`);
console.log(`轮数=${rounds.length} 包数=${rounds.length * 3}  ramp自洽=${checkRamp(rounds) ? '✅' : '❌'}`);
for (const [name, f] of [['sneeze(长度=frame[i]+180)', decodeSneeze(rounds, fp)], ['group(目的IP后两字节)', decodeGroup(rounds, fp)]]) {
  const same = f.equals(frame);
  const r = decrypt(f);
  console.log(`\n[${name}] 还原字节与真实帧${same ? '完全一致 ✅' : '不一致 ❌ -> ' + f.toString('hex')}`);
  console.log(`           解密：${r.ok ? 'tag 通过 ✅ 明文=' + r.pt.toString('hex') : '失败 ❌ ' + r.why}`);
  if (!same && r.ok) console.log('           （字节序/索引映射仍与发送端不同，注意）');
}
