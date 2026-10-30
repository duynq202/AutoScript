# Transcript kênh YouTube và viết lại hàng loạt

## Cách dùng trên Windows

Tải bản đầy đủ mới nhất, giải nén toàn bộ và chạy AutoScript.exe. Python đã đi kèm. Tab **Transcript YouTube** chỉ thu thập dữ liệu, không gọi Gemini, không tóm tắt, dịch hay viết lại.

Cấu hình **một lần** trong phần mở rộng Cấu hình API:

- YouTube Data API v3 key: bật YouTube Data API v3 trong dự án Google Cloud, tạo key có quyền gọi API này. Key này độc lập với Gemini key. Tài liệu: https://developers.google.com/youtube/v3/getting-started
- Nguồn **vidIQ MCP**: endpoint mặc định `https://mcp.vidiq.com/mcp`, Bearer token do vidIQ cung cấp. Endpoint và kiểu xác thực dựa trên lệnh kết nối người dùng cung cấp; không giả định vidIQ có REST API công khai.
- Hoặc chọn **Supadata native**, nhập Supadata API key có quota. Xem https://docs.supadata.ai/get-started và SDK chính thức https://github.com/supadata-ai/js . Bộ thu thập dùng `GET https://api.supadata.ai/v1/transcript` với `url`, `text=false`, `mode=native` và header `x-api-key`. Không gửi `lang`, không gọi mode auto/generate hay endpoint dịch.
- Chọn thư mục lưu và nhấn **Lưu cấu hình và key trên máy này**. Khóa nằm trong Windows Credential Manager của tài khoản hiện tại, không nằm trong JSON, CSV, dòng lệnh hay GitHub. Có thể quản lý/xóa ở Windows Credential Manager > Windows Credentials > Generic Credentials > AutoScript/… . Gemini key vẫn chỉ ở bộ nhớ như trước.

Sau đó, mỗi lượt chỉ cần dán URL kênh và nhấn **Start**. Hỗ trợ `youtube.com/@handle`, `/channel/UC…` và `/user/tên-cũ`. Với URL `/c/…`, dùng URL @handle hoặc /channel/ChannelID thay thế để ánh xạ chính xác, không đoán bằng tìm kiếm.

## Thu thập, lưu và chạy lại

Dùng `channels.list` để xác định kênh và uploads playlist; `playlistItems.list` phân trang tới hết; `videos.list` theo lô để lấy metadata và xác minh `privacyStatus=public`. Không dùng `search.list` có thể giới hạn số video. Video ID không bị xử lý trùng. Các video không tồn tại trong API, riêng tư hoặc unlisted bị loại; không vượt hạn chế truy cập, không đăng nhập hay cào YouTube bằng cookie.

Thư mục theo **Channel ID** để vẫn tìm đúng dữ liệu nếu kênh đổi tên:

```text
<output-root>/UCxxxxxxxxxxxxxxxxxxxxxx/
  videos.csv
  failed_videos.csv
  state.json
  .collector.lock
  transcripts/
    <VideoID>.txt
```

TXT giữ nguyên văn bản/ngôn ngữ và thứ tự các đoạn, thêm timestamp dạng `[00:01:23.456]`. Không loại lời, viết lại hay dịch. CSV gồm ID, tiêu đề, URL, ngày đăng, lượt xem, thời lượng ISO 8601, trạng thái, ngôn ngữ, đường dẫn và lỗi. Ô CSV có thể bị Excel hiểu thành công thức được escape; nội dung TXT không bị thay đổi.

Lưu manifest và CSV sau **mỗi video** bằng thay thế file nguyên tử. Video lỗi hoặc không có phụ đề không ngăn video sau chạy. Retry lỗi HTTP tạm thời/timeouts tối đa ba lần với backoff, tôn trọng Retry-After có giới hạn. HTTP 401/403 không lặp vô ích. Supadata native job bất đồng bộ được kiểm tra trạng thái có giới hạn thời gian; chưa có kết quả thì ghi lỗi và chuyển tiếp.

Chạy lại cùng kênh: cập nhật danh sách video, bỏ qua transcript có trạng thái success và file khớp SHA-256. Video thất bại, thiếu file hoặc file bị đổi sẽ thử lấy lại. Có khóa hệ điều hành ngăn hai tác vụ cùng ghi một kênh; khóa tự nhả khi tiến trình kết thúc. Không xóa transcript cũ nếu video sau này bị ẩn. Dữ liệu kênh lớn có thể cần quota YouTube/provider đáng kể.

Các trạng thái CSV: pending, success, failed, no_transcript. Giao diện hiển thị tổng, thành công, lỗi/không có transcript, còn lại và video hiện tại. Hủy có thể dừng ngay tiến trình Python; kết quả đã checkpoint vẫn giữ, video đang dở sẽ thử lại lần sau. Không bảo đảm hủy ngừng phí phía nhà cung cấp.

## Tích hợp MCP và giới hạn xác minh

