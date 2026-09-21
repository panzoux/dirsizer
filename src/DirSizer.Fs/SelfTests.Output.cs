using System.Text.Json;

// Text and JSON output of a real scan.
static partial class FsSelfTests
{
    static partial void AddOutputTests(List<SelfTest> tests)
    {
        tests.Add(new("text output has the tables and the summary", TextOutput));
        tests.Add(new("JSON output has the documented fields", JsonOutput));
        tests.Add(new("unreadable directories give a warning, the counters and the error samples", UnreadableDirectoriesAreReported));
    }

    // A result made by hand, so the reporting of denied and failed directories is tested without a file system.
    static FsResult SyntheticResult(long denied, long failed, string[] samples)
    {
        var zero = TimeSpan.Zero;
        var counters = new FsCounters(5, denied, failed, 1, 8, 20, 1234);
        var metrics = new FsMetrics(zero, TimeSpan.FromSeconds(1), zero, zero, TimeSpan.FromSeconds(1), zero, zero, 4, true, 0, 30, 8, 1234, 0, 0);
        return new FsResult(@"C:\r", new ResultItem(@"C:\r", 1234), [], [new ResultItem(@"C:\r", 1234)], [], counters, metrics, samples, [new DirNode(0, -1, @"C:\r")]);
    }

    static void UnreadableDirectoriesAreReported()
    {
        var sample = @"C:\r\x: The network path was not found. (error 53)";
        var text = new StringWriter();
        var error = new StringWriter();
        FsOutput.Write(SyntheticResult(denied: 2, failed: 1, [sample]), FsOptions.Parse([@"C:\r"]), text, error);
        Assert(error.ToString().Contains("warning: 3 directories could not be read (denied 2, failed 1); the sizes are a lower bound."), "the warning gives the counts");
        Assert(error.ToString().Contains("  failed: " + sample), "the error sample is listed");
        Assert(text.ToString().Contains("directories_denied=2 directories_failed=1"), "the summary shows both counters");

        var json = new StringWriter();
        FsOutput.Write(SyntheticResult(denied: 2, failed: 1, [sample]), FsOptions.Parse([@"C:\r", "--json"]), json, new StringWriter());
        using var document = JsonDocument.Parse(json.ToString());
        var statistics = document.RootElement.GetProperty("statistics");
        AssertEqual(2L, statistics.GetProperty("directories_denied").GetInt64(), "json denied");
        AssertEqual(1L, statistics.GetProperty("directories_failed").GetInt64(), "json failed");
        AssertEqual(sample, statistics.GetProperty("error_samples")[0].GetString(), "json error sample");

        var quietError = new StringWriter();
        FsOutput.Write(SyntheticResult(0, 0, []), FsOptions.Parse([@"C:\r"]), new StringWriter(), quietError);
        AssertEqual("", quietError.ToString(), "nothing on stderr when every directory was read");
    }

    static void TextOutput()
    {
        using var tree = StandardTree();
        var options = FsOptions.Parse([tree.Root, "--files", "--top=3", "--benchmark"]);
        var result = Scan(tree.Root, 2, files: true, top: 3);
        var text = new StringWriter();
        var error = new StringWriter();
        FsOutput.Write(result, options, text, error);
        var output = text.ToString();
        Assert(output.Contains("Directories (largest 3)"), "directory table");
        Assert(output.Contains("Files (largest 3)"), "file table");
        Assert(output.Contains($"bytes={result.Root.Size}"), "summary shows the byte total");
        Assert(output.Contains("directories_denied=0 directories_failed=0"), "summary shows the error counters");
        Assert(output.Contains("7,011"), "sizes are grouped");
        Assert(error.ToString().Contains("benchmark: workers=2"), "--benchmark prints the timings to stderr");
        Assert(!error.ToString().Contains("warning"), "no warning when every directory was read");
    }

    static void JsonOutput()
    {
        using var tree = StandardTree();
        var options = FsOptions.Parse([tree.Root, "--json", "--top=3"]);
        var result = Scan(tree.Root, 2, top: 3);
        var text = new StringWriter();
        FsOutput.Write(result, options, text, new StringWriter());
        using var document = JsonDocument.Parse(text.ToString());
        var root = document.RootElement;
        AssertEqual("win32-find", root.GetProperty("reader").GetString(), "reader");
        AssertEqual("logical", root.GetProperty("size_mode").GetString(), "size_mode");
        AssertEqual(3, root.GetProperty("top").GetInt32(), "top");
        AssertEqual(result.Root.Size, root.GetProperty("root").GetProperty("size").GetInt64(), "root size is a number");
        AssertEqual(tree.Root, root.GetProperty("volume").GetString(), "volume is the root path");
        AssertEqual(3, root.GetProperty("directories").GetArrayLength(), "directories limited to top");
        AssertEqual(3, root.GetProperty("root_children").GetArrayLength(), "root_children limited to top");
        AssertEqual(0, root.GetProperty("files").GetArrayLength(), "files are empty without --files");
        var statistics = root.GetProperty("statistics");
        AssertEqual(result.Counters.Directories, statistics.GetProperty("directories").GetInt64(), "statistics.directories");
        AssertEqual(result.Root.Size, statistics.GetProperty("bytes").GetInt64(), "statistics.bytes equals root.size");
        foreach (var name in new[] { "directories_scanned", "directories_denied", "directories_failed", "reparse_skipped", "files", "error_samples" })
            Assert(statistics.TryGetProperty(name, out _), $"statistics.{name} is present");
        var performance = statistics.GetProperty("performance");
        foreach (var name in new[] { "open_ms", "walk_ms", "aggregation_ms", "finalize_ms", "other_ms", "total_ms", "phase_sum_ms", "enum_ms_total", "idle_ms_total", "workers", "large_fetch", "peak_queued_dirs", "entries_per_sec", "directories_per_sec", "logical_mib_per_sec" })
            Assert(performance.TryGetProperty(name, out _), $"performance.{name} is present");
        AssertEqual(2, performance.GetProperty("workers").GetInt32(), "performance.workers");
    }
}
