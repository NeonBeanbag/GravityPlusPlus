// 最小 ADB over TCP 客户端（无依赖）。用于 7788 网络 adb。
// 协议：24 字节头 = cmd(u32LE) arg0 arg1 len checksum magic(=cmd^0xFFFFFFFF)，随后是 payload。
// 用法: node adb.mjs [host] [port] "命令"
import net from 'node:net';
import crypto from 'node:crypto';
import { createWriteStream, writeFileSync, readFileSync } from 'node:fs';

// 模式:
//   node adb.mjs <host> <port> "shell 命令"
//   node adb.mjs <host> <port> --exec "命令"            原始字节到 stdout（二进制安全）
//   node adb.mjs <host> <port> --pull /远端/文件 本地文件
//   node adb.mjs <host> <port> --push /远端路径 本地文件     (sync SEND，以 adbd 当前身份写文件)
const argv = process.argv.slice(2);
const HOST = argv[0] || '192.168.1.12';
const PORT = parseInt(argv[1] || '7788', 10);
// argv[2] 以 -- 开头才是模式开关，否则整个就是 shell 命令（默认 shell 模式）
const MODE = (argv[2] || '').startsWith('--') ? argv[2] : '';
const REMOTE = (MODE ? argv[3] : argv[2]) || '';
const LOCAL = MODE ? argv[4] : undefined;
const CMD = MODE === '--pull' ? `cat '${REMOTE}'` : REMOTE;
const tcpPayload = MODE === '--tcp' ? Buffer.from(LOCAL || '', 'utf8') : null;

const A_CNXN = 0x4e584e43, A_OPEN = 0x4e45504f, A_OKAY = 0x59414b4f,
      A_CLSE = 0x45534c43, A_WRTE = 0x45545257, A_AUTH = 0x48545541;
const VERSION = 0x01000001, MAXDATA = 256 * 1024;

function checksum(buf) { let s = 0; for (const b of buf) s = (s + b) >>> 0; return s >>> 0; }
function msg(cmd, arg0, arg1, payload = Buffer.alloc(0)) {
  const h = Buffer.alloc(24);
  h.writeUInt32LE(cmd, 0); h.writeUInt32LE(arg0, 4); h.writeUInt32LE(arg1, 8);
  h.writeUInt32LE(payload.length, 12); h.writeUInt32LE(checksum(payload), 16);
  h.writeUInt32LE((cmd ^ 0xffffffff) >>> 0, 20);
  return Buffer.concat([h, payload]);
}
function parse(buf) {
  if (buf.length < 24) return null;
  return { cmd: buf.readUInt32LE(0), arg0: buf.readUInt32LE(4), arg1: buf.readUInt32LE(8),
           len: buf.readUInt32LE(12), data: buf.subarray(24, 24 + buf.readUInt32LE(12)) };
}

const key = crypto.generateKeyPairSync('rsa', {
  modulusLength: 2048,
  publicKeyEncoding: { type: 'spki', format: 'pem' },
  privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
});

const sock = net.connect(PORT, HOST);
let rx = Buffer.alloc(0), peerId = 1, authed = false;
const sink = MODE === '--pull' ? createWriteStream(LOCAL) : null;
let bytesOut = 0;

function emit(buf) {
  bytesOut += buf.length;
  if (sink) sink.write(buf); else process.stdout.write(buf);
}

function sendOpen(service) {
  sock.write(msg(A_OPEN, peerId++, 0, Buffer.from(service + '\0', 'utf8')));
}

