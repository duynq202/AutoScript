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
        handler.Invalid = false; handler.RepairFinalRemaining = 1;
        var result = await AutomaticRewrite.RunToFileAsync(automatic, "fake-key", "fake-model", output, null, default);
        Check(result.Words == 600 && automatic.Stage == Stage.Complete, "Automatic resume did not finish");
        Check(await File.ReadAllTextAsync(output) == automatic.FullScript, "Export differs from final script");
        Check(automatic.FullScript.Contains("repaired"), "Final audit did not apply the individual-part repair");
        Check(automatic.FullScript == string.Join("\n", Enumerable.Range(1, automatic.PartCount).Select(i => File.ReadAllText(Path.Combine(automatic.DirectoryPath, $"part{i}.txt")))), "Repaired final script differs from persisted parts");
        Check(Directory.GetFiles(Path.GetDirectoryName(output)!).Length == 1, "Automatic run exported more than one file");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await AutomaticRewrite.RunToFileAsync(automatic, "fake-key", "fake-model", output, null, cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Check(await File.ReadAllTextAsync(output) == automatic.FullScript, "Cancellation damaged final output");
        Console.WriteLine("PASS: automatic one-click workflow, single TXT export, validation failure preserves old output, resume from failed step, cancellation.");
    } finally { if (Directory.Exists(automatic.DirectoryPath)) Directory.Delete(automatic.DirectoryPath, true); }
    Check(session.FullScript == string.Join("\n", Enumerable.Range(1, session.PartCount).Select(i => File.ReadAllText(Path.Combine(session.DirectoryPath, $"part{i}.txt")))), "Final file must be the exact concatenation of all part files");
    Check(!session.FullScript.Contains("review metadata"), "Audit response replaced the story");
    using (var delayed = new DelayedHandler())
    using (var transport = new HttpClient(delayed))
    using (var unlimited = new GeminiClient(transport)) {
        Check(transport.Timeout == Timeout.InfiniteTimeSpan, "Gemini wait still has a local deadline");
        var pending = unlimited.GenerateAsync("fake-key", "fake-model", "", "", default);
        await Task.Delay(60);
        Check(!pending.IsCompleted, "Request did not keep waiting for the response");
        delayed.Release.TrySetResult();
        await pending;
    }
    using (var delayed = new DelayedHandler())
    using (var unlimited = new GeminiClient(new HttpClient(delayed)))
    using (var cancelRequest = new CancellationTokenSource()) {
        var pending = unlimited.GenerateAsync("fake-key", "fake-model", "", "", cancelRequest.Token);
        cancelRequest.Cancel();
        try { await pending; throw new Exception("Explicit cancellation did not stop waiting"); }
        catch (OperationCanceledException) { }
    }
    Console.WriteLine("PASS: infinite Gemini HTTP timeout, delayed response remains pending, explicit cancellation, exact local concatenation and audit cannot replace script.");
    using (var streaming = new StreamingHandler())
    using (var live = new GeminiClient(new HttpClient(streaming))) {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        live.Transfer += value => { if (value.Characters > 0) received.TrySetResult(); };
        var response = live.GenerateAsync("fake-key", "fake-model", "", "", default);
        streaming.Write(new { candidates = new[] { new { content = new { parts = new[] { new { text = "hidden thought", thought = true } } } } } });
        streaming.Write(new { candidates = new[] { new { content = new { parts = new[] { new { text = "hello " } } } } } });
        await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!response.IsCompleted, "Streaming request ended before final packet");
        streaming.Write(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = "world" } } } } } });
        Check(await response.WaitAsync(TimeSpan.FromSeconds(3)) == "hello world", "Streaming lost chunks, included thinking or waited for EOF after STOP");
    }
    using (var streaming = new StreamingHandler())
    using (var live = new GeminiClient(new HttpClient(streaming)))
    using (var stop = new CancellationTokenSource()) {
        var response = live.GenerateAsync("fake-key", "fake-model", "", "", stop.Token);
        stop.Cancel();
        try { await response; throw new Exception("Streaming cancellation ignored"); }
        catch (OperationCanceledException) { }
    }
    using (var streaming = new StreamingHandler())
    using (var live = new GeminiClient(new HttpClient(streaming))) {
        var response = live.GenerateAsync("fake-key", "fake-model", "", "", default);
        streaming.Write(new { candidates = new[] { new { content = new { parts = new[] { new { text = "incomplete" } } } } } });
        streaming.Stream.End();
        try { await response; throw new Exception("Incomplete stream accepted"); }
        catch (InvalidOperationException) { }
    }
    Console.WriteLine("PASS: incremental SSE progress before completion, excludes thought content, STOP without EOF, cancellation while waiting, rejects incomplete streams.");
    handler.Truncated = true;
    try { await client.GenerateAsync("fake-key", "fake-model", "", "", default); throw new Exception("Truncated response accepted"); }
    catch (InvalidOperationException) { }
    Console.WriteLine("PASS: model filtering, HTTP failure rollback, invalid part rejection, approval workflow, Python counts, persisted files, truncated response rejection. No live API calls.");
} finally { Directory.Delete(session.DirectoryPath, true); }

