param([int]$Seconds = 40, [string]$Ip = '192.168.1.10',
       [string]$Dir = $PSScriptRoot)
# 需要管理员：抓 Windows <-> 音响 的全部包，用来读出 Apple 真实发出的元数据/封面帧。
$etl = Join-Path $Dir 'airplay.etl'
$pcap = Join-Path $Dir 'airplay.pcapng'
Remove-Item $etl, $pcap, (Join-Path $Dir 'pkt_done.txt') -ErrorAction SilentlyContinue
pktmon filter remove | Out-Null
pktmon filter add -a $Ip | Out-Null
pktmon start --capture --pkt-size 0 -f $etl | Out-Null
Write-Output ("capturing " + $Seconds + "s -> " + $etl)
Start-Sleep -Seconds $Seconds
pktmon stop | Out-Null
pktmon etl2pcap $etl -o $pcap | Out-Null
pktmon filter remove | Out-Null
"ok $(Get-Date -Format s)" | Out-File (Join-Path $Dir 'pkt_done.txt') -Encoding ascii
