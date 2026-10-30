using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
namespace AutoScript.Services;
public sealed class GeminiClient : IDisposable
{
    private readonly HttpClient http;
    public GeminiClient(HttpClient? transport = null)
    {
        http = transport ?? new HttpClient();
        http.BaseAddress = new Uri("https://generativelanguage.googleapis.com/v1beta/");
        // Generation may take arbitrarily long. Only caller cancellation stops waiting.
        http.Timeout = Timeout.InfiniteTimeSpan;
    }
    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string key, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Add("x-goog-api-key", key);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(res.StatusCode switch {
                HttpStatusCode.BadRequest => "Gemini từ chối yêu cầu. Kiểm tra model, kích thước đầu vào và quyền dùng model.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "API key không hợp lệ hoặc chưa được cấp quyền Gemini.",
                HttpStatusCode.TooManyRequests => "Đã hết quota hoặc bị giới hạn tốc độ. Chờ rồi thử lại.",
                HttpStatusCode.NotFound => "Không tìm thấy model. Tải lại danh sách model.",
                _ => $"Gemini gặp lỗi HTTP {(int)res.StatusCode}. Thử lại sau."
            });
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }
    public async Task<List<string>> ModelsAsync(string key, CancellationToken ct)
    {
        var result = new List<string>(); string? token = null;
        do {
            using var doc = await SendAsync(HttpMethod.Get, "models?pageSize=100" + (token == null ? "" : "&pageToken=" + Uri.EscapeDataString(token)), key, null, ct);
            if (doc.RootElement.TryGetProperty("models", out var models))
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("supportedGenerationMethods", out var methods) && methods.EnumerateArray().Any(x => x.GetString() == "generateContent"))
                        result.Add(m.GetProperty("name").GetString()!.Replace("models/", ""));
            token = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrEmpty(token));
        return result.Distinct().Order().ToList();
    }
    public async Task<string> GenerateAsync(string key, string model, string system, string prompt, CancellationToken ct)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(model, @"^[a-zA-Z0-9._-]+$")) throw new InvalidOperationException("Model ID không hợp lệ.");
        using var doc = await SendAsync(HttpMethod.Post, $"models/{model}:generateContent", key, new {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new { responseMimeType = "application/json" }
        }, ct);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new InvalidOperationException("Gemini không trả nội dung; yêu cầu có thể bị bộ lọc chặn.");
        var candidate = candidates[0];
        if (!candidate.TryGetProperty("finishReason", out var finish) || finish.GetString() != "STOP")
            throw new InvalidOperationException("Phản hồi bị cắt hoặc bị chặn. Không lưu nội dung chưa hoàn chỉnh; hãy thử lại hoặc đổi model.");
        var text = string.Concat(candidate.GetProperty("content").GetProperty("parts").EnumerateArray()
            .Where(x => !x.TryGetProperty("thought", out var thought) || !thought.GetBoolean())
            .Where(x => x.TryGetProperty("text", out _)).Select(x => x.GetProperty("text").GetString()));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Gemini trả nội dung trống.");
        return text;
    }
    public void Dispose() => http.Dispose();
}
