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
}
