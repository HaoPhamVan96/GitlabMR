# GitLab MR

App desktop nhỏ gọn để **tạo Merge Request hàng loạt** trên GitLab (và merge luôn nếu muốn) cho nhiều project trong một group, ví dụ `pre-uat → uat` cho tất cả service.

Chạy trên **Windows** và **macOS** (.NET 8 + [Photino](https://www.tryphotino.io/): Windows dùng WebView2, macOS dùng WKWebView).

## Tính năng

- **Nhiều workspace** (tab trên cùng, nút **+**): mỗi workspace là 1 GitLab với link + access token riêng
  (vd. `peoplex` trên gitlab công ty, `peoplex2024` trên GitLab khác). Chuyển tab là hiện ngay, không tải lại.
  Bấm tab đang mở (hoặc nút ⚙ bên phải) để sửa tên, link, token hoặc xoá workspace.
- Trong 1 workspace nhập **nhiều group / repo** (mỗi dòng 1 link, nút **+** để thêm) + Personal Access Token (scope `api`) → app gộp tất cả project, bỏ trùng.
  Group thì lấy hết project bên trong kể cả subgroup; nếu bạn chỉ là member của subgroup/project con (API group trả 404) app vẫn tự tìm project theo quyền Developer+.
- Tab **Favorite / All**: bấm ☆ để lưu repo hay dùng. Mở app là vào thẳng Favorite, repo yêu thích được tick sẵn và tải branch luôn.
- Ẩn sẵn các project library/shared (cấu hình `Exclude` trong config).
- Gợi ý branch thật từ GitLab kèm số project có branch đó (`pre-uat · 12/14 project`), nút ⟳ để tải lại.
- **Kiểm tra** (dry-run): báo từng project Sẵn sàng / Không có thay đổi / Thiếu branch / Đã có MR.
- **Tạo MR** hoặc **Tạo & merge**: dùng lại MR đang mở nếu có, chờ GitLab check mergeable rồi merge;
  pipeline đang chạy thì có thể bật auto-merge. Không merge được thì ghi rõ lý do (conflict, chưa approve, draft…).
- Xem / copy toàn bộ link MR một lần để dán vào chat.
- Token được lưu an toàn: Windows dùng DPAPI, macOS dùng Keychain.

## Tải bản build

Vào tab **Actions** (artifact của mỗi lần build) hoặc **Releases** (khi push tag `v*`):

| File | Máy |
|---|---|
| `GitLabMR-win-x64.zip` | Windows 10/11 x64 (cần Edge WebView2 Runtime – Win 11 có sẵn) |
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
```

```bash
./build.sh                  # macOS Apple Silicon -> publish/osx-arm64/GitLabMR.app
./build.sh osx-x64          # macOS Intel
```

## Phát hành

```bash
git tag v1.0.0 && git push origin v1.0.0
```

GitHub Actions build Windows + macOS và tạo Release kèm file zip.

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
| `GitLabMR/Platform.cs` | Token (DPAPI/Keychain), clipboard, mở link theo từng OS |
| `GitLabMR/ui.html` | Giao diện |
