#!/usr/bin/env python3
"""把包含指定 dex 偏移的那个方法整个反汇编出来（每条指令带 dex 内偏移），用来挑 NOP 目标。
用法: python dex_method.py <odex或dex> <dexOff，如 0x31f34a>
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
            return n                    # androguard 这里给的就是字节数，别再乘 2
    return 2


targets = [int(a, 0) for a in sys.argv[2:]]
path = sys.argv[1]
blob = open(path, 'rb').read()
d = dex_offset(blob)
# androguard 会对"给它的那段字节"整体算 adler32，所以必须按 header.file_size 切，
# 不能把 odex 后面的优化段一起喂进去（那样必报 Wrong Adler32 checksum）
fs = struct.unpack_from('<I', blob, d + 32)[0]
dex = DEX(blob[d:d + fs])
found = 0
for cls in dex.get_classes():
    for m in cls.get_methods():
        em = m.get_method() if hasattr(m, 'get_method') else m      # androguard4 直接给 EncodedMethod
        code = em.get_code() if hasattr(em, 'get_code') else None
        if not code:
            continue
        try:
            off = code.get_off()
            nins = code.get_insns_size()
        except Exception as e:
            print('取 code 偏移失败:', type(e).__name__, e)
            continue
        span = 16 + nins * 2
        hit=[t for t in targets if off <= t < off + span]
        if not hit:
            continue
        found += 1
        print(f'== {cls.get_name()} -> {m.get_name()}  code_item@0x{off:x} insns={nins} 单位  命中={[hex(h) for h in hit]}')
        cur = off + 16
        for ins in code.get_bc().get_instructions():
            w = width_of(ins)
            mark = '   <<< 命中点' if any(cur <= t < cur + w for t in hit) else ''
            print(f'  dexOff=0x{cur:x} fileOff=0x{d + cur:x} ({w}B)  '
                  f'{ins.get_name()} {ins.get_output()}{mark}')
            cur += w
        print(f'   合计 {cur - (off + 16)} 字节 / 应为 {nins * 2} 字节'
              + ('' if cur - (off + 16) == nins * 2 else '   <<< 对不上，偏移不可信'))
print('命中方法数:', found)
