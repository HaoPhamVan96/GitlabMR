# Build GitLabMR cho Windows (mặc định) – file exe đơn, tự chứa .NET runtime.
#   ./build.ps1                 -> publish/win-x64/GitLabMR.exe + publish/GitLabMR-win-x64.zip
#   ./build.ps1 -Install        -> build xong cài vào %LOCALAPPDATA%\Programs\GitLabMR (đường dẫn cố định)
#                                  + shortcut Start Menu / Desktop. Ghim shortcut lên taskbar 1 lần,
#                                  các lần build -Install sau sẽ đè đúng file đó -> taskbar luôn mở bản mới nhất.
#   ./build.ps1 -Runtime win-arm64
param(
    [string]$Runtime = "win-x64",
    [switch]$Install,
    [switch]$NoDesktopShortcut
)

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

if (-not $Install) { return }

# ---------------------------------------------------------------- cài vào chỗ cố định
$installDir = Join-Path $env:LOCALAPPDATA "Programs\GitLabMR"
$target = Join-Path $installDir "GitLabMR.exe"
New-Item -ItemType Directory -Force $installDir | Out-Null

# Dọn các bản cũ đã đổi tên ở lần trước (bản nào còn đang chạy thì để lần sau)
Get-ChildItem $installDir -Filter "*.old" -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item $_.FullName -Force -ErrorAction Stop } catch { }
}

# App đang mở thì exe bị khoá: Windows vẫn cho đổi tên -> đổi sang .old rồi chép bản mới vào
if (Test-Path $target) {
    try { Remove-Item $target -Force -ErrorAction Stop }
    catch { Rename-Item $target ("GitLabMR.exe.{0}.old" -f (Get-Date -Format "yyyyMMddHHmmss")) }
}
Copy-Item (Join-Path $out "GitLabMR.exe") $target -Force
$version = (Get-Item $target).VersionInfo.ProductVersion

# ---------------------------------------------------------------- shortcut (icon lấy từ exe)
function New-Shortcut([string]$path) {
    $ws = New-Object -ComObject WScript.Shell
    $s = $ws.CreateShortcut($path)
    $s.TargetPath = $target
    $s.WorkingDirectory = $installDir
    $s.IconLocation = "$target,0"
    $s.Description = "GitLab MR - tạo / merge MR và tạo branch hàng loạt"
    $s.Save()
}
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\GitLab MR.lnk"
New-Shortcut $startMenu
if (-not $NoDesktopShortcut) { New-Shortcut (Join-Path ([Environment]::GetFolderPath("Desktop")) "GitLab MR.lnk") }

Write-Host ""
Write-Host "Đã cài v$version -> $target"
Write-Host "Shortcut: Start Menu$(if (-not $NoDesktopShortcut) { ' + Desktop' })"
Write-Host "Ghim taskbar (1 lần): Start -> gõ 'GitLab MR' -> chuột phải -> Pin to taskbar."
Write-Host "Nếu app đang mở: tắt và mở lại để dùng bản mới."
