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
    public MainWindow() { InitializeComponent(); }
    private string Key() => string.IsNullOrWhiteSpace(ApiKey.Password) ? throw new InvalidOperationException("Nhập Gemini API key.") : ApiKey.Password.Trim();
    private string Model() => string.IsNullOrWhiteSpace(Models.Text) ? throw new InvalidOperationException("Tải danh sách và chọn model, hoặc nhập model ID.") : Models.Text.Trim();
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (running != null) return;
        using var cts = new CancellationTokenSource(); running = cts;
        Inputs.IsEnabled = false; Approve.IsEnabled = Revise.IsEnabled = Export.IsEnabled = false;
        Cancel.IsEnabled = true; Status.Text = "Đang xử lý… Gemini có thể cần vài phút. Có thể hủy tác vụ.";
        try { await action(cts.Token); }
        catch (OperationCanceledException) { Status.Text = cts.IsCancellationRequested ? "Đã hủy. Bước trước vẫn được giữ; có thể thử lại." : "Hết thời gian chờ. Thử lại hoặc chọn model khác."; }
        catch (InvalidOperationException ex) { Status.Text = ex.Message; }
        catch (Exception) { Status.Text = "Không hoàn tất được tác vụ. Kiểm tra mạng, quyền đọc ghi thư mục và cài đặt; sau đó thử lại."; }
        finally { running = null; Inputs.IsEnabled = true; Cancel.IsEnabled = false; RefreshButtons(); }
    }
    private void RefreshButtons()
    {
        Approve.IsEnabled = session != null && session.Current.Length > 0 && session.Stage != Stage.Complete;
        Revise.IsEnabled = session != null && session.Current.Length > 0;
        Export.IsEnabled = session?.Stage == Stage.Complete;
    }
    private async Task ShowSession(CancellationToken ct)
    {
        if (session == null) return;
        string title = session.Stage switch {
            Stage.Outline => "Duyệt outline và Story Bible",
            Stage.Style => "Duyệt Style Guide",
            Stage.Part => $"Duyệt phần {session.PartNumber}/{session.PartCount}",
            Stage.Lesson => "Duyệt bài học · tiếp tục để rà soát toàn truyện",
            _ => "Bản cuối sau rà soát · kiểm tra và xuất file"
        };
        StageLabel.Text = title; Preview.Text = session.Current; Feedback.Clear(); Tabs.SelectedIndex = 1;
        Status.Text = session.Summary;
        if (session.Stage >= Stage.Part) {
            var counts = await session.CountText(session.Current, ct);
            Status.Text = $"Python: {counts.Words:N0} từ · {counts.Characters:N0} ký tự · {counts.Paragraphs:N0} đoạn · Đạt kiểm tra kỹ thuật. Nội dung đã được AI tự rà soát; bạn cần duyệt.";
        }
        Preview.ScrollToHome();
    }
    private async void LoadModels_Click(object sender, RoutedEventArgs e) => await Run(async ct => {
        var models = await client.ModelsAsync(Key(), ct);
        if (models.Count == 0) throw new InvalidOperationException("API key không có model hỗ trợ generateContent.");
        Models.ItemsSource = models; Models.SelectedIndex = 0; Status.Text = $"Đã tải {models.Count} model. Chọn model phù hợp với quota của bạn.";
    });
    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (session != null && MessageBox.Show("Bắt đầu dự án mới? Các file dự án cũ vẫn được giữ trong LocalAppData/AutoScript.", "Dự án mới", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Run(async ct => {
            var key = Key(); var model = Model();
            int? low = null, high = null;
            if (!string.IsNullOrWhiteSpace(MinWords.Text) || !string.IsNullOrWhiteSpace(MaxWords.Text)) {
                if (!int.TryParse(MinWords.Text, out var l) || !int.TryParse(MaxWords.Text, out var h) || l < 600 || h < l || h > 60000)
                    throw new InvalidOperationException("Nhập cả hai mốc số từ: tối thiểu sáu trăm, tối đa sáu mươi nghìn, mốc trên không nhỏ hơn mốc dưới.");
                low = l; high = h;
            }
            var draft = new RewriteSession(client);
            await draft.InitializeAsync(Source.Text, Reference.Text, low, high, ct);
            await draft.GenerateAsync(key, model, false, "", ct);
            session = draft; await ShowSession(ct);
        });
    }
    private async void Approve_Click(object sender, RoutedEventArgs e) => await Run(async ct => {
        await session!.GenerateAsync(Key(), Model(), true, "", ct); await ShowSession(ct);
    });
    private async void Revise_Click(object sender, RoutedEventArgs e) => await Run(async ct => {
        if (string.IsNullOrWhiteSpace(Feedback.Text)) throw new InvalidOperationException("Nhập góp ý cho bước cần tạo lại.");
        await session!.GenerateAsync(Key(), Model(), false, Feedback.Text, ct); await ShowSession(ct);
    });
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
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (session?.Stage != Stage.Complete) return;
        var dialog = new SaveFileDialog { Filter = "Kịch bản (*.txt)|*.txt", FileName = "full_script.txt", DefaultExt = ".txt", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog() != true) return;
        var lessonPath = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName) + "_lesson.txt");
        if (File.Exists(lessonPath) && MessageBox.Show("File bài học đã tồn tại. Ghi đè?", "Xuất file", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Run(async ct => {
            var counts = await session.CountText(session.FullScript, ct);
            var lessonCounts = await session.CountText(session.Lesson, ct);
            if (counts.Errors.Length > 0 || lessonCounts.Errors.Length > 0) throw new InvalidOperationException("File không đạt kiểm tra kỹ thuật; hãy tạo lại bản cuối.");
            await File.WriteAllTextAsync(dialog.FileName, session.FullScript, new UTF8Encoding(false), ct);
            await File.WriteAllTextAsync(lessonPath, session.Lesson, new UTF8Encoding(false), ct);
            Status.Text = $"Đã xuất {dialog.FileName} ({counts.Words:N0} từ) và {lessonPath}. Hồ sơ: {session.DirectoryPath}";
        });
    }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (running != null) {
            e.Cancel = true; running.Cancel(); Status.Text = "Đang hủy tác vụ. Đóng cửa sổ lại sau khi tác vụ dừng.";
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e) { ApiKey.Clear(); client.Dispose(); base.OnClosed(e); }
}
