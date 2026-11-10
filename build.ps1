# Build GitLabMR cho Windows (mặc định) – file exe đơn, tự chứa .NET runtime.
#   ./build.ps1                 -> publish/win-x64/GitLabMR.exe + publish/GitLabMR-win-x64.zip
#   ./build.ps1 -Runtime win-arm64
param([string]$Runtime = "win-x64")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "publish/$Runtime"
$zip = Join-Path $root "publish/GitLabMR-$Runtime.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$root/GitLabMR/GitLabMR.csproj" -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out/*" -DestinationPath $zip
Write-Host "OK -> $out"
Write-Host "Zip -> $zip"
