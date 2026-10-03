// 纯 Windows 复刻 libcooee.so 的 send_cooee（Broadcom Cooee/Airkiss 系）—— 逐条指令对照版。
//
// 反汇编依据（正式版 x/lib/armeabi/libcooee.so，vaddr==file offset）：
//   send_cooee@0x27274  send_packet@0x27204  dump@0x2717c  aes_enc@0x27558  set_packet_interval@0x271d8
//   循环体 0x2742e..0x274d0（`blt #0x2742e` 就是回跳目标）
//
// 帧的装配（关键，之前理解错过）：
//   0x272ab  r5 = sp+0x7c                     ← TLV 缓冲
//   0x27337  r5 = sp+0x40；[0]=0x20 [1]=TLV长+18 [2..9]=nonce 前 8 字节(=8 个 0)   ← 10 字节头
//   0x273c1  aes_enc(key16, nonce13, aad=sp+0x40 长度 **10**, 明文=TLV, out=malloc)
//   0x273d7  memcpy(sp+0x86, out, TLV长+8)    ← 密文+tag 紧跟在头后面
//   0x273e1  把头 10 字节拷到 sp+0x7c         ← **覆盖 TLV 的前 10 字节**
//   ⇒ 最终帧就在 sp+0x7c：[0x20][帧长][00×8][密文][tag8]，总长 = TLV长+18
//   而发送循环 0x2745b/0x274ab 读的字节基址也是 sp+0x7c ⇒ **②④ 用的是帧字节，不是明文字节**
//
// 每轮（i = 0 .. 帧长-1）按序发 3 个包，端口一律 1503，然后 usleep(interval_ms*1000)：
//   ② 239.254.frame[G].frame[G+1]（G+1 越界时取 0）  长度 = G
//   ③ 255.255.255.255                                 长度 = i + 20
//   ④ 255.255.255.255                                 长度 = frame[i] + 180
//   G 是全局计数的（0xa2fc8，.data 初值 9），每轮 +2，>=帧长就归零
//   ① 0 长信标包（239.246.0.0）：条件 `beacon_int / interval == 0` 才发；
//      而 set_packet_interval(n) 写 interval=n、beacon_int=n<<2 ⇒ 商恒为 4 ⇒ **手机永远不发信标**。
//
// 用法: node airkiss_send.mjs --ssid MyWiFi-A --pass <密码> --local 192.168.1.20 [--pace 8] [--beacon off|every] [--g0 9]
import dgram from 'node:dgram';
import crypto from 'node:crypto';

const PORT = 1503;
const BEACON_IP = '239.246.0.0';
const BCAST = '255.255.255.255';
const HEAD_OFF = 20;      // adds r2, #0x14
const DATA_OFF = 180;     // adds r2, #0xb4
const HDR_LEN = 18;       // 帧长 = TLV长 + 18（= 10 字节头 + 8 字节 tag）

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 && process.argv[i + 1] !== undefined && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : d; };
const ssid = arg('ssid', ''), pass = arg('pass', '');
const pace = parseInt(arg('pace', '8'), 10);        // Cooee.SetPacketInterval(8)
const localAddr = arg('local', '');
const senderIp = arg('ip', localAddr);               // TLV 02|04 = 发送端 IPv4（WifiInfo.getIpAddress）
const beaconMode = arg('beacon', 'off');             // 手机恒为不发；every 只用来做对照
const maxPasses = parseInt(arg('passes', '0'), 10);
const gap = parseInt(arg('gap', '1000'), 10);        // App 侧 SendThread: send() 完一遍就 sleep(1000)
const joinGroups = arg('join', '1') !== '0';         // 加入自己要发的组，触发 IGMP Report，换 AP 把组播泛洪到空口
const g0 = parseInt(arg('g0', '0'), 10);              // 轮计数初值：lib 的 .data@0xa2fc8 是 9，但 0 是实测已解成功的配置
if (!ssid || !pass) {
  console.log('用法: node airkiss_send.mjs --ssid <SSID> --pass <密码> [--local 192.168.1.20] [--pace 8] [--beacon off|every] [--passes 0]');
  process.exit(1);
}

// lib 的常量（.rodata 直接读出，与固件 0x3d6a0/0x3d6b0 逐字节相同）
const KEY = Buffer.from('abcdabcdabcdabcd', 'ascii');
const NONCE = Buffer.concat([Buffer.alloc(8, 0), Buffer.from('wiced', 'ascii')]);   // 13B：memset 13 再在 +8 处放 "wiced"

