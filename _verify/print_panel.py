import ctypes, ctypes.wintypes as W, os, subprocess, sys, time
from PIL import Image

u = ctypes.windll.user32
u.SetProcessDpiAwarenessContext(-4)
gdi = ctypes.windll.gdi32


class RECT(ctypes.Structure):
    _fields_ = [("l", W.LONG), ("t", W.LONG), ("r", W.LONG), ("b", W.LONG)]


REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EXE = os.path.join(REPO, "app", "GravityPanel", "bin", "x64", "Debug",
                   "net9.0-windows10.0.19041.0", "Gravity++.exe")
pid = int(subprocess.run(["powershell", "-NoProfile", "-Command",
    "(Get-Process 'Gravity++',GravityPanel -ErrorAction SilentlyContinue | Select -First 1).Id"],
    capture_output=True, text=True).stdout.strip())


def find_windows():
    res = []

    def proc(h, l):
        p = W.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            r = RECT()
            u.GetWindowRect(h, ctypes.byref(r))
            if (r.r - r.l) > 50:
                res.append((h, r.l, r.t, r.r, r.b, bool(u.IsWindowVisible(h))))
        return True

    u.EnumWindows(ctypes.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)(proc), 0)
    return res


wins = find_windows()
if not [w for w in wins if w[5]]:
    subprocess.Popen([EXE, "--toggle-panel"])
    time.sleep(0.9)
    wins = find_windows()
vis = [w for w in wins if w[5]]
if not vis:
    print("面板没显示，PrintWindow 也拍不到隐藏窗口")
    sys.exit(1)
h, l, t, r, b, _ = vis[0]
# 这个 python 进程拿到的坐标是被 DPI 虚拟化过的一半（GetDpiForWindow=192 而 rect 只有 420 宽），
# 按窗口真实 DPI 放大才不会只拍到左上角。
dpi = u.GetDpiForWindow(h) or 96
k = dpi / 96
l, t, r, b = int(l * k), int(t * k), int(r * k), int(b * k)
w_px, h_px = r - l, b - t

# PrintWindow + PW_RENDERFULLCONTENT(2)：不依赖 z-order，被别的窗口压着也能取到内容
hwnd_dc = u.GetWindowDC(h)
mem = gdi.CreateCompatibleDC(hwnd_dc)
bmp = gdi.CreateCompatibleBitmap(hwnd_dc, w_px, h_px)
gdi.SelectObject(mem, bmp)
class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [("biSize", W.DWORD), ("biWidth", W.LONG), ("biHeight", W.LONG),
                ("biPlanes", W.WORD), ("biBitCount", W.WORD), ("biCompression", W.DWORD),
                ("biSizeImage", W.DWORD), ("biXPelsPerMeter", W.LONG),
                ("biYPelsPerMeter", W.LONG), ("biClrUsed", W.DWORD), ("biClrImportant", W.DWORD)]


ok = u.PrintWindow(h, mem, 2)
bi = BITMAPINFOHEADER()
bi.biSize = ctypes.sizeof(bi)
bi.biWidth = w_px
bi.biHeight = -h_px
bi.biPlanes = 1
bi.biBitCount = 32
buf = ctypes.create_string_buffer(w_px * h_px * 4)
got = gdi.GetDIBits(mem, bmp, 0, h_px, buf, ctypes.byref(bi), 0)
out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "panel_print.png")
img = Image.frombytes("RGBA", (w_px, h_px), buf.raw, "raw", "BGRA")
img.save(out)
print("PrintWindow ok=%s bits=%d size=%s -> %s" % (ok, got, img.size, out))
gdi.DeleteObject(bmp); gdi.DeleteDC(mem); u.ReleaseDC(h, hwnd_dc)
