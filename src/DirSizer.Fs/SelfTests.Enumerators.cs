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
        tests.Add(new("FallbackEnumerator falls back only on a first-query ERROR_INVALID_PARAMETER, never re-probes once triggered", FallbackTriggersOnlyOnFirstQueryUnsupportedError));
    }

    static void ReadResultFirstQueryDefaultsToFalse()
    {
        AssertEqual(false, default(ReadResult).FirstQuery, "a default ReadResult has FirstQuery false");
        AssertEqual(false, new ReadResult(ReadOutcome.Complete, 0).FirstQuery, "the two-argument constructor defaults FirstQuery to false");
        AssertEqual(true, new ReadResult(ReadOutcome.Failed, 87, true).FirstQuery, "the three-argument constructor sets it");
    }

    // A fake IDirectoryEnumerator whose Read() returns a pre-programmed sequence of results, one per call,
    // repeating the last one after the sequence is exhausted. Used only to drive FallbackEnumerator without
    // touching real Win32/ntdll calls.
    sealed class ScriptedEnumerator(params ReadResult[] results) : IDirectoryEnumerator
    {
        int _calls;
        public int Calls => _calls;

        public ReadResult Read(string directoryPath, IEntrySink sink)
        {
            var index = Math.Min(_calls, results.Length - 1);
            _calls++;
            return results[index];
        }
    }

    static void FallbackTriggersOnlyOnFirstQueryUnsupportedError()
    {
        // Case 1: the first query fails with FirstQuery=true, Error=87 -> falls back, not counted Failed by
        // the wrapper itself (Worker's own counting is exercised in the whole-walk test below).
        var state1 = new FallbackState();
        var primary1 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Failed, Win32Find.ErrorInvalidParameter, true));
        var secondary1 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Complete, 0));
        var sink = new CollectingSink();
        var result1 = new FallbackEnumerator(primary1, secondary1, state1).Read(@"\\?\C:\anything", sink);
        AssertEqual(ReadOutcome.Complete, result1.Outcome, "case 1: the secondary's result is returned, not the primary's Failed");
        AssertEqual(1, secondary1.Calls, "case 1: the secondary was used for this directory");
        Assert(state1.Triggered, "case 1: the shared state is now triggered");
        AssertEqual(Win32Find.ErrorInvalidParameter, state1.Reason, "case 1: the reason is recorded");

        // Case 2: a later query (not the first) fails with the same error code -> ordinary Failed, no trigger.
        var state2 = new FallbackState();
        var primary2 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Failed, Win32Find.ErrorInvalidParameter, false));
        var secondary2 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Complete, 0));
        var result2 = new FallbackEnumerator(primary2, secondary2, state2).Read(@"\\?\C:\anything", sink);
        AssertEqual(ReadOutcome.Failed, result2.Outcome, "case 2: a non-first-query failure is returned as-is");
        AssertEqual(Win32Find.ErrorInvalidParameter, result2.Error, "case 2: the error is kept");
        AssertEqual(0, secondary2.Calls, "case 2: the secondary was never used");
        Assert(!state2.Triggered, "case 2: the shared state is not triggered");

        // Case 3: a fresh FallbackEnumerator whose shared state is already triggered never calls its primary.
        var state3 = new FallbackState();
        state3.TryTrigger(Win32Find.ErrorInvalidParameter);
        var primary3 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Failed, 999, true));   // would fail loudly if ever called
        var secondary3 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Complete, 0));
        var result3 = new FallbackEnumerator(primary3, secondary3, state3).Read(@"\\?\C:\anything", sink);
        AssertEqual(ReadOutcome.Complete, result3.Outcome, "case 3: goes straight to the secondary");
        AssertEqual(0, primary3.Calls, "case 3: the primary is never called once the state is triggered");

        // Other outcomes and other errors on the first query never trigger: Denied, and a first-query error
        // that is not 87.
        var state4 = new FallbackState();
        var primary4 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Denied, Win32Find.ErrorAccessDenied, true));
        var secondary4 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Complete, 0));
        var result4 = new FallbackEnumerator(primary4, secondary4, state4).Read(@"\\?\C:\anything", sink);
        AssertEqual(ReadOutcome.Denied, result4.Outcome, "case 4: Denied passes through untouched");
        Assert(!state4.Triggered, "case 4: Denied never triggers");

        var state5 = new FallbackState();
        var primary5 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Failed, 32, true));   // ERROR_SHARING_VIOLATION, first query
        var secondary5 = new ScriptedEnumerator(new ReadResult(ReadOutcome.Complete, 0));
        var result5 = new FallbackEnumerator(primary5, secondary5, state5).Read(@"\\?\C:\anything", sink);
        AssertEqual(ReadOutcome.Failed, result5.Outcome, "case 5: a first-query error that is not 87 is not treated as unsupported");
        AssertEqual(32, result5.Error, "case 5: the real error is kept");
        Assert(!state5.Triggered, "case 5: not triggered");
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
