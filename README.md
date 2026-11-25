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
- **Gerrit** (chọn loại workspace = Gerrit): đăng nhập username + HTTP password, nhập link prefix (vd `https://review.company.com/peoplex/api/`) hoặc link clone từng project.
  - Chế độ **Branch**: tạo branch hàng loạt như GitLab.
  - Chế độ **MR**: tick repo (không tick = mọi repo trong tab) → nhập branch đích (vd `uat`, để trống = mọi branch) → **Tải change**. Change đang mở hiện dưới từng repo, kèm nhãn CR / Verified và trạng thái
    (*Submit được*, *Cần +2*, *Thiếu Verified*, *Bị CR −2*, *Có CR −1*, *WIP*, *Conflict*). App chọn sẵn change submit được hoặc chỉ còn thiếu +2.
    Bấm **Submit**: mỗi change được Code-Review +2 (nếu chưa có) rồi submit. Còn comment chưa resolve thì app trả lời "Done" và resolve trước khi submit. Khác repo chạy song song, cùng repo chạy lần lượt.
    Change nào không submit được thì báo rõ lý do Gerrit trả về (vẫn giữ +2). Cần quyền Code-Review +2 và Submit trên branch.
  - **Auto submit** (bấm nút ⚙ ở góc phải, phần dưới của panel cài đặt workspace Gerrit): cứ mỗi 60 giây (đổi được, tối thiểu 10 giây), app quét change đang mở của các **account dev** đã nhập.
    Quét nhanh: giữa các lần quét đầy đủ (5 phút 1 lần) app chỉ hỏi Gerrit change mới cập nhật, nên đặt 10–15 giây vẫn nhẹ. Tạo / merge MR GitLab và theo dõi pipeline chạy ở luồng riêng: submit xong chạy ngay, còn việc chờ thì 10 giây kiểm tra 1 lần.
    Change nào **reviewer** (vd `khoavn2`) đã Code-Review +1 thì app tự +2 & submit. App bỏ qua change WIP, change có phiếu âm, và change còn thiếu Verified / điều kiện khác (xét lại ở lượt sau).
    Submit xong, nếu có chọn workspace GitLab: app chờ code sync sang GitLab, rồi tạo MR (hoặc dùng lại MR đang mở) `{branch}` → branch đích (vd `testing2`) và merge nếu bật **Merge**.
    Conflict hoặc lỗi được báo qua **Zalo Bot** (nút **Cấu hình…** mở popup: bot token + tối đa 3 nơi nhận (nhóm / người) có đặt tên; **+ Lấy chat ID**: @ bot trong nhóm hoặc nhắn riêng cho bot; **Gửi thử** gửi tới tất cả). Mỗi patchset chỉ được xử lý 1 lần: dev đẩy patchset mới thì app xét lại.
    Gerrit đã submit mà GitLab chưa merge xong (không có commit mới, MR chưa merge, pipeline đang chạy) thì luôn báo ⏳. Bật **Báo kết quả pipeline sau khi merge**: merge xong app theo dõi pipeline trên branch đích, pass / fail thì báo kèm link.
    **Auto-fix conflict testing2** (chỉ hiện khi branch đích là `testing2`, theo quy trình skill `ppx-merge-testing`): MR bị conflict thì app tạo `{branch}_fix_conflict` từ `testing2`,
    merge branch nguồn vào (branch feature giữ sạch, không merge môi trường vào), gọi **Claude Code CLI** (`claude -p`, ô **model** chọn `opus` / `sonnet`…, trống = mặc định) sửa đúng các file conflict, đánh giá độ nặng / độ tự tin (không build, không trừ điểm vì không build; chỉ lệch khoảng trắng = Nhẹ),
    rồi commit `merge branch {branch} to testing2` (author = git user của máy) và push branch đó. Không có conflict Nặng, tự tin ≥ 70% thì tạo MR & merge, ngược lại chỉ tạo MR và báo cần duyệt.
    Báo 2 lần: có conflict (kèm danh sách file) và kết quả fix. Branch fix cũ đã có trên GitLab (kể cả commit khác) thì app tạo lại từ đầu và ghi đè. Cần cài Claude Code và đăng nhập 1 lần (chạy `claude` trong terminal), bấm **Kiểm tra** để thử.
    **Tự resolve comment** (mặc định bật): reviewer đã +1 mà còn comment chưa resolve thì app trả lời "Done" cho các comment viết trước lúc +1 rồi submit và báo Zalo; comment mới hơn lúc +1 thì không đụng, báo ⏳ và chờ dev resolve. Tắt thì chỉ báo ⏳.
    **Fix conflict qua Zalo:** tin báo conflict (chưa tự merge được) kèm **mã fix** 6 ký tự (dùng 1 lần, hết hạn sau 3 ngày). Member fix trên 1 branch riêng (không merge môi trường vào branch feature), push lên GitLab rồi reply bot `<branch> <mã>`:
    app kiểm tra branch còn dấu conflict không, tạo / dùng lại MR branch → môi trường, merge, báo kết quả và theo dõi pipeline. Chỉ nhận lệnh từ nhóm / người đã cấu hình trong Zalo Bot. Tag / reply bot mà sai cú pháp thì bot đáp lại đanh đá (câu ngẫu nhiên, tag lần nào đáp lần đó; tắt trong popup Zalo Bot).
    **Tự +1 khi reviewer −2 vì từ khoá** (mặc định `IgnoreQueryFilter`): reviewer ở trên cho −2 trên change của account dev, lý do (tin nhắn / comment của reviewer) có từ khoá đó thì app Code-Review +1 và báo Zalo.
    **Chặn file Excel sinh bằng code** (mặc định bật): change đang mở trong các repo của tab có file `.xlsx` / `.xlsm` mà không có thông tin ứng dụng hoặc do Openpyxl / thư viện khác tạo (chưa mở Excel save lại – Black Duck nhận nhầm thành thư viện, vd `excel_writer`):
    app Code-Review −2, comment vào từng file nhắc mở bằng Excel → Save → push lại, báo Zalo; patchset mới đã save lại thì tự gỡ −2. Combo **quét repo** chọn nhiều repo cần quét (không chọn = mọi repo của tab).
    Panel Auto chia 3 tab: **Gerrit** (reviewer, dev, branch, quy tắc tự động), **GitLab** (workspace, branch, merge, conflict testing2), **Thông báo** (Zalo Bot, báo những gì); panel tự cuộn khi cửa sổ thấp.
    Bật **Chạy thử** để chỉ ghi vào Nhật ký những gì sẽ làm. Auto chỉ chạy khi app đang mở; tab Gerrit có Auto đang bật hiện dấu ⚡.
