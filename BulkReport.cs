// Diagnostic lines for the raw bulk reader. All go to stderr so stdout stays the listing or JSON.
static class BulkReport
{
    public static void PrintReaderSummary(string volume, BulkNative.VolumeData metadata, int extentCount, BulkResult scan, bool diagnose, TextWriter? writer = null)
    {
        writer ??= Console.Error;
        writer.WriteLine($"reader=bulk volume={volume} mft_bytes={metadata.MftValidDataLength} record_size={metadata.RecordSize} extents={extentCount} mft_tail_bytes={scan.TailBytes}");
        writer.WriteLine($"mft_slots={scan.Slots} in_use_slots={scan.InUseSlots} parse_successful={scan.ParseSuccessful} extension_records={scan.ExtensionRecords} logical_records={scan.LogicalRecords} unused_slots={scan.UnusedSlots} malformed={scan.Malformed} fixup_failures={scan.FixupFailures}");
        writer.WriteLine($"raw_read_operations={scan.ReadOperations} raw_bytes_read={scan.BytesRead} raw_read_ms={scan.ReadTime.TotalMilliseconds:N1} raw_read_MB_per_sec={scan.MegabytesPerSecond:N1}");
        writer.WriteLine($"deleted_slots={scan.DeletedSlots} deleted_directories={scan.DeletedDirectories} signature_malformed={scan.SignatureMalformed} " + FormatRejects(scan.RejectCounts));
        if (!diagnose) return;
        foreach (var pair in scan.RejectFlags) writer.WriteLine($"reject_flags reason={pair.Key.Reason} flags=0x{pair.Key.Flags:X4} count={pair.Value}");
        foreach (var sample in scan.Samples) writer.WriteLine(sample);
    }

    public static void PrintStability(BulkScanOutcome outcome)
    {
        if (outcome.IsStable)
            Console.Error.WriteLine($"scan_stability=stable attempts={outcome.Attempts} (MFT layout unchanged during the scan; changes inside existing records are not detected)");
        else
            Console.Error.WriteLine($"scan_stability=UNSTABLE attempts={outcome.Attempts} changes={outcome.Change} (the MFT layout changed during the scan; the results are not a consistent snapshot)");
    }

    public static string FormatRejects(int[] counts)
    {
        var text = new System.Text.StringBuilder();
        for (var reason = 1; reason < counts.Length; reason++)
        {
            if (text.Length != 0) text.Append(' ');
            text.Append($"reject_{(ParseReject)reason}={counts[reason]}");
        }
        return text.ToString();
    }

    // Same keys as the reference reader's benchmark line where the meaning is the same. The FSCTL "query" phase is
    // replaced by the raw-read and USA-fixup phases, and "returned" counts the in-use slots handed to the parser.
    public static void PrintBenchmark(BulkScanOutcome outcome)
    {
        var pipeline = outcome.Pipeline;
        var metrics = pipeline.Metrics;
        var scan = pipeline.Scan;
        var relationships = pipeline.Relationships;
        var records = pipeline.Records;
        var files = 0;
        var directories = 0;
        foreach (var record in records.Values)
        {
            if (record.IsDirectory) directories++;
            else files++;
        }
        Console.Error.WriteLine(
            $"benchmark: reader=bulk, attempts={outcome.Attempts}, discarded_attempt_ms={outcome.DiscardedTime.TotalMilliseconds:N1}, raw_reads={scan.ReadOperations}, raw_MB_per_sec={scan.MegabytesPerSecond:N1}, " +
            $"open_ms={metrics.OpenTime.TotalMilliseconds:N1}, volume_ms={metrics.VolumeTime.TotalMilliseconds:N1}, extents_ms={metrics.ExtentsTime.TotalMilliseconds:N1}, " +
            $"raw_read_ms={metrics.RawReadTime.TotalMilliseconds:N1}, fixup_ms={metrics.FixupTime.TotalMilliseconds:N1}, parser_ms={metrics.ParserTime.TotalMilliseconds:N1}, " +
            $"merge_ms={metrics.MergeTime.TotalMilliseconds:N1}, stability_ms={metrics.StabilityTime.TotalMilliseconds:N1}, relationships_ms={metrics.RelationshipTime.TotalMilliseconds:N1}, " +
            $"aggregation_ms={metrics.AggregationTime.TotalMilliseconds:N1}, finalize_ms={metrics.FinalizeTime.TotalMilliseconds:N1}, other_ms={metrics.OtherTime.TotalMilliseconds:N1}, " +
            $"total_ms={metrics.TotalTime.TotalMilliseconds:N1}, phase_sum_ms={metrics.PhaseSum.TotalMilliseconds:N1}, managed_allocated={metrics.ManagedAllocatedBytes}, " +
            $"peak_working_set={metrics.PeakWorkingSetBytes}, returned={scan.InUseSlots}, parsed={scan.ParseSuccessful}, " +
            $"extensions={scan.ExtensionRecords}, logical_records={scan.LogicalRecords}, records_accepted={records.Count}, files={files}, directories={directories}, exact={relationships.Exact}, " +
            $"fallback={relationships.Fallback}, fallback_zero={relationships.FallbackZeroSequence}, fallback_mismatch={relationships.FallbackMismatch}, " +
            $"unresolved={relationships.Unresolved}");
    }

    public static void PrintDiagnostics(RelationshipResult relationships)
    {
        Console.Error.WriteLine($"unresolved_records={relationships.UnresolvedRecords.Length}");
        foreach (var record in relationships.UnresolvedRecords)
            Console.Error.WriteLine($"unresolved: ref={record.Reference.RecordNumber}:{record.Reference.SequenceNumber} directory={record.IsDirectory} size={record.LogicalSize} names={record.NameCount} parent={record.Parent.RecordNumber}:{record.Parent.SequenceNumber}");
        Console.Error.WriteLine($"relationship_samples={relationships.Samples.Length}");
        foreach (var sample in relationships.Samples) Console.Error.WriteLine($"relationship_sample: {sample}");
    }
}
