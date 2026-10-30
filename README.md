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

## Bản v1.1.4 — nhiều key, tác vụ nhỏ và tự động theo skill

Nhập từng Gemini API key vào ô được che, nhấn **Thêm key vào danh sách**; lặp lại để thêm các key bạn có quyền sử dụng. Có thể dùng ngay một key đang nhập mà không thêm. Danh sách chỉ hiện Key số một, hai…; không ghi key vào file, nhật ký hoặc lưu mặc định. Key trùng bị loại. Xóa key đã chọn bằng nút tương ứng.

Chọn tối đa **1–4 luồng API**, **khoảng nghỉ 1/2/3/5/10 giây** (mặc định hai giây) và **500/750/1.000/1.500 từ mỗi tác vụ viết** (mặc định bảy trăm năm mươi). Số yêu cầu đồng thời thực tế không vượt số key khác nhau; mỗi key chỉ có một yêu cầu hoạt động. Có khoảng nghỉ cả giữa lúc bắt đầu yêu cầu và sau khi thao tác hoàn tất. Lỗi dịch vụ tiếp tục áp dụng backoff và Retry-After.

Khi có ít nhất hai key và chọn ít nhất hai luồng, **phân tích văn phong tham khảo và tạo outline/Story Bible** chạy độc lập song song. Style Guide được áp dụng sau outline; **các phần truyện vẫn viết lần lượt** theo trạng thái nhân vật, bằng chứng và phần trước. Không viết đồng thời các phần phụ thuộc nhau. Khi chỉ dùng một key/một luồng, chạy tuần tự như trước. Nếu một tác vụ chuẩn bị lỗi không phục hồi được, tác vụ chuẩn bị còn lại được hủy và chờ dừng hẳn trước khi báo lỗi.

Key được luân phiên theo lượt. Key bị HTTP 401/403 được loại khỏi phiên xử lý và thử key khác; hết key có quyền thì báo lỗi. Key gặp lỗi dịch vụ được nghỉ trước khi dùng lại. Không đổi key để tự lặp lỗi quota 429. Nhiều key **cùng dự án Google thường dùng chung quota**; nhiều luồng/key không bảo đảm hết lỗi 503, và quá nhiều yêu cầu có thể làm quá tải nặng hơn. Các yêu cầu có thể tính phí trên dự án tương ứng.

Skill đính kèm đã được đối chiếu, giữ nguyên bản tham chiếu. Các yêu cầu người dùng ưu tiên trong ứng dụng: **không xin xác nhận từng bước, độ dài là kế hoạch tham khảo và không viết lại chỉ để rút ngắn**. Phần dài hơn dự tính được giữ nếu đạt nội dung/định dạng; dự toán cho phần còn lại được điều chỉnh nhưng không cắt beat hoặc bỏ kết truyện. Tổng có thể dài hơn khoảng đã nhập và vẫn xuất file, với số từ thực do Python đếm. Phần quá ngắn, lỗi logic hay ký tự cấm vẫn cần sửa. Đoạn văn quá một trăm mười từ được chia dòng cục bộ, không thêm/bớt hoặc viết lại từ ngữ. Phản hồi bị API cắt giữa chừng vẫn chưa phải phần hoàn chỉnh và không được chấp nhận.

Nhấn **Bắt đầu** một lần: chuẩn bị → viết đủ các phần → bài học nội bộ → rà soát → ghép cục bộ → **một file TXT kịch bản hoàn chỉnh**. Không có bước duyệt. Các cảnh báo độ dài và tiến độ ở giao diện/nhật ký, không chèn vào kịch bản. Kiểm tra Python của phần đã xác nhận được nhớ lại trong phiên, tránh chạy lặp tiến trình cho cùng văn bản.

## Bản sửa v1.1.3 — tự phục hồi HTTP 503

Lỗi 503 nghĩa là Gemini hoặc máy chủ trung gian tạm thời không phục vụ được yêu cầu; không đồng nghĩa key sai. Tự thử lại phần đang xử lý khi gặp HTTP 408/500/502/503/504, kể cả lỗi 503 nằm trong luồng SSE. Chờ giãn cách khoảng 5, 10, 20, 40 rồi 60 giây (thêm một chút ngẫu nhiên); nếu máy chủ yêu cầu Retry-After lâu hơn thì chờ đủ. Hiển thị mã lỗi, số lần thử và đếm ngược.

Không giới hạn số lần retry cho các mã tạm thời này; có thể bấm Hủy cả trong lúc đếm ngược. Không đặt timeout tổng cho Gemini. Phần đã hoàn tất được giữ, chỉ gửi lại yêu cầu của phần đang dở, không nối nội dung dở dang của lần lỗi vào phần mới. Nếu quá tải kéo dài, có thể Hủy, chọn model khác rồi Tiếp tục. Mỗi yêu cầu thử lại có thể ảnh hưởng quota/chi phí theo chính sách nhà cung cấp.

