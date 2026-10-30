using System.ComponentModel;
using System.Runtime.CompilerServices;
namespace AutoScript.Services;
public sealed class BatchItem : INotifyPropertyChanged
{
    public required string SourcePath { get; init; }
    public bool HasTimestamps { get; init; }
    public string Name => Path.GetFileName(SourcePath);
    public string? OutputPath { get; set; }
    public RewriteSession? Session { get; set; }
    private string status = "Chờ xử lý";
    public string Status { get => status; set { status = value; OnChanged(); } }
    public bool Completed { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public static string StripTimestamps(string text) => System.Text.RegularExpressions.Regex.Replace(text,
        @"^\[\d{2,}:\d{2}:\d{2}(?:\.\d{3})?\] ?", "", System.Text.RegularExpressions.RegexOptions.Multiline);
}
