#!/usr/bin/env python3
"""在 odex/dex 里按字符串找引用点，并把命中方法的指令逐条打出来（带文件偏移），
用来挑 NOP 哪一条。用法:
    python dex_find.py <odex或dex> "结束airplay恢复播放"
"""
import struct
import sys


def dex_offsets(blob):
    """odex 里内嵌 dex 的起点：老格式头 0x28，新格式(035/036/037)头 0x2C。"""
    for off in (0x28, 0x2C, 0x00):
        if blob[off:off + 4] == b'dex\n':
            return off
    i = blob.find(b'dex\n')
    return i if i >= 0 else -1


def string_index(dex, needle):
    size, off = struct.unpack_from('<II', dex, 0x38)
    for i in range(size):
        p = struct.unpack_from('<I', dex, off + i * 4)[0]
        n, shift, val = 0, 0, 0
        while True:                       # uleb128 长度（MUTF-8 里 0 也算一字节）
            b = dex[p + n]
            val |= (b & 0x7F) << shift
            n += 1
            if not (b & 0x80):
                break
            shift += 7
        s = dex[p + n:p + n + val * 3 + 20].decode('utf-8', 'replace').split('\x00')[0]
        if needle in s:
            yield i, s


def hits(dex, idx):
    """const-string(0x1a) / const-string/jumbo(0x1b)：op, 寄存器(任意), string_idx 小端两字节。"""
    lo, hi = idx & 0xFF, (idx >> 8) & 0xFF
    p = 0
    while True:
        p = dex.find(bytes([lo, hi]), p)
        if p < 0:
            return
        if p >= 2 and dex[p - 2] in (0x1A, 0x1B):
            yield p - 2
        p += 1


def main():
    path, needle = sys.argv[1], sys.argv[2]
    blob = open(path, 'rb').read()
    d = dex_offsets(blob)
    dex = blob[d:]
    print(f'file={len(blob)} dexOffset={d} dexSize={struct.unpack_from("<I", dex, 32)[0]}')
    for idx, s in string_index(dex, needle):
        print(f'\n=== string_id[{idx}] = {s!r}')
        for h in hits(dex, idx):
            print(f'  const-string @ dexOff=0x{h:x}  fileOff=0x{d + h:x}  bytes={dex[h:h + 8].hex()}')


main()
