using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

// The parallel directory walk: a fixed number of worker threads take directories from one shared LIFO stack, enumerate them,
// and push the sub-directories they find. The stack is intentionally not capacity-bounded (a worker is also the producer of
// new tasks and would deadlock on a full stack); the number of workers is what is bounded.

sealed class WorkQueue
{
    readonly Stack<(DirNode Node, string Path)> _stack = new();
    readonly object _lock = new();
    int _pending;       // directories on the stack plus directories being enumerated
    int _peakQueued;
    bool _canceled;

    public int PeakQueued
    {
        get { lock (_lock) return _peakQueued; }
    }

    public void Seed(DirNode node, string path)
    {
        lock (_lock)
        {
            _stack.Push((node, path));
            _pending++;
            _peakQueued = Math.Max(_peakQueued, _stack.Count);
        }
    }

    // Blocks until a directory is available. Returns false when the walk is over: nothing is queued or in progress, or it was canceled.
    public bool TryTake(out (DirNode Node, string Path) task)
    {
        lock (_lock)
        {
            while (true)
            {
                if (_canceled)
                {
                    task = default;
                    return false;
                }
                if (_stack.Count > 0)
                {
                    task = _stack.Pop();
                    return true;
                }
                if (_pending == 0)
                {
                    task = default;
                    return false;
                }
                Monitor.Wait(_lock);
            }
        }
    }

    // Called once for every directory taken, with the sub-directories it produced. The new tasks are counted before the finished
    // one is subtracted, so _pending cannot reach zero while work remains.
    public void Finish(List<(DirNode Node, string Path)> children)
    {
        lock (_lock)
        {
            foreach (var child in children) _stack.Push(child);
            _pending += children.Count - 1;
            _peakQueued = Math.Max(_peakQueued, _stack.Count);
            if (children.Count > 0 || _pending == 0) Monitor.PulseAll(_lock);
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _canceled = true;
            Monitor.PulseAll(_lock);
        }
    }
}

// Counters kept by one worker and summed after the join. The main thread may read them while the walk runs (progress); each has
// exactly one writer.
sealed class Counters
{
    public long Scanned, Denied, Failed, ReparseSkipped, Files, Entries, Bytes, EnumTicks, IdleTicks;

    public void Add(Counters other)
    {
        Scanned += other.Scanned;
        Denied += other.Denied;
        Failed += other.Failed;
        ReparseSkipped += other.ReparseSkipped;
        Files += other.Files;
        Entries += other.Entries;
        Bytes += other.Bytes;
        EnumTicks += other.EnumTicks;
        IdleTicks += other.IdleTicks;
    }
}

sealed class Worker : IEntrySink
{
    public const int MaxErrorSamples = 20;

    readonly Walker _walker;
    readonly WorkQueue _queue;
    readonly DirectoryReader _reader;
    readonly BoundedTop<FileHit>? _files;
    readonly BoundedTop<FileHit> _rootFiles;
    readonly List<(DirNode Node, string Path)> _children = [];
    Win32FindData _data;
    DirNode _node = null!;
    string _path = null!;
    long _ownSize;

    public readonly Counters Counters = new();
    public readonly List<DirNode> Created = [];
    public readonly List<string> ErrorSamples = [];
    public BoundedTop<FileHit>? Files => _files;
    public BoundedTop<FileHit> RootFiles => _rootFiles;

    public Worker(Walker walker, WorkQueue queue, DirectoryReader reader, int top, bool collectFiles)
    {
        _walker = walker;
        _queue = queue;
        _reader = reader;
        _files = collectFiles ? new BoundedTop<FileHit>(top) : null;
        _rootFiles = new BoundedTop<FileHit>(top);
    }

    public void Run()
    {
        try
        {
            while (true)
            {
                var waitStart = Stopwatch.GetTimestamp();
                var taken = _queue.TryTake(out var task);
                Counters.IdleTicks += Stopwatch.GetTimestamp() - waitStart;
                if (!taken) return;
                Process(task.Node, task.Path);
            }
        }
        catch (Exception exception)
        {
            _walker.Fail(exception, _queue);
        }
    }

    void Process(DirNode node, string path)
    {
        _node = node;
        _path = path;
        _ownSize = 0;
        _children.Clear();
        var start = Stopwatch.GetTimestamp();
        var result = _reader.Read(path, ref _data, this);
        Counters.EnumTicks += Stopwatch.GetTimestamp() - start;
        node.OwnFileSize = _ownSize;
        switch (result.Outcome)
        {
            case ReadOutcome.Complete:
                Counters.Scanned++;
                break;
            case ReadOutcome.Denied:
                Counters.Denied++;
                break;
            default:
                Counters.Failed++;
                if (ErrorSamples.Count < MaxErrorSamples)
                    ErrorSamples.Add($"{RootPath.ToDisplay(path)}: {Marshal.GetPInvokeErrorMessage(result.Error).TrimEnd()} (error {result.Error})");
                break;
        }
        if (node.Id == 0) _walker.RootRead = result;
        _queue.Finish(_children);
    }

