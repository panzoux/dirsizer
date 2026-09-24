using System.Text.Json;
using System.Text.Json.Serialization;

// dirsizer-index's text and JSON output. The listing has the same layout as dirsizer.exe's (UnifiedOutput); the Summary
// line and the JSON "index" object add what the index did. Warnings and the reason for a full scan go to stderr.
static class IndexOutput
{
    public static void Write(IndexRun run, IndexOptions options, TextWriter output, TextWriter error)
    {
        if (run.RebuildReason is not null) error.WriteLine($"index: full scan ({run.RebuildReason})");
        if (!run.Stable) error.WriteLine("warning: scan_stability=UNSTABLE; the MFT layout changed while it was read, so the index was not saved (exit code 3)");
        if (options.Verify)
        {
            if (run.Verify is null) error.WriteLine("verify: skipped (the index was not saved)");
            else
            {
                error.WriteLine($"verify: differences={run.Verify.Differences}");
                foreach (var sample in run.Verify.Samples) error.WriteLine($"  {sample}");
            }
        }
        if (options.Changes && run.Changes is null) error.WriteLine("changes: there is no earlier index of this volume to compare with");
        if (options.Json) output.WriteLine(JsonSerializer.Serialize(ToJson(run, options.Top), IndexJsonContext.Default.JsonIndexOutput));
        else WriteText(run, options, output);
        if (options.Benchmark)
        {
            var t = run.Timings;
            error.WriteLine($"benchmark: mode={run.Mode}, records={run.RecordCount}, index_bytes={run.IndexBytes}, save_kind={run.SaveKind}, delta_records={run.DeltaRecords}, delta_bytes={run.DeltaBytes}, load_ms={Ms(t.Load):F1}, usn_ms={Ms(t.Usn):F1}, " +
                $"update_ms={Ms(t.Update):F1}, scan_ms={Ms(t.Scan):F1}, recompute_ms={Ms(t.Recompute):F1}, save_ms={Ms(t.Save):F1}, query_ms={Ms(t.Query):F1}, " +
                $"verify_ms={Ms(t.Verify):F1}, total_ms={Ms(t.Total):F1}");
        }
    }

    static void WriteText(IndexRun run, IndexOptions options, TextWriter output)
    {
        var result = run.Result;
        output.WriteLine($"Showing up to {options.Top} largest directories by logical size");
        if (options.Files) output.WriteLine($"Showing up to {options.Top} largest files by logical size");
        output.WriteLine();
        output.WriteLine($"Directories (largest {options.Top})");
        output.WriteLine("Size\tPath");
        foreach (var item in result.Directories) output.WriteLine($"{item.Size,12:N0}\t{item.Path}");
        if (options.Files)
        {
            output.WriteLine();
            output.WriteLine($"Files (largest {options.Top})");
            output.WriteLine("Size\tPath");
            foreach (var item in result.Files) output.WriteLine($"{item.Size,12:N0}\t{item.Path}");
        }
        output.WriteLine();
        output.WriteLine("Summary");
        output.WriteLine($"strategy=index mode={run.Mode} path={result.Volume} directories={result.DirectoriesScanned} files={result.FileCount} " +
            $"usn_changes={run.Update?.Changes ?? 0} records_reread={run.Update?.Reread ?? 0} saved={(run.Saved ? "yes" : "no")}");
        if (run.Changes is { } changes) WriteChanges(changes, options.Top, output);
    }

    static void WriteChanges(ChangeReport changes, int top, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine($"Changes since {changes.SinceUtc:yyyy-MM-dd HH:mm:ss} UTC (when the previous index was written)");
        output.WriteLine("Change\tBefore\tAfter\tPath");
        output.WriteLine($"{Signed(changes.RootAfter - changes.RootBefore)}\t{changes.RootBefore}\t{changes.RootAfter}\t(the directory queried)");
        WriteChangeList($"Shrunk (largest {top})", changes.Shrunk, output);
        WriteChangeList($"Grew (largest {top})", changes.Grew, output);
    }

