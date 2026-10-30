using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AutoScript.Services;
namespace AutoScript;

public partial class MainWindow
{
    private string Provider => (TranscriptProvider.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "supadata";
    private void LoadLocalSettings()
    {
        var settings = LocalPreferences.Load();
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        TranscriptRoot.Text = string.IsNullOrWhiteSpace(settings.OutputRoot) ? Path.Combine(documents, "AutoScript", "Transcripts") : settings.OutputRoot;
        BatchRoot.Text = string.IsNullOrWhiteSpace(settings.BatchRoot) ? Path.Combine(documents, "AutoScript", "Rewritten") : settings.BatchRoot;
        McpEndpoint.Text = string.IsNullOrWhiteSpace(settings.McpEndpoint) ? "https://mcp.vidiq.com/mcp" : settings.McpEndpoint;
        TranscriptProvider.SelectedIndex = settings.Provider == "vidiq_mcp" ? 1 : 0;
        YouTubeKey.Password = CredentialStore.Load("YouTubeKey");
        TranscriptKey.Password = CredentialStore.Load(Provider);
        try { if (File.Exists(LocalPreferences.ReferencePath)) Reference.Text = File.ReadAllText(LocalPreferences.ReferencePath); }
        catch (IOException) { Status.Text = "Không đọc được văn phong mặc định. Có thể nhập lại thủ công."; }
    }
    private void ProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TranscriptKey != null) TranscriptKey.Password = CredentialStore.Load(Provider);
    }
    private async void SaveDefaultStyle_Click(object sender, RoutedEventArgs e) => await Run(ct => {
        if (string.IsNullOrWhiteSpace(Reference.Text)) throw new InvalidOperationException("Nhập kịch bản tham khảo trước khi lưu mặc định.");
        LocalPreferences.SaveReference(Reference.Text); Status.Text = "Đã lưu văn phong mặc định, tự nạp khi mở ứng dụng.";
        return Task.CompletedTask;
    });
    private async void ClearDefaultStyle_Click(object sender, RoutedEventArgs e) => await Run(ct => {
        if (File.Exists(LocalPreferences.ReferencePath)) File.Delete(LocalPreferences.ReferencePath);
        Status.Text = "Đã xóa mặc định trên đĩa; nội dung đang nhập vẫn được giữ.";
        return Task.CompletedTask;
    });
    private async void SaveCollectorSettings_Click(object sender, RoutedEventArgs e) => await Run(ct => {
        LocalPreferences.Save(new(TranscriptRoot.Text, BatchRoot.Text, Provider, McpEndpoint.Text.Trim()));
        if (!string.IsNullOrWhiteSpace(YouTubeKey.Password)) CredentialStore.Save("YouTubeKey", YouTubeKey.Password.Trim());
        if (!string.IsNullOrWhiteSpace(TranscriptKey.Password)) CredentialStore.Save(Provider, TranscriptKey.Password.Trim());
        Status.Text = "Đã lưu cấu hình. Key nằm trong Windows Credential Manager của tài khoản hiện tại.";
        return Task.CompletedTask;
    });
    private void ChooseTranscriptRoot_Click(object sender, RoutedEventArgs e) => ChooseFolder(TranscriptRoot);
    private void ChooseBatchRoot_Click(object sender, RoutedEventArgs e) => ChooseFolder(BatchRoot);
    private static void ChooseFolder(TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "Chọn thư mục kết quả" };
        if (dialog.ShowDialog() == true) target.Text = dialog.FolderName;
    }
    private async void Collect_Click(object sender, RoutedEventArgs e) => await Run(async ct => {
        if (string.IsNullOrWhiteSpace(ChannelUrl.Text) || string.IsNullOrWhiteSpace(YouTubeKey.Password))
            throw new InvalidOperationException("Nhập URL kênh và cấu hình YouTube Data API key.");
        if (string.IsNullOrWhiteSpace(TranscriptRoot.Text)) throw new InvalidOperationException("Chọn thư mục transcript.");
        var progress = new Progress<CollectorEvent>(value => {
            if (value.Type == "progress") {
                CollectorStatus.Text = value.Message + (value.Folder == null ? "" : $"\nTổng: {value.Total} · Thành công: {value.Success} · Lỗi/không có: {value.Failed} · Còn lại: {value.Remaining}");
                Status.Text = value.Message ?? "Đang thu thập transcript…";
            }
            // Keep access to success files even if later processing is interrupted.
            if (value.Folder != null) collectedFolder = value.Folder;
        });
        var result = await CollectorProcess.RunAsync(ChannelUrl.Text.Trim(), YouTubeKey.Password.Trim(), TranscriptKey.Password.Trim(),
            Path.GetFullPath(TranscriptRoot.Text), Provider, McpEndpoint.Text.Trim(), progress, ct);
        collectedFiles = result.Files ?? [];
        collectedFolder = result.Folder;
        OpenTranscripts.IsEnabled = collectedFolder != null; QueueTranscripts.IsEnabled = collectedFiles.Length > 0;
        CollectorStatus.Text = $"Hoàn tất · Tổng: {result.Total} · Thành công: {result.Success} · Lỗi/không có: {result.Failed} · Còn lại: {result.Remaining}";
        Status.Text = "Đã lưu transcript và CSV. Chưa chạy bất kỳ bước AI nào.";
    });
    private void OpenTranscripts_Click(object sender, RoutedEventArgs e)
    {
        if (collectedFolder != null) {
            try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { collectedFolder }, UseShellExecute = true }); }
            catch { Status.Text = collectedFolder; }
        }
    }
    private void AddToBatch(IEnumerable<string> paths, bool timestamped)
    {
        var existing = batch.Select(x => x.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) {
            var full = Path.GetFullPath(path);
            if (existing.Add(full)) batch.Add(new() { SourcePath = full, HasTimestamps = timestamped });
        }
        BatchStatus.Text = $"Hàng đợi: {batch.Count} kịch bản. Đã xong: {batch.Count(x => x.Completed)}.";
    }
    private void QueueTranscripts_Click(object sender, RoutedEventArgs e)
    {
        AddToBatch(collectedFiles.Where(File.Exists), true); Tabs.SelectedIndex = 3;
    }
    private void AddBatchFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Kịch bản TXT (*.txt)|*.txt", Multiselect = true };
        if (dialog.ShowDialog() == true) AddToBatch(dialog.FileNames, false);
    }
    private void ClearBatch_Click(object sender, RoutedEventArgs e) { batch.Clear(); BatchStatus.Text = "Hàng đợi trống."; }
    private async void RunBatch_Click(object sender, RoutedEventArgs e) => await Run(async ct => {
        if (batch.Count == 0) throw new InvalidOperationException("Thêm file hoặc transcript vào hàng đợi trước.");
        var key = Key(); var model = Model(); var reference = Reference.Text;
        if (string.IsNullOrWhiteSpace(reference)) throw new InvalidOperationException("Nhập hoặc lưu văn phong mặc định ở tab Đầu vào.");
        if (string.IsNullOrWhiteSpace(BatchRoot.Text)) throw new InvalidOperationException("Chọn thư mục kết quả viết lại.");
        int? low = null, high = null;
        if (!string.IsNullOrWhiteSpace(MinWords.Text) || !string.IsNullOrWhiteSpace(MaxWords.Text)) {
            if (!int.TryParse(MinWords.Text, out int l) || !int.TryParse(MaxWords.Text, out int h) || l < 600 || h < l || h > 60000)
                throw new InvalidOperationException("Nhập cả hai mốc số từ từ sáu trăm đến sáu mươi nghìn.");
            low = l; high = h;
        }
        var folder = Path.GetFullPath(BatchRoot.Text);
        var progress = new Progress<BatchProgress>(value => {
            Status.Text = value.Message;
            BatchStatus.Text = $"Tổng: {batch.Count} · Xong: {value.Success} · Lỗi lượt này: {value.Failed} · Chưa xong: {value.Remaining}";
        });
        var result = await BatchRewrite.RunAsync(batch, client, key, model, reference, low, high, folder, progress, ct);
        Status.Text = $"Đã xử lý hàng đợi. Xong {result.Success}/{batch.Count} · Lỗi lượt này: {result.Failed} · {folder}";
    });
}
