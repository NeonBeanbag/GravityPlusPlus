// 配网实验驾驶舱：读基线状态 → 打 cooee 包 → 每秒轮询，状态一变立刻报。
// 用法: node drive.mjs --ssid MyWiFi-A --pass <pwd> --local 192.168.1.20 [--host 192.168.1.12] [--passes 8] [--secs 60]
import { spawn } from 'node:child_process';
import net from 'node:net';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 && process.argv[i + 1] ? process.argv[i + 1] : d; };
const HOST = arg('host', '192.168.1.12');
const PASSES = arg('passes', '0');          // 0 = 一直打
const SECS = parseFloat(arg('secs', '60'));
const FORWARD = ['--ssid', '--pass', '--local', '--ip', '--pace', '--beacon', '--variant', '--bcast', '--join', '--gap'];

function poll(ms = 1800) {
  return new Promise(res => {
    const s = net.connect(8888, HOST);
    let rx = '', done = false;
    const fin = () => { if (done) return; done = true; try { s.destroy(); } catch { } res(rx); };
    s.on('connect', () => s.write('BST0000E'));
    s.on('data', d => { rx += d.toString('latin1'); if (rx.length >= 5) fin(); });
    s.on('error', () => fin());
    s.setTimeout(ms); s.on('timeout', fin);
  });
}

(async () => {
  const base = await poll();
  console.log(`[基线] BST0000E -> "${base.replace(/[^\x20-\x7e]/g, '.')}"   (hex ${Buffer.from(base, 'latin1').toString('hex')})`);
  const fwd = [];
  process.argv.slice(2).forEach((a, i, arr) => {
    if (FORWARD.includes(a)) { fwd.push(a, arr[i + 1]); }
  });
  console.log(`[发包] node airkiss_send.mjs ${fwd.join(' ')} --passes ${PASSES}`);
  const child = spawn(process.execPath, ['airkiss_send.mjs', ...fwd, '--passes', String(PASSES)], { stdio: ['ignore', 'pipe', 'inherit'] });
  const lines = [];
  child.stdout.on('data', d => { lines.push(d.toString()); process.stdout.write(d.toString().replace(/\r[^\n]*$/, '\r')); });
  child.on('exit', c => console.log(`\n[发包进程退出] code=${c}`));

  const t0 = Date.now();
  let ticks = 0;
  while ((Date.now() - t0) < SECS * 1000) {
    const r = await poll();
    ticks++;
    if (r !== base) {
      console.log(`\n*** 状态变化: "${base}" -> "${r.replace(/[^\x20-\x7e]/g, '.')}" hex=${Buffer.from(r, 'latin1').toString('hex')}  (+${((Date.now() - t0) / 1000).toFixed(1)}s)`);
      break;
    }
    process.stdout.write(`\r[轮询 #${ticks}] 仍是 "${base.replace(/[^\x20-\x7e]/g, '.')}"  +${((Date.now() - t0) / 1000).toFixed(0)}s        `);
    await new Promise(r2 => setTimeout(r2, 700));
  }
  console.log('');
  try { child.kill(); } catch { }
  process.exit(0);
})();
