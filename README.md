# AutoScript — WPF + Gemini

Ứng dụng Windows viết lại kịch bản theo file `rewrite-script_SKILL.md` đính kèm. Nhập Gemini API key, chọn model, nhập kịch bản gốc, khoảng số từ mong muốn và kịch bản tham khảo. Xuất kịch bản TXT UTF-8 và bài học riêng.

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

## Sử dụng

1. Nhập API key; nhấn **Tải model**, chọn model hỗ trợ `generateContent`. Cũng có thể nhập model ID thủ công. Danh sách phụ thuộc API và quyền của key; một model được liệt kê vẫn có thể chưa có quota.
2. Dán kịch bản gốc và bản tham khảo hoặc mở TXT UTF-8. Bản đầu hỗ trợ TXT và văn bản dán, chưa trích xuất DOCX/PDF/SRT.
3. Nhập cả hai mốc số từ hoặc để trống cả hai để dùng số từ bản gốc. Mục tiêu là trung điểm khoảng. Giới hạn hiện tại: 600–60.000 từ. Ít nhất ba phần, khoảng 1.500 từ/phần; kiểm tra ±10% từng phần, tổng nằm trong cả khoảng nhập và ±5% mục tiêu. Bài học 180–220 từ không tính trong độ dài kịch bản.
4. **Bắt đầu**, đọc và duyệt outline. Nhấn **Duyệt và tiếp tục** để tạo Style Guide; duyệt lần nữa để viết phần một. Mỗi lượt chỉ viết một phần, chờ bạn duyệt. Có thể nhập góp ý và tạo lại bước hiện tại.
5. Sau phần cuối, duyệt để tạo bài học, sau đó duyệt để rà soát toàn truyện. Đọc bản cuối và nhấn **Xuất file TXT**. Kết quả: tên file bạn chọn và `<tên>_lesson.txt`. File truyện không chứa tiêu đề, metadata hoặc bài học.

Theo skill, **kịch bản đầu ra là tiếng Anh Mỹ**, sáng tạo tuyến truyện mới từ mô típ trừu tượng; bản tham khảo chỉ dùng để học đặc điểm văn phong chung. Thay đổi ở tab đầu vào sau khi bắt đầu không thay đổi dữ liệu dự án hiện tại; hãy bắt đầu dự án mới để dùng đầu vào hoặc độ dài mới. Key và model có thể đổi giữa các lượt.

## Đếm và kiểm tra

Ứng dụng thực sự chạy `Resources/validate_script.py` bằng Python cho bản gốc, từng phần, bài học và bản cuối. Số từ là `len(text.split())`, không phải đếm token hoặc ước lượng của AI. Ký tự dùng `len(text)` (Unicode code points); newline đọc thành LF. Bản gốc đếm toàn bộ văn bản, bản truyện theo quy chuẩn skill bỏ newline ngoài cùng trước kiểm tra.

Kiểm tra chặn: dòng trắng, khoảng trắng đầu/cuối dòng, chữ số, ký tự cấm, từ rút gọn phổ biến, từ viết tắt trong danh sách skill, đoạn quá 110 từ, gạch nối đứng riêng, dấu ba chấm và độ dài. JSON không hợp lệ hoặc phản hồi bị cắt/bị chặn không được nhận. Khi không đạt, yêu cầu AI sửa, tối đa ba lượt; hết lượt thì báo lỗi, giữ bước duyệt trước để thử lại.

K13 được đưa đầy đủ vào system prompt; Gemini phải tự rà soát nội dung và cập nhật Story Bible cùng narrative audit. **AI tự rà soát không phải bảo đảm logic, tính nguyên bản hoặc chất lượng nghệ thuật**. Bạn cần đọc duyệt; kiểm tra Python chỉ xác nhận các quy tắc kỹ thuật đã lập trình. Không cam kết retention hay điểm chất lượng.

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

CoreChecks dùng HTTP giả lập, chạy Python thật: lọc model, header API key, lỗi HTTP không tiến bước, từ chối phần sai kiểm tra, tiến trình duyệt tới xuất bản cuối, đếm chính xác và từ chối phản hồi bị cắt. Không gọi Gemini thật. Cần kiểm thử thủ công giao diện trên Windows và một lượt Gemini với key thật trước khi sử dụng chính thức.
