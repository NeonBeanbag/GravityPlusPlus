param(
    [int]$Port = 8123,
    [string]$Remove = ''
)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$name = "Gravity++ 推流入站 $Port"
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File', $PSCommandPath, '-Port', "$Port", '-Remove', "$Remove" -Wait
    exit
}
if ($Remove -eq 'y') {
    netsh advfirewall firewall delete rule name="$name" | Out-String | Write-Output
    Write-Output "已删除规则：$name"
    exit
}
$node = (Get-Command node.exe -ErrorAction SilentlyContinue).Source
Write-Output ("node 路径: " + $node)
# 先删同名规则，避免重复叠加
netsh advfirewall firewall delete rule name="$name" > $null 2>&1
if ($node) {
    netsh advfirewall firewall add rule name="$name" dir=in action=allow protocol=TCP localport=$Port program="$node" profile=any | Out-String | Write-Output
} else {
    netsh advfirewall firewall add rule name="$name" dir=in action=allow protocol=TCP localport=$Port profile=any | Out-String | Write-Output
}
netsh advfirewall firewall show rule name="$name" | Select-String -Pattern 'Rule Name|Local port|Program path|Action|方向|操作|本地端口|程序路径' | ForEach-Object { $_.Line }
