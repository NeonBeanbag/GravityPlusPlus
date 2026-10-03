// Gravity 纯 Windows 配网 —— 一条命令版
//   node gravity_provision.mjs --ssid MyWiFi-A --pass '你的WiFi密码'
// 流程：体检(WLAN IP / 数据包合并开关) → 等你按键进配网模式 → 单流开打 → 盯"掉线后自己回线" → 回读解码值。
// 报文按 libcooee.so 的 send_cooee 逐条复刻：见 airkiss_send.mjs 顶部注释。
import { spawn, execSync } from 'node:child_process';
import net from 'node:net';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : d; };
const SSID = arg('ssid', ''), PASS = arg('pass', '');
const TIMEOUT = parseFloat(arg('timeout', '200'));
const SPEAKER_MAC = arg('mac', '94-A1-A2');     // 魅族 OUI，用邻居表按 MAC 认它，不靠固定 IP
if (!SSID || !PASS) {
  console.log('用法: node gravity_provision.mjs --ssid <WiFi名> --pass <WiFi密码> [--timeout 200]');
  process.exit(1);
}
const ps = cmd => { try { return execSync(`powershell -NoProfile -Command "${cmd.replace(/"/g, '\\"')}"`, { encoding: 'utf8' }).trim(); } catch { return ''; } };

// --- 1) 体检：本机 WLAN IPv4（要同时当 TLV 里的"发送端IP"和 socket 绑定地址）---
function localIP() {
  const o = ps(`(Get-NetIPAddress -InterfaceAlias WLAN -AddressFamily IPv4 -ErrorAction SilentlyContinue).IPAddress`);
  return o.split(/\s+/).find(x => /^\d+\.\d+\.\d+\.\d+$/.test(x)) || '';
}
// --- 「数据包合并」必须是关闭状态，否则一轮 3 个小包会被合成一个空口帧，长度通道全废 ---
function coalesceState() {
  const v = ps(`(Get-NetAdapterAdvancedProperty -Name WLAN -ErrorAction SilentlyContinue | Where-Object RegistryKeyword -eq '*PacketCoalescing').DisplayValue`);
  return v || '(读不到)';
}
// --- 按 MAC 找音响当前 IP（它重连后可能换 DHCP 地址）---
function speakerIP() {
  const o = ps(`Get-NetNeighbor -IPAddress 192.168.* -ErrorAction SilentlyContinue | Where-Object { $_.LinkLayerAddress -like '${SPEAKER_MAC}*' -and $_.State -in 'Reachable','Stale' } | Select-Object -ExpandProperty IPAddress`);
  return o.split(/\s+/).find(x => /^\d+\.\d+\.\d+\.\d+$/.test(x)) || '';
}
function tcp(host, port, payload, to = 1800) {
  return new Promise(res => {
    if (!host) return res('');
    const s = net.connect({ host, port, timeout: to });
    let rx = '', done = false;
    const fin = () => { if (done) return; done = true; try { s.destroy(); } catch { } res(rx); };
    s.on('connect', () => { if (payload) s.write(Buffer.from(payload, 'ascii')); else fin(); });
    s.on('data', d => { rx += d.toString('latin1'); if (rx.length >= 6) fin(); });
    s.on('timeout', fin); s.on('error', fin); s.on('close', fin);
    setTimeout(fin, to + 250);
  });
}
const httpGet = (path) => new Promise(res => {
  const ip = speakerIP(); if (!ip) return res('');
  const s = net.connect({ host: ip, port: 7766, timeout: 2500 });
  let buf = '';
  s.on('connect', () => s.write(`GET ${path} HTTP/1.1\r\nHost: ${ip}\r\n\r\n`));
  s.on('data', d => { buf += d.toString(); if (buf.includes('\r\n\r\n') && buf.length > 80) { s.destroy(); res(buf.split('\r\n\r\n')[1]); } });
  s.on('timeout', () => { s.destroy(); res(''); }); s.on('error', () => res(''));
  setTimeout(() => { s.destroy(); res(''); }, 3200);
});

