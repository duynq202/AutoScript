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
    using (var transient = new RetryHandler(Enumerable.Range(0, 7).Select(i => (Func<HttpResponseMessage>)(() => {
        var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        if (i == 0) unavailable.Headers.RetryAfter = new(TimeSpan.FromSeconds(120));
        return unavailable;
    })).Append(() => RetryHandler.Success("recovered"))))
    {
        double delayedSeconds = 0; int retries = 0;
        using var recover = new GeminiClient(new HttpClient(transient), (duration, token) => { token.ThrowIfCancellationRequested(); delayedSeconds += duration.TotalSeconds; return Task.CompletedTask; });
        recover.Transfer += update => { if (update.Phase == "retry") retries++; };
        Check(await recover.GenerateAsync("fake-key", "fake-model", "system", "same prompt", default) == "recovered", "503 recovery failed");
        Check(transient.Calls == 8, "Transient service overload stopped after a fixed attempt count");
        Check(delayedSeconds >= 120 && retries > 0, "Retry-After/backoff/progress ignored");
        Check(transient.Bodies.Distinct().Count() == 1, "Retry changed the current part prompt");
    }
    using (var transient = new RetryHandler(new Func<HttpResponseMessage>[] {
        () => new(HttpStatusCode.OK) { Content = new StringContent("data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"discarded\"}]}}]}\n\ndata: {\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\"}}\n\n", Encoding.UTF8, "text/event-stream") },
        () => RetryHandler.Success("recovered")
    }))
    using (var recover = new GeminiClient(new HttpClient(transient), (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; })) {
        Check(await recover.GenerateAsync("fake-key", "fake-model", "", "", default) == "recovered", "SSE 503 did not recover or concatenated rejected partial output");
        Check(transient.Calls == 2, "SSE 503 attempt count wrong");
    }
    foreach (var status in new[] { HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests }) {
        using var permanent = new RetryHandler(new Func<HttpResponseMessage>[] { () => new(status) });
        using var noRetry = new GeminiClient(new HttpClient(permanent), (_, _) => throw new Exception("Retried permanent/quota error"));
        try { await noRetry.GenerateAsync("fake-key", "fake-model", "", "", default); throw new Exception("Permanent error accepted"); }
        catch (InvalidOperationException) { }
        Check(permanent.Calls == 1, "Permanent error request repeated");
    }
    using (var transient = new RetryHandler(new Func<HttpResponseMessage>[] { () => new(HttpStatusCode.ServiceUnavailable) }))
    using (var stopRetry = new CancellationTokenSource())
    using (var recover = new GeminiClient(new HttpClient(transient), (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token))) {
        var backoffStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recover.Transfer += update => { if (update.Phase == "retry") backoffStarted.TrySetResult(); };
        var waiting = recover.GenerateAsync("fake-key", "fake-model", "", "", stopRetry.Token);
        await backoffStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); stopRetry.Cancel();
        try { await waiting; throw new Exception("Cancel did not stop retry backoff"); }
        catch (OperationCanceledException) { }
        Check(transient.Calls == 1, "Sent another request after cancellation during backoff");
    }
    Console.WriteLine("PASS: 503 recovery beyond fixed retry count, Retry-After, backoff progress, unchanged prompt, retry SSE 503 without partial text, no retries for permanent/quota errors, cancellation during backoff.");
    using (var pool = new ApiKeyPool(new[] { "a", "b", "a" }, 4, TimeSpan.Zero)) {
        Check(pool.Count == 2 && pool.Capacity == 2, "Duplicate keys inflated concurrency");
        using var first = await pool.RentAsync(default);
        using var second = await pool.RentAsync(default);
        Check(first.Number != second.Number && pool.Active == 2, "Same key used concurrently");
        using var stop = new CancellationTokenSource();
        var queued = pool.RentAsync(stop.Token); Check(!queued.IsCompleted, "Pool exceeded worker limit");
        stop.Cancel(); try { await queued; throw new Exception("Queued API wait ignored cancellation"); } catch (OperationCanceledException) { }
        first.Dispose(); using var next = await pool.RentAsync(default);
        Check(next.Number == first.Number, "Canceled waiter leaked capacity or occupied a key");
    }
    using (var pool = new ApiKeyPool(new[] { "a", "b" }, 2, TimeSpan.FromMilliseconds(40))) {
        var first = await pool.RentAsync(default); first.Dispose();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var second = await pool.RentAsync(default);
        Check(watch.Elapsed >= TimeSpan.FromMilliseconds(30), "Post-operation pause not enforced");
    }
    using (var rejected = new HeaderHandler((key, _) => key == "bad-test-key" ? new(HttpStatusCode.Unauthorized) : RetryHandler.Success("okay")))
    using (var rotate = new GeminiClient(new HttpClient(rejected))) {
        rotate.ConfigureKeys(new[] { "bad-test-key", "good-test-key" }, 2, 0);
        Check(await rotate.GenerateAsync("unused", "fake-model", "", "", default) == "okay", "Rejected key did not switch to another key");
        Check(rejected.Keys.SequenceEqual(new[] { "bad-test-key", "good-test-key" }), "Wrong key rotation");
    }
    using (var parallel = new PreparationHandler())
    using (var multi = new GeminiClient(new HttpClient(parallel))) {
        multi.ConfigureKeys(new[] { "first-test-key", "second-test-key" }, 2, 0);
        var prepared = new RewriteSession(multi);
        try {
            await prepared.InitializeAsync(Prose(600), "Reference sample.", 600, 600, default, 500);
            var result = await AutomaticRewrite.RunToFileAsync(prepared, "unused", "fake-model", Path.Combine(prepared.DirectoryPath, "result.txt"), null, default).WaitAsync(TimeSpan.FromSeconds(15));
            Check(result.Words == 600 && parallel.MaxActive == 2, "Independent preparation did not run concurrently");
            Check(parallel.OverlappingKey == false, "Parallel tasks reused one active key");
            Check(parallel.StyleCalls == 1 && parallel.PartCalls == 3, "Prepared style was regenerated or script parts duplicated");
            Check(parallel.MaxPartsActive == 1, "Narrative parts ran out of order");
        } finally { if (Directory.Exists(prepared.DirectoryPath)) Directory.Delete(prepared.DirectoryPath, true); }
    }
    using (var failedPrep = new PreparationHandler { FailOutline = true })
    using (var multi = new GeminiClient(new HttpClient(failedPrep))) {
        multi.ConfigureKeys(new[] { "first-test-key", "second-test-key" }, 2, 0);
        var prepared = new RewriteSession(multi);
        try {
            await prepared.InitializeAsync(Prose(600), "Reference", 600, 600, default);
            try { await prepared.PrepareAsync("unused", "fake-model", default).WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Failed preparation accepted"); }
            catch (InvalidOperationException) { }
            multi.ConfigureKeys(new[] { "first-test-key" }, 1, 0);
            Check(!File.Exists(Path.Combine(prepared.DirectoryPath, "full_script.txt")), "Failed preparation exposed an incomplete script");
        } finally { if (Directory.Exists(prepared.DirectoryPath)) Directory.Delete(prepared.DirectoryPath, true); }
    }
    using (var tooLong = new PreparationHandler { LongParts = true })
    using (var keep = new GeminiClient(new HttpClient(tooLong))) {
        var longer = new RewriteSession(keep);
        try {
            await longer.InitializeAsync(Prose(600), "Reference", 600, 600, default);
            var result = await AutomaticRewrite.RunToFileAsync(longer, "one-test-key", "fake-model", Path.Combine(longer.DirectoryPath, "result.txt"), null, default);
            Check(result.Words == 780 && tooLong.PartCalls == 3, "Overlong parts were rejected or regenerated");
            Check((await longer.CountText(longer.FullScript, default)).Errors.Length == 0, "Local paragraph reflow did not pass Python");
            Check(longer.FullScript.Split(' ', '\n').Length == 780, "Reflow changed word count");
        } finally { if (Directory.Exists(longer.DirectoryPath)) Directory.Delete(longer.DirectoryPath, true); }
    }
    Console.WriteLine("PASS: pool limits, key deduplication, cancel queued work, pause between operations, key failover, independent preparation in parallel, sequential narrative parts, accepts overlong parts without rewriting, lossless paragraph reflow.");
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

sealed class RetryHandler(IEnumerable<Func<HttpResponseMessage>> responses) : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> queue = new(responses);
    public int Calls { get; private set; }
    public List<string> Bodies { get; } = [];
    public static HttpResponseMessage Success(string text) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } } }), Encoding.UTF8, "application/json")
    };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++; Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct));
        if (queue.Count == 0) throw new Exception("Unexpected extra retry");
        return queue.Dequeue()();
    }
}

