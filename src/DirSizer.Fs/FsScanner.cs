using System.Diagnostics;
using System.Runtime.InteropServices;

readonly record struct ResultItem(string Path, long Size);

sealed record FsCounters(long DirectoriesScanned, long DirectoriesDenied, long DirectoriesFailed, long ReparseSkipped, long Directories, long Files, long Bytes)
{
    public long Unreadable => DirectoriesDenied + DirectoriesFailed;
}

// Wall-clock phases (open, walk, aggregation, finalize, other) add up to Total exactly. EnumTotal and IdleTotal are summed over
// all workers while they run in parallel inside `walk`, so they are diagnostics and not phases.
sealed record FsMetrics(
    TimeSpan Open, TimeSpan Walk, TimeSpan Aggregation, TimeSpan Finalize, TimeSpan Total,
    TimeSpan EnumTotal, TimeSpan IdleTotal, int Workers, bool LargeFetch, int PeakQueuedDirs,
    long Entries, long Directories, long Bytes, long ManagedAllocatedBytes, long PeakWorkingSetBytes)
{
    public TimeSpan Other => TimeSpan.FromTicks(Math.Max(0, Total.Ticks - Open.Ticks - Walk.Ticks - Aggregation.Ticks - Finalize.Ticks));
    public TimeSpan PhaseSum => Open + Walk + Aggregation + Finalize + Other;
    public double EntriesPerSec => Walk.TotalSeconds == 0 ? 0 : Entries / Walk.TotalSeconds;
    public double DirectoriesPerSec => Walk.TotalSeconds == 0 ? 0 : Directories / Walk.TotalSeconds;
    public double LogicalMibPerSec => Walk.TotalSeconds == 0 ? 0 : Bytes / 1048576.0 / Walk.TotalSeconds;
}

sealed record FsResult(
    string RootPath,
    ResultItem Root,
    ResultItem[] RootChildren,
    ResultItem[] Directories,
    ResultItem[] Files,
    FsCounters Counters,
    FsMetrics Metrics,
    string[] ErrorSamples,
    DirNode[] Nodes);

sealed record ScanSettings(int Workers, int Top, bool CollectFiles, bool ShowProgress, CancellationToken Cancel = default, IEnumeratorFactory? Enumerators = null);

readonly record struct RootChild(DirNode? Directory, FileHit File);

static class FsScanner
{
    // Throws ArgumentException (bad settings, bad or missing root), IOException (root cannot be read), OperationCanceledException.
    public static FsResult Scan(string rootArgument, ScanSettings settings)
    {
        // With no worker nothing would be read; the walk would end at once and the result would look like an empty tree.
        if (settings.Workers < 1) throw new ArgumentOutOfRangeException(nameof(settings), "The number of workers must be at least 1.");
        var total = Stopwatch.StartNew();
        var allocatedAtStart = GC.GetTotalAllocatedBytes();

        var rootPath = RootPath.Normalize(rootArgument);
        if (!Directory.Exists(rootPath.Extended)) throw new ArgumentException($"Not a directory, or not found: {rootPath.Display}");
        var open = total.Elapsed;

        var root = new DirNode(0, -1, rootPath.Display);
        Action<long, long>? progress = settings.ShowProgress
            ? (directories, files) => Console.Error.Write($"\rScanning: {directories:N0} directories, {files:N0} files")
            : null;
        WalkResult walk;
        try
        {
            walk = new Walker(settings.Enumerators).Run(root, rootPath.Extended, settings.Workers, settings.Top, settings.CollectFiles, settings.Cancel, progress);
        }
        finally
        {
            // Also when the walk failed or was canceled: the error message must not follow a half-written progress line.
            if (settings.ShowProgress) Console.Error.Write("\r" + new string(' ', 60) + "\r");
        }
        if (walk.RootRead.Outcome == ReadOutcome.NotRead) throw new InvalidOperationException("The root directory was never read (internal error).");
        if (walk.RootRead.Outcome != ReadOutcome.Complete)
        {
            var hint = walk.RootRead.Outcome == ReadOutcome.Denied ? " Choose a directory you can read, or start the tool from an elevated terminal." : "";
            throw new IOException($"Cannot read {rootPath.Display}: {Marshal.GetPInvokeErrorMessage(walk.RootRead.Error).TrimEnd()} (error {walk.RootRead.Error}).{hint}");
        }

        var mark = total.Elapsed;
        var nodes = walk.Nodes;
        DirTable.Aggregate(nodes);
        var aggregation = total.Elapsed - mark;

        mark = total.Elapsed;
        var top = settings.Top;
        var directoryTop = new BoundedTop<DirNode>(top);
        var rootChildTop = new BoundedTop<RootChild>(top);
        foreach (var node in nodes)
        {
            directoryTop.Add(node, node.Total);
            if (node.ParentId == 0) rootChildTop.Add(new RootChild(node, default), node.Total);
        }
        foreach (var hit in walk.RootFiles) rootChildTop.Add(new RootChild(null, hit), hit.Size);

        var directories = new List<ResultItem>();
        foreach (var node in directoryTop.ToDescendingArray()) directories.Add(new ResultItem(DirTable.PathOf(nodes, node.Id), node.Total));
        var rootChildren = new List<ResultItem>();
        foreach (var child in rootChildTop.ToDescendingArray())
        {
            rootChildren.Add(child.Directory is { } directory
                ? new ResultItem(DirTable.PathOf(nodes, directory.Id), directory.Total)
                : new ResultItem(DirTable.Combine(nodes[0].Name, child.File.Name), child.File.Size));
        }
        var files = new List<ResultItem>();
        foreach (var hit in walk.Files) files.Add(new ResultItem(DirTable.Combine(DirTable.PathOf(nodes, hit.DirId), hit.Name), hit.Size));
        var finalize = total.Elapsed - mark;

        var totals = walk.Totals;
        var counters = new FsCounters(totals.Scanned, totals.Denied, totals.Failed, totals.ReparseSkipped, nodes.Length, totals.Files, totals.Bytes);
        using var process = Process.GetCurrentProcess();
        var metrics = new FsMetrics(
            open, walk.WalkTime, aggregation, finalize, total.Elapsed,
            TimeSpan.FromSeconds((double)totals.EnumTicks / Stopwatch.Frequency),
            TimeSpan.FromSeconds((double)totals.IdleTicks / Stopwatch.Frequency),
            settings.Workers, walk.LargeFetch, walk.PeakQueuedDirs,
            totals.Entries, nodes.Length, totals.Bytes,
            GC.GetTotalAllocatedBytes() - allocatedAtStart, process.PeakWorkingSet64);
        return new FsResult(rootPath.Display, new ResultItem(rootPath.Display, nodes[0].Total), rootChildren.ToArray(), directories.ToArray(), files.ToArray(), counters, metrics, walk.ErrorSamples, nodes);
    }
}
