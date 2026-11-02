$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$version = '18.0.1'
$out = Join-Path $root 'dist/release-v18.0.1'
$stage = Join-Path $PSScriptRoot 'staging'
New-Item -ItemType Directory -Force -Path $out,$stage | Out-Null
$base = "https://mootop.top/wp-content/uploads/vectorforge"
$names = @{Installer="VectorForge-v$version-Windows-x64-Installer.zip";Portable="VectorForge-v$version-Windows-x64-Portable.zip"}
foreach($type in @('Installer','Portable')) {
    $directory = Join-Path $stage $type
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    if($type -eq 'Installer') { Copy-Item -LiteralPath (Join-Path $root 'dist/win-x64-v18.0.1-installer/VectorForge-Setup-v18.0.1.exe') -Destination $directory }
    else { Copy-Item -LiteralPath (Join-Path $root 'dist/win-x64-v18.0.1/VectorForge.exe') -Destination $directory }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE.txt') -Destination $directory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'guide.md') -Destination (Join-Path $directory '使用指南.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'story.md') -Destination (Join-Path $directory '开发手记.md')
    $runtime = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget/packages/microsoft.netcore.app.runtime.win-x64') -Recurse -Filter 'THIRD-PARTY-NOTICES.TXT' | Select-Object -First 1
    if($runtime) { Copy-Item -LiteralPath $runtime.FullName -Destination (Join-Path $directory 'THIRD-PARTY-NOTICES.txt') }
    $zip = Join-Path $out $names[$type]
    if(Test-Path -LiteralPath $zip){throw "Versioned ZIP already exists: $zip"}
    Compress-Archive -Path (Join-Path $directory '*') -DestinationPath $zip -CompressionLevel Optimal
}
$sums = foreach($type in @('Installer','Portable')){ $file=Join-Path $out $names[$type]; "$( (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant())  $($names[$type])" }
$sums | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding utf8
$zip = Join-Path $out $names.Installer
$exe = Join-Path $root 'dist/win-x64-v18.0.1-installer/VectorForge-Setup-v18.0.1.exe'
$payload = [ordered]@{Product='vectorforge';Platform='win-x64';Version=$version;Url="$base/$($names.Installer)";Bytes=(Get-Item -LiteralPath $zip).Length;Sha256=(Get-FileHash -LiteralPath $zip).Hash;Entry='VectorForge-Setup-v18.0.1.exe';InstallerSha256=(Get-FileHash -LiteralPath $exe).Hash}
$bytes=[Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Compress))
$rsa=[Security.Cryptography.RSA]::Create();$rsa.ImportFromPem([IO.File]::ReadAllText((Join-Path $env:USERPROFILE '.ssh/vectorforge-update-signing.pem')))
$envelope=@{payload=[Convert]::ToBase64String($bytes);signature=[Convert]::ToBase64String($rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1))}
$envelope | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $out 'update.json') -Encoding utf8
$rsa.Dispose()
Get-ChildItem -LiteralPath $out | Select-Object Name,Length
