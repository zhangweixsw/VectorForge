$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$workspace = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$delivery = Join-Path $workspace 'dist/win-x64-v18'
$installerDelivery = Join-Path $workspace 'dist/win-x64-v18-installer'
$assets = Join-Path $workspace 'src/VectorForge/Assets'
$exe = Join-Path $delivery 'VectorForge.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'EXE missing' }
$iconBytes = [System.IO.File]::ReadAllBytes((Join-Path $assets 'VectorForge.ico'))
if ([BitConverter]::ToUInt16($iconBytes,4) -ne 9) { throw 'ICO must contain nine frames' }
$main = [System.Drawing.Bitmap]::new((Join-Path $assets 'VectorForge.png'))
if ($main.Width -ne 1024 -or $main.Height -ne 1024 -or $main.GetPixel(0,0).A -ne 0) { throw 'Master size/alpha mismatch' }
$main.Dispose()
$associated = [System.Drawing.Icon]::ExtractAssociatedIcon($exe)
if ($null -eq $associated) { throw 'EXE icon missing' }
$associated.Dispose()
$sheet = [System.Drawing.Bitmap]::new(1100,600)
$g = [System.Drawing.Graphics]::FromImage($sheet)
$g.Clear([System.Drawing.Color]::FromArgb(244,246,250))
$dark = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(24,29,40))
$g.FillRectangle($dark,0,0,1100,300)
$font = [System.Drawing.Font]::new('Segoe UI',12)
foreach ($row in @(0,1)) {
    $x = 24
    foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
        $icon = [System.Drawing.Image]::FromFile((Join-Path $assets "icon-$size.png"))
        $g.DrawImage($icon,$x,($row*300+10+[int]((256-$size)/2)),$size,$size)
        $brush = if ($row -eq 0) { [System.Drawing.Brushes]::White } else { [System.Drawing.Brushes]::Black }
        $g.DrawString([string]$size,$font,$brush,[single]$x,[single]($row*300+270))
        $x += $size + 40
        $icon.Dispose()
    }
}
$sheet.Save((Join-Path $workspace 'artifacts/v18-icon-preview.png'),[System.Drawing.Imaging.ImageFormat]::Png)
$font.Dispose();$dark.Dispose();$g.Dispose();$sheet.Dispose()
[System.IO.Directory]::CreateDirectory((Join-Path $delivery 'icons')) | Out-Null
foreach ($name in @('VectorForge.png','VectorForge-small.png','VectorForge.ico')) {
    Copy-Item -LiteralPath (Join-Path $assets $name) -Destination (Join-Path $delivery "icons/$name")
}
Copy-Item -LiteralPath (Join-Path $workspace 'README.md') -Destination (Join-Path $delivery '使用说明.md')
Copy-Item -LiteralPath (Join-Path $workspace 'artifacts/v18-verification.md') -Destination (Join-Path $delivery '验证记录.md')
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
[System.IO.File]::WriteAllText((Join-Path $delivery 'SHA256.txt'),"$hash  VectorForge.exe" + [Environment]::NewLine)
$installer = Join-Path $installerDelivery 'VectorForge-Setup-v18.exe'
if (!(Test-Path -LiteralPath $installer)) { throw 'Installer missing' }
$verify = Start-Process -FilePath $installer -ArgumentList '--verify' -Wait -PassThru -WindowStyle Hidden
if ($verify.ExitCode -ne 0) { throw "Installer payload verification failed: $($verify.ExitCode)" }
Copy-Item -LiteralPath (Join-Path $workspace 'README.md') -Destination (Join-Path $installerDelivery '使用说明.md')
Copy-Item -LiteralPath (Join-Path $workspace 'artifacts/v18-verification.md') -Destination (Join-Path $installerDelivery '验证记录.md')
$installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
[System.IO.File]::WriteAllText((Join-Path $installerDelivery 'SHA256.txt'),"$installerHash  VectorForge-Setup-v18.exe" + [Environment]::NewLine)
[pscustomobject]@{PortableEXE=$exe;PortableBytes=(Get-Item -LiteralPath $exe).Length;PortableSHA256=$hash;Installer=$installer;InstallerBytes=(Get-Item -LiteralPath $installer).Length;InstallerSHA256=$installerHash;IcoFrames=9;Version=(Get-Item -LiteralPath $exe).VersionInfo.FileVersion} | Format-List
