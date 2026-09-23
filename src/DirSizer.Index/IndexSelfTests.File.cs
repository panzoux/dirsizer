using System.Buffers.Binary;
using System.Security.Cryptography;

static partial class IndexSelfTests
{
    static readonly VolumeIdentity TestIdentity = new(0x1234_5678_9ABC_DEF0, 1024, 4096, 786432);

    static FileRef Ref(ulong number, ushort sequence = 1) => new(number | ((ulong)sequence << 48));

    static ParsedRecord Parsed(ulong number, bool directory, long size, ulong baseNumber = 0, params FileName[] names) =>
        new(Ref(number), 1, baseNumber == 0 ? default : Ref(baseNumber), directory, size, [.. names]);

    static FileName Name(string name, ulong parent = 5, byte nameSpace = 1) => new(Ref(parent), name, nameSpace);

    // Root 5: A (30) with B (31) holding b.bin (40, 100 bytes) and a.bin (41, 20); C (32) with a non-ASCII name file (42, 7);
    // r.bin (43, 3) at the root; x.bin (44, 50) hard-linked in A and C, with extension record 45 adding its DOS name.
    // Merged in descending record order, as a scan does. Sizes after aggregation: root 180, A 170, B 100, C 7.
    static Dictionary<ulong, FileRecord> SampleRecords()
    {
        var records = new Dictionary<ulong, FileRecord>();
        foreach (var parsed in new[]
        {
            Parsed(45, false, 0, 44, Name("X~1.BIN", 30, 2)),
            Parsed(44, false, 50, 0, Name("x.bin", 30), Name("x.bin", 32)),
            Parsed(43, false, 3, 0, Name("r.bin")),
            Parsed(42, false, 7, 0, Name("日本語\U0001F600.txt", 32)),
            Parsed(41, false, 20, 0, Name("a.bin", 30)),
            Parsed(40, false, 100, 0, Name("b.bin", 31)),
            Parsed(32, true, 0, 0, Name("C")),
            Parsed(31, true, 0, 0, Name("B", 30)),
            Parsed(30, true, 0, 0, Name("A")),
            Parsed(5, true, 0, 0, Name(".", 5)),
        })
            RecordMerger.Merge(records, parsed);
        return records;
    }

    static VolumeIndex Aggregated(Dictionary<ulong, FileRecord> records)
    {
        var index = new VolumeIndex(TestIdentity, 7, 1000, new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc), records);
        index.Recompute();
        return index;
    }

    static void IndexFileRoundTripsRecordsInOrder()
    {
        var original = new VolumeIndex(TestIdentity, 0xABCDEF, 123456789, new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc), SampleRecords());
        var loaded = IndexFile.Read(IndexFile.Serialize(original));
        AssertEqual(original.Identity, loaded.Identity, "identity");
        AssertEqual(original.JournalId, loaded.JournalId, "journal id");
        AssertEqual(original.NextUsn, loaded.NextUsn, "next USN");
        AssertEqual(original.WrittenUtc, loaded.WrittenUtc, "written time");
        AssertEqual(string.Join(',', original.Records.Keys), string.Join(',', loaded.Records.Keys), "record order");
        foreach (var (number, expected) in original.Records)
        {
            var actual = loaded.Records[number];
            AssertEqual(expected.Reference, actual.Reference, $"record {number} reference");
            AssertEqual(expected.SequenceNumber, actual.SequenceNumber, $"record {number} sequence");
            AssertEqual(expected.IsDirectory, actual.IsDirectory, $"record {number} directory flag");
            AssertEqual(expected.LogicalSize, actual.LogicalSize, $"record {number} logical size");
            AssertEqual(expected.Names.Count, actual.Names.Count, $"record {number} name count");
            for (var i = 0; i < expected.Names.Count; i++) AssertEqual(expected.Names[i], actual.Names[i], $"record {number} name {i}");
        }
    }

    static void LoadedIndexAggregatesLikeTheScan()
    {
        var scanned = Aggregated(SampleRecords());
        var loaded = IndexFile.Read(IndexFile.Serialize(scanned));
        loaded.Recompute();
        AssertEqual(180L, loaded.Records[5].Size, "root size: 100 + 20 + 7 + 3 + 50");
        AssertEqual(170L, loaded.Records[30].Size, "A: B (100) + a.bin (20) + x.bin (50, whose selected parent is A)");
        AssertEqual(7L, loaded.Records[32].Size, "C: only its own file; the hard link counts once, under A");
        foreach (var (number, expected) in scanned.Records)
        {
            var actual = loaded.Records[number];
            AssertEqual(expected.Parent, actual.Parent, $"record {number} parent");
            AssertEqual(expected.DisplayName, actual.DisplayName, $"record {number} display name");
            AssertEqual(expected.Size, actual.Size, $"record {number} size");
        }
    }

    static void RecomputeCanRunTwice()
    {
        var index = Aggregated(SampleRecords());
        index.Recompute();
        AssertEqual(180L, index.Records[5].Size, "root size after a second Recompute (sizes are reset, not added twice)");
    }

    static void DamagedIndexFileIsRejected()
    {
        var bytes = IndexFile.Serialize(Aggregated(SampleRecords()));
        var flipped = (byte[])bytes.Clone();
        flipped[bytes.Length / 2] ^= 0x40;
        AssertThrowsWithMessage<InvalidDataException>(() => IndexFile.Read(flipped), "checksum", "one flipped byte");
        AssertThrows<InvalidDataException>(() => IndexFile.Read(bytes[..^1]), "a truncated file");
        AssertThrows<InvalidDataException>(() => IndexFile.Read(new byte[10]), "a 10-byte file");

        // A newer format version with a valid checksum must be refused by its version, not misread.
        var body = bytes[..^32];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), IndexFile.Version + 1);
        var future = new byte[bytes.Length];
        body.CopyTo(future, 0);
        SHA256.HashData(body).CopyTo(future, body.Length);
        AssertThrowsWithMessage<InvalidDataException>(() => IndexFile.Read(future), "version", "a newer format version");
    }

    static void SaveReplacesTheFileAtomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dirsizer-index-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = IndexFile.PathFor(directory, TestIdentity.SerialNumber);
            AssertEqual("123456789ABCDEF0.dsix", Path.GetFileName(path), "file name is the volume serial in hex");
            var index = Aggregated(SampleRecords());
            IndexFile.Save(index, path);
            index.NextUsn = 2000;
            IndexFile.Save(index, path);
            AssertEqual(2000L, IndexFile.Read(File.ReadAllBytes(path)).NextUsn, "the second save replaced the first");
            Assert(!File.Exists(path + ".tmp"), "no temporary file is left behind");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    static void TryLoadExplainsWhy()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dirsizer-index-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = IndexFile.PathFor(directory, 1);
            Assert(IndexFile.TryLoad(path, out var problem) is null, "a missing file loads nothing");
            AssertEqual("no saved index", problem, "reason for a missing file");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, new byte[100]);
            Assert(IndexFile.TryLoad(path, out problem) is null, "a damaged file loads nothing");
            AssertContains(problem, "cannot be used", "reason for a damaged file");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
