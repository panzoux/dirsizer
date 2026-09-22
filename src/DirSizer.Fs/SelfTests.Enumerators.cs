using System.Security.Principal;

// Every enumerator against the same fixtures and the same independent oracle (the framework's own directory enumeration).
static partial class FsSelfTests
{
    // A (find, find:nolarge), then B and C in their classes, at the default 64 KiB buffer and at the 4 KiB minimum, where a
    // directory of a few hundred entries needs many refills of the buffer: the boundary at which a parser goes wrong.
    static readonly string[] EnumeratorSpecTexts =
    [
        "find", "find:nolarge",
        "handle:idextd:64", "handle:full:64", "handle:idextd:4", "handle:full:4",
        "nt:dir:64", "nt:full:64", "nt:idextd:64", "nt:dir:4", "nt:full:4", "nt:idextd:4",
    ];

    const uint AttributeMask = 0x17;    // read-only, hidden, system, directory: what the framework and the API report alike

    static partial void AddEnumeratorTests(List<SelfTest> tests)
    {
        tests.Add(new("every enumerator lists a directory like the framework does (300 entries, several buffer refills)", EnumeratorsListLikeTheFramework));
        tests.Add(new("every enumerator reports a missing directory as failed and an empty one as complete", EnumeratorsClassifyDirectories));
        tests.Add(new("every enumerator reports a directory that may not be read as denied", EnumeratorsReportDenied));
        tests.Add(new("every enumerator shows the reparse attribute of a junction entry", EnumeratorsShowJunctions));
        tests.Add(new("every enumerator walks the standard tree like the oracle, with 1, 3 and 8 workers", EnumeratorsWalkLikeTheOracle));
        tests.Add(new("ReadResult.FirstQuery defaults to false and the two-argument constructor is unaffected", ReadResultFirstQueryDefaultsToFalse));
    }

    static void ReadResultFirstQueryDefaultsToFalse()
    {
        AssertEqual(false, default(ReadResult).FirstQuery, "a default ReadResult has FirstQuery false");
        AssertEqual(false, new ReadResult(ReadOutcome.Complete, 0).FirstQuery, "the two-argument constructor defaults FirstQuery to false");
        AssertEqual(true, new ReadResult(ReadOutcome.Failed, 87, true).FirstQuery, "the three-argument constructor sets it");
    }

    static void EnumeratorsListLikeTheFramework()
    {
        using var tree = new TempTree();
        for (var index = 0; index < 300; index++) tree.MakeFile($"file-{index:000}.txt", index * 7 + 1);
        for (var index = 0; index < 20; index++) tree.MakeDir($"dir-{index:00}");
        tree.MakeFile("日本語.txt", 4242);
        tree.MakeFile("hidden.txt", 9);
        File.SetAttributes(tree.Full("hidden.txt"), FileAttributes.Hidden);

        var expected = new Dictionary<string, (uint Attributes, long Size)>(StringComparer.Ordinal);
        foreach (var info in new DirectoryInfo(tree.Base).EnumerateFileSystemInfos())
            expected[info.Name] = ((uint)info.Attributes & AttributeMask, info is FileInfo file ? file.Length : 0);

        foreach (var text in EnumeratorSpecTexts)
        {
            var sink = new CollectingSink();
            var result = EnumeratorSpec.Parse(text).CreateFactory().Create().Read(tree.Base, sink);
            AssertEqual(ReadOutcome.Complete, result.Outcome, $"{text}: outcome (error {result.Error})");
            AssertEqual(expected.Count, sink.Entries.Count, $"{text}: number of entries");
            foreach (var (name, want) in expected)
            {
                Assert(sink.Entries.TryGetValue(name, out var attributes), $"{text}: entry missing: {name}");
                AssertEqual(want.Attributes, attributes & AttributeMask, $"{text}: attributes of {name}");
                AssertEqual(want.Size, sink.Sizes[name], $"{text}: size of {name}");
            }
        }
    }

