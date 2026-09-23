// Always available, no elevation required -- the fallback of last resort, and (Step 1) the only strategy.
sealed class FileSystemStrategy : IScanStrategy
{
    public string Name => "filesystem";

    public UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options)
    {
        var settings = new ScanSettings(
            options.Workers == 0 ? FsOptions.DefaultWorkers : options.Workers,
            options.Top,
            options.Files,
            ShowProgress: !Console.IsErrorRedirected);
        var result = FsScanner.Scan(rootPath, settings);
        return Adapt(result, options.Top);
    }

    static UnifiedScanResult Adapt(FsResult result, int top)
    {
        var c = result.Counters;
        return new UnifiedScanResult(
            result.RootPath, top,
            new UnifiedItem(result.Root.Path, result.Root.Size),
            Items(result.RootChildren), Items(result.Directories), Items(result.Files),
            c.DirectoriesScanned, c.DirectoriesDenied + c.DirectoriesFailed, c.Files, c.Bytes, result.ErrorSamples,
            "filesystem", null, null, result.Metrics.Total.TotalMilliseconds);
    }

    static UnifiedItem[] Items(ResultItem[] items)
    {
        var result = new UnifiedItem[items.Length];
        for (var i = 0; i < items.Length; i++) result[i] = new UnifiedItem(items[i].Path, items[i].Size);
        return result;
    }
}
