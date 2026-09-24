using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

// Phase timings of one run. Phases that did not run stay zero.
sealed class IndexTimings
{
    public TimeSpan Load, Usn, Update, Scan, Recompute, Save, Query, Verify, Total;
}

// What an incremental update did (roadmap I3): Changes = USN records read; Reread = distinct MFT records read again
// (journal-named plus NTFS metadata); Replaced / Removed = index entries rewritten / dropped; ExtensionReads = extension
// records read through attribute lists. RebuildReason is set when the update could not be completed.
sealed record UpdateResult(int Changes, int Reread, int Replaced, int Removed, int ExtensionReads, string? RebuildReason);

// SaveKind: "full" (the whole base file), "delta" (only the entries changed since it, DeltaRecords of them) or "none".
sealed record IndexRun(
    UnifiedScanResult Result, string Mode, string? RebuildReason, string IndexPath, long IndexBytes, bool Saved, bool Stable,
    int RecordCount, ulong JournalId, long NextUsn, UpdateResult? Update, VerifyResult? Verify, IndexTimings Timings,
    string SaveKind = "none", int DeltaRecords = 0, long DeltaBytes = 0, ChangeReport? Changes = null)
{
    // 0 = result written; 2 = --verify found differences; 3 = the full scan was unstable, so the index was not saved.
    public int ExitCode => Verify is { Differences: > 0 } ? 2 : Stable ? 0 : 3;
}

static class IndexRunner
{
    public static IndexRun Run(IndexOptions options)
    {
        var timings = new IndexTimings();
        var total = Stopwatch.StartNew();
        var timer = new Stopwatch();
        var target = Path.GetFullPath(options.Target);
        var pathRoot = Path.GetPathRoot(target);
        if (pathRoot is null || pathRoot.Length != 3 || pathRoot[1] != ':')
            throw new ArgumentException($"{options.Target}: dirsizer-index works on local drives only (a path such as C:\\Users).");
        var volume = DriveRoot.Validate(pathRoot);
        using var handle = BulkNative.OpenVolume($"\\\\.\\{volume}");
        var data = BulkNative.ReadVolumeData(handle, volume);
        var identity = VolumeIdentity.From(data);
        // Read before anything else: whatever changes after this point is seen again by the next run (harmless).
        var journal = UsnJournal.Query(handle);
        var indexPath = IndexFile.PathFor(options.IndexDirectory, identity.SerialNumber);

        VolumeIndex? index = null;
        UpdateResult? update = null;
        IndexSnapshot? before = null;
        string? reason;
        if (options.Rebuild) reason = "--rebuild was given";
        else
        {
            timer.Restart();
            index = IndexFile.TryLoad(indexPath, out reason);
            timings.Load = timer.Elapsed;
            if (index is not null && options.Changes && index.Identity == identity)
            {
                // The baseline for --changes: the saved index aggregated as it was, before anything is applied to it.
                timer.Restart();
                index.Recompute();
                timings.Recompute += timer.Elapsed;
                before = IndexSnapshot.Capture(index);
            }
            if (index is not null) reason = IndexValidity.Check(index, identity, journal);
            if (index is not null && reason is null) reason = Update(index, handle, data, journal!.Value, timings, out update);
            if (reason is not null) index = null;
        }

        var mode = "incremental";
        var stable = true;
        if (index is null)
        {
            mode = "full";
            timer.Restart();
            var outcome = BulkScanner.Scan(new BulkScanSettings(volume, Progress: new ScanProgress()));
            timings.Scan = timer.Elapsed;
            stable = outcome.IsStable;
            index = new VolumeIndex(identity, journal?.JournalId ?? 0, journal?.NextUsn ?? 0, DateTime.UtcNow, outcome.Pipeline.Records)
            {
                Relationships = outcome.Pipeline.Relationships,
            };
        }

        var saved = false;
        var saveKind = "none";
        long indexBytes = 0;
        long deltaBytes = 0;
        if (!options.NoSave && stable)
        {
            timer.Restart();
            // After an incremental run, only the entries changed since the base file are written, while there are few.
            if (mode == "incremental" && IndexFile.ShouldSaveDelta(index))
            {
                IndexFile.SaveDelta(index, indexPath);
                saveKind = "delta";
                deltaBytes = new FileInfo(IndexFile.DeltaPathFor(indexPath)).Length;
            }
            else
            {
                IndexFile.Save(index, indexPath);
                saveKind = "full";
            }
            timings.Save = timer.Elapsed;
            saved = true;
            indexBytes = new FileInfo(indexPath).Length;
        }

        timer.Restart();
        var root = PathResolver.Find(index, target);
        var result = SubtreeQuery.Query(index.Records, volume, root, options.Top);
        var changes = before is null ? null : ChangeReporter.Compare(before, index, volume, root, options.Top);
        timings.Query = timer.Elapsed;

        VerifyResult? verify = null;
        if (options.Verify)
        {
            // The index as it is now must equal a fresh full scan (on a quiescent volume; a live one changes in between).
            timer.Restart();
            var fresh = BulkScanner.Scan(new BulkScanSettings(volume, Progress: new ScanProgress()));
            verify = IndexVerifier.Compare(index.Records, fresh.Pipeline.Records);
            timings.Verify = timer.Elapsed;
        }
        timings.Total = total.Elapsed;
        return new IndexRun(result with { TotalMs = timings.Total.TotalMilliseconds }, mode, reason, indexPath, indexBytes, saved, stable,
            index.Records.Count, index.JournalId, index.NextUsn, update, verify, timings, saveKind, saveKind == "delta" ? index.Dirty.Count : 0, deltaBytes, changes);
    }

    // Returns null when the index is now current, or the reason a full scan is needed instead.
    static string? Update(VolumeIndex index, SafeFileHandle handle, BulkNative.VolumeData data, JournalState journal, IndexTimings timings, out UpdateResult? update)
    {
        update = null;
        var timer = Stopwatch.StartNew();
        var changes = new List<UsnChange>();
        var buffer = new byte[1 << 20];
        var next = UsnJournal.ReadChanges(start => UsnJournal.Fetch(handle, journal.JournalId, start, buffer), index.NextUsn, changes);
        timings.Usn = timer.Elapsed;
        if (next is null) return "the USN journal no longer holds the saved position (it wrapped)";

        timer.Restart();
        update = IndexUpdater.Apply(index.Records, changes, IndexUpdater.MetadataRecords(index.Records), new FsctlRecordSource(handle, data.RecordSize, data.BytesPerCluster), index.Dirty);
        timings.Update = timer.Elapsed;
        if (update.RebuildReason is not null) return update.RebuildReason;

        index.NextUsn = next.Value;
        index.WrittenUtc = DateTime.UtcNow;
        timer.Restart();
        index.Recompute();
        timings.Recompute += timer.Elapsed;
        return null;
    }
}
