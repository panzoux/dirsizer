using System.Security.Principal;
using System.Text;

// The whole walk on real temporary folders, checked against an independent computation.
static partial class FsSelfTests
{
    const int WideFanOut = 10_000;
    const int DeepChain = 800;

    static partial void AddWalkTests(List<SelfTest> tests)
    {
        tests.Add(new("walk matches the independent oracle for 1, 3 and 8 workers", WalkMatchesOracle));
        tests.Add(new("walk does not enter a junction, but enters one named as the root", WalkJunction));
        tests.Add(new("walk counts a hard link in every directory that holds a name", WalkHardLink));
        tests.Add(new("walk skips a directory it may not read and counts it", WalkDenied));
        tests.Add(new("walk finishes on a very deep chain (1 and 8 workers)", WalkDeepChain));
        tests.Add(new("walk finishes on a very wide fan-out and reports the queue peak", WalkWideFanOut));
        tests.Add(new("walk with LARGE_FETCH rejected: one worker fails once, eight at most eight times", WalkLargeFetchRejected));
        tests.Add(new("walk counts a directory that fails and keeps the rest", WalkFailedDirectory));
        tests.Add(new("walk reports a bad root or bad settings as an error", WalkBadRoot));
        tests.Add(new("walk stops when canceled, before and while it runs", WalkCanceled));
        tests.Add(new("walk stops and rethrows when a worker throws", WalkWorkerThrows));
        tests.Add(new("walk leaves no worker running when the caller fails", WalkStopsWorkersWhenTheCallerFails));
    }

    static FsResult Scan(string root, int workers, bool files = false, int top = 25, CancellationToken cancel = default, FindFirstFn? findFirst = null) =>
        FsScanner.Scan(root, new ScanSettings(workers, top, files, false, cancel, findFirst));

    // Nested and empty directories, a zero-byte file, Unicode names, a path over 260 characters, and files of distinct sizes.
    static TempTree StandardTree()
    {
        var tree = new TempTree();
        tree.MakeFile("a.bin", 1001);
        tree.MakeDir("empty");
        tree.MakeFile("zero.txt", 0);
        tree.MakeFile("Ünï\\日本語.txt", 2003);
        tree.MakeFile("deep\\l1\\l2\\l3\\f.bin", 4007);
        for (var index = 0; index < 50; index++) tree.MakeFile($"wide\\d{index:000}\\f.bin", 5000 + index);
        var segment = new string('x', 60);
        var nested = new StringBuilder(segment);
        for (var index = 0; index < 5; index++) nested.Append('\\').Append(segment);
        tree.MakeFile($"long\\{nested}\\deep.bin", 7011);
        return tree;
    }