Không tự retry 400/401/403/404 hoặc 429 vì cần xử lý input/quyền/model/quota. Lỗi mạng, dữ liệu bị cắt, không đạt kiểm tra kỹ thuật/nội dung vẫn được báo riêng; retry dịch vụ không bỏ qua các kiểm tra này.

## Bản sửa v1.1.2 — tiến độ viết thực và nhận theo luồng

Dùng Gemini `streamGenerateContent?alt=sse`, nhận từng gói thay vì chờ toàn bộ phản hồi rồi mới cập nhật màn hình. Hiển thị bước/phần đang viết, lượt sửa, tổng thời gian, số ký tự/gói nhận được và số giây chưa nhận gói mới. Đây là tiến độ truyền dữ liệu, không phải số từ đã đạt kiểm tra; số từ chuẩn vẫn do Python xác minh khi phần hoàn tất. Không hiển thị suy nghĩ nội bộ của model.

Giữ chế độ không đặt timeout Gemini; nút Hủy có thể ngắt cả khi đang chờ gói đầu tiên. Luồng kết thúc trước STOP, bị chặn hoặc lỗi mạng được báo rõ, không nhận phần dở dang. Nút **Mở thư mục làm việc / nhật ký** truy cập các `partN.txt` đã xong và `progress.log` (chỉ các bước/lượt và lỗi kiểm tra; không ghi API key, prompt hay toàn bộ phản hồi).

Giảm dữ liệu gửi lặp: kịch bản gốc chỉ dùng khi lập outline, bản tham khảo dùng khi học Style Guide. Lúc viết từng phần dùng outline, Style Guide, Story Bible, narrative audit và phần gần nhất; rà soát cuối vẫn đọc toàn truyện. Mục tiêu là tránh gửi lại các đầu vào dài và toàn bộ các phần ở mỗi lần viết.

Chưa xác nhận được nguyên nhân cụ thể trên máy người dùng hoặc kiểm thử bằng Gemini key thật. Bản sửa khắc phục việc chờ im không có tiến độ và giảm context dư thừa; nếu vẫn chờ, số gói nhận và nhật ký sẽ giúp phân biệt chờ API với vòng sửa định dạng/độ dài.

## Bản sửa v1.1.1 trên nền v1.1.0

Không đặt timeout phía ứng dụng cho HTTP Gemini (`Timeout.InfiniteTimeSpan`). Tác vụ đang chờ không bị dừng sau năm phút như trước; chỉ nút Hủy/đóng ứng dụng hoặc lỗi thực tế từ mạng/API mới ngắt chờ. Máy chủ, proxy hoặc hệ điều hành vẫn có thể tự đóng kết nối; ứng dụng không thể bảo đảm phía dịch vụ giữ kết nối vô hạn. Giới hạn ngắn của tiến trình Python kiểm tra file không phải thời hạn sinh kịch bản Gemini.

Tự viết lần lượt đủ các phần rồi **ghép bằng chương trình** với đúng một newline, kiểm tra tổng bằng Python và lưu duy nhất một TXT. Bước rà soát chỉ xin metadata đánh giá, không bắt AI sinh lại toàn bộ văn bản. Nếu cần sửa nội dung, chỉ sửa riêng phần lỗi, kiểm tra lại rồi ghép lại; file cuối khớp chính xác nội dung các `partN.txt` sau rà soát. Điều này tránh bước xuất bản dài bị cắt bởi giới hạn đầu ra model.

Đây là nhánh bảo trì `release/v1.1`, giữ giao diện tự động của v1.1.0. Các thay đổi transcript/hàng đợi ở nhánh main được giữ riêng.

## Sử dụng — phiên bản tự động

1. Nhập API key; nhấn **Tải model**, chọn model có quota hoặc nhập model ID.
2. Dán kịch bản gốc và bản tham khảo hoặc mở TXT UTF-8. Chưa trích xuất DOCX/PDF/SRT.
3. Nhập khoảng số từ hoặc để trống cả hai mốc để dùng số từ bản gốc. Mục tiêu là trung điểm khoảng, từ 600–60.000 từ. Ít nhất ba phần; dùng độ dài từng phần và tổng làm kế hoạch; chấp nhận dài hơn khi nội dung hoàn chỉnh.
4. Chọn đường dẫn file TXT đầu ra (mặc định trong Documents/AutoScript), rồi nhấn **Bắt đầu**. Ứng dụng tự lập outline, học văn phong, viết các phần, tạo bài học nội bộ, rà soát toàn truyện, kiểm tra bằng Python và lưu kết quả. **Không cần duyệt hay nhấn tiếp giữa các bước.**
5. Khi hoàn tất, nhấn **Mở thư mục kết quả**. Chỉ xuất **một file TXT kịch bản hoàn chỉnh**, không tiêu đề, metadata hoặc bài học riêng. Bài học và hồ sơ quản lý vẫn nằm trong thư mục làm việc nội bộ; bài học không tính vào độ dài truyện.

