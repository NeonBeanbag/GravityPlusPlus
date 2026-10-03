# Gravity++

把一块**变砖的 MEIZU Gravity（A8）音响**在**纯 Windows** 下救活并日常使用：不用安卓手机、不用监听模式的无线网卡。

- **配网**：Windows 直接对着空中发 Broadcom **Cooee / Airkiss** 长度编码包，把音响重新连上 Wi-Fi（它被重置后没有已存网络，官方 App 又只支持安卓，所以这块砖原本只能扔）
- **托盘面板**：WinUI 3 原生小面板，正在播放 / 音量 / 音效 / 设备连接，走音响自带的 7766 HTTP 接口
- **音源**：音响的 WiFi 音源是它自带的 **AirPlay** 接收端（fireair），Windows 版 Apple Music 点投屏即可出声；本仓库也自带一个实验性的 RAOP 发送端
- **设备侧工具**：网络 adb（7788）、`system_daemon`（8888）回读解码结果，用于取证而不是猜

## 目录

```
app/GravityPanel/   WinUI 3 托盘面板（主产物）
app/GravityTray/     旧版 WebView 面板的宿主（已被上面取代，暂留作功能对照）
index.html          旧面板界面（同上）
server.mjs          旧面板的 Node 后端（同上）
_refs/apk/          配网与设备侧脚本工具链（不含任何厂商固件/APK）
_verify/            RAOP 发送端实验、面板截图取证
```

## 构建并运行面板

需要 .NET 9 SDK（含 Windows 桌面负载）。

```bash
cd app/GravityPanel
dotnet build            # 免打包 + 自包含 Windows App SDK
dotnet build -c Release
```

跑起来后托盘会出现图标：左键点开/收起，右键菜单里有「开机自启」。

**呼起面板的两条对外接口**（给别的软件或快捷键用）：

```bash
Gravity++.exe --toggle-panel          # 单实例 IPC：面板没开就开，开着就关
```

全局热键 **Ctrl+Alt+G**。想换就改 `%LOCALAPPDATA%\Gravity++\panel.json` 里的 `Hotkey`，写法如 `Win+Shift+F8`；被别的程序占用时启动日志会写明并降级到上面两条。

无头自检（不开窗口、不抢前台，用来证明数据通路本身是通的）：

```bash
Gravity++.exe --probe env      # 当前 Wi-Fi / 网卡合并 / 防火墙 / 本机 WLAN IP
Gravity++.exe --probe send     # 配网包发得出去吗（3 秒假账号，随即停止）
Gravity++.exe --probe link     # 音响现在听谁 + 本机 SMTC 认到了什么
```

## 配网（cooee / Airkiss）

在音响上同时按背面 `+` `−` 两秒进配网模式（它会自己断开网络，解出来后再自己回线 —— 回线就是成功），然后：

```bash
cd _refs/apk
node airkiss_send.mjs --ssid MyWiFi-A --pass 'your-wifi-password' --local 192.168.1.20
# 一条命令版（含体检 + 盯回线 + 回读）：
node gravity_provision.mjs --ssid MyWiFi-A --pass 'your-wifi-password'
```

协议要点（都在 `_refs/apk/airkiss_send.mjs` 里实现）：UDP **1503**，每轮三个包构成一条**只靠包长度传数据**的通道 ——
组播 `239.254.<frame[G]>.<frame[G+1]>` 长度 = `G`，两个广播包长度分别 = `i+20` 和 `frame[i]+180`；
载荷是 **AES-128-CCM** 加密的 TLV（`00|len|SSID` `03|len|PWD` `02|04|发送端IPv4`），AAD 是那 10 字节明文头，tag 8 字节。
节奏 8 ms 一轮、每遍之间隔 1 s；**长度取的是装配完的加密帧的字节**，不是明文 TLV 的字节 —— 这一条错了，从第 11 轮起全部作废。

### 两个必踩的坑

1. **Intel 网卡的「数据包合并」必须关掉**（`*PacketCoalescing = 0`）。开着它，一轮 3 个小包会被合成一次空口发送，接收端量到的是三包之和 —— 整条长度通道报废。这是"手机能配、Windows 不能配"的真正原因。
   一次性关掉：`powershell -File _refs/apk/set_coalesce.ps1 -Value 0`（会弹 UAC，改完 WLAN 瞬断自连）。
2. **发送期间电脑不能漫游到别的 SSID**。包只从当前关联的 AP 出去，中途换网等于后面全白发自。`gravity_provision.mjs` 和面板都带了看门狗，一旦漂走立刻停止并说明发了多少轮。

## 设备侧接口（取证用）

| 通道 | 用途 |
| --- | --- |
| TCP **7788** | 网络 adb：`node _refs/apk/adb.mjs <ip> 7788 "logcat -d -v time"`（**不要加 `shell ` 前缀**；设备里没有 `tail`/`head`） |
| TCP **7766** | 厂商 HTTP 接口：`Status` / `Info` / `Play` / `Pause` / `Next` / `Prev` / `SetVolume{CurrentVolume}` / `SetEQMode{EQMode}`。字段名必须一字不差，写错照样回 `Success` 但设备没反应 |
| TCP **8888** | `system_daemon`：8 字节命令可回读它真正解到的 SSID/密码（配网成功与否的硬判据）。注意配网窗口只有约 3.4 分钟 |

判"配网成功"只认两条硬证据：目标子网上 7766 有应答，或设备日志里 `commend_ssid` 变成非空。ARP 表里出现它的 MAC **不算**（电脑自己漫游回去时会重新学到老条目）。

## 音源

音响的 WiFi 音源是它自带的 AirPlay 接收端（`libjni_fireair.so`，`softwinner::CAirTunesServer`）。它的 mDNS TXT 是 `et=0,1 md=0,1,2`，也就是**接受明文、支持文字元数据和封面**。
`_verify/raop_send.mjs` 是我们自己的 RAOP 发送端实验：`ANNOUNCE`（SDP 报 `L16/44100/2`）→ `SETUP` → `RECORD` → 往它给的 `server_port` 发 12 字节头 + 裸 PCM，不需要 ALAC 编码也不需要 RSA/AES 密钥交换。

## 隐私

仓库里不含任何厂商固件、APK、odex 或抓包样本，也不含真实网络的 SSID / 密码 / MAC / 内网 IP —— 文档和示例里的 `MyWiFi-A`、`your-wifi-password`、`192.168.1.x` 全是占位。

## 许可证

MIT，见 [LICENSE](LICENSE)。
