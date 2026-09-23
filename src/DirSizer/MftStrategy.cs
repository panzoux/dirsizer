using System.ComponentModel;

// See the design spec, "The availability/failure boundary". The probe is a separate, redundant OpenVolume
// call -- BulkIntegration.Run/BulkScanner.Scan are never modified, so dirsizer-mft.exe's behavior cannot
// regress from this file existing.
sealed class MftStrategy : IScanStrategy
{
    public string Name => "mft";

    public UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options)
    {
        var drive = DriveRoot.Validate(rootPath);
        try
        {
            using var probe = BulkNative.OpenVolume($"\\\\.\\{drive}");
        }
        catch (Win32Exception cause)
        {
            throw new StrategyUnavailableException("mft", cause);
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = BulkIntegration.Run(Options.From(drive, options.Top, options.Files, options.Dirs));
        return ScanResultAdapter.Adapt(result, "mft", options.Top, started.Elapsed.TotalMilliseconds);
    }
}
