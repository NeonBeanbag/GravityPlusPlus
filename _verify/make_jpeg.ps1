param([string]$Out = "$PSScriptRoot\test_cover.jpg",
       [int]$Size = 300)
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap($Size, $Size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.FillRectangle([System.Drawing.Brushes]::DodgerBlue, 0, 0, $Size, $Size)
$g.FillEllipse([System.Drawing.Brushes]::Yellow, ($Size*0.25), ($Size*0.25), ($Size*0.5), ($Size*0.5))
$g.DrawString("GRAVITY RAOP", [System.Drawing.Font]::new("Arial", 16), [System.Drawing.Brushes]::Black, 20, ($Size - 40))
$g.Dispose()
$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq "image/jpeg" }
$ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
$ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]92)
$bmp.Save($Out, $codec, $ep)
$bmp.Dispose()
Write-Output ("saved " + $Out + " " + (Get-Item $Out).Length + " bytes")
