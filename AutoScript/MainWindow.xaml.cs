using System.Diagnostics;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using AutoScript.Services;
namespace AutoScript;
public partial class MainWindow : Window
{
    private readonly GeminiClient client = new();
    private RewriteSession? session;
    private CancellationTokenSource? running;
    private string? activeOutput;
    private bool outputReady;
    private readonly System.Windows.Threading.DispatcherTimer ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch elapsed = new();
    private string liveStep = "Đang xử lý", liveNetwork = "";
    private DateTimeOffset? lastPacket;
    private long lastTransferUi;
    private int lastUiCharacters;
    private void UpdateLiveStatus()
    {
        if (running == null) return;
        var silence = lastPacket.HasValue ? $" · Không có gói mới: {(DateTimeOffset.UtcNow - lastPacket.Value).TotalSeconds:N0}s" : "";
        var duration = $"{(int)elapsed.Elapsed.TotalHours:00}:{elapsed.Elapsed.Minutes:00}:{elapsed.Elapsed.Seconds:00}";
        Status.Text = $"{liveStep} · {liveNetwork} · Tổng {duration}{silence} · Có thể Hủy, không tự ngắt vì chờ lâu.";
    }
    private void OnTransfer(GeminiTransfer value)
    {
        long now = Stopwatch.GetTimestamp();
        if (value.Phase == "receiving" && lastUiCharacters > 0 && Stopwatch.GetElapsedTime(lastTransferUi, now).TotalMilliseconds < 250) return;
        lastTransferUi = now; lastUiCharacters = value.Characters;
        Dispatcher.BeginInvoke(new Action(() => {
            if (running == null) return;
            if (value.Phase == "sending") { lastPacket = DateTimeOffset.UtcNow; liveNetwork = "Đang chờ Gemini trả dữ liệu"; }
            else if (value.Phase == "headers") { lastPacket = DateTimeOffset.UtcNow; liveNetwork = "Đã kết nối · Gemini đang xử lý"; }
            else { lastPacket = DateTimeOffset.UtcNow; liveNetwork = value.Phase == "complete" ? $"Đã nhận xong {value.Characters:N0} ký tự" : $"Đã nhận {value.Characters:N0} ký tự · {value.Events:N0} gói"; }
            UpdateLiveStatus();
        }));
    }
    private void OnSessionProgress(string message)
    {
        Dispatcher.BeginInvoke(new Action(() => {
            if (running == null) return;
            liveStep = message; StageLabel.Text = message; UpdateLiveStatus();
        }));
    }
    public MainWindow()
    {
        InitializeComponent();
        client.Transfer += OnTransfer;
        ticker.Tick += (_, _) => UpdateLiveStatus();
        OutputPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AutoScript", $"full_script_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
    }
    private string Key() => string.IsNullOrWhiteSpace(ApiKey.Password) ? throw new InvalidOperationException("Nhập Gemini API key.") : ApiKey.Password.Trim();
    private string Model() => string.IsNullOrWhiteSpace(Models.Text) ? throw new InvalidOperationException("Tải danh sách và chọn model, hoặc nhập model ID.") : Models.Text.Trim();
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (running != null) return;
        using var cts = new CancellationTokenSource(); running = cts;
        elapsed.Restart(); liveStep = "Chuẩn bị"; liveNetwork = ""; lastPacket = null; ticker.Start();
        Inputs.IsEnabled = false; Retry.IsEnabled = OpenFolder.IsEnabled = false;
        Cancel.IsEnabled = true; Status.Text = "Đang xử lý… Không giới hạn thời gian chờ Gemini. Có thể bấm Hủy.";
        try { await action(cts.Token); }
        catch (OperationCanceledException) { Status.Text = cts.IsCancellationRequested ? "Đã hủy. Nhấn tiếp tục để chạy từ bước đang dở." : "Kết nối bị gián đoạn từ phía dịch vụ. Ứng dụng không đặt thời hạn chờ Gemini; có thể tiếp tục."; }
        catch (InvalidOperationException ex) { Status.Text = ex.Message; }
        catch (Exception) { Status.Text = "Không hoàn tất tác vụ. Kiểm tra mạng, quyền đọc ghi thư mục và cài đặt; sau đó thử tiếp tục."; }
        finally {
            ticker.Stop(); elapsed.Stop(); running = null; Inputs.IsEnabled = true; Cancel.IsEnabled = false;
            Retry.IsEnabled = session != null && !outputReady;
            OpenFolder.IsEnabled = outputReady;
        }
    }
    private async Task ProcessSession(string key, string model, CancellationToken ct)
    {
        Tabs.SelectedIndex = 1;
        OpenWorkFolder.IsEnabled = session != null;
        var progress = new Progress<string>(message => {
            liveStep = message; StageLabel.Text = message; UpdateLiveStatus();
            Preview.Text = session?.Current ?? "";
        });
        var counts = await AutomaticRewrite.RunToFileAsync(session!, key, model, activeOutput!, progress, ct);
        outputReady = true;
        StageLabel.Text = "Hoàn tất · Đã lưu kịch bản";
        Preview.Text = session!.FullScript;
        Preview.ScrollToHome();
        Status.Text = $"Đã lưu {activeOutput} · Python: {counts.Words:N0} từ, {counts.Characters:N0} ký tự, {counts.Paragraphs:N0} đoạn.";
    }
    private async void LoadModels_Click(object sender, RoutedEventArgs e) => await Run(async ct => {
        var models = await client.ModelsAsync(Key(), ct);
        if (models.Count == 0) throw new InvalidOperationException("API key không có model hỗ trợ generateContent.");
        Models.ItemsSource = models; Models.SelectedIndex = 0; Status.Text = $"Đã tải {models.Count} model.";
    });
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        await Run(async ct => {
            var key = Key(); var model = Model();
            int? low = null, high = null;
            if (!string.IsNullOrWhiteSpace(MinWords.Text) || !string.IsNullOrWhiteSpace(MaxWords.Text)) {
                if (!int.TryParse(MinWords.Text, out var l) || !int.TryParse(MaxWords.Text, out var h) || l < 600 || h < l || h > 60000)
                    throw new InvalidOperationException("Nhập cả hai mốc số từ: tối thiểu sáu trăm, tối đa sáu mươi nghìn, mốc trên không nhỏ hơn mốc dưới.");
                low = l; high = h;
            }
            if (string.IsNullOrWhiteSpace(OutputPath.Text)) throw new InvalidOperationException("Chọn đường dẫn file TXT đầu ra.");
            var destination = Path.GetFullPath(OutputPath.Text.Trim());
            if (!string.Equals(Path.GetExtension(destination), ".txt", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("File đầu ra phải có đuôi .txt.");
            // Ask only before starting when replacing an existing user file, never between AI stages.
            if (File.Exists(destination) && MessageBox.Show("File đầu ra đã tồn tại. Thay thế khi xử lý thành công?", "Lưu kết quả", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var draft = new RewriteSession(client);
            await draft.InitializeAsync(Source.Text, Reference.Text, low, high, ct);
            if (session != null) session.Progress -= OnSessionProgress;
            session = draft; session.Progress += OnSessionProgress; activeOutput = destination; outputReady = false;
            await ProcessSession(key, model, ct);
        });
    }
    private async void Retry_Click(object sender, RoutedEventArgs e) => await Run(async ct => {
        if (session == null || activeOutput == null) throw new InvalidOperationException("Chưa có tác vụ để tiếp tục.");
        await ProcessSession(Key(), Model(), ct);
    });
    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "Kịch bản (*.txt)|*.txt", FileName = Path.GetFileName(OutputPath.Text), DefaultExt = ".txt", AddExtension = true, OverwritePrompt = false };
        if (dialog.ShowDialog() == true) OutputPath.Text = dialog.FileName;
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (activeOutput == null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", activeOutput }, UseShellExecute = true }); }
        catch { Status.Text = $"File kết quả: {activeOutput}"; }
    }
    private void OpenWorkFolder_Click(object sender, RoutedEventArgs e)
    {
        if (session == null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { session.DirectoryPath }, UseShellExecute = true }); }
        catch { Status.Text = session.DirectoryPath; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => running?.Cancel();
    private async void OpenSource_Click(object sender, RoutedEventArgs e) => await OpenText(true);
    private async void OpenReference_Click(object sender, RoutedEventArgs e) => await OpenText(false);
    private async Task OpenText(bool original)
    {
        var dialog = new OpenFileDialog { Filter = "Kịch bản UTF-8 (*.txt)|*.txt", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        await Run(async ct => {
            var text = await File.ReadAllTextAsync(dialog.FileName, new UTF8Encoding(false, true), ct);
            if (original) Source.Text = text; else Reference.Text = text;
            Status.Text = "Đã đọc file TXT.";
        });
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (running != null) { e.Cancel = true; running.Cancel(); Status.Text = "Đang hủy tác vụ. Đóng cửa sổ lại sau khi tác vụ dừng."; }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e) { ticker.Stop(); client.Transfer -= OnTransfer; if (session != null) session.Progress -= OnSessionProgress; ApiKey.Clear(); client.Dispose(); base.OnClosed(e); }
}
