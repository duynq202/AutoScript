using System.Text;
namespace AutoScript.Services;

/// <summary>Runs the full skill workflow without interactive approvals. Can resume a failed session.</summary>
public static class AutomaticRewrite
{
    public static async Task<Counts> RunToFileAsync(RewriteSession session, string key, string model,
        string outputPath, IProgress<string>? progress, CancellationToken ct)
    {
        while (session.Stage != Stage.Complete || session.Current.Length == 0)
        {
            ct.ThrowIfCancellationRequested();
            string step = session.Current.Length == 0 ? "Lập outline và Story Bible" : session.Stage switch {
                Stage.Outline => "Học văn phong tham khảo",
                Stage.Style => $"Viết phần 1/{session.PartCount}",
                Stage.Part when session.PartNumber < session.PartCount => $"Viết phần {session.PartNumber + 1}/{session.PartCount}",
                Stage.Part => "Tạo bài học và chuẩn bị rà soát",
                Stage.Lesson => "Rà soát nội dung và ghép các phần thành một file TXT",
                _ => "Hoàn thiện"
            };
            progress?.Report(step + "…");
            await session.GenerateAsync(key, model, session.Current.Length > 0, "", ct);
        }
        progress?.Report("Kiểm tra Python và lưu file TXT…");
        var counts = await session.CountText(session.FullScript, ct);
        if (counts.Errors.Length != 0) throw new InvalidOperationException("Kịch bản cuối không đạt kiểm tra Python.");
        // Do not expose a partial output, or destroy an existing result on cancellation/failure.
        var fullPath = Path.GetFullPath(outputPath);
        var folder = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(folder);
        var temp = Path.Combine(folder, ".autoscript-" + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            await File.WriteAllTextAsync(temp, session.FullScript, new UTF8Encoding(false), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return counts;
    }
}
