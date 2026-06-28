using System.Threading.Channels;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// Base class for all background workers in the code-index pipeline.
///
/// DESIGN: Each subclass gets exactly one dedicated OS thread that synchronously drains an
/// unbounded Channel&lt;TJob&gt;. No thread-pool threads are used — neither for waiting on new jobs
/// nor for executing them — so HTTP request handling and the ASP.NET Core pipeline are never
/// starved.
///
/// UNBOUNDED CHANNEL: Using an unbounded channel so no jobs are silently dropped.
/// Each IngestionJob is a small struct (~200 bytes); even a 10 000-file workspace uses only ~2 MB.
/// Back-pressure is applied naturally — the file watcher debounces writes, and the staleness
/// scanner only re-enqueues files that have actually changed.
///
/// THREADING: Subclasses implement Execute() which runs entirely on the dedicated thread.
/// All blocking (event-wait, GetAwaiter().GetResult() on truly-sync "async" methods) is
/// confined to that one thread at ThreadPriority.BelowNormal.
///
/// LIFECYCLE: StartAsync spawns the thread; StopAsync cancels the CTS and waits up to 5 s for
/// the thread to drain its current job and exit cleanly.
/// </summary>
public abstract class DedicatedWorker<TJob> : IHostedService
{
    private readonly Channel<TJob> _channel;
    private Thread? _thread;
    private CancellationTokenSource _cts = new();
    private int _depth;

    public int QueueDepth => _depth;

    protected virtual ThreadPriority WorkerPriority => ThreadPriority.BelowNormal;
    protected abstract string WorkerName { get; }

    protected DedicatedWorker()
    {
        _channel = Channel.CreateUnbounded<TJob>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Priority     = WorkerPriority,
            Name         = WorkerName,
        };
        _thread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _cts.Dispose();
        return Task.CompletedTask;
    }

    // ── Protected queue API (for subclasses) ──────────────────────────────────

    protected bool TryWrite(TJob job)
    {
        if (!_channel.Writer.TryWrite(job)) return false;
        Interlocked.Increment(ref _depth);
        OnEnqueued(job);
        return true;
    }

    protected void DrainQueue()
    {
        while (_channel.Reader.TryRead(out var job))
        {
            Interlocked.Decrement(ref _depth);
            OnDrained(job);
        }
        Interlocked.Exchange(ref _depth, 0);
    }

    // ── Dedicated-thread loop ─────────────────────────────────────────────────

    private void Run()
    {
        var ct = _cts.Token;
        OnWorkerStarted();

        while (!ct.IsCancellationRequested)
        {
            TJob job;
            try
            {
                // Block this OS thread until a job arrives — zero thread-pool involvement.
                job = _channel.Reader.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { break; }
            catch (ChannelClosedException) { break; }
            catch { break; }

            var remaining = Math.Max(0, Interlocked.Decrement(ref _depth));

            try
            {
                OnBeforeJob(job, remaining);
                Execute(job, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { OnJobError(job, ex); }
            finally { OnAfterJob(job); }
        }

        OnWorkerStopped();
    }

    // ── Abstract + virtual hooks (override in subclasses as needed) ───────────

    protected abstract void Execute(TJob job, CancellationToken ct);

    protected virtual void OnWorkerStarted() { }
    protected virtual void OnWorkerStopped() { }
    protected virtual void OnEnqueued(TJob job) { }
    protected virtual void OnDrained(TJob job) { }
    protected virtual void OnBeforeJob(TJob job, int remainingQueueDepth) { }
    protected virtual void OnAfterJob(TJob job) { }
    protected virtual void OnJobError(TJob job, Exception ex) { }
}
