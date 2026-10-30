using System.Diagnostics;
using System.Text.Json;
namespace AutoScript.Services;
public sealed record Counts(int Words, int Characters, int Paragraphs, string[] Errors);
public sealed class PythonValidator
{
    public async Task<Counts> CountAsync(string file, CancellationToken ct, bool countOnly = false)
    {
        var interpreters = new List<(string Exe, string Prefix)>();
        var bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "python", "python.exe");
        if (OperatingSystem.IsWindows() && File.Exists(bundled)) interpreters.Add((bundled, "-I"));
        interpreters.AddRange(new[] { ("py", "-3"), ("python", ""), ("python3", "") });
        foreach (var (exe, prefix) in interpreters)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            if (prefix.Length > 0) info.ArgumentList.Add(prefix);
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Resources", "validate_script.py"));
            info.ArgumentList.Add(file);
            if (countOnly) info.ArgumentList.Add("--count-only");
            Process? process;
            try { process = Process.Start(info); }
            catch (System.ComponentModel.Win32Exception) { continue; }
            using var p = process ?? throw new InvalidOperationException("Không khởi động được Python.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = p.StandardError.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch { if (!p.HasExited) p.Kill(true); throw; }
            if (p.ExitCode != 0) throw new InvalidOperationException("Python kiểm tra thất bại. Hãy kiểm tra cài đặt Python ba và file Resources/validate_script.py.");
            _ = await error;
            return JsonSerializer.Deserialize<Counts>(await output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Python trả kết quả không hợp lệ.");
        }
        throw new InvalidOperationException("Không tìm thấy Python ba. Cài từ python.org và bật Add Python to PATH.");
    }
}
