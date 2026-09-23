// The unified dirsizer.exe's own tests: the selector/adapter/CLI layer unique to this project. DirSizer.Fs.Core's
// own 39 tests already cover the engine and run via dirsizer-fs.exe --self-test; this file does not repeat them.

// Named DirSizerSelfTest, not SelfTest, to avoid colliding with DirSizer.Fs.Core's own internal SelfTest record
// (visible here via InternalsVisibleTo).
readonly record struct DirSizerSelfTest(string Name, Action Body);

static class DirSizerSelfTests
{
    // Returns the process exit code: 0 if no test failed.
    public static int Run()
    {
        var tests = new List<DirSizerSelfTest>
        {
            new("selector picks filesystem (Step 1, the only strategy)", ScanStrategySelectorPicksFileSystem),
            new("unified result matches FsScanner.Scan's own result at the substance level", UnifiedResultMatchesFileSystemScannerSubstance),
            new("--enumerator and other strategy-naming flags are rejected: not part of this CLI", UnifiedCliOptionsRejectsEnumeratorAndStrategyFlags),
            new("options: defaults and the documented set are accepted", UnifiedCliOptionsAcceptsTheDocumentedSet),
            new("selector moves to the next strategy only on StrategyUnavailableException, nothing else", SelectorFallsBackOnlyOnStrategyUnavailable),
        };

        var failed = 0;
        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine($"ok    {test.Name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"FAIL  {test.Name}: {exception.Message}");
            }
        }
        Console.WriteLine(failed == 0 ? $"{tests.Count} self-tests passed, 0 skipped." : $"{failed} of {tests.Count} self-tests FAILED.");
        return failed == 0 ? 0 : 1;
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void AssertEqual<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    // A small fixture: a couple of files and a subdirectory, enough to exercise root/root-children/directories.
    static TempTree Fixture()
    {
        var tree = new TempTree();
        tree.MakeFile("a.bin", 1001);
        tree.MakeFile("sub\\b.bin", 2002);
        return tree;
    }

    static void ScanStrategySelectorPicksFileSystem()
    {
        using var tree = Fixture();
        var result = ScanStrategySelector.Scan(tree.Root, new UnifiedScanOptions(25, false, true, false, 0));
        AssertEqual("filesystem", result.Strategy, "strategy");
        AssertEqual(3003L, result.Root.Size, "root size (1001 + 2002)");
    }