    public void OnEntry(in Win32FindData entry)
    {
        Counters.Entries++;
        var attributes = entry.FileAttributes;
        if ((attributes & Win32Find.DirectoryAttribute) != 0)
        {
            // A reparse-point directory (junction, directory symlink, mount point, ...) is never entered and contributes nothing.
            if ((attributes & Win32Find.ReparsePointAttribute) != 0)
            {
                Counters.ReparseSkipped++;
                return;
            }
            var name = Win32Find.NameString(in entry);
            var child = new DirNode(_walker.NextId(), _node.Id, name);
            Created.Add(child);
            _children.Add((child, DirTable.Combine(_path, name)));
            return;
        }

        var size = Win32Find.FileSize(in entry);
        Counters.Files++;
        Counters.Bytes += size;
        _ownSize += size;
        if (size <= 0) return;
        var forFiles = _files is not null && _files.WouldAccept(size);
        var forRoot = _node.Id == 0 && _rootFiles.WouldAccept(size);
        if (!forFiles && !forRoot) return;
        // The name is created only for a file that is actually kept.
        var hit = new FileHit(_node.Id, Win32Find.NameString(in entry), size);
        if (forFiles) _files!.Add(hit, size);
        if (forRoot) _rootFiles.Add(hit, size);
    }
}

sealed record WalkResult(
    DirNode[] Nodes,
    Counters Totals,
    FileHit[] Files,
    FileHit[] RootFiles,
    int PeakQueuedDirs,
    TimeSpan WalkTime,
    bool LargeFetch,
    ReadResult RootRead,
    string[] ErrorSamples);

sealed class Walker(FindFirstFn? findFirst = null)
{
    int _nextId;                       // the root is 0; the first id handed out is 1
    Exception? _failure;

    public ReadResult RootRead;        // written by the worker that enumerates the root

    public int NextId() => Interlocked.Increment(ref _nextId);

    // Called from a worker thread that hit an unexpected exception: remember the first one and stop everybody.
    public void Fail(Exception exception, WorkQueue queue)
    {
        Interlocked.CompareExchange(ref _failure, exception, null);
        queue.Cancel();
    }

    public WalkResult Run(DirNode root, string rootExtendedPath, int workers, int top, bool collectFiles, CancellationToken cancel, Action<long, long>? progress)
    {
        var queue = new WorkQueue();
        var reader = new DirectoryReader(findFirst);
        var all = new List<Worker>(workers);
        var threads = new List<Thread>(workers);
        for (var index = 0; index < workers; index++)
        {
            var worker = new Worker(this, queue, reader, top, collectFiles);
            all.Add(worker);
            threads.Add(new Thread(worker.Run) { Name = $"dirsizer-worker-{index}" });
        }
        queue.Seed(root, rootExtendedPath);
        using var registration = cancel.Register(queue.Cancel);

        var start = Stopwatch.GetTimestamp();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads)
        {
            while (!thread.Join(250))
            {
                if (progress is null) continue;
                long directoriesSoFar = 0, filesSoFar = 0;
                foreach (var worker in all)
                {
                    directoriesSoFar += Volatile.Read(ref worker.Counters.Scanned) + Volatile.Read(ref worker.Counters.Denied) + Volatile.Read(ref worker.Counters.Failed);
                    filesSoFar += Volatile.Read(ref worker.Counters.Files);
                }
                progress(directoriesSoFar, filesSoFar);
            }
        }
        var walkTime = Stopwatch.GetElapsedTime(start);

        var failure = _failure;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        cancel.ThrowIfCancellationRequested();

        var totals = new Counters();
        var created = new List<List<DirNode>>(workers);
        var files = collectFiles ? new BoundedTop<FileHit>(top) : null;
        var rootFiles = new BoundedTop<FileHit>(top);
        var samples = new List<string>();
        foreach (var worker in all)
        {
            totals.Add(worker.Counters);
            created.Add(worker.Created);
            if (files is not null && worker.Files is not null) files.AddAll(worker.Files);
            rootFiles.AddAll(worker.RootFiles);
            foreach (var sample in worker.ErrorSamples)
            {
                if (samples.Count < Worker.MaxErrorSamples) samples.Add(sample);
            }
        }
        return new WalkResult(
            DirTable.Build(root, created),
            totals,
            files?.ToDescendingArray() ?? [],
            rootFiles.ToDescendingArray(),
            queue.PeakQueued,
            walkTime,
            reader.LargeFetch,
            RootRead,
            samples.ToArray());
    }
}
