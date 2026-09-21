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
        var metrics = new FsMetrics(zero, TimeSpan.FromSeconds(1), zero, zero, TimeSpan.FromSeconds(1), zero, zero, 4, true, "find", 0, 30, 8, 1234, 0, 0);
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
        Assert(!error.ToString().Contains("more failed"), "no 'more failed' line when every failed directory has a sample");

        // More failed directories than samples: the reader is told that the list is cut.
        var cutError = new StringWriter();
        FsOutput.Write(SyntheticResult(denied: 0, failed: 25, [sample]), FsOptions.Parse([@"C:\r"]), new StringWriter(), cutError);
        Assert(cutError.ToString().Contains("... and 24 more failed directories that are not listed"), "the sample list says that it is cut");

        // JSON mode with --benchmark: stdout stays exactly one JSON document; the warning and the benchmark line go to stderr.
        var jsonOut = new StringWriter();
        var jsonErr = new StringWriter();
        FsOutput.Write(SyntheticResult(denied: 2, failed: 1, [sample]), FsOptions.Parse([@"C:\r", "--json", "--benchmark"]), jsonOut, jsonErr);
        using (JsonDocument.Parse(jsonOut.ToString())) { }   // throws unless stdout holds one JSON document and nothing else
        Assert(jsonErr.ToString().Contains("warning:") && jsonErr.ToString().Contains("benchmark: workers=4"), "the warning and the benchmark line are on stderr");
        Assert(!jsonOut.ToString().Contains("warning:") && !jsonOut.ToString().Contains("benchmark:"), "and not in the JSON");

        // The benchmark line can be split on "," and "=" whatever the size of the numbers (no thousands separators).
        var big = SyntheticResult(0, 0, []);
        big = big with { Metrics = big.Metrics with { Walk = TimeSpan.FromMilliseconds(19973.4), Total = TimeSpan.FromMilliseconds(20000), Entries = 2_500_000 } };
        var bigError = new StringWriter();
        FsOutput.Write(big, FsOptions.Parse([@"C:\r", "--benchmark"]), new StringWriter(), bigError);
        var line = bigError.ToString().Trim();
        Assert(line.Contains("walk_ms=19973.4"), "a walk of 19973.4 ms is written without a thousands separator");
        AssertEqual(line.Split(',').Length, line.Split('=').Length - 1, "every field between commas has exactly one '=': no comma inside a value");

        // Non-ASCII and volume paths survive a round trip through JSON, and the JSON itself is ASCII only.
        var volumePath = "\\\\?\\Volume{12345678-1234-1234-1234-123456789abc}\\\u65e5\u672c\u8a9e & 'x'";
        var unicode = SyntheticResult(0, 0, []) with { Directories = [new ResultItem(volumePath, 1)] };
        var unicodeOut = new StringWriter();
        FsOutput.Write(unicode, FsOptions.Parse([@"C:\r", "--json"]), unicodeOut, new StringWriter());
        using var unicodeDocument = JsonDocument.Parse(unicodeOut.ToString());
        AssertEqual(volumePath, unicodeDocument.RootElement.GetProperty("directories")[0].GetProperty("path").GetString(), "the path comes back unchanged");
        foreach (var character in unicodeOut.ToString()) Assert(character < 128, "the JSON text is ASCII only (non-ASCII characters are escaped)");
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
        Assert(error.ToString().Contains("benchmark: workers=2, enumerator=find, large_fetch=on"), "--benchmark prints the enumerator and the timings to stderr");
        Assert(!error.ToString().Contains("warning"), "no warning when every directory was read");

        var plain = new StringWriter();
        FsOutput.Write(Scan(tree.Root, 2, top: 3), FsOptions.Parse([tree.Root, "--top=3"]), plain, new StringWriter());
        Assert(plain.ToString().Contains("Directories (largest 3)"), "the directory table is always shown");
        Assert(!plain.ToString().Contains("Files (largest"), "the file table only with --files");
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
        foreach (var name in new[] { "open_ms", "walk_ms", "aggregation_ms", "finalize_ms", "other_ms", "total_ms", "phase_sum_ms", "enum_ms_total", "idle_ms_total", "workers", "large_fetch", "enumerator", "peak_queued_dirs", "managed_allocated_bytes", "peak_working_set_bytes", "entries_per_sec", "directories_per_sec", "logical_mib_per_sec" })
            Assert(performance.TryGetProperty(name, out _), $"performance.{name} is present");
        AssertEqual(2, performance.GetProperty("workers").GetInt32(), "performance.workers");
        Assert(performance.GetProperty("large_fetch").ValueKind is JsonValueKind.True or JsonValueKind.False, "performance.large_fetch is a JSON boolean");
        Assert(performance.GetProperty("total_ms").ValueKind == JsonValueKind.Number, "timings are JSON numbers");

        // root_children: contents and order (wide 251225, long 7011, deep 4007 for the standard tree).
        var children = root.GetProperty("root_children");
        AssertEqual("251225,7011,4007", string.Join(',', new[] { children[0].GetProperty("size").GetInt64(), children[1].GetProperty("size").GetInt64(), children[2].GetProperty("size").GetInt64() }), "root_children sizes, largest first");
        Assert(children[0].GetProperty("path").GetString()!.EndsWith("\\wide"), "the largest root child is the wide directory");

        // --files with --json: a non-empty, descending files array.
        var withFiles = new StringWriter();
        FsOutput.Write(Scan(tree.Root, 2, files: true, top: 3), FsOptions.Parse([tree.Root, "--json", "--files", "--top=3"]), withFiles, new StringWriter());
        using var filesDocument = JsonDocument.Parse(withFiles.ToString());
        var fileItems = filesDocument.RootElement.GetProperty("files");
        AssertEqual(3, fileItems.GetArrayLength(), "--files: three files");
        AssertEqual("7011,5049,5048", string.Join(',', new[] { fileItems[0].GetProperty("size").GetInt64(), fileItems[1].GetProperty("size").GetInt64(), fileItems[2].GetProperty("size").GetInt64() }), "--files: largest first");
    }
}