    // The independent computation: the framework's own directory enumeration, no code from the tool. Keys are paths below the root.
    static (Dictionary<string, long> Totals, long Files) Oracle(string extendedRoot)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        long files = 0;
        long Walk(DirectoryInfo directory, string relative)
        {
            long sum = 0;
            foreach (var file in directory.EnumerateFiles())
            {
                sum += file.Length;
                files++;
            }
            foreach (var child in directory.EnumerateDirectories())
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                sum += Walk(child, relative.Length == 0 ? child.Name : relative + "\\" + child.Name);
            }
            totals[relative] = sum;
            return sum;
        }
        Walk(new DirectoryInfo(extendedRoot), "");
        return (totals, files);
    }

    static Dictionary<string, long> TotalsOf(DirNode[] nodes)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var node in nodes) totals[DirTable.RelativePath(nodes, node.Id)] = node.Total;
        return totals;
    }

    static void AssertSameTotals(Dictionary<string, long> expected, Dictionary<string, long> actual, string label)
    {
        AssertEqual(expected.Count, actual.Count, $"{label}: number of directories");
        foreach (var (path, total) in expected)
        {
            Assert(actual.TryGetValue(path, out var found), $"{label}: directory missing: '{path}'");
            AssertEqual(total, found, $"{label}: total of '{path}'");
        }
    }

    static long[] Sizes(ResultItem[] items)
    {
        var sizes = new long[items.Length];
        for (var index = 0; index < items.Length; index++) sizes[index] = items[index].Size;
        return sizes;
    }

    static void WalkMatchesOracle()
    {
        using var tree = StandardTree();
        var (expected, fileCount) = Oracle(tree.Base);
        foreach (var workers in new[] { 1, 3, 8 })
        {
            var label = $"workers={workers}";
            var result = Scan(tree.Root, workers, files: true, top: 5);
            AssertSameTotals(expected, TotalsOf(result.Nodes), label);
            AssertEqual(fileCount, result.Counters.Files, $"{label}: files");
            AssertEqual((long)expected.Count, result.Counters.Directories, $"{label}: directories");
            AssertEqual(result.Counters.Bytes, result.Root.Size, $"{label}: the root total equals the sum of every file size");
            AssertEqual(result.Counters.Directories, result.Counters.DirectoriesScanned, $"{label}: every directory was read");
            AssertEqual(0L, result.Counters.Unreadable + result.Counters.ReparseSkipped, $"{label}: nothing skipped");
            AssertEqual(tree.Root, result.Root.Path, $"{label}: root path");
            AssertEqual(result.Metrics.Total, result.Metrics.PhaseSum, $"{label}: phases add up to the total");
            Assert(result.Metrics.Other >= TimeSpan.Zero && result.Metrics.Walk <= result.Metrics.Total, $"{label}: the residual is not negative and the walk fits in the total");

            // Distinct file sizes, so the order is fully defined.
            AssertEqual("7011,5049,5048,5047,5046", string.Join(',', Sizes(result.Files)), $"{label}: largest files");
            AssertEqual(result.Root.Size, result.Directories[0].Size, $"{label}: the root is the largest directory");
            AssertEqual(tree.Root, result.Directories[0].Path, $"{label}: and is listed first");
            // root children: wide (50 files 5000..5049), long, deep, the Unicode directory, then a.bin; the empty directory and zero.txt are below the cut.
            AssertEqual("251225,7011,4007,2003,1001", string.Join(',', Sizes(result.RootChildren)), $"{label}: root children");
            Assert(result.RootChildren[4].Path.EndsWith("\\a.bin"), $"{label}: a root-level file appears among the root's children");
        }

        // The boundary of the bounded selections: one result each.
        var single = Scan(tree.Root, 2, files: true, top: 1);
        AssertEqual("7011", string.Join(',', Sizes(single.Files)), "top=1: the largest file");
        AssertEqual(1, single.Directories.Length, "top=1: one directory");
        AssertEqual(tree.Root, single.Directories[0].Path, "top=1: the root");
        AssertEqual("251225", string.Join(',', Sizes(single.RootChildren)), "top=1: the largest root child");
    }

    static void WalkJunction()
    {
        using var tree = new TempTree();
        using var outside = new TempTree();
        outside.MakeFile("target.bin", 9013);
        tree.MakeFile("inside.bin", 100);
        var link = tree.Root + "\\link";
        if (RunProgram("cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Root}\"") != 0) throw new SkipException("cannot create a junction here");
        tree.OnDispose(() => Directory.Delete(tree.Full("link")));

        var scan = Scan(tree.Root, 4);
        AssertEqual(100L, scan.Root.Size, "the junction's target is not counted");
        AssertEqual(1L, scan.Counters.ReparseSkipped, "the junction is counted as skipped");
        AssertEqual(1L, scan.Counters.Directories, "the junction is not a directory node");

        var viaRoot = Scan(link, 4);
        AssertEqual(9013L, viaRoot.Root.Size, "a junction named as the root is entered");
        AssertEqual(0L, viaRoot.Counters.ReparseSkipped, "nothing is skipped below it");
    }

    static void WalkHardLink()
    {
        using var tree = new TempTree();
        tree.MakeFile("a\\f.bin", 3000);
        tree.MakeDir("b");
        if (RunProgram("cmd.exe", $"/c mklink /H \"{tree.Root}\\b\\g.bin\" \"{tree.Root}\\a\\f.bin\"") != 0) throw new SkipException("cannot create a hard link here (not NTFS?)");
        var result = Scan(tree.Root, 2);
        var totals = TotalsOf(result.Nodes);
        AssertEqual(3000L, totals["a"], "directory a");
        AssertEqual(3000L, totals["b"], "directory b: the other name of the same file counts too");
        AssertEqual(6000L, totals[""], "the root counts the file once per name");
    }

    static void WalkDenied()
    {
        using var tree = new TempTree();
        tree.MakeFile("open\\f.bin", 111);
        tree.MakeFile("locked\\g.bin", 222);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new SkipException("cannot determine the current user");
        var locked = tree.Root + "\\locked";
        if (RunProgram("icacls.exe", $"\"{locked}\" /deny \"*{sid}:(RD)\"") != 0) throw new SkipException("cannot set a deny ACL here");
        tree.OnDispose(() => RunProgram("icacls.exe", $"\"{locked}\" /remove:d \"*{sid}\""));
        // The precondition of the whole test: the ACL must really keep this process out. Some environments (a restricted or sandboxed
        // token) ignore the deny entry, and then there is nothing to test.
        try
        {
            Directory.GetFileSystemEntries(locked);
            throw new SkipException("the deny ACL has no effect for this process (restricted token?)");
        }
        catch (UnauthorizedAccessException)
        {
        }

        var result = Scan(tree.Root, 4);
        var counters = result.Counters;
        AssertEqual(1L, counters.DirectoriesDenied, $"the locked directory is counted (scanned {counters.DirectoriesScanned}, failed {counters.DirectoriesFailed}; {string.Join(" | ", result.ErrorSamples)})");
        AssertEqual(0L, result.Counters.DirectoriesFailed, "and is not a failure");
        AssertEqual(111L, result.Root.Size, "the readable part of the tree is complete; the locked file is not counted");
        AssertEqual(3L, result.Counters.Directories, "the locked directory is still a node");

        var rootError = ThrownIOException(() => Scan(locked, 2));
        Assert(rootError.Message.Contains("Cannot read"), "an unreadable root is an error, not a result");
    }

    static IOException ThrownIOException(Action action)
    {
        try
        {
            action();
        }
        catch (IOException exception)
        {
            return exception;
        }
        throw new Exception("expected IOException, nothing was thrown");
    }

    static void WalkDeepChain()
    {
        using var tree = new TempTree();
        var chain = new StringBuilder("d");
        for (var index = 1; index < DeepChain; index++) chain.Append("\\d");
        tree.MakeFile(chain + "\\bottom.bin", 12345);
        foreach (var workers in new[] { 1, 8 })
        {
            var result = Scan(tree.Root, workers);
            AssertEqual(12345L, result.Root.Size, $"workers={workers}: the file at the bottom reaches the root");
            AssertEqual((long)DeepChain + 1, result.Counters.Directories, $"workers={workers}: directories");
        }
    }

    static void WalkWideFanOut()
    {
        using var tree = new TempTree();
        tree.MakeFile("many\\f.bin", 7);
        for (var index = 0; index < WideFanOut; index++) tree.MakeDir($"many\\e{index:00000}");
        foreach (var workers in new[] { 1, 8 })
        {
            var result = Scan(tree.Root, workers);
            AssertEqual(7L, result.Root.Size, $"workers={workers}: total");
            AssertEqual((long)WideFanOut + 2, result.Counters.Directories, $"workers={workers}: directories");
            // One worker pushes all the sub-directories before any of them is taken: the stack really is unbounded.
            Assert(result.Metrics.PeakQueuedDirs >= WideFanOut, $"workers={workers}: peak queued directories was {result.Metrics.PeakQueuedDirs}, expected at least {WideFanOut}");
            Console.WriteLine($"      workers={workers}: peak_queued_dirs={result.Metrics.PeakQueuedDirs}, peak_working_set={result.Metrics.PeakWorkingSetBytes / 1048576} MiB");
        }
    }

    static void WalkLargeFetchRejected()
    {
        using var tree = StandardTree();
        var (expected, _) = Oracle(tree.Base);

        var one = new LargeFetchRejecter(Win32Find.ErrorInvalidParameter);
        var single = Scan(tree.Root, 1, findFirst: one.Call);
        AssertSameTotals(expected, TotalsOf(single.Nodes), "workers=1");
        AssertEqual(1, CountTrue(one.Calls), "one worker: exactly one call with the flag, and it failed");
        AssertEqual(true, one.Calls[0], "the first call is the one with the flag");
        Assert(!single.Metrics.LargeFetch, "the flag is reported off");
        Assert(one.Calls.Count > 2, "the walk went on making calls");

        var many = new LargeFetchRejecter(Win32Find.ErrorInvalidParameter);
        var parallel = Scan(tree.Root, 8, findFirst: many.Call);
        AssertSameTotals(expected, TotalsOf(parallel.Nodes), "workers=8");
        Assert(CountTrue(many.Calls) <= 8, $"eight workers: at most eight calls with the flag, got {CountTrue(many.Calls)}");
        Assert(!parallel.Metrics.LargeFetch, "the flag is reported off");
    }

    static int CountTrue(List<bool> values)
    {
        var count = 0;
        foreach (var value in values)
        {
            if (value) count++;
        }
        return count;
    }

    // Find-first fails with ERROR_SHARING_VIOLATION (32) for the directory "bad" and is the real call for every other directory.
    static void WalkFailedDirectory()
    {
        using var tree = new TempTree();
        tree.MakeFile("ok\\f.bin", 111);
        tree.MakeFile("bad\\g.bin", 222);
        tree.MakeFile("z.bin", 5);
        FindFirstResult FailBad(string pattern, ref Win32FindData data, bool largeFetch) =>
            pattern.Contains("\\bad\\") ? new FindFirstResult(Win32Find.InvalidHandle, 32) : Win32Find.FindFirst(pattern, ref data, largeFetch);
        foreach (var workers in new[] { 1, 4 })
        {
            var label = $"workers={workers}";
            var result = Scan(tree.Root, workers, findFirst: FailBad);
            AssertEqual(116L, result.Root.Size, $"{label}: everything except the failed directory is counted");
            AssertEqual(result.Counters.Bytes, result.Root.Size, $"{label}: the total still equals the sum of the file sizes");
            AssertEqual(1L, result.Counters.DirectoriesFailed, $"{label}: one failed directory");
            AssertEqual(0L, result.Counters.DirectoriesDenied, $"{label}: and it is not counted as denied");
            AssertEqual(2L, result.Counters.DirectoriesScanned, $"{label}: the root and ok were read");
            AssertEqual(3L, result.Counters.Directories, $"{label}: the failed directory is still a node");
            AssertEqual(1, result.ErrorSamples.Length, $"{label}: one error sample");
            Assert(result.ErrorSamples[0].Contains("\\bad") && result.ErrorSamples[0].Contains("error 32"), $"{label}: the sample names the directory and the error: {result.ErrorSamples[0]}");
        }
    }

    static void WalkBadRoot()
    {
        using var tree = new TempTree();
        tree.MakeFile("a.bin", 1);
        AssertThrows<ArgumentException>(() => Scan(tree.Root + "\\missing", 2), "missing directory");
        AssertThrows<ArgumentException>(() => Scan(tree.Root + "\\a.bin", 2), "a file is not a directory");
        // No worker would read anything and the result would look like an empty tree.
        AssertThrows<ArgumentException>(() => Scan(tree.Root, 0), "zero workers");
        AssertThrows<ArgumentException>(() => Scan(tree.Root, -1), "a negative number of workers");
        AssertEqual(ReadOutcome.NotRead, default(ReadResult).Outcome, "a read result that was never filled in is not a success");
    }

    static void WalkCanceled()
    {
        using var tree = StandardTree();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        AssertThrows<OperationCanceledException>(() => Scan(tree.Root, 4, cancel: canceled.Token), "canceled token");

        // Canceled while the walk is running: every find-first call is slow, so the workers are busy or waiting when the token fires.
        FindFirstResult Slow(string pattern, ref Win32FindData data, bool largeFetch)
        {
            Thread.Sleep(40);
            return Win32Find.FindFirst(pattern, ref data, largeFetch);
        }
        using var late = new CancellationTokenSource();
        late.CancelAfter(150);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        AssertThrows<OperationCanceledException>(() => Scan(tree.Root, 4, cancel: late.Token, findFirst: Slow), "canceled while running");
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        Assert(elapsed < TimeSpan.FromSeconds(10), $"the walk stopped promptly after the cancel ({elapsed.TotalSeconds:N1} s)");
    }

    static void WalkWorkerThrows()
    {
        using var tree = StandardTree();
        FindFirstResult Boom(string pattern, ref Win32FindData data, bool largeFetch) => throw new InvalidOperationException("boom");
        try
        {
            Scan(tree.Root, 4, findFirst: Boom);
            throw new Exception("an exception in a worker must reach the caller, but nothing was thrown");
        }
        catch (InvalidOperationException exception)
        {
            AssertEqual("boom", exception.Message, "the worker's own exception reaches the caller (and the walk does not hang)");
        }
    }

    // If Walker.Run is left early (here: the progress callback throws) no worker may go on walking in the background.
    static void WalkStopsWorkersWhenTheCallerFails()
    {
        using var tree = StandardTree();
        var calls = 0;
        FindFirstResult Slow(string pattern, ref Win32FindData data, bool largeFetch)
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(100);
            return Win32Find.FindFirst(pattern, ref data, largeFetch);
        }
        var root = new DirNode(0, -1, tree.Root);
        try
        {
            new Walker(Slow).Run(root, tree.Base, 2, 5, false, default, (directories, files) => throw new InvalidOperationException("progress failed"));
            throw new Exception("the failing progress callback must reach the caller, but nothing was thrown");
        }
        catch (InvalidOperationException exception)
        {
            AssertEqual("progress failed", exception.Message, "the callback's exception reaches the caller");
        }
        var atThrow = Volatile.Read(ref calls);
        Thread.Sleep(500);
        AssertEqual(atThrow, Volatile.Read(ref calls), "no worker is still walking after Run has thrown");
        Assert(atThrow < 65, $"the walk was cut short ({atThrow} of 65 directories were read)");
    }
}