Nếu mất mạng, hết quota hoặc kiểm tra không đạt sau các lượt sửa tự động, ứng dụng báo lỗi. Khắc phục rồi nhấn **Tiếp tục tác vụ bị gián đoạn** để chạy lại bước đang dở; không viết lại các bước đã hoàn tất. Hủy không tạo file đầu ra dở dang. File đầu ra cũ chỉ bị thay thế sau khi bản mới hoàn tất.

Theo yêu cầu mới, các điểm dừng xin duyệt trong skill được thay bằng tự động chuyển bước; các quy tắc sáng tạo, K1–K13, Story Bible, narrative audit và Python vẫn áp dụng. **Kịch bản đầu ra là tiếng Anh Mỹ**. Thay đổi đầu vào hoặc độ dài chỉ áp dụng khi bắt đầu dự án mới. API key và model có thể đổi trước khi tiếp tục tác vụ lỗi.

## Đếm và kiểm tra

Ứng dụng thực sự chạy `Resources/validate_script.py` bằng Python cho bản gốc, từng phần, bài học và bản cuối. Số từ là `len(text.split())`, không phải đếm token hoặc ước lượng của AI. Ký tự dùng `len(text)` (Unicode code points); newline đọc thành LF. Bản gốc đếm toàn bộ văn bản, bản truyện theo quy chuẩn skill bỏ newline ngoài cùng trước kiểm tra.

Kiểm tra chặn: dòng trắng, khoảng trắng đầu/cuối dòng, chữ số, ký tự cấm, từ rút gọn phổ biến, từ viết tắt trong danh sách skill, đoạn quá 110 từ, gạch nối đứng riêng, dấu ba chấm và độ dài tối thiểu. Phần dài hơn kế hoạch chỉ được cảnh báo, không bắt viết lại. JSON không hợp lệ hoặc phản hồi bị cắt/bị chặn không được nhận. Khi không đạt, yêu cầu AI sửa, tối đa ba lượt; hết lượt thì báo lỗi, giữ các bước hoàn tất để tiếp tục.

K13 được đưa đầy đủ vào system prompt; Gemini phải tự rà soát nội dung và cập nhật Story Bible cùng narrative audit. **AI tự rà soát không phải bảo đảm logic, tính nguyên bản hoặc chất lượng nghệ thuật**. Bạn nên đọc bản cuối; kiểm tra Python chỉ xác nhận các quy tắc kỹ thuật đã lập trình. Không cam kết retention hay điểm chất lượng.

## Dữ liệu và giới hạn

- API key chỉ giữ trong bộ nhớ cửa sổ, gửi qua header `x-goog-api-key`, không ghi file hoặc đưa vào URL. Không có cơ chế ghi log API key.
- Kịch bản, bản tham khảo và hồ sơ continuity được gửi tới Google Gemini. Mỗi lần tạo/sửa có thể phát sinh phí; không tự retry lỗi quota.
- File làm việc lưu tại `%LOCALAPPDATA%/AutoScript/<thời-gian-id>/`: source, reference, outline, style guide, từng phần, lesson, bản cuối, Story Bible và narrative audit. Tự xóa thư mục khi không cần. Chưa có giao diện mở lại dự án sau khi tắt ứng dụng.
- Sau rà soát toàn truyện, `full_script.txt` là bản cuối ghép từ các `partN.txt` đã kiểm tra và cập nhật nếu có sửa riêng phần.
- Hủy dừng yêu cầu phía ứng dụng, không bảo đảm Google ngừng tính phí. Lỗi mạng, hết quota, thiếu Python hoặc bị cắt do giới hạn model sẽ hiển thị để thử lại; không xuất phần chưa đạt kiểm tra.
- Độ dài lớn cần model đủ context và output quota. Bước rà soát không sinh lại toàn bộ văn bản; ứng dụng ghép các phần cục bộ. Từng phần và bản vá vẫn cần vừa giới hạn đầu ra model; phản hồi bị cắt bị từ chối rõ ràng. Chọn model phù hợp hoặc giảm độ dài.
- WPF chỉ chạy trên Windows. Linux có thể cross-build và kiểm tra service/Python, không chạy được giao diện.

## Kiểm thử

```powershell
python -m unittest discover -s tests -v
dotnet run --project tests/CoreChecks/CoreChecks.csproj
dotnet build AutoScript/AutoScript.csproj -c Release
```

CoreChecks dùng HTTP giả lập, chạy Python thật: lọc model, header API key, lỗi HTTP không tiến bước, từ chối phần sai kiểm tra, tiến trình tự động tới xuất bản cuối, đếm chính xác và từ chối phản hồi bị cắt. Không gọi Gemini thật. Cần kiểm thử thủ công giao diện trên Windows và một lượt Gemini với key thật trước khi sử dụng chính thức.
