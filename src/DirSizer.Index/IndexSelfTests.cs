// dirsizer-index's own tests. Record-level tests use synthetic records (RecordFixture, shared with the P0 self-tests);
// the path tests use a temporary directory; nothing here opens a volume, so --self-test needs no elevation to pass.
readonly record struct IndexSelfTest(string Name, Action Body);

static partial class IndexSelfTests
{
    // Returns the process exit code: 0 if no test failed.
    public static int Run()
    {
        var tests = new List<IndexSelfTest>
        {
            new("options: defaults, flags, a bare drive letter becomes its root", OptionsDefaultsAndFlags),
            new("options: bad input is rejected", OptionsRejectBadInput),
            new("index file: records, names and header survive a round trip, in order", IndexFileRoundTripsRecordsInOrder),
            new("index file: a loaded index aggregates exactly like the scan that wrote it", LoadedIndexAggregatesLikeTheScan),
            new("index: Recompute can run twice", RecomputeCanRunTwice),
            new("index file: damage, truncation and a newer version are rejected", DamagedIndexFileIsRejected),
            new("index file: a wrong checksum is reported even when parsing fails", ChecksumIsReportedEvenWhenParsingFails),
            new("index file: damage behind a valid checksum is InvalidData, never another exception", DamageBehindAValidChecksumIsInvalidData),
            new("index file: saving replaces the file and leaves no temporary file", SaveReplacesTheFileAtomically),
            new("index file: TryLoad says why nothing was loaded", TryLoadExplainsWhy),
            new("verifier: finds size, name, missing, extra, total and reference differences", VerifierFindsEachKindOfDifference),
            new("query: the whole volume equals the scan's own selection", WholeVolumeQueryMatchesTheScanSelection),
            new("query: a subtree covers only that directory", SubtreeQueryCoversOnlyTheDirectory),
            new("output: text summary line, stderr reason, JSON index and verify objects", OutputHasTheSummaryAndTheIndexObject),
            new("usn: V2 records are parsed to record numbers", UsnRecordsAreParsed),
            new("usn: short, undersized and non-V2 records are errors", DamagedUsnRecordsAreErrors),
            new("usn: reading follows the journal to its end", ReadChangesFollowsTheJournalToItsEnd),
            new("usn: a wrapped journal gives null; other errors and no progress throw", ReadChangesReportsWrapsAndErrors),
            new("attribute list: a resident list names the extension records", ResidentAttributeListNamesExtensionRecords),
            new("attribute list: a damaged entry is not guessed", DamagedAttributeListIsNotGuessed),
            new("attribute list: a non-resident list is read through its run list", NonResidentAttributeListIsReadFromItsClusters),
            new("update: create, modify, delete, rename and move equal a fresh scan", UpdateAppliesCreateModifyDeleteRenameAndMove),
            new("update: a reused record is a new file", UpdateSeesAReusedRecordAsANewFile),
            new("update: extension records are read through the base record's attribute list", UpdateReadsExtensionRecordsThroughTheAttributeList),
            new("update: a record that became an extension record is dropped", UpdateDropsARecordThatBecameAnExtension),
            new("update: an unreadable extension record asks for a full scan", UnreadableExtensionAsksForARebuild),
            new("update: NTFS metadata records are read again without journal entries", MetadataRecordsAreAlwaysReread),
            new("validity: identity and journal rules decide between update and full scan", ValidityRulesDecideBetweenUpdateAndRebuild),
            new("delta: only the changes are written, and they load on top of the base", DeltaHoldsOnlyTheChangesAndLoadsOnTopOfTheBase),
            new("delta: changes accumulate across runs", DeltaAccumulatesAcrossRuns),
            new("delta: a full save drops it; a delta of an older base is ignored", FullSaveDropsTheDeltaAndAStaleDeltaIsIgnored),
            new("delta: a damaged delta is rejected", DamagedDeltaIsRejected),
            new("delta: used only while it stays small", DeltaIsUsedOnlyWhileItStaysSmall),
            new("index file: a file of several read pieces loads exactly and is checked piece by piece", LargeFileIsReadAndCheckedPieceByPiece),
            new("update: the entries it replaced or removed are recorded", UpdateRecordsWhichEntriesChanged),
            new("path: resolved by Windows to the record; missing, file and other-volume cases are refused", PathResolverFindsTheDirectoryRecord),
            new("changes: shrunk and grew, largest first, scoped to the queried directory", ChangesShowWhatShrankAndGrew),
            new("changes: a deleted directory is listed by its old path", ChangesListADeletedDirectoryByItsOldPath),
            new("changes: a reused record is one directory gone and one new", ChangesTreatAReusedRecordAsGoneAndNew),
            new("changes: a directory moved into the queried one counts from 0", ChangesCountADirectoryMovedInAsNew),
            new("output: changes in text and JSON; a note when there is no earlier index", OutputListsTheChanges),
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

    static void AssertThrows<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new Exception($"{what}: expected {typeof(TException).Name}, nothing was thrown");
    }

    static void AssertThrowsWithMessage<TException>(Action action, string fragment, string what) where TException : Exception
    {
        try { action(); }
        catch (TException exception)
        {
            if (!exception.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                throw new Exception($"{what}: the message \"{exception.Message}\" does not mention \"{fragment}\"");
            return;
        }
        throw new Exception($"{what}: expected {typeof(TException).Name}, nothing was thrown");
    }

    static void AssertContains(string? text, string fragment, string what)
    {
        if (text is null || !text.Contains(fragment, StringComparison.OrdinalIgnoreCase)) throw new Exception($"{what}: expected text containing \"{fragment}\", got \"{text}\"");
    }

    static void OptionsDefaultsAndFlags()
    {
        var defaults = IndexOptions.Parse(["C:"]);
        AssertEqual("C:\\", defaults.Target, "a bare drive letter becomes its root");
        AssertEqual(25, defaults.Top, "default top");
        Assert(!defaults.Files && !defaults.Json && !defaults.Benchmark && !defaults.NoSave && !defaults.Verify && !defaults.Rebuild && !defaults.Changes, "default flags");
        AssertEqual(IndexFile.DefaultDirectory, defaults.IndexDirectory, "default index directory");
        Assert(defaults.IndexDirectory.EndsWith("dirsizer\\index", StringComparison.OrdinalIgnoreCase), $"under LOCALAPPDATA: {defaults.IndexDirectory}");

        var all = IndexOptions.Parse(["D:\\", "--top=7", "--files", "--json", "--benchmark", "--verify", "--index-dir=X:\\idx"]);
        AssertEqual(7, all.Top, "--top=N");
        Assert(all.Files && all.Json && all.Benchmark && all.Verify, "flags");
        AssertEqual("X:\\idx", all.IndexDirectory, "--index-dir");
        Assert(IndexOptions.Parse(["D:\\", "--no-save"]).NoSave, "--no-save");
        Assert(IndexOptions.Parse(["D:\\", "--rebuild"]).Rebuild, "--rebuild");
        Assert(IndexOptions.Parse(["D:\\", "--changes"]).Changes, "--changes");
        Assert(IndexOptions.Parse(["D:\\", "--verify", "--no-save"]).Verify, "--verify with --no-save compares with a fresh scan");
    }

    static void OptionsRejectBadInput()
    {
        AssertThrows<ArgumentException>(() => IndexOptions.Parse([]), "no path");
        AssertThrows<ArgumentException>(() => IndexOptions.Parse(["C:\\", "D:\\"]), "two paths");
        AssertThrows<ArgumentException>(() => IndexOptions.Parse(["C:\\", "--strategy=mft"]), "an unknown option");
        AssertThrows<ArgumentException>(() => IndexOptions.Parse(["C:\\", "--top=x"]), "--top without a number");
        AssertThrows<ArgumentException>(() => IndexOptions.Parse(["C:\\", "--index-dir"]), "--index-dir without a directory");    }
}
