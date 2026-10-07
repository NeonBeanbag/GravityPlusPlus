#!/usr/bin/env python3
"""按 类+方法名 反查所有 invoke 调用点（dex 里 method_id 索引 → 扫 invoke-virtual/interface 指令）。
用法: python dex_xref.py <odex或dex> Landroid/media/MediaPlayer; seekTo
"""
import struct
import sys

TYPE_IDX_SENTINEL = 0xFFFF   # 我们只按名字匹配，不区分重载


def dex_offset(blob):
    for off in (0x28, 0x2C, 0x00):
        if blob[off:off + 4] == b'dex\n':
            return off
    return blob.find(b'dex\n')


def read_uleb(b, p):
    v = 0
    for i in range(5):
        x = b[p + i]
        v |= (x & 0x7F) << (7 * i)
        if not (x & 0x80):
            return v, p + i + 1
    return v, p + 5


def str_at(dex, s):
    """s = string_data_item 的偏移：先是 uleb128 的 utf16 长度，再是 NUL 结尾的 MUTF-8。"""
    _, p = read_uleb(dex, s)
    e = dex.find(b'\x00', p)
    return dex[p:e].decode('utf-8', 'replace')


path, cls_want, mth_want = sys.argv[1], sys.argv[2], sys.argv[3]
blob = open(path, 'rb').read()
d = dex_offset(blob)
dex = blob[d:d + struct.unpack_from('<I', blob, d + 32)[0]]

ssize, soff = struct.unpack_from('<II', dex, 0x38)
strings = [str_at(dex, struct.unpack_from('<I', dex, soff + i * 4)[0]) for i in range(ssize)]
type_size, type_off = struct.unpack_from('<II', dex, 0x40)
types = [strings[struct.unpack_from('<I', dex, type_off + i * 4)[0]] for i in range(type_size)]
mcount, moff = struct.unpack_from('<II', dex, 0x58)
want = []
for i in range(mcount):
    ci, pi, ni = struct.unpack_from('<HHI', dex, moff + i * 8)
    name = strings[ni] if ni < len(strings) else '?'
    cls = types[ci] if ci < len(types) else '?'
    if name == mth_want and (cls_want == '*' or cls == cls_want):
        if cls_want == '*':
            print(f'  method_id[{i}] = {cls}->{name}')
        want.append(i)
if cls_want == '*':
    sys.exit(0)
if not want:
    print(f'没找到 {cls_want}->{mth_want}（method_id 总数 {mcount}）')
    sys.exit(1)
print(f'method_id: {want}  = {cls_want}->{mth_want}')

hits = []
for m in want:
    pat = struct.pack('<H', m)
    p = 0
    while True:
        p = dex.find(pat, p)
        if p < 0:
            break
        # 35c 族(invoke-virtual/super/direct/static/interface)：方法索引在 opcode 后第 2 字节
        if p >= 2 and 0x6E <= dex[p - 2] <= 0x72:
            hits.append((p - 2, '35c'))
        # 3rc 族(0x74..0x78)：方法索引在 opcode 后第 4 字节
        elif p >= 4 and 0x74 <= dex[p - 4] <= 0x78:
            hits.append((p - 4, '3rc'))
        p += 1
print(f'调用点 {len(hits)} 处：')
for off, form in hits:
    print(f'  dexOff=0x{off:x} fileOff=0x{d + off:x}  ({form})  bytes={dex[off:off + 8].hex()}')
