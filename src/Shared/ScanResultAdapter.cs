// Shared by MftStrategy and (Step 3) FsctlStrategy: both produce the same native ScanResult (see the design
// spec, "MftScanner (Step 2) and NtfsFsctlScanner (Step 3) share one adapter"). Lives alongside Cli.cs, not in
// DirSizer.Core, because it needs ScanResult (defined in Cli.cs, itself compiled into a consumer via
// <Compile Include>, not part of DirSizer.Core's own compilation) -- discovered while implementing this step;
// the design spec said "DirSizer.Core", which assumed ScanResult lived there. It does not.
static class ScanResultAdapter
{
    public static UnifiedScanResult Adapt(ScanResult result, string strategy, int top, double totalMs)
    {
        return new UnifiedScanResult(
            result.Volume, top,
            new UnifiedItem(RecordPaths.Build(result.Volume, result.Records, result.Root), result.Root.Size),
            Items(result, result.RootChildren, record => record.IsDirectory ? record.Size : record.LogicalSize),
            Items(result, result.Directories, record => record.Size),
            Items(result, result.Files, record => record.LogicalSize),
            result.Scanned, result.Skipped, CountFiles(result), [],
            strategy, null, null, totalMs);
    }

    static long CountFiles(ScanResult result)
    {
        long count = 0;
        foreach (var record in result.Records.Values)
            if (!record.IsDirectory) count++;
        return count;
    }

    static UnifiedItem[] Items(ScanResult result, FileRecord[] records, Func<FileRecord, long> size)
    {
        var items = new UnifiedItem[records.Length];
        for (var i = 0; i < records.Length; i++) items[i] = new UnifiedItem(RecordPaths.Build(result.Volume, result.Records, records[i]), size(records[i]));
        return items;
    }
}