sealed class FakeHandler : HttpMessageHandler
{
    public bool Fail, Invalid, Truncated;
    public int RepairFinalRemaining;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.GetValues("x-goog-api-key").Single() != "fake-key") throw new Exception("API key header missing");
        if (request.RequestUri!.Query.Contains("key=")) throw new Exception("Key exposed in URL");
        if (Fail) return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        if (request.Method == HttpMethod.Get) return Json(new { models = new object[] {
            new { name = "models/fake-model", supportedGenerationMethods = new[] { "generateContent" } },
            new { name = "models/embedding", supportedGenerationMethods = new[] { "embedContent" } }
        } });
        var body = await request.Content!.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var task = doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        if (task.StartsWith("Step three")) {
            var marker = "INPUT DATA:\n";
            var input = task[(task.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..].Split('\n')[0];
            using var context = JsonDocument.Parse(input);
            if (context.RootElement.GetProperty("source").GetString() != "" || context.RootElement.GetProperty("reference").GetString() != "")
                throw new Exception("Part generation resent full source/reference unnecessarily");
            if (context.RootElement.GetProperty("approvedParts").GetArrayLength() > 1) throw new Exception("Part generation context grows with entire story");
        }
        var isFinal = task.StartsWith("Step four final");
        var prose = string.Join("\n", Enumerable.Range(0, isFinal ? 30 : 10).Select(_ => string.Join(" ", Enumerable.Repeat("story", 20))));
        bool needsRepair = isFinal && RepairFinalRemaining-- > 0;
        object[] repairs = needsRepair ? new object[] { new { part = 2, text = string.Join("\n", Enumerable.Repeat(string.Join(" ", Enumerable.Repeat("repaired", 20)), 10)) } } : Array.Empty<object>();
        var payload = JsonSerializer.Serialize(new { text = Invalid ? "Bad 123" : isFinal ? "This is only review metadata." : prose, bible = "Bible", audit = "Audit", contentPass = !needsRepair, problems = needsRepair ? new[] { "Repair the second part" } : Array.Empty<string>(), repairs, lesson = string.Join("\n", Enumerable.Repeat(string.Join(" ", Enumerable.Repeat("lesson", 20)), 10)) });
        if (!request.RequestUri!.AbsoluteUri.Contains(":streamGenerateContent?alt=sse")) throw new Exception("Wrong Gemini streaming endpoint");
        string first = JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = payload[..(payload.Length / 2)] } } } } } });
        string last = JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = Truncated ? "MAX_TOKENS" : "STOP", content = new { parts = new[] { new { text = payload[(payload.Length / 2)..] } } } } } });
        return new(HttpStatusCode.OK) { Content = new StringContent("data: " + first + "\n\ndata: " + last + "\n\n", Encoding.UTF8, "text/event-stream") };
    }
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

sealed class DelayedHandler : HttpMessageHandler
{
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Release.Task.WaitAsync(ct);
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"{}\"}]}}]}") };
    }
}

sealed class StreamingHandler : HttpMessageHandler
{
    public FeedStream Stream { get; } = new();
    public void Write(object packet) => Stream.WritePacket("data: " + JsonSerializer.Serialize(packet) + "\n\n");
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var content = new StreamContent(Stream);
        content.Headers.ContentType = new("text/event-stream");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
sealed class FeedStream : Stream
{
    private readonly System.Threading.Channels.Channel<byte[]> queue = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    private byte[] current = []; private int offset;
    public void WritePacket(string packet) => queue.Writer.TryWrite(Encoding.UTF8.GetBytes(packet));
    public void End() => queue.Writer.TryComplete();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (offset == current.Length) {
            if (!await queue.Reader.WaitToReadAsync(ct)) return 0;
            if (!queue.Reader.TryRead(out var next)) continue;
            current = next; offset = 0;
        }
        int size = Math.Min(buffer.Length, current.Length - offset);
        current.AsMemory(offset, size).CopyTo(buffer); offset += size; return size;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
}
