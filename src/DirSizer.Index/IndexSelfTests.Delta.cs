// Tests of the delta file: an incremental run writes only the records changed since the base file was written.
static partial class IndexSelfTests
{
    static void WithIndexDirectory(Action<string> body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "dirsizer-index-test-" + Guid.NewGuid().ToString("N"));
        try { body(IndexFile.PathFor(directory, TestIdentity.SerialNumber)); }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // a.bin (41) grows, r.bin (43) is deleted, n.bin (46) is created in C, and the journal position moves on.
    static void ChangeSample(VolumeIndex index)
    {
        var grown = new FileRecord(Ref(41), 1, false) { LogicalSize = 90 };
        grown.Names.Add(Name("a.bin", 30));
        index.Records[41] = grown;
        index.Records.Remove(43);
        var created = new FileRecord(Ref(46, 2), 2, false) { LogicalSize = 11 };
        created.Names.Add(Name("n.bin", 32));
        index.Records[46] = created;
        index.Dirty.UnionWith([41, 43, 46]);
        index.NextUsn = 2000;
    }

    static void AssertSameRecords(VolumeIndex expected, VolumeIndex actual, string what)
    {
        var result = IndexVerifier.Compare(Aggregated(actual.Records).Records, Aggregated(expected.Records).Records);
        AssertEqual(0, result.Differences, $"{what} ({string.Join("; ", result.Samples)})");
    }

    static void DeltaHoldsOnlyTheChangesAndLoadsOnTopOfTheBase()
    {
        WithIndexDirectory(path =>
        {
            var index = Aggregated(SampleRecords());
            IndexFile.Save(index, path);
            Assert(index.BaseHash is not null && index.Dirty.Count == 0, "a full save sets the base hash and clears the changed set");
            var baseBytes = File.ReadAllBytes(path);
            ChangeSample(index);
            IndexFile.SaveDelta(index, path);
            Assert(File.ReadAllBytes(path).AsSpan().SequenceEqual(baseBytes), "the base file is not rewritten");
            Assert(new FileInfo(IndexFile.DeltaPathFor(path)).Length < baseBytes.Length, "the delta is smaller than the base");
            Assert(!File.Exists(IndexFile.DeltaPathFor(path) + ".tmp"), "no temporary delta is left behind");

            var loaded = IndexFile.TryLoad(path, out var problem)!;
            AssertEqual((string?)null, problem, "loads");
            AssertEqual(2000L, loaded.NextUsn, "the journal position comes from the delta");
            AssertEqual(TestIdentity, loaded.Identity, "the identity comes from the base");
            AssertEqual("41,43,46", string.Join(',', loaded.Dirty), "the changed set survives, so the next delta still holds these records");
            Assert(loaded.BaseHash!.AsSpan().SequenceEqual(index.BaseHash!), "the same base");
            Assert(!loaded.Records.ContainsKey(43), "a removed record stays removed");
            AssertSameRecords(index, loaded, "base + delta equals the index that was saved");
        });
    }

    static void DeltaAccumulatesAcrossRuns()
    {
        WithIndexDirectory(path =>
        {
            var index = Aggregated(SampleRecords());
            IndexFile.Save(index, path);
            ChangeSample(index);
            IndexFile.SaveDelta(index, path);
            var second = IndexFile.TryLoad(path, out _)!;
            second.Records[31].Names[0] = Name("B-renamed", 30);
            second.Dirty.Add(31);
            second.NextUsn = 3000;
            IndexFile.SaveDelta(second, path);
            var third = IndexFile.TryLoad(path, out var problem)!;
            AssertEqual((string?)null, problem, "loads");
            AssertEqual("B-renamed", third.Records[31].Names[0].Name, "the second run's change");
            AssertEqual(90L, third.Records[41].LogicalSize, "the first run's change is still there");
            AssertSameRecords(second, third, "two deltas in a row");
        });
    }

    static void FullSaveDropsTheDeltaAndAStaleDeltaIsIgnored()
    {
        WithIndexDirectory(path =>
        {
            var index = Aggregated(SampleRecords());
            IndexFile.Save(index, path);
            ChangeSample(index);
            IndexFile.SaveDelta(index, path);
            var oldDelta = File.ReadAllBytes(IndexFile.DeltaPathFor(path));
            index.NextUsn = 5000;
            IndexFile.Save(index, path);
            Assert(!File.Exists(IndexFile.DeltaPathFor(path)), "a full save deletes the delta");
            // A crash between renaming the new base and deleting the delta leaves a delta of the old base behind.
            File.WriteAllBytes(IndexFile.DeltaPathFor(path), oldDelta);
            var loaded = IndexFile.TryLoad(path, out var problem)!;
            AssertEqual((string?)null, problem, "loads");
            AssertEqual(5000L, loaded.NextUsn, "the delta written for the old base is ignored");
            AssertEqual(0, loaded.Dirty.Count, "and none of its records are applied");
        });
    }

    static void DamagedDeltaIsRejected()
    {
        WithIndexDirectory(path =>
        {
            var index = Aggregated(SampleRecords());
            IndexFile.Save(index, path);
            ChangeSample(index);
            IndexFile.SaveDelta(index, path);
            var deltaPath = IndexFile.DeltaPathFor(path);
            var bytes = File.ReadAllBytes(deltaPath);
            bytes[bytes.Length / 2] ^= 0x40;
            File.WriteAllBytes(deltaPath, bytes);
            Assert(IndexFile.TryLoad(path, out var problem) is null, "a damaged delta loads nothing (the base alone is older than the delta)");
            AssertContains(problem, "delta", "the reason names the delta");
            AssertContains(problem, "checksum", "and what is wrong with it");
        });
    }

    static void DeltaIsUsedOnlyWhileItStaysSmall()
    {
        var index = Aggregated(SampleRecords());
        Assert(!IndexFile.ShouldSaveDelta(index), "no base file yet: full save");
        index.BaseHash = new byte[32];
        Assert(IndexFile.ShouldSaveDelta(index), "nothing changed: delta");
        for (ulong number = 0; number < IndexFile.MinDeltaLimit; number++) index.Dirty.Add(1000 + number);
        Assert(IndexFile.ShouldSaveDelta(index), "at the limit: delta");
        index.Dirty.Add(999);
        Assert(!IndexFile.ShouldSaveDelta(index), "over the limit: full save, which folds the delta into the base");
    }

    static void UpdateRecordsWhichEntriesChanged()
    {
        var source = SampleVolume();
        var records = source.Scan();
        source.Records[41] = FileBytes(41, 30, "a.bin", 90);
        source.Records.Remove(43);
        var dirty = new SortedSet<ulong>();
        IndexUpdater.Apply(records, [new UsnChange(41, 0, 0), new UsnChange(43, 0, 0), new UsnChange(77, 0, 0)], new SortedSet<ulong>(), source, dirty);
        AssertEqual("41,43", string.Join(',', dirty), "replaced and removed entries; 77 was never in the index and is not on the volume");
    }
}