(async () => {
  const LOCAL = localIP();
  const coal = coalesceState();
  console.log('== 环境体检 ==');
  console.log(`  本机 WLAN IPv4 : ${LOCAL || '❌ 取不到（用 --ssid/--pass 前先确认连着 WiFi）'}`);
  console.log(`  网卡「数据包合并」: ${coal}   ${/禁用|Disabled/i.test(coal) ? '✅' : '❌ 必须关闭，否则长度通道会被合成一个帧：  powershell Set-NetAdapterAdvancedProperty -Name WLAN -RegistryKeyword *PacketCoalescing -RegistryValue 0'}`);
  const okCoal = /禁用|Disabled/i.test(coal);
  if (process.argv.includes('--check')) {
    const ip = speakerIP();
    console.log(`  音响(按 MAC ${SPEAKER_MAC}*) : ${ip || '不在网上'}`);
    const d = ip ? await tcp(ip, 8888, 'BSS0000E') : '';
    console.log(`  守护进程 BSS0000E : ${d ? JSON.stringify(d) : '无回复'}`);
    console.log(`  结论: ${LOCAL && okCoal ? '✅ 可以开打' : '❌ 先解决上面打叉的两项'}`);
    process.exit(LOCAL && okCoal ? 0 : 2);
  }
  if (!LOCAL || !okCoal) process.exit(2);
  const before = speakerIP();
  console.log(`  音响当前 IP    : ${before || '(不在网上 —— 正常，配网模式或刚断电)'}`);

  console.log('\n== 请让音响进入配网模式 ==');
  console.log('   同时按背面 `+` `-` 两秒，屏幕出现配网页面后，回到这里按 [回车] 开打');
  await new Promise(res => {
    process.stdin.resume(); process.stdin.once('data', () => { process.stdin.pause(); res(); });
  });

  const child = spawn(process.execPath, ['airkiss_send.mjs', '--ssid', SSID, '--pass', PASS, '--local', LOCAL, '--passes', '0'],
    { stdio: ['ignore', 'pipe', 'inherit'] });
  let stat = '';
  child.stdout.on('data', d => { const a = d.toString().split(/[\r\n]/); stat = a[a.length - 1] || stat; });
  child.on('exit', c => console.log(`\n(发包进程退出 code=${c})`));

  let ip = '';
  console.log(`\n== 开打，盯"自己回线"（上限 ${TIMEOUT}s）==`);
  const t0 = Date.now();
  let n = 0, sawOffline = !before;
  while (Date.now() - t0 < TIMEOUT * 1000) {
    await new Promise(r => setTimeout(r, 3000)); n++;
    ip = speakerIP();
    const online = ip ? await pingOk(ip) : false;
    if (!online) sawOffline = true;
    process.stdout.write(`\r[#${n}] +${((Date.now() - t0) / 1000).toFixed(0)}s 音响IP=${ip || '无'} ${online ? '在线' : '不在线'}${sawOffline ? '' : ' (还没掉过线，判据未成立)'}  ${stat.slice(0, 40)}      `);
    if (online && sawOffline) break;
  }
  try { child.kill('SIGKILL'); } catch { }
  await new Promise(r => setTimeout(r, 600));
  console.log('');
  const ipFinal = speakerIP();
  const ssidVal = await tcp(ipFinal, 8888, 'BSS0000E');
  const pwdVal = await tcp(ipFinal, 8888, 'BPA0000E');
  const info = await httpGet('/Info');
  console.log('\n== 结果 ==');
  console.log(`  音响 IP        : ${ipFinal || '（没回来 —— 检查 SSID/密码是否填错、2.4G 还是 5G）'}`);
  console.log(`  守护进程回读    : BSS0000E=${ssidVal ? JSON.stringify(ssidVal) : '无回复(可能已被 App 抢走)'}  BPA0000E=${pwdVal ? JSON.stringify(pwdVal) : '无回复'}`);
  const m = /"deviceName":"([^"]+)".*?"ip":"([^"]+)"/.exec(info) || /"ip":"([^"]+)"/.exec(info);
  console.log(`  7766 /Info      : ${info ? (m ? `应答正常 ✅ ${info.slice(0, 90)}…` : '应答正常 ✅') : '无应答（多半还在配网页或 IP 变了）'}`);
  console.log(`\n  判据提醒：只有"先掉线、再在我们打的窗口里自己回线"才算成功；配网模式里它 16 分钟都不会自己回来。`);
  process.exit(0);
})();

async function pingOk(ip) {
  try { execSync(`ping -n 1 -w 400 ${ip}`, { stdio: 'pipe' }); return true; } catch { return false; }
}
