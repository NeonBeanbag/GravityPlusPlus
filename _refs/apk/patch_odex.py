#!/usr/bin/env python3
"""给 odex 里内嵌的 dex 打指令补丁：把指定位置的若干 code unit 填成 nop(0x0000)，
然后重算 dex 的 SHA-1 签名(偏移 12..31) 和 adler32 校验和(偏移 8..11) —— 不重算 dvm 会拒载。
用法: python patch_odex.py <in.odex> <out.odex> <dexOff:codeUnits>...
   例: python patch_odex.py a.odex b.odex 0x33ff94:2 0x401c22:3
"""
import hashlib
import struct
import sys
import zlib


def dex_offset(blob):
    for off in (0x28, 0x2C, 0x00):
        if blob[off:off + 4] == b'dex\n':
            return off
    i = blob.find(b'dex\n')
    if i < 0:
        raise SystemExit('找不到内嵌 dex 头')
    return i


def main():
    src, dst = sys.argv[1], sys.argv[2]
    blob = bytearray(open(src, 'rb').read())
    d = dex_offset(blob)
    size = struct.unpack_from('<I', blob, d + 32)[0]      # header.file_size
    if not (0 < size <= len(blob) - d):
        raise SystemExit(f'file_size 不合理: {size}')
    print(f'dexOffset=0x{d:x} file_size={size}')
    for spec in sys.argv[3:]:
        off, units = spec.split(':')
        off, units = int(off, 0), int(units, 0)
        p = d + off
        if not (0 <= off and off + units * 2 <= size):
            raise SystemExit(f'越界: {spec}')
        print(f'  改前 @{off:#x}: {bytes(blob[p:p + units * 2]).hex()}')
        blob[p:p + units * 2] = b'\x00' * (units * 2)
    seg = bytearray(blob[d:d + size])                  # 切片是拷贝，改完必须写回 blob
    seg[12:32] = hashlib.sha1(bytes(seg[32:])).digest()
    seg[8:12] = struct.pack('<I', zlib.adler32(bytes(seg[12:]), 1))
    blob[d:d + size] = seg
    print(f'signature={bytes(seg[12:32]).hex()[:16]}… checksum={struct.unpack_from("<I", seg, 8)[0]:#010x}')
    open(dst, 'wb').write(bytes(blob))
    print(f'写出 {dst}（{len(blob)} 字节）')
    chk = bytearray(open(dst, 'rb').read())[d:d + size]
    ok_ck = struct.unpack_from('<I', chk, 8)[0] == zlib.adler32(bytes(chk[12:]), 1)
    ok_sg = chk[12:32] == hashlib.sha1(bytes(chk[32:])).digest()
    print(f'自检：checksum={"过" if ok_ck else "不过"} signature={"过" if ok_sg else "不过"}')
    if not (ok_ck and ok_sg):
        raise SystemExit('补丁版头字段自洽失败，别往设备上推')


main()
