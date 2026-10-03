// 最小 bplist00 编码器：只需要字符串 / 字典 / 二进制数据（AirPlay 元数据和封面就这三种）
// 用法: import { encode } from './bplist.mjs'
const intBytes = n => {
  if (n < 0x10) return Buffer.from([0x10 | n]);
  let b = Buffer.alloc(0); let v = n;
  while (v > 0) { b = Buffer.concat([Buffer.from([v & 0xff]), b]); v >>>= 8; }
  return Buffer.concat([Buffer.from([0x10 | (b.length - 1)]), b]);   // 0x1f 后跟 (len-1) 字节的整数
};
const lenPrefix = (mark, n) => n < 0x0f
  ? Buffer.from([mark | n])
  : (n < 0x100 ? Buffer.concat([Buffer.from([mark | 0x0f, 0x10, n])])
               : Buffer.concat([Buffer.from([mark | 0x0f, 0x11]), Buffer.from([(n >> 24) & 0xff, (n >> 16) & 0xff, (n >> 8) & 0xff, n & 0xff])]));

class Builder {
  constructor() { this.objects = []; this.index = new Map(); }
  add(buf) {
    const key = buf.toString('base64');
    if (this.index.has(key)) return this.index.get(key);
    const id = this.objects.length;
    this.objects.push(buf); this.index.set(key, id);
    return id;
  }
  string(s) {
    if ([...s].every(c => c.charCodeAt(0) < 0x80)) return this.add(Buffer.concat([lenPrefix(0x50, s.length), Buffer.from(s, 'ascii')]));
    const u = Buffer.from(s, 'utf16le');
    return this.add(Buffer.concat([lenPrefix(0x60, u.length / 2), u]));
  }
  data(b) { return this.add(Buffer.concat([lenPrefix(0x40, b.length), b])); }
  dict(pairs) {                       // pairs: [[k, idOrBufOrString]]，值先建好再传 id
    const ids = pairs.map(([, v]) => v);
    const keys = pairs.map(([k]) => typeof k === 'number' ? k : this.string(k));
    const body = Buffer.concat([...keys, ...ids].map(x => Buffer.from([x])));
    return this.add(Buffer.concat([lenPrefix(0xd0, pairs.length), body]));
  }
  build(top) {
    const head = Buffer.from('bplist00');
    const body = Buffer.concat(this.objects.map(o => o));
    const offSize = 1 + (head.length + body.length + this.objects.length > 0xffff ? 1 : 0);
    const offsets = Buffer.alloc(this.objects.length * offSize);
    let p = head.length;
    this.objects.forEach((o, i) => {
      if (offSize === 1) offsets[i] = p & 0xff;
      else offsets.writeUInt16BE(p, i * offSize);
      p += o.length;
    });
    const trailer = Buffer.alloc(32);
    trailer.writeUInt8(offSize, 6);
    trailer.writeUInt8(1, 7);                          // objectRefSize
    trailer.writeUInt32BE(this.objects.length, 8 + 4); // numObjects (大端 8 字节)
    trailer.writeUInt32BE(top, 16 + 4);                // topObject
    trailer.writeUInt32BE(head.length + body.length, 24 + 4);  // offsetTableOffset
    return Buffer.concat([head, body, offsets, trailer]);
  }
}

export function encode(obj) {
  const b = new Builder();
  const top = walk(b, obj);
  return b.build(top);
}
function walk(b, v) {
  if (typeof v === 'string') return b.string(v);
  if (Buffer.isBuffer(v)) return b.data(v);
  if (v && typeof v === 'object') {
    const ids = Object.entries(v).map(([k, val]) => [k, walk(b, val)]);
    return b.dict(ids);
  }
  throw new Error('bplist 不支持的类型: ' + typeof v);
}
export { intBytes };
