// 长连接读 system_daemon：发一条 8 字节命令后**不关闭**，把服务器后续推来的每一帧都打时间戳。
// 依据：dev_system_daemon 里有 "ssid is %s" / "password is %s" / "100%02d" / "000%02x" / "200%02d"
//       这些字符串，说明解出 SSID 后是由这条 socket 回报的。
// 用法: node daemon_listen.mjs [cmd] [秒数]      默认 BST0000E 240s
import net from 'node:net';
const CMD = process.argv[2] || 'BST0000E';
const SECS = parseFloat(process.argv[3] || '240');
const HOST = process.argv[4] || '192.168.1.12';
const t0 = Date.now();
const stamp = () => `+${((Date.now() - t0) / 1000).toFixed(1)}s`;
const s = net.connect(8888, HOST);
s.setTimeout(0);
s.on('connect', () => { console.log(`${stamp()} 已连接，发送 ${CMD}`); s.write(Buffer.from(CMD, 'ascii')); });
s.on('data', d => console.log(`${stamp()} 收到 ${d.length}B  "${d.toString('latin1').replace(/[^\x20-\x7e]/g, '.')}"  hex=${d.toString('hex')}`));
s.on('error', e => console.log(`${stamp()} 错误 ${e.code}`));
s.on('close', () => { console.log(`${stamp()} 连接被关闭`); process.exit(0); });
setTimeout(() => { console.log(`${stamp()} 超时结束（共等了 ${SECS}s）`); process.exit(0); }, SECS * 1000);