// 明文 TLV：00|ssidLen|SSID|03|pwdLen|PWD|[02|04|发送端IPv4]
function buildTlv() {
  const s = Buffer.from(ssid, 'utf8').subarray(0, 32);          // cmp r6,#0x20; ble → 截到 32
  const p = Buffer.from(pass, 'utf8').subarray(0, 64);          // cmp r4,#0x40; ble → 截到 64
  const parts = [Buffer.from([0x00, s.length]), s, Buffer.from([0x03, p.length]), p];
  if (senderIp) {
    const q = senderIp.split('.').map(x => parseInt(x, 10) & 0xff);
    if (q.length !== 4 || q.some(Number.isNaN)) throw new Error('--ip 不是合法 IPv4: ' + senderIp);
    parts.push(Buffer.from([0x02, 0x04]), Buffer.from(q));      // lsrs/strb 序列 = 点分顺序
  }
  return Buffer.concat(parts);
}

// 帧 = [0x20][帧长][nonce 前 8 字节] + AES-128-CCM(TLV, AAD=这10字节头) + tag(8)
function buildFrame(t) {
  const h = Buffer.alloc(10, 0);
  h[0] = 0x20;
  h[1] = t.length + HDR_LEN;                    // = 帧长 = 轮数
  NONCE.copy(h, 2, 0, 8);
  const e = crypto.createCipheriv('aes-128-ccm', KEY, NONCE, { authTagLength: 8 });
  e.setAAD(h, { plaintextLength: t.length });   // str r3,[sp,#4] 里 r3=0xa ⇒ AAD 长 10
  return Buffer.concat([h, Buffer.concat([e.update(t), e.final()]), e.getAuthTag()]);
}

const tlv = buildTlv();
const frame = buildFrame(tlv);
const fp = frame.length;
if (fp !== tlv.length + HDR_LEN) throw new Error('帧长不自洽');
// 载荷内容设备看不见（它只量长度），给足最长包即可
const payload = Buffer.alloc(DATA_OFF + 256, 0x41);

const sock = dgram.createSocket({ type: 'udp4', reuseAddr: true });
const listener = dgram.createSocket({ type: 'udp4', reuseAddr: true });   // 只用来入组
listener.on('error', () => { }); listener.on('message', () => { });
sock.on('message', (m, r) => console.log(`\n*** 回包 ${r.address}:${r.port} (${m.length}B): ${m.subarray(0, 48).toString('hex')}`));

// 单实例互斥：并发两条流会把轮序交错成噪声（2026-10-02 因此废掉一整轮实验）
const MUTEX_PORT = 15103;
const mutex = dgram.createSocket('udp4');
await new Promise(res => {
  mutex.once('error', e => {
    console.error(`\n!! 已有另一个 airkiss_send 在跑（${e.code}）。先清场：\n   powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \\"Name='node.exe'\\" | ?{ $_.CommandLine -match 'airkiss_send' } | %{ Stop-Process -Id $_.ProcessId -Force }"\n`);
    process.exit(9);
  });
  mutex.bind(MUTEX_PORT, '0.0.0.0', res);
});

const sleep = ms => new Promise(r => setTimeout(r, ms));
// lib 是原生 usleep(8000)。await 每个 send 回调会被 Windows 的 ~15ms 定时器粒度拖慢一倍以上，
// 所以一轮 3 包连发（不 await），再用自旋对齐到绝对期限。
let sendErrs = 0, sent = 0, sockErr = '';
sock.on('error', e => { sockErr = e.message; });
function sendPkt(ip, len) {
  try { sock.send(payload, 0, len, PORT, ip, e => { if (e) sendErrs++; else sent++; }); }
  catch { sendErrs++; }
}
function spinUntil(deadline) {
  let guard = 0;
  while (Date.now() < deadline && guard++ < 4000000) { /* 忙等，一周期最多 8ms */ }
}
const groupOf = G => `239.254.${frame[G]}.${G + 1 < fp ? frame[G + 1] : 0}`;
const sendBeacon = beaconMode === 'every';