- Xem / copy toàn bộ link kết quả (MR hoặc branch) một lần để dán vào chat.
- **Cache**: danh sách project + branch gợi ý được lưu theo workspace, mở app / đổi tab hiện ngay không gọi server (chấm xám + "x giờ trước" trên nhãn kết nối). Bấm ⟳ để tải lại; cache cũ hơn `CacheMaxAgeHours` (mặc định 12 giờ, 0 = tắt) thì tự làm mới khi mở.
- Token được lưu an toàn: Windows dùng DPAPI, macOS dùng Keychain.
- **Chạy nền ở khay hệ thống** (Windows): bấm ✕ thì cửa sổ ẩn xuống khay, app vẫn chạy Auto / Zalo / theo dõi pipeline. Bấm icon ở khay: mini popup trạng thái Auto, việc đang chờ, sự kiện gần đây (bấm để mở link), Mở / Quét ngay / Tạm dừng Auto / Thoát hẳn. Mở app lần nữa thì hiện lại cửa sổ đang ẩn.
- **1 máy chỉ chạy 1 app**: mở GitLab MR lần nữa thì cửa sổ đang chạy được đưa lên trước (tránh 2 app cùng chạy Auto: submit / merge / báo Zalo trùng).
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
| `GitLabMR/GerritClient.cs` | Gerrit REST API (liệt kê project, tạo branch, change: tải / +2 / submit) |
| `GitLabMR/GerritReview.cs` | Gerrit chế độ MR: tải change đang mở, +2 & submit hàng loạt |
| `GitLabMR/AutoRunner.cs` | Auto submit: quét định kỳ, reviewer +1 → +2 & submit → tạo / merge MR GitLab |
| `GitLabMR/ConflictFixer.cs` | Auto-fix conflict testing2: branch `_fix_conflict`, gọi Claude Code CLI sửa, commit & push |
| `GitLabMR/XlsxGuard.cs` | Chặn file Excel sinh bằng code trên Gerrit (−2 + comment, save lại thì gỡ −2) |
| `GitLabMR/ZaloBot.cs` | Nghe tin nhắn Zalo Bot: lệnh `<branch> <mã fix>` merge branch fix conflict, Lấy chat ID |
| `GitLabMR/Zalo.cs` | Gửi thông báo Zalo Bot (sendMessage, lấy chat ID qua getUpdates) |
| `GitLabMR/Updater.cs` | Tự cập nhật (Velopack) / báo có bản mới |
| `GitLabMR/Platform.cs` | Token (DPAPI/Keychain), clipboard, mở link theo từng OS |
| `GitLabMR/ui.html` | Giao diện |
