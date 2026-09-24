static partial class IndexSelfTests
{
    static string Join(UnifiedItem[] items) => string.Join(',', Array.ConvertAll(items, item => $"{item.Path}={item.Size}"));

    static void WholeVolumeQueryMatchesTheScanSelection()
    {
        var index = Aggregated(SampleRecords());
        var result = SubtreeQuery.Query(index.Records, "T:", index.Records[5], 3);
        AssertEqual("T:\\", result.Root.Path, "root path");
        AssertEqual(180L, result.Root.Size, "root size");
        AssertEqual("T:\\=180,T:\\A=170,T:\\A\\B=100", Join(result.Directories), "largest 3 directories");
        AssertEqual("T:\\A\\B\\b.bin=100,T:\\A\\x.bin=50,T:\\A\\a.bin=20", Join(result.Files), "largest 3 files");
        AssertEqual("T:\\A=170,T:\\C=7,T:\\r.bin=3", Join(result.RootChildren), "root children, largest first");
        AssertEqual(4L, result.DirectoriesScanned, "directories: root, A, B, C");
        AssertEqual(5L, result.FileCount, "files: 40-44 (45 is merged into 44)");
        AssertEqual("index", result.Strategy, "strategy name");
    }

    static void SubtreeQueryCoversOnlyTheDirectory()
    {
        var index = Aggregated(SampleRecords());
        var result = SubtreeQuery.Query(index.Records, "T:", index.Records[30], 10);
        AssertEqual("T:\\A=170", $"{result.Root.Path}={result.Root.Size}", "root");
        AssertEqual("T:\\A=170,T:\\A\\B=100", Join(result.Directories), "directories at or below A");
        AssertEqual("T:\\A\\B\\b.bin=100,T:\\A\\x.bin=50,T:\\A\\a.bin=20", Join(result.Files), "files below A");
        AssertEqual("T:\\A\\B=100,T:\\A\\x.bin=50,T:\\A\\a.bin=20", Join(result.RootChildren), "A's children, largest first");
        AssertEqual(2L, result.DirectoriesScanned, "directories: A and B");
        AssertEqual(3L, result.FileCount, "files: b.bin, a.bin, x.bin");
        AssertEqual(1, SubtreeQuery.Descendants(index.Records, 32).Count, "below C: only its own file; x.bin's selected parent is A");
    }

    static IndexRun SampleRun(VerifyResult? verify = null)
    {
        var result = new UnifiedScanResult("T:\\", 25, new UnifiedItem("T:\\", 180), [new UnifiedItem("T:\\A", 170)],
            [new UnifiedItem("T:\\", 180), new UnifiedItem("T:\\A", 170)], [new UnifiedItem("T:\\A\\B\\b.bin", 100)],
            4, 0, 5, [], "index", null, null, 12.5);
        return new IndexRun(result, "full", "no saved index", "X:\\idx\\0000000000000001.dsix", 4096, true, true, 10, 7, 1000, null, verify, new IndexTimings(), "delta", 3, 512);
    }

    static void OutputHasTheSummaryAndTheIndexObject()
    {
        var text = new StringWriter();
        var error = new StringWriter();
        IndexOutput.Write(SampleRun(), IndexOptions.Parse(["T:"]), text, error);
        AssertContains(text.ToString(), "strategy=index mode=full path=T:\\ directories=4 files=5 usn_changes=0 records_reread=0 saved=yes", "text summary line");
        AssertContains(error.ToString(), "index: full scan (no saved index)", "the reason for the full scan is on stderr");

        var json = new StringWriter();
        IndexOutput.Write(SampleRun(new VerifyResult(0, [])), IndexOptions.Parse(["T:", "--json", "--verify"]), json, new StringWriter());
        using var document = System.Text.Json.JsonDocument.Parse(json.ToString());
        var index = document.RootElement.GetProperty("index");
        AssertEqual("full", index.GetProperty("mode").GetString(), "json index.mode");
        AssertEqual("no saved index", index.GetProperty("rebuild_reason").GetString(), "json index.rebuild_reason");
        AssertEqual(7UL, index.GetProperty("journal_id").GetUInt64(), "json index.journal_id");
        AssertEqual("delta", index.GetProperty("save_kind").GetString(), "json index.save_kind");
        AssertEqual(3, index.GetProperty("delta_records").GetInt32(), "json index.delta_records");
        AssertEqual(512L, index.GetProperty("delta_bytes").GetInt64(), "json index.delta_bytes");
        AssertEqual(0, document.RootElement.GetProperty("verify").GetProperty("differences").GetInt32(), "json verify.differences");
        AssertEqual(180L, document.RootElement.GetProperty("root").GetProperty("size").GetInt64(), "json root.size");
    }

    static void PathResolverFindsTheDirectoryRecord()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dirsizer-index-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        File.WriteAllBytes(Path.Combine(directory, "f.bin"), [1, 2, 3]);
        try
        {
            var (serial, reference) = PathResolver.Identify(directory);
            var (_, sub) = PathResolver.Identify(Path.Combine(directory, "sub"));
            Assert(reference != sub && reference.RecordNumber != 0, "two directories, two records");
            var records = new Dictionary<ulong, FileRecord> { [reference.RecordNumber] = new FileRecord(reference, reference.SequenceNumber, true) };
            var index = new VolumeIndex(TestIdentity with { SerialNumber = serial }, 1, 1, DateTime.UnixEpoch, records);
            AssertEqual(reference, PathResolver.Find(index, directory).Reference, "found by its record and sequence number");
            AssertThrowsWithMessage<ArgumentException>(() => PathResolver.Find(index, Path.Combine(directory, "sub")), "not in the index", "a directory the index does not have");
            var reused = new FileRef(reference.FullReference ^ (1UL << 48));
            var staleRecords = new Dictionary<ulong, FileRecord> { [reference.RecordNumber] = new FileRecord(reused, reused.SequenceNumber, true) };
            var stale = new VolumeIndex(TestIdentity with { SerialNumber = serial }, 1, 1, DateTime.UnixEpoch, staleRecords);
            AssertThrowsWithMessage<ArgumentException>(() => PathResolver.Find(stale, directory), "not in the index", "the same record number with another sequence number");
            var (_, file) = PathResolver.Identify(Path.Combine(directory, "f.bin"));
            records[file.RecordNumber] = new FileRecord(file, file.SequenceNumber, false);
            AssertThrowsWithMessage<ArgumentException>(() => PathResolver.Find(index, Path.Combine(directory, "f.bin")), "is a file", "a file");
            var otherVolume = new VolumeIndex(TestIdentity with { SerialNumber = serial + 1L }, 1, 1, DateTime.UnixEpoch, records);
            AssertThrowsWithMessage<ArgumentException>(() => PathResolver.Find(otherVolume, directory), "not on the indexed volume", "another volume's index");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