async function pump() {
  if (joinGroups) {
    const groups = new Set([BEACON_IP]);
    for (let g = 0; g < fp; g += 2) groups.add(groupOf(g));
    for (const g of groups) {
      try { listener.addMembership(g, localAddr || undefined); } catch (e) { console.log(`加组 ${g} 失败: ${e.message}`); }
    }
    console.log(`已加入 ${groups.size} 个组（含 ${BEACON_IP}），让 AP 把组播泛洪到空口`);
  }
  let G = g0, passes = 0, pkts = 0, worst = 0, over = 0;
  const t0 = Date.now();
  while (maxPasses === 0 || passes < maxPasses) {
    let next = Date.now();
    for (let i = 0; i < fp; i++) {
      next += pace;
      if (sendBeacon) { sendPkt(BEACON_IP, 0); pkts++; }
      sendPkt(groupOf(G), G); pkts++;
      G += 2; if (G >= fp) G = 0;
      sendPkt(BCAST, i + HEAD_OFF); pkts++;
      sendPkt(BCAST, frame[i] + DATA_OFF); pkts++;
      if (i % 8 === 0) process.stdout.write(`\r[pass ${passes} round ${i + 1}/${fp}] G=${G} pkts=${pkts} err=${sendErrs} ${sockErr} ${((Date.now() - t0) / 1000).toFixed(1)}s 最慢轮=${worst}ms   `);
      const lag = Date.now() - next;
      if (lag > 0) { over++; if (lag > worst) worst = lag; }
      if (pace) spinUntil(next);
    }
    passes++;
    if (gap) await sleep(gap);
  }
  console.log('\n发包结束。');
  return { passes, pkts, worst, over };
}

// 只打印计划，不上空口：跟汇编逐轮对表
function printPlan(n = 12) {
  console.log('轮i | ②组播目的(长度=G)      | ③广播(长=i+20) | ④广播(长=frame[i]+180)');
  let G = g0;
  for (let i = 0; i < Math.min(n, fp); i++) {
    console.log(` ${String(i).padStart(2)} | ${groupOf(G).padEnd(21)} (${String(G).padStart(3)}) | ${String(i + HEAD_OFF).padStart(12)}     | ${String(frame[i] + DATA_OFF).padStart(4)}  frame[${i}]=0x${frame[i].toString(16).padStart(2, '0')}`);
    G += 2; if (G >= fp) G = 0;
  }
  console.log(`一遍 = ${fp} 轮 × ${sendBeacon ? 4 : 3} 包 = ${fp * (sendBeacon ? 4 : 3)} 包，理论耗时 ${fp * pace}ms，G 起点=${g0}`);
}

// 自检：用同一套参数解密自己，确认帧构造与 AAD 长度一致
function selfCheck() {
  const d = crypto.createDecipheriv('aes-128-ccm', KEY, NONCE, { authTagLength: 8 });
  d.setAAD(frame.subarray(0, 10), { plaintextLength: tlv.length });
  d.setAuthTag(frame.subarray(fp - 8));
  const back = Buffer.concat([d.update(frame.subarray(10, fp - 8)), d.final()]);
  return back.equals(tlv);
}

(async () => {
  if (process.argv.includes('--plan')) { printPlan(parseInt(arg('plan', '12'), 10) || 12); process.exit(0); }
  const local = localAddr || undefined;
  await new Promise(r => sock.bind(0, local, r));
  sock.setBroadcast(true);
  if (localAddr) { try { sock.setMulticastInterface(localAddr); } catch (e) { console.log('setMulticastInterface 跳过: ' + e.code); } }
  console.log(`SSID="${ssid}"(${tlv[1]}B) 密码=${Buffer.byteLength(pass)}B 发送端IP=${senderIp || '(不带 02 段)'}`);
  console.log(`TLV=${tlv.length}B → 帧=${fp}B=轮数  每轮${sendBeacon ? 4 : 3}包  端口${PORT}  节奏${pace}ms  G0=${g0}  信标=${beaconMode}`);
  console.log(`TLV(hex) = ${tlv.toString('hex')}`);
  console.log(`帧(hex)  = ${frame.toString('hex')}`);
  console.log(`自检(解密自己): ${selfCheck() ? '一致 ✅' : '不一致 ❌'}`);
  console.log(`②④ 取的字节序列 = ${[...frame.subarray(0, 8)].map(x => '0x' + x.toString(16)).join(' ')} …`);
  await new Promise(r => listener.bind(PORT, '0.0.0.0', r));
  const { passes, pkts, worst, over, ms } = await pump();
  console.log(`共 ${passes} 遍 / ${pkts} 包 / 实际送达 ${sent} / 错误 ${sendErrs} ${sockErr}`);
  console.log(`实测：${passes} 遍用了 ${(ms / 1000).toFixed(1)}s → 每遍 ${(ms / Math.max(passes, 1)).toFixed(0)}ms（期望 ${fp * pace}+${gap}=${fp * pace + gap}ms）；超期轮数=${over} 最大超期=${worst}ms（期望 0）`);
  sock.close(); listener.close(); mutex.close();
  process.exit(0);
})();
