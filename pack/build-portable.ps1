# Build the portable (no-install) zip. ASCII-only source: Windows PowerShell 5.1 reads a
# BOM-less UTF-8 .ps1 as cp936, so no non-ASCII literals here; the CJK file name inside the
# zip is built from code points instead.
param(
    [Version]$Version = '0.2.0',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'app/GravityPanel/GravityPanel.csproj'
$dist = Join-Path $root 'dist'
$folder = "GravityPP-$Version-win-x64"
$src = Join-Path $dist $folder
$zip = Join-Path $dist "$folder.zip"

if (-not $SkipPublish) {
    if (Test-Path $src) { Remove-Item $src -Recurse -Force }
    dotnet publish $proj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -o $src
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}
if (-not (Test-Path (Join-Path $src 'Gravity++.exe'))) { throw "Gravity++.exe missing in $src" }

Remove-Item (Join-Path $src '*.pdb') -Force -ErrorAction SilentlyContinue

# user-facing notes, named in the target language
$notesName = -join ([char]0x4F7F, [char]0x7528, [char]0x8BF4, [char]0x660E) + '.txt'
$notes = Get-ChildItem -Path (Join-Path $root 'pack') -Filter '*.txt' | Select-Object -First 1
if ($notes) { Copy-Item $notes.FullName (Join-Path $src $notesName) -Force }

Compress-Archive -Path (Join-Path $src '*') -DestinationPath $zip -CompressionLevel Optimal

Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($zip)
$names = @($z.Entries | ForEach-Object { $_.FullName })
$z.Dispose()
$must = @('Gravity++.exe', 'Gravity++.pri', 'GravityPanel.pri', 'GravityPanel.dll',
          'Microsoft.UI.Xaml.Controls.pri', 'Microsoft.WindowsAppRuntime.dll', 'fix-firewall.ps1')
$missing = @($must | Where-Object { $names -notcontains $_ })
if ($missing.Count) { throw "missing from zip: $($missing -join ', ')" }
foreach ($f in (Get-ChildItem $src -Filter '*.txt')) {
    if ($names -notcontains $f.Name) { throw "notes file not at zip root: $($f.Name)" }
}

Write-Output ("folder  = {0}  ({1} MB)" -f $src, [math]::Round((Get-ChildItem $src -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1))
Write-Output ("zip     = {0}  ({1} MB, {2} file entries)" -f $zip,
    [math]::Round((Get-Item $zip).Length / 1MB, 1),
    @($names | Where-Object { $_ -notlike '*/' }).Count)
Write-Output ("sha256  = {0}" -f (Get-FileHash $zip -Algorithm SHA256).Hash)
Write-Output ("checked = {0}" -f ($must -join ', '))
