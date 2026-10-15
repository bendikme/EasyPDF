using EasyPDF.Interop;
using EasyPDF.Services;

namespace EasyPDF.Pdf;

/// <summary>How soon a job runs. Lower runs first; equal priorities run in the order queued.</summary>
public static class JobPriority
{
    /// <summary>Something the user is waiting on right now: opening, following a link, copying.</summary>
    public const int Interactive = 0;

    /// <summary>Tiles on screen.</summary>
    public const int Visible = 10;

    /// <summary>Links and text of the pages on screen.</summary>
    public const int PageInfo = 20;

    /// <summary>Sidebar thumbnails the user is looking at.</summary>
    public const int Thumbnail = 30;

    /// <summary>One page of a running search.</summary>
    public const int Search = 40;

    /// <summary>Tiles just off screen, rendered while the user is reading.</summary>
    public const int Prefetch = 50;
}

/// <summary>
/// The one thread that talks to PDFium. PDFium keeps global state and is not thread-safe, so
/// every native call in the app is a job queued here. Jobs run strictly one at a time, highest
/// priority first, which is also what keeps the screen responsive: a long search is queued page
/// by page, so a tile the user is waiting for never waits behind more than one page of it.
/// </summary>
public sealed class PdfEngine
{
    private static readonly Lazy<PdfEngine> LazyInstance = new(() => new PdfEngine(), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly object _gate = new();
    private readonly PriorityQueue<Job, (int Priority, long Sequence)> _queue = new();
    private readonly Thread _thread;
    private long _sequence;

    private PdfEngine()
    {
        // A deep stack: PDFium recurses through nested content and forms, and running out of
        // stack cannot be caught - it ends the process. Only what is used is ever committed.
        _thread = new Thread(Loop, 16 << 20)
        {
            IsBackground = true,
            Name = "EasyPDF.Pdfium",
        };
        _thread.Start();
    }

    /// <summary>The process-wide engine, started on first use.</summary>
    public static PdfEngine Instance => LazyInstance.Value;

    /// <summary>Starts the engine thread and PDFium early, so the first document does not wait for it.</summary>
    public static void Warm() => _ = Instance;

    public bool IsEngineThread => Thread.CurrentThread == _thread;

    /// <summary>Jobs waiting to run. For tests and diagnostics.</summary>
    public int QueueLength
    {
        get { lock (_gate) return _queue.Count; }
    }

    public Task<T> Run<T>(int priority, Func<T> work, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var job = new Job<T>(work, cancellation);
        Enqueue(job, priority);
        return job.Task;
    }

    public Task Run(int priority, Action work, CancellationToken cancellation = default) =>
        Run(priority, () =>
        {
            work();
            return true;
        }, cancellation);

    /// <summary>Runs <paramref name="work"/> now when already on the engine thread, otherwise waits for it.</summary>
    public T Invoke<T>(Func<T> work, int priority = JobPriority.Interactive)
    {
        if (IsEngineThread) return work();
        return Run(priority, work).GetAwaiter().GetResult();
    }

    private void Enqueue(Job job, int priority)
    {
        lock (_gate)
        {
            _queue.Enqueue(job, (priority, _sequence++));
            Monitor.Pulse(_gate);
        }
    }

    private void Loop()
    {
        try
        {
            InitializeLibrary();
        }
        catch (Exception ex)
        {
            // Without the library nothing can be opened; every job reports why.
            AppLog.Error("PDFium could not be initialized.", ex);
            _initFailure = ex;
        }

        while (true) RunNext();
    }

    /// <summary>
    /// Waits for and runs one job. A method of its own so nothing of the job outlives it: a job
    /// held in a local of the loop while the thread waited would keep its document alive.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void RunNext()
    {
        Job job;
        lock (_gate)
        {
            while (_queue.Count == 0) Monitor.Wait(_gate);
            job = _queue.Dequeue();
        }

        if (_initFailure is { } failure) job.Fail(new InvalidOperationException("PDFium is not available.", failure));
        else job.Execute();
    }

    private Exception? _initFailure;

    private static unsafe void InitializeLibrary()
    {
        var config = new Pdfium.LibraryConfig { Version = 2 };
        Pdfium.FPDF_InitLibraryWithConfig(&config);
    }

    private abstract class Job
    {
        public abstract void Execute();

        public abstract void Fail(Exception ex);
    }

    private sealed class Job<T> : Job
    {
        private readonly Func<T> _work;
        private readonly CancellationToken _cancellation;

        // Continuations never run inline on the engine thread: they would hold PDFium up, or
        // worse, run UI code here.
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Job(Func<T> work, CancellationToken cancellation)
        {
            _work = work;
            _cancellation = cancellation;
        }

        public Task<T> Task => _completion.Task;

        public override void Execute()
        {
            if (_cancellation.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellation);
                return;
            }

            try
            {
                _completion.TrySetResult(_work());
            }
            catch (OperationCanceledException oce)
            {
                _completion.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }

        public override void Fail(Exception ex) => _completion.TrySetException(ex);
    }
}