    static void WriteChangeList(string title, DirectoryChange[] list, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine(title);
        output.WriteLine("Change\tBefore\tAfter\tPath");
        foreach (var change in list) output.WriteLine($"{Signed(change.Delta)}\t{change.Before}\t{change.After}\t{change.Path}");
    }

    // Byte counts without separators, so the columns can be parsed; the sign is always shown.
    static string Signed(long value) => value.ToString("+0;-0;0");

    static JsonChanges ToJson(ChangeReport changes) => new(
        changes.SinceUtc.ToString("o"), changes.RootBefore, changes.RootAfter, changes.RootAfter - changes.RootBefore,
        Array.ConvertAll(changes.Shrunk, ToJson), Array.ConvertAll(changes.Grew, ToJson));

    static JsonChange ToJson(DirectoryChange change) => new(change.Path, change.Before, change.After, change.Delta);

    static double Ms(TimeSpan time) => time.TotalMilliseconds;

    static JsonIndexOutput ToJson(IndexRun run, int top)
    {
        var result = run.Result;
        var t = run.Timings;
        var update = run.Update;
        return new JsonIndexOutput(
            result.Volume, top, "logical", Item(result.Root), Items(result.RootChildren), Items(result.Directories), Items(result.Files),
            new JsonIndexStatistics(result.DirectoriesScanned, result.FileCount),
            new JsonIndexInfo(run.Mode, run.RebuildReason, run.IndexPath, run.Saved, run.Stable ? "stable" : "unstable", run.RecordCount, run.IndexBytes, run.SaveKind, run.DeltaRecords, run.DeltaBytes,
                run.JournalId, run.NextUsn, update?.Changes ?? 0, update?.Reread ?? 0, update?.Replaced ?? 0, update?.Removed ?? 0, update?.ExtensionReads ?? 0,
                Ms(t.Load), Ms(t.Usn), Ms(t.Update), Ms(t.Scan), Ms(t.Recompute), Ms(t.Save), Ms(t.Query), Ms(t.Verify), Ms(t.Total)),
            run.Verify is null ? null : new JsonVerify(run.Verify.Differences, run.Verify.Samples),
            run.Changes is null ? null : ToJson(run.Changes));
    }

    static JsonItem Item(UnifiedItem item) => new(item.Path, item.Size);

    static JsonItem[] Items(UnifiedItem[] items)
    {
        var result = new JsonItem[items.Length];
        for (var i = 0; i < items.Length; i++) result[i] = Item(items[i]);
        return result;
    }
}

sealed record JsonIndexOutput(string Path, int Top, string SizeMode, JsonItem Root, JsonItem[] RootChildren, JsonItem[] Directories, JsonItem[] Files,
    JsonIndexStatistics Statistics, JsonIndexInfo Index,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonVerify? Verify = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonChanges? Changes = null);
sealed record JsonIndexStatistics(long Directories, long Files);
sealed record JsonIndexInfo(string Mode, string? RebuildReason, string File, bool Saved, string ScanStability, int Records, long IndexBytes, string SaveKind, int DeltaRecords, long DeltaBytes,
    ulong JournalId, long NextUsn, int UsnChanges, int RecordsReread, int RecordsReplaced, int RecordsRemoved, int ExtensionReads,
    double LoadMs, double UsnMs, double UpdateMs, double ScanMs, double RecomputeMs, double SaveMs, double QueryMs, double VerifyMs, double TotalMs);
sealed record JsonVerify(int Differences, string[] Samples);
sealed record JsonChanges(string SinceUtc, long RootBefore, long RootAfter, long RootDelta, JsonChange[] Shrunk, JsonChange[] Grew);
sealed record JsonChange(string Path, long Before, long After, long Delta);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonIndexOutput))]
partial class IndexJsonContext : JsonSerializerContext;
