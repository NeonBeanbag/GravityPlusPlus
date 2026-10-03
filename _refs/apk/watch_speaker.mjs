// 盯音响有没有真的连上 MyWiFi-2.4G：判据 = ARP 里出现魅族 OUI 94:A1:A2
// 每轮 ping 扫 192.168.1.20-210，再读 ARP 表；一命中就探 7766/Info 和 7788
import { exec } from 'node:child_process';
import net from 'node:net';
import http from 'node:http';

const BASE = process.argv[2] || '192.168.0';
const lo = parseInt(process.argv[3] || '90', 10), hi = parseInt(process.argv[4] || '210', 10);
const run = cmd => new Promise(r => exec(cmd, { windowsHide: true }, (e, o) => r((o || '') + (e ? '' : ''))));

async function sweep() {
  const ps = [];
  for (let i = lo; i <= hi; i++) ps.push(run(`ping -n 1 -w 200 ${BASE}.${i}`));
  await Promise.all(ps);
  const t = await run('arp -a');
  for (const line of t.split(/\r?\n/)) {
    const mm = line.match(/(\d+\.\d+\.\d+\.\d+)\s+([0-9a-f-]{6,})/i);
    if (mm && mm[2].replace(/[^0-9a-f]/g, '').toLowerCase().startsWith('94a1a2')) return mm[1];
  }
  return null;
}

function tcp(host, port, ms = 1200) {
  return new Promise(res => {
    const s = net.connect({ host, port, timeout: ms });
    s.on('connect', () => { s.destroy(); res('OPEN'); });
    s.on('timeout', () => { s.destroy(); res('TIMEOUT'); });
    s.on('error', () => res('ERR'));
  });
}

function httpInfo(ip) {
  return new Promise(res => {
    const req = http.get({ host: ip, port: 7766, path: '/Info', timeout: 2000 }, r => {
      let b = ''; r.on('data', c => b += c); r.on('end', () => res(b.trim().slice(0, 240)));
    });
    req.on('error', e => res('http err ' + e.code));
    req.on('timeout', () => { req.destroy(); res('http timeout'); });
  });
}

(async () => {
  const ips = [...new Set(((await run('ipconfig')).match(/(\d+\.\d+\.\d+\.\d+)/g) || []))];
  console.log('本机IP: ' + ips.join('  '));
  console.log(`开始盯 ${BASE}.${lo}–${BASE}.${hi}，判据 MAC 前缀 94:A1:A2\n`);
  for (let n = 1; ; n++) {
    const ip = await sweep();
    if (ip) {
      console.log(`\n*** FOUND 音响 @ ${ip}  (第 ${n} 轮) ***`);
      console.log('7766 → ' + await tcp(ip, 7766) + '    7788 → ' + await tcp(ip, 7788));
      console.log('/Info → ' + await httpInfo(ip));
      process.exit(0);
    }
    process.stdout.write(`\r[轮 ${n}] ARP 里还没有 94:A1:A2，继续…   `);
    await new Promise(r => setTimeout(r, 3000));
  }
})();