// ---- ADB sync: 协议（真 adb pull 走的就是这条，老 adbd 也支持）----
// 请求/响应都以 8 字节头 (ID[4] + size[4]) 打包，塞进 ADB 的 WRTE 载荷里。
let syncRx = Buffer.alloc(0), syncReady = false, remoteId = 0, tcpReady = false;
function syncSend(id, payloadBuf) {
  const b = Buffer.alloc(8);
  b.write(id, 0, 'ascii'); b.writeUInt32LE(payloadBuf.length, 4);
  if (process.env.ADB_DEBUG) console.error('  sync-> ' + Buffer.concat([b, payloadBuf]).subarray(0, 40).toString('hex'));
  sock.write(msg(A_WRTE, peerId - 1, remoteId, Buffer.concat([b, payloadBuf])));
}
function syncRecv(path) {
  // 线上格式：'RECV' + u32(路径长度) + 路径 —— 长度就是头里的 size 字段，不再另加一个 u32
  syncSend('RECV', Buffer.from(path, 'utf8'));
}
function handleSync(buf) {
  syncRx = Buffer.concat([syncRx, buf]);
  while (syncRx.length >= 8) {
    const id = syncRx.subarray(0, 4).toString('ascii');
    const size = syncRx.readUInt32LE(4);
    if (id === 'DONE') {
      syncRx = syncRx.subarray(8);
      if (sink) sink.end();
      console.error(`\n[done] ${MODE === '--push' ? '已写入 ' + pushPos : bytesOut} bytes -> ${MODE === '--push' ? REMOTE : LOCAL}`);
      return true;
    }
    if (syncRx.length < 8 + size) return false;
    const body = syncRx.subarray(8, 8 + size);
    syncRx = syncRx.subarray(8 + size);
    if (id === 'DATA') { emit(body); }
    else if (id === 'FAIL') { console.error('\n[sync FAIL]', body.toString('utf8')); if (sink) sink.end(); return true; }
    else if (id === 'OKAY') { if (MODE === '--push' && pushState === 'wait-okay') { console.error('[push] 远端接受 SEND，开始灌数据'); pushState = 'data'; pushPump(); } }
    else { console.error('\n[sync unknown id]', JSON.stringify(id), size); }
  }
  return false;
}

// ---- sync SEND（写文件回设备）----
let pushData = null, pushPos = 0, pushState = 'idle', peerMaxData = 4096;
function pushPump() {
  if (pushPos >= pushData.length) {
    const tail = Buffer.alloc(8); tail.write('DONE', 0, 'ascii'); tail.writeUInt32LE(0, 4);
    sock.write(msg(A_WRTE, peerId - 1, remoteId, tail));
    pushState = 'wait-done';
    return;
  }
  const n = Math.min(peerMaxData - 8, pushData.length - pushPos);   // 8 字节 sync 头也计入 maxdata
  const b = Buffer.alloc(8); b.write('DATA', 0, 'ascii'); b.writeUInt32LE(n, 4);
  sock.write(msg(A_WRTE, peerId - 1, remoteId, Buffer.concat([b, pushData.subarray(pushPos, pushPos + n)])));
  pushPos += n;
  if (process.env.ADB_DEBUG) console.error('  push -> DATA ' + pushPos + '/' + pushData.length);
  if (pushPos >= pushData.length) {   // 最后一块：同一条流上紧跟 DONE，不等 ack
    const tail = Buffer.alloc(8); tail.write('DONE', 0, 'ascii'); tail.writeUInt32LE(0, 4);
    sock.write(msg(A_WRTE, peerId - 1, remoteId, tail));
    pushState = 'wait-done';
    console.error('[push] 数据发完，等远端 DONE 回执');
  }
}

sock.on('connect', () => {
  sock.write(msg(A_CNXN, VERSION, MAXDATA, Buffer.from('host::features=shell_v2,cmd\0', 'utf8')));
});

