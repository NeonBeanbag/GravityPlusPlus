$log = "$PSScriptRoot\rule_result.txt"
$exe = "$PSScriptRoot\..\..\app\GravityPanel\bin\x64\Debug\net9.0-windows10.0.19041.0\GravityPanel.exe"
$name = 'Gravity++ push stream inbound 8123'
netsh advfirewall firewall delete rule name="$name" | Out-Null
$a = netsh advfirewall firewall add rule name="$name" dir=in action=allow protocol=TCP localport=8123 program="$exe" profile=any
$b = netsh advfirewall firewall add rule name="$name port" dir=in action=allow protocol=TCP localport=8123 profile=any | Out-String
($a | Out-String) + "`n" + $b | Out-File -FilePath $log -Encoding UTF8
