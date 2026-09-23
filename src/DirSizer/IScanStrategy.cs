// See docs/superpowers/specs/2026-09-23-unified-scan-strategy-design.md, "IScanStrategy".
interface IScanStrategy
{
    string Name { get; }   // "mft", "fsctl", "filesystem"

    // Throws StrategyUnavailableException if this strategy cannot run here at all (see the design spec's
    // availability/failure boundary). Any other exception is a real scan failure.
    UnifiedScanResult Scan(string rootPath, UnifiedScanOptions options);
}
