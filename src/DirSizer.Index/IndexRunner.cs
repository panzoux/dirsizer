using System.Diagnostics;

// Phase timings of one run. Phases that did not run stay zero.
sealed class IndexTimings
{
    public TimeSpan Load, Usn, Update, Scan, Recompute, Save, Query, Verify, Total;
}

// What an incremental update did (roadmap I3): Changes = USN records read; Reread = distinct MFT records read again
// (journal-named plus NTFS metadata); Replaced / Removed = index entries rewritten / dropped; ExtensionReads = extension
// records read through attribute lists. RebuildReason is set when the update could not be completed.
sealed record UpdateResult(int Changes, int Reread, int Replaced, int Removed, int ExtensionReads, string? RebuildReason);

sealed record IndexRun(
    UnifiedScanResult Result, string Mode, string? RebuildReason, string IndexPath, long IndexBytes, bool Saved, bool Stable,
    int RecordCount, ulong JournalId, long NextUsn, UpdateResult? Update, VerifyResult? Verify, IndexTimings Timings)
{
    // 0 = result written; 2 = --verify found differences; 3 = the full scan was unstable, so the index was not saved.
    public int ExitCode => Verify is { Differences: > 0 } ? 2 : Stable ? 0 : 3;
}

static class IndexRunner
{
    // I2: always a full scan; the index is saved, and --verify loads it back and compares it with that scan.
    public static IndexRun Run(IndexOptions options)
    {
        var timings = new IndexTimings();
        var total = Stopwatch.StartNew();
        var timer = new Stopwatch();
        var volume = DriveRoot.Validate(options.Target);
        BulkNative.VolumeData data;
        JournalState? journal;
        using (var handle = BulkNative.OpenVolume($"\\\\.\\{volume}"))
        {
            data = BulkNative.ReadVolumeData(handle, volume);
            // Read before the scan: whatever changes during the scan is seen again by the next update (harmless).
            journal = UsnJournal.Query(handle);
        }
        var identity = VolumeIdentity.From(data);
        var indexPath = IndexFile.PathFor(options.IndexDirectory, identity.SerialNumber);

        timer.Restart();
        var outcome = BulkScanner.Scan(new BulkScanSettings(volume, Progress: new ScanProgress()));
        timings.Scan = timer.Elapsed;
        var index = new VolumeIndex(identity, journal?.JournalId ?? 0, journal?.NextUsn ?? 0, DateTime.UtcNow, outcome.Pipeline.Records)
        {
            Relationships = outcome.Pipeline.Relationships,
        };

        var saved = false;
        long indexBytes = 0;
        if (!options.NoSave && outcome.IsStable)
        {
            timer.Restart();
            IndexFile.Save(index, indexPath);
            timings.Save = timer.Elapsed;
            saved = true;
            indexBytes = new FileInfo(indexPath).Length;
        }

        VerifyResult? verify = null;
        if (options.Verify && saved)
        {
            timer.Restart();
            var loaded = IndexFile.Read(File.ReadAllBytes(indexPath));
            timings.Load = timer.Elapsed;
            timer.Restart();
            loaded.Recompute();
            timings.Recompute = timer.Elapsed;
            timer.Restart();
            verify = IndexVerifier.Compare(loaded.Records, index.Records);
            timings.Verify = timer.Elapsed;
        }

        timer.Restart();
        var result = SubtreeQuery.Query(index.Records, volume, index.Records[SubtreeQuery.RootRecord], options.Top);
        timings.Query = timer.Elapsed;
        timings.Total = total.Elapsed;
        return new IndexRun(result with { TotalMs = timings.Total.TotalMilliseconds }, "full", null, indexPath, indexBytes, saved, outcome.IsStable,
            index.Records.Count, index.JournalId, index.NextUsn, null, verify, timings);
    }
}