`vidiq_provider.py` dùng MCP Streamable HTTP: initialize → notifications/initialized → tools/list (có phân trang) → tools/call. Token đi bằng `Authorization: Bearer …`. Không tự cài Gemini CLI; lệnh CLI người dùng cung cấp là thông tin endpoint/authentication, không phải phụ thuộc của ứng dụng.

Adapter chỉ gọi tên tool **thực sự xuất hiện trong tools/list**: `vidiq_video_transcript`, `video_transcript` hoặc `get_video_transcript` (đúng một kết quả). Dò inputSchema cho một trường chuỗi `video_id`, `videoId`, `video_url` hoặc `url`; nếu có tham số bắt buộc khác, báo chưa hỗ trợ. Không tự bịa tham số.

Output hỗ trợ structuredContent/JSON text `{segments:[{start:<giây>,text:<chuỗi>}],language:<chuỗi>}`, dạng `transcript` là mảng tương tự, hoặc văn bản đã có timestamp `[HH:MM:SS.mmm]`. Nếu server có schema khác, báo rõ cần adapter, không lưu một chuỗi lỗi/metadata thành transcript. Xem giao thức https://modelcontextprotocol.io/specification . Đây là hợp đồng adapter được kiểm thử giả lập, **không phải tuyên bố đã xác nhận schema thực tế của vidIQ**.

Tại lúc xây dựng, proxy môi trường phát triển chặn truy cập `mcp.vidiq.com`, `api.supadata.ai` và các trang tài liệu trực tiếp. Endpoint/auth vidIQ dựa trên thông tin người dùng cung cấp. Chưa có kiểm thử end-to-end với tài khoản/API key thật. SDK chính thức Supadata `@supadata/js` phiên bản 1.4.0 đã được tải từ npm, đối chiếu checksum integrity, đọc README, implementation và type definitions để xác minh `/v1/transcript`, header, mode native và cấu trúc chunk. SDK chỉ là nguồn tham chiếu; ứng dụng Python không cần npm.

Nếu server từ chối quyền, không có tool đúng tên hoặc trả schema khác, cần kiểm tra tài khoản/tài liệu nhà cung cấp trước khi gọi tích hợp đó là hoạt động. Supadata là nguồn thay thế độc lập, không âm thầm chuyển nhà cung cấp hay tính phí nguồn khác khi vidIQ lỗi.

## Dùng transcript cho đầu vào và viết nhiều kịch bản

Sau khi thu thập, nhấn **Đưa transcript đã lấy vào hàng đợi viết lại**. Không tự động viết lại lúc thu thập. Cũng có thể chọn nhiều file TXT bằng **Thêm file TXT**.

Ở tab Đầu vào, nhập Gemini key/model và bản tham khảo. Nhấn **Lưu văn phong mặc định** để tự nạp bản tham khảo mỗi khi mở ứng dụng; lưu tại `%LOCALAPPDATA%/AutoScript/default_style.txt` (văn bản thuần). Có nút xóa mặc định đã lưu. Việc này lưu bản tham khảo, không phải toàn bộ dự án Gemini. Phân tích Style Guide vẫn thực hiện cho từng kịch bản.

Ở tab Viết lại hàng loạt, chọn thư mục kết quả và nhấn **Viết lại hàng đợi / tiếp tục**. Chạy tuần tự từng kịch bản, tự động hết các bước, mỗi nguồn một TXT hoàn chỉnh. Bỏ timestamp của transcript khi đưa vào input AI; TXT transcript gốc không bị sửa. Dùng khoảng số từ ở tab Đầu vào, hoặc để trống để mỗi nguồn có mục tiêu bằng số từ riêng của nó. File cùng tên ở thư mục khác nhau có hậu tố riêng để tránh ghi đè. Một mục lỗi không dừng các mục tiếp theo. Các mục hoàn tất được bỏ qua khi tiếp tục trong cùng phiên.

Hàng đợi và trạng thái viết AI hiện giữ trong bộ nhớ; chưa khôi phục hàng đợi sau khi đóng ứng dụng. Transcript và kết quả TXT vẫn còn trên đĩa, có thể thêm lại. Dữ liệu/reference đã chốt cho một mục đang viết sẽ giữ nguyên khi tiếp tục; bắt đầu mục mới để dùng nội dung/độ dài khác. Bộ thu thập có resume bền vững trên đĩa độc lập với hạn chế này.

## Python riêng, không giao diện

Python 3.12, chỉ dùng thư viện chuẩn. Gửi một đối tượng JSON qua stdin cho `AutoScript/Resources/collector/youtube_collector.py`; không đưa key vào đối số shell. Các trường: `channel_url`, `youtube_key`, `provider` (`vidiq_mcp` hoặc `supadata`), `provider_key`, `mcp_endpoint` (vidIQ), `output_root`. Cấu hình nên tạo trong bộ nhớ hoặc lấy từ kho bí mật, không commit file chứa key. Stdout là JSON Lines (progress, complete, error), không in stack trace hay credential. WPF dùng chính đường chạy này và interpreter Python đi kèm.
