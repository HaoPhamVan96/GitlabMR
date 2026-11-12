# GitLab MR

App desktop nhỏ gọn để **tạo Merge Request hàng loạt** trên GitLab (và merge luôn nếu muốn) cho nhiều project trong một group, ví dụ `pre-uat → uat` cho tất cả service.

Chạy trên **Windows** và **macOS** (.NET 8 + [Photino](https://www.tryphotino.io/): Windows dùng WebView2, macOS dùng WKWebView).

## Tính năng

- **Nhiều workspace** (tab trên cùng, nút **+**): mỗi workspace là 1 GitLab với link + access token riêng
  (vd. `peoplex` trên gitlab công ty, `peoplex2024` trên GitLab khác). Chuyển tab là hiện ngay, không tải lại.
  Bấm tab đang mở (hoặc nút ⚙ bên phải) để sửa tên, link, token hoặc xoá workspace.
- Trong 1 workspace nhập **nhiều group / repo** (mỗi dòng 1 link, nút **+** để thêm) + Personal Access Token (scope `api`) → app gộp tất cả project, bỏ trùng.
  Group thì lấy hết project bên trong kể cả subgroup; nếu bạn chỉ là member của subgroup/project con (API group trả 404) app vẫn tự tìm project theo quyền Developer+.
- Tab **Favorite / All**: bấm ☆ để lưu repo hay dùng, mở app là vào thẳng Favorite. ☆ và checkbox độc lập: ☆ chỉ đánh dấu yêu thích, checkbox mới là chọn repo để chạy (cả hai đều được lưu riêng).
- Ẩn sẵn các project library/shared (cấu hình `Exclude` trong config).
- Gợi ý branch thật từ GitLab kèm số project có branch đó (`pre-uat · 12/14 project`), nút ⟳ để tải lại.
- **Kiểm tra** (dry-run): báo từng project Sẵn sàng / Không có thay đổi / Thiếu branch / Đã có MR.
- **Tạo MR** hoặc **Tạo & merge**: dùng lại MR đang mở nếu có, chờ GitLab check mergeable rồi merge;
  pipeline đang chạy thì có thể bật auto-merge. Không merge được thì ghi rõ lý do (conflict, chưa approve, draft…).
- **Tạo branch hàng loạt** (chế độ **Branch** trên thanh): nhập branch gốc (vd `prod`) và tên mới (vd `feature/jwt`) → tạo cho mọi repo đã tick; repo thiếu branch gốc hoặc đã có branch đó thì bỏ qua.
- **Gerrit** (chọn loại workspace = Gerrit): đăng nhập username + HTTP password, nhập link prefix (vd `https://review.company.com/peoplex/api/`) hoặc link clone từng project; chỉ hỗ trợ **tạo branch**, chế độ MR bị khoá.
- Xem / copy toàn bộ link kết quả (MR hoặc branch) một lần để dán vào chat.
- **Cache**: danh sách project + branch gợi ý được lưu theo workspace, mở app / đổi tab hiện ngay không gọi server (chấm xám + "x giờ trước" trên nhãn kết nối). Bấm ⟳ để tải lại; cache cũ hơn `CacheMaxAgeHours` (mặc định 12 giờ, 0 = tắt) thì tự làm mới khi mở.
- Token được lưu an toàn: Windows dùng DPAPI, macOS dùng Keychain.
- **Tự cập nhật** (Windows, bản cài bằng `GitLabMR-win-Setup.exe`): app tự kiểm tra GitHub Releases lúc mở và mỗi 6 giờ,
  tải ngầm bản mới. Khi tải xong, header hiện nút **↻ vX.Y.Z**: bấm vào thì app khởi động lại vào bản mới; không bấm thì bản mới được cài khi đóng app
  (nếu còn cửa sổ GitLab MR khác đang mở thì đợi lần đóng sau, không đóng ngang cửa sổ đang chạy).
  macOS / exe chạy trực tiếp: chỉ báo có bản mới, bấm để mở trang tải. Bấm logo để kiểm tra ngay.

## Tải bản build

Vào tab **Actions** (artifact của mỗi lần build) hoặc **Releases** (khi push tag `v*`):

| File | Máy |
|---|---|
| `GitLabMR-win-Setup.exe` | **Windows 10/11 x64 – nên dùng.** Cài vào `%LOCALAPPDATA%\GitLabMR` (không cần admin), tạo shortcut Start Menu + Desktop, tự cập nhật, tự cài WebView2 nếu thiếu. Gỡ trong *Settings → Apps* |
| `GitLabMR-win-Portable.zip` | Windows, không cần cài: giải nén chạy `GitLabMR.exe` (vẫn tự cập nhật) |
| `GitLabMR-osx-arm64.zip` | Mac Apple Silicon (M1/M2/M3…) |
| `GitLabMR-osx-x64.zip` | Mac Intel |

**macOS:** app chưa được Apple notarize, lần đầu mở bấm chuột phải → **Open**, hoặc chạy:

```bash
xattr -dr com.apple.quarantine /Applications/GitLabMR.app
```

## Build từ source

Cần .NET SDK 8+.

```powershell
./build.ps1                 # Windows -> publish/win-x64/GitLabMR.exe
./build.ps1 -Pack           # + gói Velopack ở publish/velopack (Setup.exe, Portable.zip, .nupkg, releases.win.json)
./build.ps1 -Install        # bản dev: cài vào %LOCALAPPDATA%\Programs\GitLabMR (không tự cập nhật)
```

```bash
./build.sh                  # macOS Apple Silicon -> publish/osx-arm64/GitLabMR.app
./build.sh osx-x64          # macOS Intel
```

**Ghim taskbar:** cài bằng `GitLabMR-win-Setup.exe`, rồi Start → gõ "GitLab MR" → chuột phải → *Pin to taskbar*.
Shortcut trỏ vào launcher cố định của Velopack, nên sau mỗi lần tự cập nhật icon trên taskbar vẫn mở bản mới nhất.
(Bản dev `./build.ps1 -Install` cũng giữ đường dẫn cố định, nhưng không tự cập nhật.)

## Phát hành

```bash
git tag v1.6.0 && git push origin v1.6.0
```

GitHub Actions lấy version từ tag, build Windows (`vpk pack` → Setup.exe, Portable.zip, `.nupkg`, `releases.win.json`) + macOS (zip),
rồi tạo Release và đặt là *latest*. App tự cập nhật bằng cách đọc
`https://github.com/HaoPhamVan96/GitlabMR/releases/latest/download/releases.win.json`, không qua GitHub API nên không bị giới hạn số lần gọi theo IP.
Vì vậy **release mới nhất phải luôn có đủ các file feed đó**: đừng tạo release tay không kèm file. Nếu cần bản nháp, tạo pre-release.

Test cập nhật với feed khác: đặt biến môi trường `GITLABMR_UPDATE_URL` (URL hoặc thư mục chứa `releases.win.json`) rồi mở app.

## Cấu hình

- Windows: `%APPDATA%\GitLabMR\config.json`
- macOS: `~/.config/GitLabMR/config.json`

Sửa `Exclude` để đổi danh sách project bị ẩn (so khớp chuỗi con trong đường dẫn project).

## Cấu trúc

| File | Nội dung |
|---|---|
| `GitLabMR/Program.cs` | Tạo cửa sổ Photino, nạp `ui.html` |
| `GitLabMR/AppHost.cs` | Cầu nối JS ↔ C#: kết nối, load branch, chạy batch |
| `GitLabMR/MrWorkflow.cs` | Luồng 1 project: check branch → compare → tạo/dùng lại MR → merge |
| `GitLabMR/GitLabClient.cs` | GitLab REST API v4 |
| `GitLabMR/GerritClient.cs` | Gerrit REST API (liệt kê project, tạo branch) |
| `GitLabMR/Updater.cs` | Tự cập nhật (Velopack) / báo có bản mới |
| `GitLabMR/Platform.cs` | Token (DPAPI/Keychain), clipboard, mở link theo từng OS |
| `GitLabMR/ui.html` | Giao diện |
