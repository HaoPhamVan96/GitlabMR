# Build GitLabMR cho Windows (mặc định) – file exe đơn, tự chứa .NET runtime.
#   ./build.ps1                 -> publish/win-x64/GitLabMR.exe + publish/GitLabMR-win-x64.zip
#   ./build.ps1 -Install        -> build xong cài vào %LOCALAPPDATA%\Programs\GitLabMR (đường dẫn cố định)
#                                  + shortcut Start Menu / Desktop. Ghim shortcut lên taskbar 1 lần,
#                                  các lần build -Install sau sẽ đè đúng file đó -> taskbar luôn mở bản mới nhất.
#   ./build.ps1 -Pack -Version 1.6.0
#                               -> thêm gói Velopack ở publish/velopack: GitLabMR-win-Setup.exe (bộ cài, tự cập nhật),
#                                  GitLabMR-<ver>-full.nupkg + releases.win.json (feed cập nhật, đưa lên GitHub Release)
#   ./build.ps1 -Runtime win-arm64
param(
    [string]$Runtime = "win-x64",
    [string]$Version,
    [switch]$Pack,
    [switch]$Install,
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "publish/$Runtime"
$zip = Join-Path $root "publish/GitLabMR-$Runtime.zip"
$VpkVersion = "1.2.161"   # giữ trùng version gói Velopack trong GitLabMR.csproj

if (-not $Version) { $Version = ([xml](Get-Content "$root/GitLabMR/GitLabMR.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
$Version = $Version.TrimStart('v')

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$root/GitLabMR/GitLabMR.csproj" -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out/*" -DestinationPath $zip
Write-Host "OK -> $out"
Write-Host "Zip -> $zip"

# ---------------------------------------------------------------- gói Velopack (bộ cài + feed tự cập nhật)
if ($Pack) {
    $vpk = (Get-Command vpk -ErrorAction SilentlyContinue).Source
    if (-not $vpk) {
        $toolDir = Join-Path $root ".tools"
        $vpk = Join-Path $toolDir "vpk.exe"
        if (-not (Test-Path $vpk)) {
            dotnet tool install vpk --version $VpkVersion --tool-path $toolDir
            if ($LASTEXITCODE -ne 0) { throw "Không cài được vpk" }
        }
    }
    $rel = Join-Path $root "publish/velopack"
    if (Test-Path $rel) { Remove-Item $rel -Recurse -Force }
    $rid = $Runtime
    & $vpk pack --packId GitLabMR --packVersion $Version --packDir $out --mainExe GitLabMR.exe `
        --packTitle "GitLab MR" --packAuthors "HaoPhamVan96" --icon "$root/GitLabMR/assets/icon.ico" `
        --channel ($(if ($rid -eq "win-x64") { "win" } else { $rid })) --runtime $rid `
        --framework webview2 --delta None --outputDir $rel
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }
    Write-Host "Velopack -> $rel"
}

if (-not $Install) { return }

# ---------------------------------------------------------------- cài vào chỗ cố định (bản dev, KHÔNG tự cập nhật)
if (Test-Path (Join-Path $env:LOCALAPPDATA "GitLabMR\current")) {
    Write-Warning "Máy đã cài bản Setup.exe (tự cập nhật) ở %LOCALAPPDATA%\GitLabMR. -Install sẽ ghi đè shortcut 'GitLab MR' sang bản dev."
}
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
