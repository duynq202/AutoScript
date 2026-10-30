using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
namespace AutoScript.Services;
public sealed record GeminiTransfer(string Phase, int Characters, int Events, string? Message = null, long RequestId = 0, int KeyNumber = 0);
public sealed class GeminiClient : IDisposable
{
    private readonly HttpClient http;
    private ApiKeyPool? pool;
    private long requestSequence;
    private sealed record RequestContext(long Id, int KeyNumber);
    private readonly AsyncLocal<RequestContext?> context = new();
    public bool CanPrepareInParallel => pool?.Capacity > 1;
    public void ConfigureKeys(IEnumerable<string> keys, int concurrency = 2, int pauseSeconds = 2)
    {
        if (pool?.Active > 0) throw new InvalidOperationException("Không thể thay key khi còn yêu cầu API đang chạy.");
        var replacement = new ApiKeyPool(keys, concurrency, TimeSpan.FromSeconds(pauseSeconds));
        pool?.Dispose(); pool = replacement;
    }
    private void Notify(GeminiTransfer value)
    {
        var request = context.Value;
        Transfer?.Invoke(request == null ? value : value with { RequestId = request.Id, KeyNumber = request.KeyNumber });
    }
    private sealed class RejectedKey() : InvalidOperationException("API key bị từ chối quyền; kiểm tra key và model.");
    private sealed class AnotherKey : Exception;
    private async Task<T> OnKeyAsync<T>(string fallback, Func<string, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var previous = context.Value; long id = Interlocked.Increment(ref requestSequence);
        context.Value = new(id, 0);
        try {
            if (pool == null) return await action(fallback, ct);
            Notify(new("queued", 0, 0, "Đang chờ lượt API / khoảng nghỉ giữa thao tác"));
            using var lease = await pool.RentAsync(ct);
            context.Value = new(id, lease.Number);
            try { return await action(lease.Key, ct); }
            catch (TemporaryFailure error) { lease.CoolDown(error.RetryAfter ?? TimeSpan.FromSeconds(5)); throw; }
            catch (RejectedKey) { lease.Disable(); Notify(new("key-rejected", 0, 0, $"Key #{lease.Number} bị từ chối · chuyển key khác")); throw new AnotherKey(); }
        } finally { Notify(new("released", 0, 0)); context.Value = previous; }
    }
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private sealed class TemporaryFailure(int code, TimeSpan? retryAfter = null) : Exception
    {
        public int Code { get; } = code;
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }
    private static bool IsTemporary(int code) => code is 408 or 500 or 502 or 503 or 504;
    private async Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        long attempts = 0;
        while (true) {
            ct.ThrowIfCancellationRequested();
            try { return await operation(ct); }
            catch (AnotherKey) { if (pool?.Enabled == 0) throw new InvalidOperationException("Tất cả API key đã bị từ chối. Kiểm tra quyền/model rồi tiếp tục."); }
            catch (TemporaryFailure error) {
                attempts++;
                double backoff = Math.Min(60, 5 * Math.Pow(2, Math.Min(attempts - 1, 4))) + Random.Shared.NextDouble() * 3;
                var remaining = TimeSpan.FromSeconds(backoff);
                if (error.RetryAfter is { } requested && requested > remaining) remaining = requested;
                while (remaining > TimeSpan.Zero) {
                    ct.ThrowIfCancellationRequested();
                    Notify(new("retry", 0, 0, $"Gemini HTTP {error.Code} tạm thời · thử lại lần {attempts} sau {Math.Ceiling(remaining.TotalSeconds):N0}s · các phần đã xong được giữ"));
                    var wait = remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
                    await delay(wait, ct);
                    remaining -= wait;
                }
            }
        }
    }
    public event Action<GeminiTransfer>? Transfer;
    public GeminiClient(HttpClient? transport = null, Func<TimeSpan, CancellationToken, Task>? retryDelay = null)
    {
        http = transport ?? new HttpClient();
        delay = retryDelay ?? ((duration, ct) => Task.Delay(duration, ct));
        http.BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/");
        http.Timeout = Timeout.InfiniteTimeSpan;
    }
    private async Task<HttpResponseMessage> SendResponseAsync(HttpMethod method, string path, string key, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Add("x-goog-api-key", key);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.IsSuccessStatusCode) return res;
        var code = res.StatusCode;
        var retryHeader = res.Headers.RetryAfter;
        TimeSpan? retryAfter = retryHeader?.Delta ?? (retryHeader?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        res.Dispose();
        if (IsTemporary((int)code)) throw new TemporaryFailure((int)code, retryAfter);
        if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new RejectedKey();
        throw new InvalidOperationException(code switch {
            HttpStatusCode.BadRequest => "Gemini từ chối yêu cầu. Kiểm tra model, kích thước đầu vào và quyền dùng model.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API key không hợp lệ hoặc chưa được cấp quyền Gemini.",
            HttpStatusCode.TooManyRequests => "Đã hết quota hoặc bị giới hạn tốc độ. Chờ rồi thử lại.",
            HttpStatusCode.NotFound => "Không tìm thấy model. Tải lại danh sách model.",
            _ => $"Gemini gặp lỗi HTTP {(int)code}. Thử lại sau."
        });
    }
    public async Task<List<string>> ModelsAsync(string key, CancellationToken ct)
    {
        var result = new List<string>(); string? token = null;
        do {
            using var doc = await WithRetryAsync(tokenCt => OnKeyAsync(key, async (selectedKey, keyCt) => {
                using var res = await SendResponseAsync(HttpMethod.Get, "models?pageSize=100" + (token == null ? "" : "&pageToken=" + Uri.EscapeDataString(token)), selectedKey, null, keyCt);
                return JsonDocument.Parse(await res.Content.ReadAsStringAsync(keyCt));
            }, tokenCt), ct);
            if (doc.RootElement.TryGetProperty("models", out var models))
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("supportedGenerationMethods", out var methods) && methods.EnumerateArray().Any(x => x.GetString() == "generateContent"))
                        result.Add(m.GetProperty("name").GetString()!.Replace("models/", ""));
            token = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(token));
        return result.Distinct().Order().ToList();
    }
    public Task<string> GenerateAsync(string key, string model, string system, string prompt, CancellationToken ct)
        => WithRetryAsync(token => OnKeyAsync(key, (selectedKey, keyCt) => GenerateOnceAsync(selectedKey, model, system, prompt, keyCt), token), ct);
    private async Task<string> GenerateOnceAsync(string key, string model, string system, string prompt, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(model, @"^[a-zA-Z0-9._-]+$")) throw new InvalidOperationException("Model ID không hợp lệ.");
        var text = new StringBuilder(); int packets = 0; string? finishReason = null;
        bool Packet(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error)) {
                if (error.ValueKind == JsonValueKind.Object) {
                    int code = error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
                    if (code == 0 && error.TryGetProperty("status", out var status) && status.GetString() == "UNAVAILABLE") code = 503;
                    if (IsTemporary(code)) throw new TemporaryFailure(code);
                }
                throw new InvalidOperationException("Gemini trả lỗi trong luồng. Các phần đã hoàn tất vẫn được giữ; có thể thử tiếp tục.");
            }
            packets++;
            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0) {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                    foreach (var part in parts.EnumerateArray()) {
                        if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                        if (part.TryGetProperty("text", out var value)) text.Append(value.GetString());
                    }
                if (candidate.TryGetProperty("finishReason", out var finish)) finishReason = finish.GetString();
            }
            else if (root.TryGetProperty("promptFeedback", out var feedback) && feedback.TryGetProperty("blockReason", out _))
                throw new InvalidOperationException("Gemini chặn yêu cầu. Không có phần kịch bản nào được nhận từ yêu cầu này.");
            Notify(new("receiving", text.Length, packets));
            return finishReason != null;
        }
        Notify(new("sending", 0, 0));
        try {
            using var res = await SendResponseAsync(HttpMethod.Post, $"models/{model}:streamGenerateContent?alt=sse", key, new {
                systemInstruction = new { parts = new[] { new { text = system } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
                generationConfig = new { responseMimeType = "application/json" }
            }, ct);
            Notify(new("headers", 0, 0));
            if (res.Content.Headers.ContentType?.MediaType == "text/event-stream") {
                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var data = new StringBuilder(); string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null) {
                    if (line.StartsWith("data:", StringComparison.Ordinal)) {
                        if (data.Length > 0) data.Append('\n');
                        data.Append(line[5..].TrimStart());
                    }
                    else if (line.Length == 0 && data.Length > 0) {
                        var packet = data.ToString(); data.Clear();
                        if (packet == "[DONE]") break;
                        if (Packet(packet)) break;
                    }
                }
                if (finishReason == null && data.Length > 0 && data.ToString() != "[DONE]") Packet(data.ToString());
            }
            else {
                // JSON responses are accepted for compatible proxies and local test transports.
                Packet(await res.Content.ReadAsStringAsync(ct));
            }
            if (finishReason != "STOP") throw new InvalidOperationException(finishReason == null
                ? $"Luồng Gemini kết thúc khi chưa đủ phản hồi ({text.Length:N0} ký tự). Phần đang dở chưa được nhận; nhấn tiếp tục để thử lại."
                : $"Gemini kết thúc với {finishReason}; phần đang dở bị cắt hoặc bị chặn. Các phần trước vẫn được giữ.");
            if (string.IsNullOrWhiteSpace(text.ToString())) throw new InvalidOperationException("Gemini trả nội dung trống.");
            Notify(new("complete", text.Length, packets));
            return text.ToString();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException) {
            throw new InvalidOperationException($"Kết nối Gemini bị gián đoạn sau {text.Length:N0} ký tự. Các phần hoàn tất vẫn được giữ; nhấn tiếp tục để thử lại.");
        }
        catch (JsonException) { throw new InvalidOperationException("Gemini trả gói dữ liệu không hợp lệ. Phần đang dở chưa được nhận; có thể thử tiếp tục."); }
    }
    public void Dispose() { pool?.Dispose(); http.Dispose(); }
}
