using System.Security.Cryptography;
using System.Text;
namespace AutoScript.Services;
public sealed record BatchProgress(string Message, int Success, int Failed, int Remaining);
public sealed record BatchResult(int Success, int Failed, int Remaining);
public static class BatchRewrite
{
    public static async Task<BatchResult> RunAsync(IList<BatchItem> batch, GeminiClient client,
        string key, string model, string reference, int? low, int? high, string folder,
        IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reference)) throw new InvalidOperationException("Cần văn phong tham khảo.");
        folder = Path.GetFullPath(folder); Directory.CreateDirectory(folder);
        var pending = batch.Where(x => !x.Completed).ToList();
        foreach (var item in pending.Where(x => x.OutputPath == null)) {
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.SourcePath)))[..8].ToLowerInvariant();
            string stem = Path.GetFileNameWithoutExtension(item.SourcePath);
            if (stem.Length > 80) stem = stem[..80];
            var path = Path.Combine(folder, stem + "_" + suffix + "_rewritten.txt");
            if (File.Exists(path)) path = Path.Combine(folder, stem + "_" + Guid.NewGuid().ToString("N") + "_rewritten.txt");
            item.OutputPath = path;
        }
        int failures = 0;
        void Report(string message) => progress?.Report(new(message, batch.Count(x => x.Completed), failures, batch.Count(x => !x.Completed)));
        foreach (var item in pending) {
            ct.ThrowIfCancellationRequested();
            try {
                item.Status = "Chuẩn bị…"; Report(item.Name + " · " + item.Status);
                if (item.Session == null) {
                    var text = await File.ReadAllTextAsync(item.SourcePath, new UTF8Encoding(false, true), ct);
                    if (item.HasTimestamps) text = BatchItem.StripTimestamps(text);
                    var draft = new RewriteSession(client);
                    await draft.InitializeAsync(text, reference, low, high, ct); item.Session = draft;
                }
                // Progress<T> on the UI thread keeps status updates safe for WPF bindings.
                var partProgress = new Progress<string>(message => { item.Status = message; Report(item.Name + " · " + message); });
                var counts = await AutomaticRewrite.RunToFileAsync(item.Session, key, model, item.OutputPath!, partProgress, ct);
                item.Completed = true; item.Status = $"Hoàn tất · {counts.Words:N0} từ";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { item.Status = "Đã hủy · có thể tiếp tục"; throw; }
            catch (InvalidOperationException ex) { failures++; item.Status = ex.Message; }
            catch (Exception) { failures++; item.Status = "Lỗi xử lý · kiểm tra file, mạng và quota; có thể tiếp tục."; }
            Report(item.Name + " · " + item.Status);
        }
        return new(batch.Count(x => x.Completed), failures, batch.Count(x => !x.Completed));
    }
}