sock.on('data', (d) => {
  rx = Buffer.concat([rx, d]);
  while (rx.length >= 24) {
    const m = parse(rx);
    if (!m || rx.length < 24 + m.len) break;
    rx = rx.subarray(24 + m.len);
    if (process.env.ADB_DEBUG) console.error(`  <- cmd=0x${m.cmd.toString(16)} arg0=${m.arg0} arg1=${m.arg1} len=${m.len}`);
    if (m.cmd === A_CNXN) {
      console.error('[cnxn] peer says:', m.data.toString('utf8').replace(/\0.*$/, ''));
      if (m.arg1) { peerMaxData = Math.min(m.arg1, 64 * 1024); console.error('[cnxn] 对端 maxdata=' + peerMaxData); }
      if (MODE === '--root') { console.error('[root] 发送 root: 服务，adbd 将重启（连接会断）'); sendOpen('root:'); }
      else if (MODE === '--pull' || MODE === '--push') sendOpen('sync:');
      else if (MODE === '--tcp') sendOpen('tcp:' + REMOTE);
      else sendOpen((MODE === '--exec' ? 'exec:' : 'shell:') + CMD);
    } else if (m.cmd === A_AUTH) {
      if (authed) { console.error('[auth] second AUTH -> device refuses our key'); sock.end(); return; }
      authed = true;
      console.error('[auth] token type', m.arg0);
      const tok = m.data;
      const sig = crypto.sign('sha1', tok, { key: key.privateKey, padding: crypto.constants.RSA_NO_PADDING });
      sock.write(msg(A_AUTH, 2, sig.length, sig));
      const pubDer = crypto.createPublicKey(key.publicKey).export({ type: 'spki', format: 'der' });
      const b64 = pubDer.toString('base64') + '\0';
      sock.write(msg(A_AUTH, 1, b64.length, Buffer.from(b64, 'utf8')));
    } else if (m.cmd === A_OKAY) {
      if (MODE === '--push' && !syncReady) {
        remoteId = m.arg0; syncReady = true;
        pushData = readFileSync(LOCAL);
        console.error('[push] 本地 ' + LOCAL + ' = ' + pushData.length + ' 字节 -> 远端 ' + REMOTE);
        const spec = Buffer.from(REMOTE + ',0644', 'utf8');
        const b = Buffer.alloc(8); b.write('SEND', 0, 'ascii'); b.writeUInt32LE(spec.length, 4);
        sock.write(msg(A_WRTE, peerId - 1, remoteId, Buffer.concat([b, spec])));
        // 老 adbd 的 sync 不发 OKAY 预确认：handle_send 直接开始读 DATA，所以立刻续发数据
        pushState = 'data';
      } else if (MODE === '--push' && pushState === 'data') {
        pushPump();   // 上一块 WRTE 被 ack，续发
      } else if (MODE === '--pull' && !syncReady) {
        remoteId = m.arg0; syncReady = true;
        console.error('[sync] opened, remote id', remoteId);
        syncRecv(REMOTE);
      } else if (MODE === '--tcp' && !tcpReady) {
        remoteId = m.arg0; tcpReady = true;
        console.error('[tcp] 已连到设备内 127.0.0.1:' + REMOTE + '，发 ' + tcpPayload.length + 'B: ' + tcpPayload.toString('utf8'));
        sock.write(msg(A_WRTE, peerId - 1, remoteId, tcpPayload));
      } else if (m.data.length) emit(m.data);
    } else if (m.cmd === A_WRTE) {
      sock.write(msg(A_OKAY, m.arg1, m.arg0, Buffer.alloc(0)));
      if (MODE === '--pull' || MODE === '--push') { if (handleSync(m.data)) setTimeout(() => { try { sock.write(msg(A_CLSE, peerId - 1, remoteId, Buffer.alloc(0))); } catch { } setTimeout(() => process.exit(0), 300); }, 100); }
      else emit(m.data);
    } else if (m.cmd === A_CLSE) {
      if (sink) { sink.end(); console.error(`\n[pulled] ${bytesOut} bytes -> ${LOCAL}`); }
      else console.error('\n[closed]');
      sock.write(msg(A_CLSE, peerId - 1, m.arg0, Buffer.alloc(0)));
      setTimeout(() => sock.end(), 200);
    }
  }
});

sock.on('error', (e) => { console.error('[error]', e.message); process.exit(1); });
setTimeout(() => {
  if (sink) { sink.end(); console.error(`\n[timeout, wrote] ${bytesOut} bytes -> ${LOCAL}`); }
  else console.error(`\n[timeout] wrote ${bytesOut} bytes`);
  try { sock.write(msg(A_CLSE, 0, peerId - 1, Buffer.alloc(0))); } catch { }
  setTimeout(() => { sock.end(); process.exit(0); }, 300);
}, parseInt(process.env.ADB_TO || '20000', 10));
