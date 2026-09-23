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
        if (options.Json) output.WriteLine(JsonSerializer.Serialize(ToJson(run, options.Top), IndexJsonContext.Default.JsonIndexOutput));
        else WriteText(run, options, output);
        if (options.Benchmark)
        {
            var t = run.Timings;
            error.WriteLine($"benchmark: mode={run.Mode}, records={run.RecordCount}, index_bytes={run.IndexBytes}, load_ms={Ms(t.Load):F1}, usn_ms={Ms(t.Usn):F1}, " +
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
    }

    static double Ms(TimeSpan time) => time.TotalMilliseconds;

    static JsonIndexOutput ToJson(IndexRun run, int top)
    {
        var result = run.Result;
        var t = run.Timings;
        var update = run.Update;
        return new JsonIndexOutput(
            result.Volume, top, "logical", Item(result.Root), Items(result.RootChildren), Items(result.Directories), Items(result.Files),
            new JsonIndexStatistics(result.DirectoriesScanned, result.FileCount),
            new JsonIndexInfo(run.Mode, run.RebuildReason, run.IndexPath, run.Saved, run.Stable ? "stable" : "unstable", run.RecordCount, run.IndexBytes,
                run.JournalId, run.NextUsn, update?.Changes ?? 0, update?.Reread ?? 0, update?.Replaced ?? 0, update?.Removed ?? 0, update?.ExtensionReads ?? 0,
                Ms(t.Load), Ms(t.Usn), Ms(t.Update), Ms(t.Scan), Ms(t.Recompute), Ms(t.Save), Ms(t.Query), Ms(t.Verify), Ms(t.Total)),
            run.Verify is null ? null : new JsonVerify(run.Verify.Differences, run.Verify.Samples));
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonVerify? Verify = null);
sealed record JsonIndexStatistics(long Directories, long Files);
sealed record JsonIndexInfo(string Mode, string? RebuildReason, string File, bool Saved, string ScanStability, int Records, long IndexBytes,
    ulong JournalId, long NextUsn, int UsnChanges, int RecordsReread, int RecordsReplaced, int RecordsRemoved, int ExtensionReads,
    double LoadMs, double UsnMs, double UpdateMs, double ScanMs, double RecomputeMs, double SaveMs, double QueryMs, double VerifyMs, double TotalMs);
sealed record JsonVerify(int Differences, string[] Samples);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonIndexOutput))]
partial class IndexJsonContext : JsonSerializerContext;
