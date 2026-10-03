// 逐个试 system_daemon 的 8 字节命令，记录回复；只读为主，改动型命令跳过。
// 用法: node daemon_probe.mjs [host] [port] [cmd1 cmd2 ...]
import net from 'node:net';
const HOST = process.argv[2] || '192.168.1.12';
const PORT = parseInt(process.argv[3] || '8888', 10);
const CMDS = process.argv.slice(4).length ? process.argv.slice(4)
  : ['BST0000E', 'BEN0000E', 'BIN0000E', 'BSS0000E', 'BPA0000E', 'BPROTOCE', 'BUPINFOE', 'BSWITCHE'];

function once(cmd) {
  return new Promise(res => {
    const t0 = Date.now();
    const s = net.connect(PORT, HOST);
    let rx = Buffer.alloc(0), done = false;
    const fin = why => { if (done) return; done = true; try { s.destroy(); } catch { } res({ cmd, rx: rx.toString('latin1'), hex: rx.toString('hex'), ms: Date.now() - t0, why }); };
    s.setTimeout(2500);
    s.on('connect', () => s.write(Buffer.from(cmd, 'ascii')));
    s.on('data', d => { rx = Buffer.concat([rx, d]); if (rx.length >= 5) fin('data'); });
    s.on('timeout', () => fin('timeout'));
    s.on('error', e => fin('err ' + e.code));
    s.on('close', () => fin(rx.length ? 'closed' : 'closed-empty'));
  });
}

for (const c of CMDS) {
  const r = await once(c);
  console.log(`${c} -> reply="${r.rx.replace(/[^\x20-\x7e]/g, '.')}" hex=${r.hex} ${r.ms}ms (${r.why})`);
}
