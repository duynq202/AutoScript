using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace AutoScript.Services;

public sealed record CollectorEvent(string Type, string? Message, string? Folder, int Total = 0,
    int Success = 0, int Failed = 0, int Remaining = 0, string[]? Files = null);
public static class CollectorProcess
{
    public static async Task<CollectorEvent> RunAsync(string channelUrl, string youtubeKey, string providerKey,
        string outputRoot, string provider, string mcpEndpoint, IProgress<CollectorEvent> progress, CancellationToken ct)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "python", "python.exe");
        var candidates = new List<(string Exe, string Prefix)>();
        if (OperatingSystem.IsWindows() && File.Exists(bundled)) candidates.Add((bundled, "-I"));
        candidates.AddRange(new[] { ("py", "-3"), ("python", ""), ("python3", "") });
        foreach (var (exe, prefix) in candidates) {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false) };
            if (prefix.Length > 0) info.ArgumentList.Add(prefix);
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Resources", "collector", "youtube_collector.py"));
            Process? process;
            try { process = Process.Start(info); }
            catch (System.ComponentModel.Win32Exception) { continue; }
            using var p = process ?? throw new InvalidOperationException("Không chạy được Python collector.");
            using var cancel = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } });
            var stderr = p.StandardError.ReadToEndAsync(ct);
            CollectorEvent? complete = null;
            string? error = null;
            try {
                await p.StandardInput.WriteAsync(JsonSerializer.Serialize(new { channel_url = channelUrl, youtube_key = youtubeKey,
                    provider_key = providerKey, provider, mcp_endpoint = mcpEndpoint, output_root = outputRoot }).AsMemory(), ct);
                p.StandardInput.Close();
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync(ct)) != null) {
                    var value = JsonSerializer.Deserialize<CollectorEvent>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (value == null) continue;
                    if (value.Type == "error") error = value.Message;
                    if (value.Type == "complete") complete = value;
                    progress.Report(value);
                }
                await p.WaitForExitAsync(ct); _ = await stderr;
                ct.ThrowIfCancellationRequested();
                if (p.ExitCode != 0 || complete == null) throw new InvalidOperationException(error ?? "Collector thất bại. Kiểm tra Python, cấu hình API và quyền ghi thư mục.");
                return complete;
            } finally {
                try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { }
            }
        }
        throw new InvalidOperationException("Không tìm thấy Python. Dùng bản Windows đầy đủ hoặc cài Python ba.");
    }
}
