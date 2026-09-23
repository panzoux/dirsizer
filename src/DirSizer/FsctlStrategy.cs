using System.ComponentModel;

// See MftStrategy.cs and the design spec's "availability/failure boundary" -- same shape, different native
// entry point. FsctlScanner.cs's own OpenVolume is reused as the probe; Scanner.Run() itself is never modified.
sealed class FsctlStrategy : IScanStrategy
{
    public string Name => "fsctl";

    public UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options)
    {
        var drive = DriveRoot.Validate(rootPath);
        try
        {
            using var probe = Native.OpenVolume($"\\\\.\\{drive}");
        }
        catch (Win32Exception cause)
        {
            throw new StrategyUnavailableException("fsctl", cause);
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = new Scanner(Options.From(drive, options.Top, options.Files, options.Dirs)).Run();
        return ScanResultAdapter.Adapt(result, "fsctl", options.Top, started.Elapsed.TotalMilliseconds);
    }
}
