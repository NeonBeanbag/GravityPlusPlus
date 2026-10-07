#!/usr/bin/env python3
"""按类名（可选方法名）把方法整个反汇编出来，带 dex 偏移 —— 用来决定 NOP 哪一条。
用法: python dex_class.py <odex或dex> Lcom/foo/Bar; [方法名子串]
"""
import struct
import sys

from androguard.core.dex import DEX


def dex_offset(blob):
    for off in (0x28, 0x2C, 0x00):
        if blob[off:off + 4] == b'dex\n':
            return off
    return blob.find(b'dex\n')


def width_of(ins):
    for attr in ('get_length', 'length'):
        v = getattr(ins, attr, None)
        if v is None:
            continue
        n = v() if callable(v) else v
        if isinstance(n, int) and n > 0:
            return n
    return 2


path, want = sys.argv[1], sys.argv[2]
meth = sys.argv[3] if len(sys.argv) > 3 else ''
blob = open(path, 'rb').read()
d = dex_offset(blob)
fs = struct.unpack_from('<I', blob, d + 32)[0]
dex = DEX(blob[d:d + fs])
n = 0
for cls in dex.get_classes():
    if cls.get_name() != want:
        continue
    for m in cls.get_methods():
        if meth and meth not in m.get_name():
            continue
        em = m.get_method() if hasattr(m, 'get_method') else m
        code = em.get_code() if hasattr(em, 'get_code') else None
        print(f'\n== {m.get_name()}  args={m.get_descriptor() if hasattr(m, "get_descriptor") else "?"}')
        if not code:
            print('   (无代码体/抽象)')
            continue
        off, nins = code.get_off(), code.get_insns_size()
        cur = off + 16
        for ins in code.get_bc().get_instructions():
            w = width_of(ins)
            print(f'  dexOff=0x{cur:x} fileOff=0x{d + cur:x} ({w}B)  {ins.get_name()} {ins.get_output()}')
            cur += w
        print(f'   合计 {cur - (off + 16)} / 应为 {nins * 2} 字节'
              + ('' if cur - (off + 16) == nins * 2 else '   <<< 宽度对不上'))
        n += 1
print(f'\n方法数 {n}')
