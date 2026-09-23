// See the design spec, "ScanStrategySelector". Step 1: exactly one strategy, always chosen -- no NTFS check,
// no drive-letter check, no probing. Step 2/3 add MftStrategy/FsctlStrategy ahead of FileSystemStrategy in
// CandidateStrategies, plus the drive-letter/NTFS pre-checks that already live there; the try/catch loop
// itself does not change.
static class ScanStrategySelector
{
    public static UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options) =>
        Scan(rootPath, options, CandidateStrategies(rootPath));

    // Injectable strategy list, for self-tests only (see SelfTests.cs): lets a test drive the fallback chain
    // with fakes instead of real strategies, without changing the production entry point's signature.
    internal static UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options, IScanStrategy[] strategies)
    {
        Exception? lastUnavailable = null;
        foreach (var strategy in strategies)
        {
            try
            {
                return strategy.Scan(rootPath, options);
            }
            catch (StrategyUnavailableException unavailable)
            {
                lastUnavailable = unavailable;
            }
        }
        // Unreachable in Step 1 (FileSystemStrategy never throws StrategyUnavailableException -- it has no
        // capability requirement), kept because Step 2/3 make it reachable and the shape should not change then.
        throw new InvalidOperationException("No scan strategy is available.", lastUnavailable);
    }

    static IScanStrategy[] CandidateStrategies(string rootPath)
    {
        string drive;
        try { drive = DriveRoot.Validate(rootPath); }
        catch (ArgumentException) { return [new FileSystemStrategy()]; }
        return VolumeInfo.IsNtfs(drive) ? [new MftStrategy(), new FileSystemStrategy()] : [new FileSystemStrategy()];
    }
}
