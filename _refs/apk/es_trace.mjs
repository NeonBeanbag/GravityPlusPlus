// 用网络 adb 抓 firmware/App 侧的配网解码痕迹，把"没解出来"变成"卡在哪一步"。
// 用法: node es_trace.mjs [host] [--dump] [--grep 正则]
//   默认 grep: CooeeFW|neeze|akiss|easy|ES:|ssid|password|cooee|Decode|Invalid|check
import { execFileSync } from 'node:child_process';

const argv = process.argv.slice(2);
const HOST = argv[0] && !argv[0].startsWith('--') ? argv[0] : '192.168.1.12';
const gi = argv.indexOf('--grep');
const RE = gi > 0 ? argv[gi + 1] : 'CooeeFW|neeze|akiss|Airkiss|airkiss|easy setup|easy_setup|ES:|cooee|Decode|decode|Invalid|check failed|ssid|password|broadcast';
const DUMP = argv.includes('--dump');

const sh = (cmd, to = 25000) => {
  try {
    return execFileSync(process.execPath, ['adb.mjs', HOST, '7788', 'shell ' + cmd],
      { encoding: 'utf8', timeout: to, env: { ...process.env, MSYS2_ARG_CONV_EXCL: '*', MSYS_NO_PATHCONV: '1', ADB_TO: String(Math.floor(to / 1000)) } });
  } catch (e) { return '<失败: ' + (e.message || '').slice(0, 80) + '>'; }
};

if (DUMP) { console.log(sh('logcat -d -v brief')); process.exit(0); }

console.log('--- logcat（main/system/radio 全缓冲）里跟配网解码有关的行 ---');
for (const buf of ['', '-b main', '-b system', '-b radio', '-b events']) {
  const out = sh(`logcat -d -v brief ${buf} 2>/dev/null`);
  const lines = out.split('\n').filter(l => new RegExp(RE, 'i').test(l));
  console.log(`\n[${buf || '默认'}] 总 ${out.split('\n').length} 行，命中 ${lines.length} 行`);
  for (const l of lines.slice(-45)) console.log('   ' + l.trim());
}
console.log('\n--- 内核侧（dhd 固件打印多半在这里）---');
for (const c of ['cat /proc/kmsg', 'dmesg', 'cat /dev/kmsg']) {
  const out = sh(c + ' 2>/dev/null');
  const lines = out.split('\n').filter(l => new RegExp(RE, 'i').test(l));
  console.log(`[${c}] 读到 ${out.split('\n').length} 行，命中 ${lines.length}`);
  if (lines.length) { for (const l of lines.slice(-30)) console.log('   ' + l.trim()); break; }
}
console.log('\n--- /sdcard 上有没有守护进程日志 ---');
console.log(sh('ls -l /sdcard/ 2>/dev/null').split('\n').slice(0, 12).join('\n'));
