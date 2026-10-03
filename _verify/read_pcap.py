import os, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
path = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "airplay.pcapng")
data = open(path, "rb").read()
off = 0
pkts = []
while off + 8 <= len(data):
    btype, blen = struct.unpack_from("<II", data, off)
    if blen < 12 or off + blen > len(data):
        break
    if btype == 0x00000006:                      # Enhanced Packet Block
        caplen = struct.unpack_from("<I", data, off + 20)[0]
        pkts.append(data[off + 28:off + 28 + caplen])
    off += blen
print("包数:", len(pkts))


def l3(p):
    if len(p) >= 20 and (p[0] >> 4) == 4:        # 直接就是 IPv4（pktmon 常见）
        return p
    if len(p) >= 14:                             # 以太网头
        t = struct.unpack_from(">H", p, 12)[0]
        if t == 0x0800:
            return p[14:]
    return None


def show(tag, payload, src, dst):
    if not payload:
        return
    txt = payload.decode("latin1").replace("\r\n", " | ")
    print(f"\n### {tag} {src} -> {dst}  {len(payload)}B")
    print("   可打印:", txt[:600])
    print("   hex:", payload[:220].hex())


for p in pkts:
    ip = l3(p)
    if not ip or (ip[0] >> 4) != 4:
        continue
    ihl = (ip[0] & 0xF) * 4
    proto = ip[9]
    src = ".".join(map(str, ip[12:16]))
    dst = ".".join(map(str, ip[16:20]))
    tot = struct.unpack_from(">H", ip, 2)[0]
    seg = ip[ihl:tot]
    if len(seg) < 4:
        continue
    sport, dport = struct.unpack_from(">HH", seg, 0)
    if proto == 6 and dport == 5000 and len(seg) > 20:
        opt = (seg[12] >> 4) * 4
        show("TCP-RTSP", seg[opt:], src + ":" + str(sport), dst + ":" + str(dport))
    elif proto == 17 and len(seg) > 8:
        body = seg[8:]
        if body and (dport != 5353):
            show("UDP", body[:200], src + ":" + str(sport), dst + ":" + str(dport))