    static void EnumeratorsClassifyDirectories()
    {
        using var tree = new TempTree();
        tree.MakeDir("empty");
        foreach (var text in EnumeratorSpecTexts)
        {
            var reader = EnumeratorSpec.Parse(text).CreateFactory().Create();
            var missing = reader.Read(tree.Full("does-not-exist"), new CollectingSink());
            AssertEqual(ReadOutcome.Failed, missing.Outcome, $"{text}: a directory that does not exist (error {missing.Error})");
            var sink = new CollectingSink();
            var empty = reader.Read(tree.Full("empty"), sink);
            AssertEqual(ReadOutcome.Complete, empty.Outcome, $"{text}: an empty directory (error {empty.Error})");
            AssertEqual(0, sink.Entries.Count, $"{text}: an empty directory has no entries");
        }
    }

    static void EnumeratorsReportDenied()
    {
        using var tree = new TempTree();
        tree.MakeFile("locked\\g.bin", 222);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new SkipException("cannot determine the current user");
        var locked = tree.Root + "\\locked";
        if (RunProgram("icacls.exe", $"\"{locked}\" /deny \"*{sid}:(RD)\"") != 0) throw new SkipException("cannot set a deny ACL here");
        tree.OnDispose(() => RunProgram("icacls.exe", $"\"{locked}\" /remove:d \"*{sid}\""));
        // The precondition: the ACL must really keep this process out (a restricted or sandboxed token can ignore it).
        try
        {
            Directory.GetFileSystemEntries(locked);
            throw new SkipException("the deny ACL has no effect for this process (restricted token?)");
        }
        catch (UnauthorizedAccessException)
        {
        }
        foreach (var text in EnumeratorSpecTexts)
        {
            var result = EnumeratorSpec.Parse(text).CreateFactory().Create().Read(tree.Full("locked"), new CollectingSink());
            AssertEqual(ReadOutcome.Denied, result.Outcome, $"{text}: a directory that may not be read (error {result.Error})");
        }
    }

    static void EnumeratorsShowJunctions()
    {
        using var tree = new TempTree();
        using var outside = new TempTree();
        outside.MakeFile("target.bin", 1);
        tree.MakeFile("inside.bin", 1);
        if (RunProgram("cmd.exe", $"/c mklink /J \"{tree.Root}\\link\" \"{outside.Root}\"") != 0) throw new SkipException("cannot create a junction here");
        tree.OnDispose(() => Directory.Delete(tree.Full("link")));
        foreach (var text in EnumeratorSpecTexts)
        {
            var sink = new CollectingSink();
            EnumeratorSpec.Parse(text).CreateFactory().Create().Read(tree.Base, sink);
            Assert(sink.Entries.TryGetValue("link", out var attributes), $"{text}: the junction is listed");
            Assert((attributes & 0x400) != 0 && (attributes & 0x10) != 0, $"{text}: the junction entry has the directory and the reparse attribute (0x{attributes:X})");
        }
    }

    static void EnumeratorsWalkLikeTheOracle()
    {
        using var tree = StandardTree();
        var (expected, fileCount) = Oracle(tree.Base);
        foreach (var text in EnumeratorSpecTexts)
        {
            var spec = EnumeratorSpec.Parse(text);
            foreach (var workers in new[] { 1, 3, 8 })
            {
                var label = $"{text}, workers={workers}";
                var result = Scan(tree.Root, workers, files: true, top: 5, enumerators: spec.CreateFactory());
                AssertSameTotals(expected, TotalsOf(result.Nodes), label);
                AssertEqual(fileCount, result.Counters.Files, $"{label}: files");
                AssertEqual(result.Counters.Bytes, result.Root.Size, $"{label}: the root total equals the sum of the file sizes");
                AssertEqual("7011,5049,5048,5047,5046", string.Join(',', Sizes(result.Files)), $"{label}: largest files");
                AssertEqual(spec.Canonical, result.Metrics.Enumerator, $"{label}: the enumerator is reported");
            }
        }
    }
}
