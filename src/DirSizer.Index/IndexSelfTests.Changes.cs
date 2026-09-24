static partial class IndexSelfTests
{
    static string Changes(DirectoryChange[] changes) => string.Join(',', Array.ConvertAll(changes, change => $"{change.Path}={change.Before}->{change.After}"));

    static void ChangesShowWhatShrankAndGrew()
    {
        var source = SampleVolume();
        source.Records[32] = DirBytes(32, 5, "C");
        var before = Aggregated(source.Scan());
        var snapshot = IndexSnapshot.Capture(before);
        source.Records.Remove(40);                              // b.bin (100) deleted: B, A and the root shrink
        source.Records[42] = FileBytes(42, 32, "c.bin", 30);    // c.bin (30) created in C: C grows
        IndexUpdater.Apply(before.Records, [new UsnChange(40, 0, 0), new UsnChange(42, 0, 0)], new SortedSet<ulong>(), source);
        var after = Aggregated(before.Records);
        var report = ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 10);
        AssertEqual(123L, report.RootBefore, "root before: 100 + 20 + 3");
        AssertEqual(53L, report.RootAfter, "root after: 20 + 3 + 30");
        AssertEqual("T:\\A=120->20,T:\\A\\B=100->0,T:\\=123->53", Changes(report.Shrunk), "shrunk, largest change first");
        AssertEqual("T:\\C=0->30", Changes(report.Grew), "grew");
        var scoped = ChangeReporter.Compare(snapshot, after, "T:", after.Records[30], 10);
        AssertEqual("T:\\A=120->20,T:\\A\\B=100->0", Changes(scoped.Shrunk), "only at or below A");
        AssertEqual("", Changes(scoped.Grew), "C is not below A");
        AssertEqual(1, ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 1).Shrunk.Length, "--top limits each list");
    }

    static void ChangesListADeletedDirectoryByItsOldPath()
    {
        var source = SampleVolume();
        var before = Aggregated(source.Scan());
        var snapshot = IndexSnapshot.Capture(before);
        source.Records.Remove(40);
        source.Records.Remove(31);   // B and its file deleted
        IndexUpdater.Apply(before.Records, [new UsnChange(40, 0, 0), new UsnChange(31, 0, 0)], new SortedSet<ulong>(), source);
        var after = Aggregated(before.Records);
        var report = ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 10);
        // Three changes of -100; equal changes are ordered by record number (root 5, A 30, B 31).
        AssertEqual("T:\\=123->23,T:\\A=120->20,T:\\A\\B=100->0", Changes(report.Shrunk), "B is listed by its old path");
    }

    static void ChangesTreatAReusedRecordAsGoneAndNew()
    {
        var source = SampleVolume();
        var before = Aggregated(source.Scan());
        var snapshot = IndexSnapshot.Capture(before);
        source.Records.Remove(40);
        source.Records[31] = DirBytes(31, 5, "D", sequence: 2);   // B deleted; its record reused by a new, empty directory D
        IndexUpdater.Apply(before.Records, [new UsnChange(40, 0, 0), new UsnChange(31, 0, 0)], new SortedSet<ulong>(), source);
        var after = Aggregated(before.Records);
        var report = ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 10);
        AssertEqual("T:\\=123->23,T:\\A=120->20,T:\\A\\B=100->0", Changes(report.Shrunk), "B is gone, by its old path");
        AssertEqual("", Changes(report.Grew), "D is new but empty: nothing to report");
    }

    static void ChangesCountADirectoryMovedInAsNew()
    {
        var source = SampleVolume();
        source.Records[32] = DirBytes(32, 5, "C");
        source.Records[42] = FileBytes(42, 32, "c.bin", 30);
        var before = Aggregated(source.Scan());
        var snapshot = IndexSnapshot.Capture(before);
        source.Records[32] = DirBytes(32, 30, "C");   // C (30) moved from the root into A
        IndexUpdater.Apply(before.Records, [new UsnChange(32, 0, 0)], new SortedSet<ulong>(), source);
        var after = Aggregated(before.Records);
        var scoped = ChangeReporter.Compare(snapshot, after, "T:", after.Records[30], 10);
        AssertEqual("T:\\A=120->150,T:\\A\\C=0->30", Changes(scoped.Grew), "C was not below A before: it counts from 0");
        AssertEqual("", Changes(scoped.Shrunk), "nothing below A shrank");
        var whole = ChangeReporter.Compare(snapshot, after, "T:", after.Records[5], 10);
        AssertEqual("T:\\A=120->150", Changes(whole.Grew), "on the whole volume C itself did not change");
        AssertEqual(0L, whole.RootAfter - whole.RootBefore, "the root did not change");
    }

    static void OutputListsTheChanges()
    {
        var report = new ChangeReport(new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc), 123, 53,
            [new DirectoryChange("T:\\A", 120, 20)], [new DirectoryChange("T:\\C", 0, 30)]);
        var run = SampleRun() with { Changes = report };
        var text = new StringWriter();
        IndexOutput.Write(run, IndexOptions.Parse(["T:", "--changes"]), text, new StringWriter());
        AssertContains(text.ToString(), "Changes since 2026-09-23 10:00:00 UTC", "text heading");
        AssertContains(text.ToString(), "-100\t120\t20\tT:\\A", "text shrunk row");
        AssertContains(text.ToString(), "+30\t0\t30\tT:\\C", "text grew row");

        var json = new StringWriter();
        IndexOutput.Write(run, IndexOptions.Parse(["T:", "--json", "--changes"]), json, new StringWriter());
        using var document = System.Text.Json.JsonDocument.Parse(json.ToString());
        var changes = document.RootElement.GetProperty("changes");
        AssertEqual(-70L, changes.GetProperty("root_delta").GetInt64(), "json changes.root_delta");
        AssertEqual(-100L, changes.GetProperty("shrunk")[0].GetProperty("delta").GetInt64(), "json changes.shrunk[0].delta");
        AssertEqual("T:\\C", changes.GetProperty("grew")[0].GetProperty("path").GetString(), "json changes.grew[0].path");

        var none = new StringWriter();
        IndexOutput.Write(SampleRun(), IndexOptions.Parse(["T:", "--changes"]), new StringWriter(), none);
        AssertContains(none.ToString(), "no earlier index", "stderr when there is nothing to compare with");
    }
}