    static void UnifiedResultMatchesFileSystemScannerSubstance()
    {
        using var tree = Fixture();
        var options = new UnifiedScanOptions(25, true, true, false, 0);
        var unified = new FileSystemStrategy().Scan(tree.Root, options);

        var settings = new ScanSettings(FsOptions.DefaultWorkers, 25, true, false);
        var fsResult = FsScanner.Scan(tree.Root, settings);
        var c = fsResult.Counters;

        AssertEqual(fsResult.Root.Size, unified.Root.Size, "root size");
        AssertEqual(c.DirectoriesScanned, unified.DirectoriesScanned, "directories_scanned");
        AssertEqual(c.DirectoriesDenied + c.DirectoriesFailed, unified.Unreadable, "unreadable");
        AssertEqual(c.Files, unified.FileCount, "file_count");
        AssertEqual(c.Bytes, unified.Bytes, "bytes");
        AssertEqual(fsResult.Directories.Length, unified.Directories.Length, "directories count");
        AssertEqual(fsResult.Files.Length, unified.Files.Length, "files count");

        var expectedDirs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in fsResult.Directories) expectedDirs.Add($"{item.Path}={item.Size}");
        var actualDirs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in unified.Directories) actualDirs.Add($"{item.Path}={item.Size}");
        Assert(expectedDirs.SetEquals(actualDirs), "directories (path, size) must match one-to-one");

        var expectedFiles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in fsResult.Files) expectedFiles.Add($"{item.Path}={item.Size}");
        var actualFiles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in unified.Files) actualFiles.Add($"{item.Path}={item.Size}");
        Assert(expectedFiles.SetEquals(actualFiles), "files (path, size) must match one-to-one");
    }

    static void UnifiedCliOptionsRejectsEnumeratorAndStrategyFlags()
    {
        AssertThrows(() => UnifiedCliOptions.Parse(["x", "--enumerator=find"]), "--enumerator is not a recognized option");
        AssertThrows(() => UnifiedCliOptions.Parse(["x", "--enumerator"]), "--enumerator (bare) is not a recognized option");
        AssertThrows(() => UnifiedCliOptions.Parse(["x", "--strategy=mft"]), "--strategy is not a recognized option");
    }

    static void AssertThrows(Action action, string what)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new Exception($"{what}: expected ArgumentException, nothing was thrown");
    }

    static void UnifiedCliOptionsAcceptsTheDocumentedSet()
    {
        var defaults = UnifiedCliOptions.Parse([@"C:\"]);
        AssertEqual(@"C:\", defaults.Root, "path");
        AssertEqual(25, defaults.Top, "default top");
        AssertEqual(0, defaults.Workers, "default workers (automatic)");
        Assert(defaults.Dirs && !defaults.Files && !defaults.Json && !defaults.Strict && !defaults.Benchmark, "default flags");

        var all = UnifiedCliOptions.Parse([@"D:\data", "--top=7", "--files", "--json", "--strict", "--benchmark", "--workers", "3"]);
        AssertEqual(7, all.Top, "--top=N");
        AssertEqual(3, all.Workers, "--workers N");
        Assert(all.Files && all.Json && all.Strict && all.Benchmark, "flags");
    }

    // A fake IScanStrategy whose Scan() either throws StrategyUnavailableException, throws something else, or
    // returns a fixed result -- lets the selector's fallback logic be driven deterministically, without a real
    // NTFS/admin environment. Mirrors FallbackEnumerator's own test shape from the enumerator-fallback work.
    sealed class FakeStrategy(string name, Func<UnifiedScanResult> behavior) : IScanStrategy
    {
        public string Name => name;
        public UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options) => behavior();
    }

    static UnifiedScanResult FixedResult(string strategy) =>
        new("C:\\", 25, new UnifiedItem("C:\\", 1), [], [], [], 1, 0, 0, 1, [], strategy, null, null, 1.0);

    static void SelectorFallsBackOnlyOnStrategyUnavailable()
    {
        // Case 1: the first strategy is unavailable -> the second one's result is returned.
        IScanStrategy[] unavailableThenReal =
        [
            new FakeStrategy("fake-unavailable", () => throw new StrategyUnavailableException("fake-unavailable", new InvalidOperationException("no access"))),
            new FakeStrategy("fake-real", () => FixedResult("fake-real")),
        ];
        var result1 = ScanStrategySelector.Scan("C:\\", new UnifiedScanOptions(25, false, true, false, 0), unavailableThenReal);
        AssertEqual("fake-real", result1.Strategy, "case 1: falls through to the second strategy");

        // Case 2: the first strategy succeeds -> its own result is returned, the second is never reached.
        var secondCalled = false;
        IScanStrategy[] realThenTracked =
        [
            new FakeStrategy("fake-real", () => FixedResult("fake-real")),
            new FakeStrategy("fake-unreached", () => { secondCalled = true; return FixedResult("fake-unreached"); }),
        ];
        var result2 = ScanStrategySelector.Scan("C:\\", new UnifiedScanOptions(25, false, true, false, 0), realThenTracked);
        AssertEqual("fake-real", result2.Strategy, "case 2: the first strategy's own result is used");
        Assert(!secondCalled, "case 2: the second strategy is never called once the first succeeds");

        // Case 3: any other exception is not caught -- it must propagate, not be treated as unavailable.
        IScanStrategy[] throwsOther = [new FakeStrategy("fake-broken", () => throw new InvalidOperationException("a real scan failure"))];
        try
        {
            ScanStrategySelector.Scan("C:\\", new UnifiedScanOptions(25, false, true, false, 0), throwsOther);
            throw new Exception("case 3: expected InvalidOperationException to propagate, nothing was thrown");
        }
        catch (InvalidOperationException exception)
        {
            AssertEqual("a real scan failure", exception.Message, "case 3: the real exception, not swallowed or replaced");
        }
    }
}
