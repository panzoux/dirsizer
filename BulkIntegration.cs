// dirsizer-bulk: runs the shared raw-bulk scan and presents it through the shared result type, so the listing and JSON
// come from the same Output code as dirsizer-fsctl.
static class BulkIntegration
{
    public static ScanResult Run(Options options)
    {
        var volume = DriveRoot.Validate(options.Volume);
#if DIRSIZER_TEST_HOOKS
        // TEST BUILD ONLY. This block exists only in the "TestHooks" build configuration, which scripts\Test-BulkInstability.ps1
        // builds for itself. It is compiled out of Debug and Release builds and of every publish, so the shipped
        // executable neither reads these variables nor contains their names. They hold the window between the scan and the
        // MFT layout re-check open and disable the rescan, so the live-instability path can be tested on a real volume.
        var testDelay = int.TryParse(Environment.GetEnvironmentVariable("DIRSIZER_TEST_BULK_DELAY_AFTER_SCAN_MS"), out var delay) ? Math.Max(0, delay) : 0;
        var testNoRetry = Environment.GetEnvironmentVariable("DIRSIZER_TEST_BULK_NO_RETRY") == "1";
        var settings = new BulkScanSettings(volume, NoRetry: testNoRetry, TestDelayAfterScanMs: testDelay);
#else
        var settings = new BulkScanSettings(volume);
#endif
        var outcome = BulkScanner.Scan(settings);
        var pipeline = outcome.Pipeline;
        var scan = pipeline.Scan;
        var metrics = pipeline.Metrics;
        var relationships = pipeline.Relationships;

        // Skipped = slots that could not be read or parsed. Deleted and unused slots are not skipped records: the FSCTL
        // reader never returns them either.
        var skipped = scan.Malformed + scan.FixupFailures + scan.SignatureMalformed;
        if (!outcome.IsStable)
            Console.Error.WriteLine($"warning: scan_stability=UNSTABLE changes={outcome.Change}; the MFT layout changed while it was read, so the result is not a consistent snapshot (exit code 3)");
        if (skipped > 0)
            Console.Error.WriteLine($"warning: {skipped} MFT records could not be read or parsed; the reported sizes may be incomplete");

        // Acquisition (extents, raw read, USA fixup, layout re-check) takes the place of the FSCTL "query" phase, and the
        // raw read operations take the place of the query count. The detailed breakdown is reported separately.
        var scanMetrics = new ScanMetrics(
            metrics.OpenTime, metrics.VolumeTime, metrics.ExtentsTime + metrics.RawReadTime + metrics.FixupTime + metrics.StabilityTime,
            metrics.ParserTime, metrics.MergeTime, metrics.RelationshipTime, metrics.AggregationTime, metrics.FinalizeTime, metrics.TotalTime,
            metrics.ManagedAllocatedBytes, metrics.PeakWorkingSetBytes, scan.ReadOperations, scan.InUseSlots, scan.ParseSuccessful, scan.ExtensionRecords,
            relationships.Exact, relationships.FallbackZeroSequence, relationships.FallbackMismatch, relationships.Unresolved, relationships.Samples);

        // Like the reference reader, top-N selection happens after the total is taken and is not part of any timed phase.
        var directories = ResultSelector.SelectTop(pipeline.Candidates.Directories, options.Top, record => record.Size);
        var files = ResultSelector.SelectTop(pipeline.Candidates.Files, options.Top, record => record.LogicalSize);
        return new ScanResult(volume, pipeline.Records, directories, files, relationships.Root, pipeline.Candidates.RootChildren.ToArray(),
            relationships.UnresolvedRecords, checked((int)scan.Slots), skipped, scanMetrics, outcome.IsStable ? 0 : 3, "bulk", new BulkDetails(outcome));
    }
}

// The bulk-only parts of the output: its own benchmark line and the "bulk" JSON object.
sealed class BulkDetails(BulkScanOutcome outcome) : IScanDetails
{
    public void PrintBenchmark() => BulkReport.PrintBenchmark(outcome);

    public JsonBulk? ToJson()
    {
        var scan = outcome.Pipeline.Scan;
        var metrics = outcome.Pipeline.Metrics;
        return new JsonBulk(outcome.IsStable ? "stable" : "unstable", outcome.Attempts, outcome.Change.ToString(), outcome.DiscardedTime.TotalMilliseconds,
            metrics.ExtentsTime.TotalMilliseconds, metrics.RawReadTime.TotalMilliseconds, metrics.FixupTime.TotalMilliseconds, metrics.StabilityTime.TotalMilliseconds,
            scan.ReadOperations, scan.MegabytesPerSecond, scan.Slots, scan.InUseSlots, scan.DeletedSlots, scan.UnusedSlots, scan.Malformed, scan.FixupFailures, scan.SignatureMalformed);
    }
}
