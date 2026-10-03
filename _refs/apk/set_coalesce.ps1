param([int]$Value = 0)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Output ("shell is admin: " + $admin)
if (-not $admin) {
    Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File', $PSCommandPath, '-Value', "$Value" -Wait
    exit
}
$kw = '*PacketCoalescing'
$before = (Get-NetAdapterAdvancedProperty -Name WLAN | Where-Object RegistryKeyword -eq $kw | Select-Object -ExpandProperty DisplayValue)
Write-Output ("before: " + $before)
Set-NetAdapterAdvancedProperty -Name WLAN -RegistryKeyword $kw -RegistryValue $Value -NoRestart
Start-Sleep -Seconds 3
$after = (Get-NetAdapterAdvancedProperty -Name WLAN | Where-Object RegistryKeyword -eq $kw | Select-Object -ExpandProperty DisplayValue)
Write-Output ("after : " + $after)
Get-NetAdapter -Name WLAN | Select-Object Name, Status, LinkSpeed | Format-Table -AutoSize | Out-String -Width 80
