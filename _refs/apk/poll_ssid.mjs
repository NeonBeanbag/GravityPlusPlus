// 轮询音响守护进程：连得上就依次问 BSS0000E(取SSID) / BPA0000E(取密码) / BPROTOCE(取模式)。
// 用法: node poll_ssid.mjs [秒数] [host]   —— 空转时打印 ping 状态，一有回包立刻高亮。
import net from 'node:net';
import { execSync } from 'node:child_process';

const SECS = parseFloat(process.argv[2] || '120');
const HOST = process.argv[3] || '192.168.1.12';
const t0 = Date.now();

function ask(cmd, timeout = 1500) {
  return new Promise(res => {
    const s = net.connect({ host: HOST, port: 8888, timeout });
    let rx = '', done = false;
    const fin = () => { if (done) return; done = true; try { s.destroy(); } catch { } res(rx); };
    s.on('connect', () => s.write(Buffer.from(cmd, 'ascii')));
    s.on('data', d => { rx += d.toString('latin1'); if (rx.length >= 5) fin(); });
    s.on('timeout', fin); s.on('error', fin); s.on('close', fin);
    setTimeout(fin, timeout + 300);
  });
}

function pingOk() {
  try { execSync(`ping -n 1 -w 400 ${HOST}`, { stdio: 'pipe' }); return true; } catch { return false; }
}

let n = 0;
while ((Date.now() - t0) < SECS * 1000) {
  n++;
  const p = pingOk();
  const ssid = await ask('BSS0000E');
  if (ssid) {
    const pwd = await ask('BPA0000E');
    const proto = await ask('BPROTOCE');
    console.log(`\n*** 守护进程有回包！(+${((Date.now() - t0) / 1000).toFixed(1)}s)`);
    console.log(`    BSS0000E -> "${ssid}" hex=${Buffer.from(ssid, 'latin1').toString('hex')}`);
    console.log(`    BPA0000E -> "${pwd}"`);
    console.log(`    BPROTOCE -> "${proto}"`);
    process.exit(0);
  }
  process.stdout.write(`\r[#${n}] +${((Date.now() - t0) / 1000).toFixed(0)}s  ping=${p ? '通' : '不通'}  BSS=无回复        `);
  await new Promise(r => setTimeout(r, 1500));
}
console.log('\n（全程无回包）');
