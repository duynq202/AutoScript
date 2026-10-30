using System.Net;
using System.Text;
using System.Text.Json;
using AutoScript.Services;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static string Prose(int words) => string.Join("\n", Enumerable.Range(0, words / 20).Select(_ => string.Join(" ", Enumerable.Repeat("story", 20))));
using var handler = new FakeHandler();
using var client = new GeminiClient(new HttpClient(handler));
var models = await client.ModelsAsync("fake-key", default);
Check(models.SequenceEqual(new[] { "fake-model" }), "Model filter failed");
var session = new RewriteSession(client);
try {
    await session.InitializeAsync(Prose(600), "Reference sample.", 600, 600, default);
    await session.GenerateAsync("fake-key", "fake-model", false, "", default);
    Check(session.Stage == Stage.Outline, "Outline stage failed");
    handler.Fail = true;
    try { await session.GenerateAsync("fake-key", "fake-model", true, "", default); throw new Exception("Expected HTTP failure"); }
    catch (InvalidOperationException) { Check(session.Stage == Stage.Outline, "Failed request advanced state"); }
    handler.Fail = false;
    await session.GenerateAsync("fake-key", "fake-model", true, "", default);
    Check(session.Stage == Stage.Style, "Style stage failed");
    handler.Invalid = true;
    try { await session.GenerateAsync("fake-key", "fake-model", true, "", default); throw new Exception("Expected validation failure"); }
    catch (InvalidOperationException) { Check(session.Stage == Stage.Style, "Invalid part advanced state"); }
    handler.Invalid = false;
    for (int i = 1; i <= 3; i++) {
        await session.GenerateAsync("fake-key", "fake-model", true, "", default);
        Check(session.Stage == Stage.Part && session.PartNumber == i, "Part approval progression failed");
        Check((await session.CountText(session.Current, default)).Words == 200, "Python part count failed");
    }
    await session.GenerateAsync("fake-key", "fake-model", true, "", default);
    Check(session.Stage == Stage.Lesson, "Lesson stage failed");
    await session.GenerateAsync("fake-key", "fake-model", true, "", default);
    Check(session.Stage == Stage.Complete, "Final stage failed");
    Check((await session.CountText(session.FullScript, default)).Words == 600, "Final exact count failed");
    Check(File.Exists(Path.Combine(session.DirectoryPath, "story_bible.md")), "Bible not persisted");
    var automatic = new RewriteSession(client);
    try {
        await automatic.InitializeAsync(Prose(600), "Reference sample.", 600, 600, default);
        var output = Path.Combine(automatic.DirectoryPath, "export", "result.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, "existing result");
        handler.Invalid = true;
        try { await AutomaticRewrite.RunToFileAsync(automatic, "fake-key", "fake-model", output, null, default); throw new Exception("Expected automatic validation failure"); }
        catch (InvalidOperationException) { }
        Check(await File.ReadAllTextAsync(output) == "existing result", "Failed automatic run replaced previous file");
        Check(automatic.Stage == Stage.Style, "Automatic failure lost completed stages");
        handler.Invalid = false;
        var result = await AutomaticRewrite.RunToFileAsync(automatic, "fake-key", "fake-model", output, null, default);
        Check(result.Words == 600 && automatic.Stage == Stage.Complete, "Automatic resume did not finish");
        Check(await File.ReadAllTextAsync(output) == automatic.FullScript, "Export differs from final script");
        Check(Directory.GetFiles(Path.GetDirectoryName(output)!).Length == 1, "Automatic run exported more than one file");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await AutomaticRewrite.RunToFileAsync(automatic, "fake-key", "fake-model", output, null, cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Check(await File.ReadAllTextAsync(output) == automatic.FullScript, "Cancellation damaged final output");
        Console.WriteLine("PASS: automatic one-click workflow, single TXT export, validation failure preserves old output, resume from failed step, cancellation.");
    } finally { if (Directory.Exists(automatic.DirectoryPath)) Directory.Delete(automatic.DirectoryPath, true); }
    var batchFolder = Path.Combine(Path.GetTempPath(), "autoscript-batch-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(batchFolder);
    var batch = new List<BatchItem>();
    try {
        for (int i = 0; i < 2; i++) {
            var inputFolder = Path.Combine(batchFolder, "input" + i); Directory.CreateDirectory(inputFolder);
            var inputFile = Path.Combine(inputFolder, "same-name.txt");
            await File.WriteAllTextAsync(inputFile, "[00:00:01.000] " + Prose(600));
            batch.Add(new() { SourcePath = inputFile, HasTimestamps = true });
        }
        batch.Insert(1, new() { SourcePath = Path.Combine(batchFolder, "missing.txt") });
        var outputFolder = Path.Combine(batchFolder, "output");
        var batchResult = await BatchRewrite.RunAsync(batch, client, "fake-key", "fake-model", "Style reference", 600, 600, outputFolder, null, default);
        Check(batchResult.Success == 2 && batchResult.Failed == 1, "Batch must continue past a failed item");
        Check(batch[0].Completed && batch[2].Completed && !batch[1].Completed, "Batch statuses wrong");
        Check(batch[0].OutputPath != batch[2].OutputPath, "Same-name inputs collided");
        Check(Directory.GetFiles(outputFolder).Length == 2, "Expected one TXT per successful batch input");
        Check((await File.ReadAllTextAsync(Path.Combine(batch[0].Session!.DirectoryPath, "source.txt"))) == Prose(600), "Transcript timestamps leaked into rewrite source");
        int requests = handler.GenerationCalls;
        await BatchRewrite.RunAsync(batch, client, "fake-key", "fake-model", "Style reference", 600, 600, outputFolder, null, default);
        Check(handler.GenerationCalls == requests, "Batch retried completed items");
        var collectorEvents = new List<CollectorEvent>();
        try {
            await CollectorProcess.RunAsync("https://example.test/@wrong", "test-only", "test-only", batchFolder,
                "supadata", "", new InlineProgress<CollectorEvent>(collectorEvents.Add), default);
            throw new Exception("Invalid channel should fail without network");
        } catch (InvalidOperationException ex) { Check(ex.Message.Contains("URL"), "Python bridge did not return structured error"); }
        Check(collectorEvents.Any(x => x.Type == "error"), "Python JSONL bridge error missing");
        Console.WriteLine("PASS: sequential batch, failed item isolation, distinct output names, timestamp stripping, skip completed jobs, real Python collector subprocess bridge.");
    } finally {
        foreach (var item in batch) if (item.Session != null && Directory.Exists(item.Session.DirectoryPath)) Directory.Delete(item.Session.DirectoryPath, true);
        Directory.Delete(batchFolder, true);
    }
    if (OperatingSystem.IsWindows()) {
        string credential = "test-" + Guid.NewGuid().ToString("N");
        try { CredentialStore.Save(credential, "fake-test-value"); Check(CredentialStore.Load(credential) == "fake-test-value", "Credential Manager roundtrip failed"); }
        finally { CredentialStore.Remove(credential); }
        Console.WriteLine("PASS: Windows Credential Manager roundtrip with a disposable test entry.");
    }
    handler.Truncated = true;
    try { await client.GenerateAsync("fake-key", "fake-model", "", "", default); throw new Exception("Truncated response accepted"); }
    catch (InvalidOperationException) { }
    Console.WriteLine("PASS: model filtering, HTTP failure rollback, invalid part rejection, approval workflow, Python counts, persisted files, truncated response rejection. No live API calls.");
} finally { Directory.Delete(session.DirectoryPath, true); }

sealed class FakeHandler : HttpMessageHandler
{
    public bool Fail, Invalid, Truncated;
    public int GenerationCalls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.GetValues("x-goog-api-key").Single() != "fake-key") throw new Exception("API key header missing");
        if (request.RequestUri!.Query.Contains("key=")) throw new Exception("Key exposed in URL");
        if (Fail) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (request.Method == HttpMethod.Get) return Json(new { models = new object[] {
            new { name = "models/fake-model", supportedGenerationMethods = new[] { "generateContent" } },
            new { name = "models/embedding", supportedGenerationMethods = new[] { "embedContent" } }
        } });
        GenerationCalls++;
        var body = await request.Content!.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var task = doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        var isFinal = task.StartsWith("Step four final");
        var prose = string.Join("\n", Enumerable.Range(0, isFinal ? 30 : 10).Select(_ => string.Join(" ", Enumerable.Repeat("story", 20))));
        var payload = JsonSerializer.Serialize(new { text = Invalid ? "Bad 123" : prose, bible = "Bible", audit = "Audit", contentPass = true, problems = Array.Empty<string>(), lesson = string.Join("\n", Enumerable.Repeat(string.Join(" ", Enumerable.Repeat("lesson", 20)), 10)) });
        return Json(new { candidates = new[] { new { finishReason = Truncated ? "MAX_TOKENS" : "STOP", content = new { parts = new[] { new { text = payload } } } } } });
    }
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
