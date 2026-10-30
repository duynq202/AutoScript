# AutoScript — WPF + Gemini

Ứng dụng Windows viết lại kịch bản theo file `rewrite-script_SKILL.md` đính kèm. Nhập Gemini API key, chọn model, nhập kịch bản gốc, khoảng số từ mong muốn và kịch bản tham khảo. Tự xử lý một mạch và xuất duy nhất một file kịch bản TXT UTF-8.

## Tải bản đầy đủ

Xem [GitHub Releases](https://github.com/duynq202/AutoScript/releases/latest) và tải **AutoScript-win-x64-full.zip**. Giải nén toàn bộ, mở **AutoScript.exe**. Gói này kèm .NET Desktop Runtime và Python 3.12.10 x64, không cần cài riêng. Cần Windows 10/11 x64, Internet và API key Gemini có quota. Có file SHA-256 đi kèm để kiểm tra tải xuống.

Để tự build bản đầy đủ trên Windows với .NET 8 SDK: chạy `powershell -ExecutionPolicy Bypass -File scripts/Package-Windows.ps1`. Script tải Python từ gói NuGet chính thức có phiên bản cố định, kiểm tra bằng interpreter đi kèm và thử khởi động cửa sổ WPF. Kết quả ở `artifacts/AutoScript-win-x64-full.zip`. Workflow `Full Windows release` tự chạy khi đẩy tag `v*`; chỉ đăng Release sau khi build và kiểm tra thành công.

## Chạy trên Windows

Nếu chạy từ mã nguồn: cần Windows 10/11 x64, **.NET 8 SDK** và **Python 3** trong PATH (`py -3`, `python` hoặc `python3`). Không cần thư viện Python bên ngoài. Cần Internet tới `generativelanguage.googleapis.com` và API key Gemini có quota.

```powershell
dotnet run --project AutoScript/AutoScript.csproj
```

Build bản phát hành:

```powershell
dotnet publish AutoScript/AutoScript.csproj -c Release -r win-x64 --self-contained false -o artifacts/AutoScript
```

Chạy `artifacts/AutoScript/AutoScript.exe`. Máy chạy bản phát hành cần **.NET 8 Desktop Runtime x64** và Python 3. Giữ nguyên thư mục `Resources` cạnh executable.

## Sử dụng — phiên bản tự động

1. Nhập API key; nhấn **Tải model**, chọn model có quota hoặc nhập model ID.
2. Dán kịch bản gốc và bản tham khảo hoặc mở TXT UTF-8. Chưa trích xuất DOCX/PDF/SRT.
3. Nhập khoảng số từ hoặc để trống cả hai mốc để dùng số từ bản gốc. Mục tiêu là trung điểm khoảng, từ 600–60.000 từ. Ít nhất ba phần; kiểm tra ±10% từng phần, tổng nằm trong khoảng nhập và ±5% mục tiêu.
4. Chọn đường dẫn file TXT đầu ra (mặc định trong Documents/AutoScript), rồi nhấn **Bắt đầu**. Ứng dụng tự lập outline, học văn phong, viết các phần, tạo bài học nội bộ, rà soát toàn truyện, kiểm tra bằng Python và lưu kết quả. **Không cần duyệt hay nhấn tiếp giữa các bước.**
5. Khi hoàn tất, nhấn **Mở thư mục kết quả**. Chỉ xuất **một file TXT kịch bản hoàn chỉnh**, không tiêu đề, metadata hoặc bài học riêng. Bài học và hồ sơ quản lý vẫn nằm trong thư mục làm việc nội bộ; bài học không tính vào độ dài truyện.

Nếu mất mạng, hết quota hoặc kiểm tra không đạt sau các lượt sửa tự động, ứng dụng báo lỗi. Khắc phục rồi nhấn **Tiếp tục tác vụ bị gián đoạn** để chạy lại bước đang dở; không viết lại các bước đã hoàn tất. Hủy không tạo file đầu ra dở dang. File đầu ra cũ chỉ bị thay thế sau khi bản mới hoàn tất.

Theo yêu cầu mới, các điểm dừng xin duyệt trong skill được thay bằng tự động chuyển bước; các quy tắc sáng tạo, K1–K13, Story Bible, narrative audit và Python vẫn áp dụng. **Kịch bản đầu ra là tiếng Anh Mỹ**. Thay đổi đầu vào hoặc độ dài chỉ áp dụng khi bắt đầu dự án mới. API key và model có thể đổi trước khi tiếp tục tác vụ lỗi.

## Đếm và kiểm tra

Ứng dụng thực sự chạy `Resources/validate_script.py` bằng Python cho bản gốc, từng phần, bài học và bản cuối. Số từ là `len(text.split())`, không phải đếm token hoặc ước lượng của AI. Ký tự dùng `len(text)` (Unicode code points); newline đọc thành LF. Bản gốc đếm toàn bộ văn bản, bản truyện theo quy chuẩn skill bỏ newline ngoài cùng trước kiểm tra.

Kiểm tra chặn: dòng trắng, khoảng trắng đầu/cuối dòng, chữ số, ký tự cấm, từ rút gọn phổ biến, từ viết tắt trong danh sách skill, đoạn quá 110 từ, gạch nối đứng riêng, dấu ba chấm và độ dài. JSON không hợp lệ hoặc phản hồi bị cắt/bị chặn không được nhận. Khi không đạt, yêu cầu AI sửa, tối đa ba lượt; hết lượt thì báo lỗi, giữ các bước hoàn tất để tiếp tục.

K13 được đưa đầy đủ vào system prompt; Gemini phải tự rà soát nội dung và cập nhật Story Bible cùng narrative audit. **AI tự rà soát không phải bảo đảm logic, tính nguyên bản hoặc chất lượng nghệ thuật**. Bạn nên đọc bản cuối; kiểm tra Python chỉ xác nhận các quy tắc kỹ thuật đã lập trình. Không cam kết retention hay điểm chất lượng.

## Dữ liệu và giới hạn

- API key chỉ giữ trong bộ nhớ cửa sổ, gửi qua header `x-goog-api-key`, không ghi file hoặc đưa vào URL. Không có cơ chế ghi log API key.
- Kịch bản, bản tham khảo và hồ sơ continuity được gửi tới Google Gemini. Mỗi lần tạo/sửa có thể phát sinh phí; không tự retry lỗi quota.
- File làm việc lưu tại `%LOCALAPPDATA%/AutoScript/<thời-gian-id>/`: source, reference, outline, style guide, từng phần, lesson, bản cuối, Story Bible và narrative audit. Tự xóa thư mục khi không cần. Chưa có giao diện mở lại dự án sau khi tắt ứng dụng.
- Sau rà soát toàn truyện, `full_script.txt` là bản cuối có thể đã được sửa; các `partN.txt` là bản duyệt trước rà soát.
- Hủy dừng yêu cầu phía ứng dụng, không bảo đảm Google ngừng tính phí. Lỗi mạng, hết quota, thiếu Python hoặc bị cắt do giới hạn model sẽ hiển thị để thử lại; không xuất phần chưa đạt kiểm tra.
- Độ dài lớn cần model đủ context và output quota. Bước rà soát toàn truyện trả lại toàn bộ văn bản, nên bản dài có thể vượt giới hạn đầu ra của model; ứng dụng phát hiện và từ chối phản hồi bị cắt. Chọn model phù hợp hoặc giảm độ dài.
- WPF chỉ chạy trên Windows. Linux có thể cross-build và kiểm tra service/Python, không chạy được giao diện.

## Kiểm thử

```powershell
python -m unittest discover -s tests -v
dotnet run --project tests/CoreChecks/CoreChecks.csproj
dotnet build AutoScript/AutoScript.csproj -c Release
```

CoreChecks dùng HTTP giả lập, chạy Python thật: lọc model, header API key, lỗi HTTP không tiến bước, từ chối phần sai kiểm tra, tiến trình tự động tới xuất bản cuối, đếm chính xác và từ chối phản hồi bị cắt. Không gọi Gemini thật. Cần kiểm thử thủ công giao diện trên Windows và một lượt Gemini với key thật trước khi sử dụng chính thức.
