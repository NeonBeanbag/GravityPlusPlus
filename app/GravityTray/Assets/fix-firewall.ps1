param(
    [int]$Port = 8123,
    [string]$Exe = ''
)
# 修 Windows 防火墙：程序第一次监听时如果放行弹窗被忽略，系统会给它生成一条 **Block** 规则，
# 而 Block 优先于任何 Allow —— 表现就是"UPnP 那边一切正常，但本机 HTTP 一次请求都收不到"。
$log = Join-Path $env:TEMP 'gravity_fw_fix.txt'
$out = @()
if ($Exe -eq '') { $Exe = (Get-Process -Id $PID -ErrorAction SilentlyContinue).Path }

$blk = @(Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | Where-Object { $_.Program -and $_.Program.ToLower() -eq $Exe.ToLower() } |
        ForEach-Object { $_ | Get-NetFirewallRule -ErrorAction SilentlyContinue } | Where-Object { $_.Action -eq 'Block' })
$out += "block rules for exe: $($blk.Count)"
foreach ($r in $blk) { $out += ("delete: " + $r.DisplayName); Remove-NetFirewallRule -Name $r.Name -ErrorAction SilentlyContinue }

$name = "Gravity++ push stream inbound $Port"
netsh advfirewall firewall delete rule name="$name" | Out-Null
$out += (netsh advfirewall firewall add rule name="$name" dir=in action=allow protocol=TCP localport=$Port program="$Exe" profile=any | Out-String)

# 复核：把和这个 exe 有关的规则列成 ASCII，便于程序解析
$left = @(Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue | Where-Object { $_.Program -and $_.Program.ToLower() -eq $Exe.ToLower() } |
        ForEach-Object { $_ | Get-NetFirewallRule -ErrorAction SilentlyContinue } |
        ForEach-Object { "$($_.Action)/$($_.DisplayName)" })
$out += "AFTER: $($left -join ' , ')"
$out | Out-File -FilePath $log -Encoding ASCII
Write-Output ($out -join "`n")
