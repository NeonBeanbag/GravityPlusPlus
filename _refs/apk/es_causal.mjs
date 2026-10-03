// 因果实验：证明"回读的 SSID/密码只能来自我刚发的包"。
// 做法：用一组音响绝不可能存过的假凭据去打，回读到假值 = 铁证；随后立刻用真凭据补一遍把它带回正轨。
// 用法: node es_causal.mjs --real-ssid MyWiFi-A --real-pass <真密码> --local 192.168.1.20 [--dev auto] [--secs 25]
import { spawn, execSync } from 'node:child_process';
import net from 'node:net';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : d; };
const REAL_SSID = arg('real-ssid', 'MyWiFi-A'), REAL_PASS = arg('real-pass', '');
const FAKE_SSID = arg('fake-ssid', 'ZZNOPE'), FAKE_PASS = arg('fake-pass', 'zznopexo');
const LOCAL = arg('local', '192.168.1.20');
const SECS = parseFloat(arg('secs', '25'));
let DEV = arg('dev', '');

function sh(cmd) { try { return execSync(cmd, { encoding: 'utf8' }); } catch { return ''; } }
function findDev() {
  if (DEV) return DEV;
  for (const cand of ['192.168.1.10', '192.168.1.12']) {
    const o = sh(`powershell -NoProfile -Command "Get-NetNeighbor -IPAddress ${cand} -ErrorAction SilentlyContinue | Where-Object { $_.LinkLayerAddress -like '94-A1-A2*' } | Select-Object -ExpandProperty IPAddress"`);
    if (/192\.168/.test(o)) return o.trim().split(/\s+/)[0];
  }
  const o = sh(`powershell -NoProfile -Command "Get-NetNeighbor -IPAddress 192.168.1.* -ErrorAction SilentlyContinue | Where-Object { $_.LinkLayerAddress -like '94-A1-A2*' -and $_.State -ne 'Unreachable' } | Select-Object -ExpandProperty IPAddress"`);
  const ip = o.trim().split(/\s+/).filter(x => /^\d+\.\d+\./.test(x))[0];
  return ip || '';
}
function ask(host, cmd, to = 2000) {
  return new Promise(res => {
    if (!host) return res('');
    const s = net.connect({ host, port: 8888, timeout: to });
    let rx = '', done = false;
    const fin = () => { if (done) return; done = true; try { s.destroy(); } catch { } res(rx); };
    s.on('connect', () => s.write(Buffer.from(cmd, 'ascii')));
    s.on('data', d => { rx += d.toString('latin1'); if (rx.length >= 6) fin(); });
    s.on('timeout', fin); s.on('error', fin); s.on('close', fin);
    setTimeout(fin, to + 300);
  });
}
function fire(ssid, pass, secs) {
  return new Promise(res => {
    const c = spawn(process.execPath, ['airkiss_send.mjs', '--ssid', ssid, '--pass', pass, '--local', LOCAL, '--passes', '0'],
      { stdio: ['ignore', 'ignore', 'inherit'] });
    let n = 0;
    const iv = setInterval(() => { n++; if (n >= secs) { clearInterval(iv); try { c.kill('SIGKILL'); } catch { } setTimeout(res, 400); } }, 1000);
  });
}
const strip = v => v.replace(/^0+/, '');

(async () => {
  DEV = findDev();
  console.log(`[目标] 音响 IP = ${DEV || '(找不到，先让它上线/进配网模式)'}`);
  if (!DEV) process.exit(1);
  const c0 = parseInt(sh(`powershell -NoProfile -Command "(Get-CimInstance Win32_Process -Filter \\"Name='node.exe'\\" | Where-Object { $_.CommandLine -match 'airkiss_send' } | Measure-Object).Count"`).trim() || '0', 10);
  if (c0 > 0) { console.error(`!! 有 ${c0} 条发包流在跑，先清场`); process.exit(1); }

  console.log(`\n[步骤1 基线] 没打任何包之前先读 —— 这一步就是排除"老配置残留"`);
  const b1 = await ask(DEV, 'BSS0000E'), p1 = await ask(DEV, 'BPA0000E');
  console.log(`   BSS0000E -> ${b1 ? JSON.stringify(b1) : '无回复'}     BPA0000E -> ${p1 ? JSON.stringify(p1) : '无回复'}`);

  console.log(`\n[步骤2] 打 ${SECS}s **假凭据** SSID="${FAKE_SSID}" PASS="${FAKE_PASS}"（音响从没存过这组）`);
  await fire(FAKE_SSID, FAKE_PASS, SECS);
  const b2 = await ask(DEV, 'BSS0000E'), p2 = await ask(DEV, 'BPA0000E');
  console.log(`   BSS0000E -> ${b2 ? JSON.stringify(b2) : '无回复'}     BPA0000E -> ${p2 ? JSON.stringify(p2) : '无回复'}`);
  const okFake = b2.includes(FAKE_SSID) && p2.includes(FAKE_PASS);
  console.log(`   >>> ${okFake ? '假凭据被原样解出 = 纯 Windows 发包解码成功，铁证 ✅✅' : '没出现假凭据（可能没解出，或回读窗口未到）❌'}`);

  if (okFake || process.argv.includes('--always-restore')) {
    console.log(`\n[步骤3] 立刻用真凭据补 ${SECS}s，把它带回 ${REAL_SSID}`);
    await fire(REAL_SSID, REAL_PASS, SECS);
    const b3 = await ask(DEV, 'BSS0000E'), p3 = await ask(DEV, 'BPA0000E');
    console.log(`   BSS0000E -> ${b3 ? JSON.stringify(b3) : '无回复'}     BPA0000E -> ${p3 ? JSON.stringify(p3) : '无回复'}`);
    console.log(`   >>> ${b3.includes(REAL_SSID) ? '真凭据也解出 ✅（两次不同内容跟着我们的发包变化 = 因果闭环）' : '未变（若上一步是假值，这里没刷新属正常）'}`);
  }
  const left = parseInt(sh(`powershell -NoProfile -Command "(Get-CimInstance Win32_Process -Filter \\"Name='node.exe'\\" | Where-Object { $_.CommandLine -match 'airkiss_send' } | Measure-Object).Count"`).trim() || '0', 10);
  console.log(`\n[收尾] 残留发包流=${left}`);
  process.exit(0);
})();
