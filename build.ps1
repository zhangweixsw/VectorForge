param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\VectorForge\VectorForge.csproj'
$setupProject = Join-Path $PSScriptRoot 'src\VectorForge.Setup\VectorForge.Setup.csproj'
$tests = Join-Path $PSScriptRoot 'tests\VectorForge.SelfTests\VectorForge.SelfTests.csproj'
$publish = Join-Path $PSScriptRoot 'dist\win-x64-v18.0.1'
$setupPublish = Join-Path $PSScriptRoot 'dist\win-x64-v18.0.1-installer'

dotnet restore (Join-Path $PSScriptRoot 'VectorForge.sln')
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败，退出码 $LASTEXITCODE" }
dotnet build (Join-Path $PSScriptRoot 'VectorForge.sln') -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败，退出码 $LASTEXITCODE" }
dotnet run --project $tests -c $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "自测试失败，退出码 $LASTEXITCODE" }
dotnet publish $project -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }

$payload = Join-Path $publish 'VectorForge.exe'
$payloadArgument = "-p:PayloadPath=$payload"
dotnet restore $setupProject -r win-x64 $payloadArgument
if ($LASTEXITCODE -ne 0) { throw "安装程序 restore 失败，退出码 $LASTEXITCODE" }
dotnet publish $setupProject -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true $payloadArgument -o $setupPublish --no-restore
if ($LASTEXITCODE -ne 0) { throw "安装程序 publish 失败，退出码 $LASTEXITCODE" }
Copy-Item -LiteralPath (Join-Path $setupPublish 'VectorForgeSetup.exe') -Destination (Join-Path $setupPublish 'VectorForge-Setup-v18.0.1.exe') -Force
Remove-Item -LiteralPath (Join-Path $setupPublish 'VectorForgeSetup.exe') -Force

Write-Host "`n便携版：$(Join-Path $publish 'VectorForge.exe')"
Write-Host "安装包：$(Join-Path $setupPublish 'VectorForge-Setup-v18.0.1.exe')"