sealed class HeaderHandler(Func<string, int, HttpResponseMessage> response) : HttpMessageHandler
{
    public List<string> Keys { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = request.Headers.GetValues("x-goog-api-key").Single(); Keys.Add(key);
        return Task.FromResult(response(key, Keys.Count));
    }
}
sealed class PreparationHandler : HttpMessageHandler
{
    private readonly object sync = new(); private readonly HashSet<string> activeKeys = [];
    private readonly TaskCompletionSource bothPrepared = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int MaxActive, StyleCalls, PartCalls, MaxPartsActive;
    private int active, partsActive, prepStarted;
    public bool OverlappingKey, LongParts, FailOutline;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = request.Headers.GetValues("x-goog-api-key").Single();
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        string task = document.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        bool seed = task.StartsWith("Independent style"), outline = task.StartsWith("Step one"), part = task.StartsWith("Step three"), final = task.StartsWith("Step four final");
        lock (sync) {
            if (!activeKeys.Add(key)) OverlappingKey = true;
            active++; MaxActive = Math.Max(active, MaxActive);
            if (seed || task.StartsWith("Step two")) StyleCalls++;
            if (part) { PartCalls++; partsActive++; MaxPartsActive = Math.Max(MaxPartsActive, partsActive); }
        }
        try {
            if (!LongParts && (seed || outline)) {
                if (Interlocked.Increment(ref prepStarted) == 2) bothPrepared.TrySetResult();
                await bothPrepared.Task.WaitAsync(ct);
            }
            if (FailOutline && outline) return new(HttpStatusCode.BadRequest);
            if (FailOutline && seed) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            await Task.Yield();
            var prose = part && LongParts ? string.Join(" ", Enumerable.Repeat("story", 260)) : string.Join("\n", Enumerable.Repeat(string.Join(" ", Enumerable.Repeat("story", 20)), 10));
            var payload = JsonSerializer.Serialize(new { text = final ? "Audit metadata only" : prose, bible = "Bible", audit = "Audit", contentPass = true, problems = Array.Empty<string>(), repairs = Array.Empty<object>() });
            return RetryHandler.Success(payload);
        } finally { lock (sync) { active--; activeKeys.Remove(key); if (part) partsActive--; } }
    }
}
