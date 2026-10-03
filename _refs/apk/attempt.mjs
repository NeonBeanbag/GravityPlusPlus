// 一次干净的配网实验：先确认全场只有一条流 → 起发 → 边发边轮询（守护进程 + ARP + ping）。
// 用法: node attempt.mjs --ssid MyWiFi-A --pass <pwd> --local 192.168.1.20 [--secs 200] [--dev 192.168.1.12]
import { spawn, execSync } from 'node:child_process';
import net from 'node:net';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : d; };
const DEV = arg('dev', '192.168.1.12');
const SECS = parseFloat(arg('secs', '200'));
const FWD = ['--ssid', '--pass', '--local', '--ip', '--pace', '--beacon', '--variant', '--bcast', '--join', '--gap'];
const fwd = [];
process.argv.slice(2).forEach((a, i, arr) => { if (FWD.includes(a)) fwd.push(a, arr[i + 1]); });

function countSenders() {
  try {
    const o = execSync('powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \\"Name=\'node.exe\'\\" | Where-Object { $_.CommandLine -match \'airkiss_send\' } | Measure-Object | Select-Object -ExpandProperty Count"', { encoding: 'utf8' });
    return parseInt(o.trim().split(/\s+/).pop(), 10) || 0;
  } catch { return -1; }
}
function ask(cmd, timeout = 1400) {
  return new Promise(res => {
    const s = net.connect({ host: DEV, port: 8888, timeout });
    let rx = '', done = false;
    const fin = () => { if (done) return; done = true; try { s.destroy(); } catch { } res(rx); };
    s.on('connect', () => s.write(Buffer.from(cmd, 'ascii')));
    s.on('data', d => { rx += d.toString('latin1'); if (rx.length >= 5) fin(); });
    s.on('timeout', fin); s.on('error', fin); s.on('close', fin);
    setTimeout(fin, timeout + 250);
  });
}
const pingOk = () => { try { execSync(`ping -n 1 -w 350 ${DEV}`, { stdio: 'pipe' }); return true; } catch { return false; } };
const arpSeen = () => {
  try {
    const o = execSync(`powershell -NoProfile -Command "Get-NetNeighbor -IPAddress 192.168.1.* -ErrorAction SilentlyContinue | Where-Object { $_.LinkLayerAddress -like '94-A1-A2*' } | Select-Object -ExpandProperty IPAddress"`, { encoding: 'utf8' });
    return o.trim().split(/\s+/).filter(x => /^\d+\./.test(x))[0] || '';
  } catch { return ''; }
};

(async () => {
  const c0 = countSenders();
  if (c0 > 0) { console.error(`!! 进场前就有 ${c0} 条发包流在跑，先清场再试（见 --help 里那条 powershell）`); process.exit(1); }
  console.log(`[前置检查] 现存发包流=${c0}  ping(${DEV})=${pingOk() ? '通' : '不通'}  ARP里音响=${arpSeen() || '没有'}`);
  const base = await ask('BSS0000E');
  console.log(`[基线] BSS0000E -> ${base ? '"' + base + '"' : '无回复（多半正在嗅探）'}`);

  const child = spawn(process.execPath, ['airkiss_send.mjs', ...fwd, '--passes', '0'], { stdio: ['ignore', 'pipe', 'inherit'] });
  let lastLine = '';
  child.stdout.on('data', d => { const a = d.toString().split(/[\r\n]/); lastLine = a[a.length - 1] || lastLine; });
  child.on('exit', code => console.log(`\n[发包进程退出 code=${code}]`));
  console.log(`[开火] ${fwd.join(' ')} —— 单流，${SECS}s 内每 3s 读一次`);

  const t0 = Date.now();
  let k = 0;
  while ((Date.now() - t0) < SECS * 1000) {
    await new Promise(r => setTimeout(r, 3000)); k++;
    const r = await ask('BSS0000E');
    if (r) {
      const pw = await ask('BPA0000E');
      console.log(`\n*** 解码回报！(+${((Date.now() - t0) / 1000).toFixed(0)}s)  BSS0000E="${r}"  BPA0000E="${pw}"`);
      console.log(`    ${r === '10000' ? '10000 = 还没解出（仪器有效但没内容）' : '≠10000 → 这就是解出来的 SSID ✅'}`);
      break;
    }
    const p = pingOk(), a = arpSeen();
    if (p || a) console.log(`\n(+${((Date.now() - t0) / 1000).toFixed(0)}s) 设备回网：ping=${p} ARP=${a}  ${lastLine}`);
    else process.stdout.write(`\r[#${k}] +${((Date.now() - t0) / 1000).toFixed(0)}s ${lastLine.slice(0, 60)}          `);
  }
  console.log('');
  try { child.kill(); } catch { }
  await new Promise(r => setTimeout(r, 300));
  console.log(`[收尾] 仍在跑的发包流=${countSenders()}  ping=${pingOk() ? '通' : '不通'}  ARP=${arpSeen() || '无'}`);
  const after = await ask('BSS0000E', 2000);
  console.log(`[结束读数] BSS0000E -> ${after ? '"' + after + '"' : '无回复'}`);
  process.exit(0);
})();
