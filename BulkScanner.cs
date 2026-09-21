using System.Diagnostics;

// The complete raw-bulk scan, independent of how its result is presented. Shared by the main executable's
// `--reader=bulk` and by the DirSizer.Bulk test harness.

// Test-only members (NoRetry, TestDelayAfterScanMs) exist so the live-instability path can be exercised on a real volume.
readonly record struct BulkScanSettings(string Volume, bool Diagnose = false, bool NoRetry = false, int TestDelayAfterScanMs = 0, IScanProgress? Progress = null);

// LayoutChange.None means the MFT layout was unchanged during the final attempt. Attempts is 1, or 2 after one rescan.
sealed record BulkScanOutcome(LayoutChange Change, int Attempts, TimeSpan DiscardedTime, PipelineResult Pipeline)
{
    public bool IsStable => Change == LayoutChange.None;
}

static class BulkScanner
{
    // Scans the MFT and, if its layout changed during the scan, rescans once. A second change is reported in the outcome,
    // not hidden: the caller still gets the results and decides how to present an unstable scan.
    public static BulkScanOutcome Scan(BulkScanSettings settings)
    {
        var discarded = TimeSpan.Zero;
        for (var attempt = 1; ; attempt++)
        {
            var canRetry = attempt == 1 && !settings.NoRetry;
            var result = RunAttempt(settings, canRetry);
            if (result.Pipeline is null)
            {
                discarded += result.Elapsed;
                Console.Error.WriteLine($"scan_stability=changed attempt={attempt} changes={result.Change} action=rescanning_once");
                continue;
            }
            return new BulkScanOutcome(result.Change, attempt, discarded, result.Pipeline);
        }
    }

    static AttemptResult RunAttempt(BulkScanSettings settings, bool canRetry)
    {
        var totalTimer = Stopwatch.StartNew();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var volume = settings.Volume;
        var openTimer = Stopwatch.StartNew();
        using var volumeHandle = BulkNative.OpenVolume($"\\\\.\\{volume}");
        openTimer.Stop();
        var volumeTimer = Stopwatch.StartNew();
        var metadata = BulkNative.ReadVolumeData(volumeHandle, volume);
        volumeTimer.Stop();
        var extentsTimer = Stopwatch.StartNew();
        using var mftHandle = BulkNative.OpenMft(volume);
        var extents = BulkNative.ReadMftExtents(mftHandle, metadata.BytesPerCluster, metadata.MftValidDataLength);
        extentsTimer.Stop();
        var startLayout = new MftLayout(metadata, extents);

        var records = new Dictionary<ulong, FileRecord>();
        var scan = BulkScan.Read(volumeHandle, metadata, extents, settings.Diagnose, null, records, settings.Progress);
        settings.Progress?.Finish();   // end the progress line before anything else (a retry notice, a warning) is written

        // Test-only: widens the window between the scan and the layout re-check so a test can change the MFT in it.
        // The wait is not a phase, so it lands in "other".
        if (settings.TestDelayAfterScanMs > 0) Thread.Sleep(settings.TestDelayAfterScanMs);
        var stabilityTimer = Stopwatch.StartNew();
        var change = MftLayout.Compare(startLayout, MftLayout.Capture(volumeHandle, mftHandle));
        stabilityTimer.Stop();
        if (change != LayoutChange.None && canRetry) return new AttemptResult(change, totalTimer.Elapsed, null);

        settings.Progress?.Message("Calculating folder sizes...");   // about a second on a large volume, and otherwise silent
        var relationshipTimer = Stopwatch.StartNew();
        var relationships = RelationshipResolver.Resolve(records);
        SizeAggregator.AddFileSizesToParents(records);
        relationshipTimer.Stop();

        var aggregationTimer = Stopwatch.StartNew();
        SizeAggregator.AggregateDirectories(records);
        aggregationTimer.Stop();

        var finalizeTimer = Stopwatch.StartNew();
        var candidates = ResultSelector.Collect(records);
        finalizeTimer.Stop();
        totalTimer.Stop();

        var process = Process.GetCurrentProcess();
        var metrics = new BulkMetrics(openTimer.Elapsed, volumeTimer.Elapsed, extentsTimer.Elapsed, scan.ReadTime, scan.ClassifyTime, scan.ParseTime, scan.MergeTime,
            stabilityTimer.Elapsed, relationshipTimer.Elapsed, aggregationTimer.Elapsed, finalizeTimer.Elapsed, totalTimer.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, process.PeakWorkingSet64);
        if (metrics.PhaseSum != metrics.TotalTime)
            throw new InvalidOperationException("Benchmark phase accounting does not sum to total scan time.");
        return new AttemptResult(change, totalTimer.Elapsed, new PipelineResult(metadata, extents.Count, scan, metrics, records, relationships, candidates));
    }
}

readonly record struct BulkMetrics(TimeSpan OpenTime, TimeSpan VolumeTime, TimeSpan ExtentsTime, TimeSpan RawReadTime, TimeSpan FixupTime, TimeSpan ParserTime, TimeSpan MergeTime, TimeSpan StabilityTime, TimeSpan RelationshipTime, TimeSpan AggregationTime, TimeSpan FinalizeTime, TimeSpan TotalTime, long ManagedAllocatedBytes, long PeakWorkingSetBytes)
{
    public TimeSpan OtherTime => TimeSpan.FromTicks(Math.Max(0, TotalTime.Ticks - OpenTime.Ticks - VolumeTime.Ticks - ExtentsTime.Ticks - RawReadTime.Ticks - FixupTime.Ticks - ParserTime.Ticks - MergeTime.Ticks - StabilityTime.Ticks - RelationshipTime.Ticks - AggregationTime.Ticks - FinalizeTime.Ticks));
    public TimeSpan PhaseSum => OpenTime + VolumeTime + ExtentsTime + RawReadTime + FixupTime + ParserTime + MergeTime + StabilityTime + RelationshipTime + AggregationTime + FinalizeTime + OtherTime;
}

sealed record AttemptResult(LayoutChange Change, TimeSpan Elapsed, PipelineResult? Pipeline);
sealed record PipelineResult(BulkNative.VolumeData Metadata, int ExtentCount, BulkResult Scan, BulkMetrics Metrics, Dictionary<ulong, FileRecord> Records, RelationshipResult Relationships, ResultCandidates Candidates);
