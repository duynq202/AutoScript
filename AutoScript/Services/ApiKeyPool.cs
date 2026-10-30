namespace AutoScript.Services;

/// <summary>Bounded API workers, one active request per key, paced starts and post-request pauses.</summary>
public sealed class ApiKeyPool : IDisposable
{
    private sealed class Entry(string key) { public string Key = key; public bool Busy, Disabled; public DateTimeOffset Ready; }
    private readonly object sync = new();
    private readonly Entry[] entries;
    private readonly SemaphoreSlim workers;
    private readonly TimeSpan interval;
    private TaskCompletionSource changed = NewSignal();
    private DateTimeOffset nextStart;
    private int cursor;
    public int Capacity { get; }
    public int Count => entries.Length;
    public int Active { get { lock (sync) return entries.Count(x => x.Busy); } }
    public int Enabled { get { lock (sync) return entries.Count(x => !x.Disabled); } }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ApiKeyPool(IEnumerable<string> keys, int concurrency, TimeSpan interval)
    {
        entries = keys.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Select(x => new Entry(x)).ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("Nhập ít nhất một API key.");
        if (concurrency < 1 || concurrency > 4 || interval < TimeSpan.Zero || interval > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(concurrency));
        Capacity = Math.Min(concurrency, entries.Length); this.interval = interval; workers = new(Capacity, Capacity);
    }
    public async Task<Lease> RentAsync(CancellationToken ct)
    {
        while (true) {
            await workers.WaitAsync(ct);
            Task signal; TimeSpan wait;
            lock (sync) {
                var now = DateTimeOffset.UtcNow;
                if (!entries.Any(x => !x.Disabled)) { workers.Release(); throw new InvalidOperationException("Tất cả API key đã bị từ chối quyền. Kiểm tra key/model rồi tiếp tục."); }
                for (int n = 0; n < entries.Length && now >= nextStart; n++) {
                    int index = (cursor + n) % entries.Length; var entry = entries[index];
                    if (entry.Busy || entry.Disabled || entry.Ready > now) continue;
                    entry.Busy = true; cursor = (index + 1) % entries.Length; nextStart = now + interval;
                    return new(this, index, entry.Key);
                }
                signal = changed.Task;
                var eligible = entries.Where(x => !x.Busy && !x.Disabled).Select(x => x.Ready > nextStart ? x.Ready : nextStart).ToArray();
                wait = eligible.Length == 0 ? Timeout.InfiniteTimeSpan : eligible.Min() - now;
                if (wait != Timeout.InfiniteTimeSpan && wait <= TimeSpan.Zero) wait = TimeSpan.FromMilliseconds(10);
            }
            workers.Release();
            if (wait == Timeout.InfiniteTimeSpan) await signal.WaitAsync(ct);
            else {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var timer = Task.Delay(wait, wake.Token);
                await Task.WhenAny(signal, timer).WaitAsync(ct); wake.Cancel();
                ct.ThrowIfCancellationRequested();
            }
        }
    }
    private void Finish(int index, TimeSpan cooldown, bool disable)
    {
        lock (sync) {
            var now = DateTimeOffset.UtcNow; var entry = entries[index];
            entry.Busy = false; entry.Disabled |= disable;
            entry.Ready = now + (cooldown > interval ? cooldown : interval);
            if (now + interval > nextStart) nextStart = now + interval;
            var old = changed; changed = NewSignal(); old.TrySetResult();
        }
        workers.Release();
    }
    public sealed class Lease : IDisposable
    {
        private ApiKeyPool? owner; private readonly int index;
        private TimeSpan cooldown; private bool disabled;
        internal Lease(ApiKeyPool owner, int index, string key) { this.owner = owner; this.index = index; Key = key; }
        public string Key { get; }
        public int Number => index + 1;
        public void CoolDown(TimeSpan duration) => cooldown = duration;
        public void Disable() => disabled = true;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Finish(index, cooldown, disabled);
        public override string ToString() => $"Key #{Number}";
    }
    public void Dispose() => workers.Dispose();
}
