using System.Text.Json;
using System.Text.Json.Serialization;

static class FsOutput
{
    public static void Write(FsResult result, FsOptions options, TextWriter output, TextWriter error)
    {
        if (options.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(ToJson(result, options.Top), FsJsonContext.Default.JsonFsOutput));
        }
        else
        {
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
            var c = result.Counters;
            output.WriteLine();
            output.WriteLine("Summary");
            output.WriteLine($"directories_scanned={c.DirectoriesScanned} directories_denied={c.DirectoriesDenied} directories_failed={c.DirectoriesFailed} reparse_skipped={c.ReparseSkipped} files={c.Files} bytes={c.Bytes}");
        }
        if (result.Counters.Unreadable > 0)
        {
            error.WriteLine($"warning: {result.Counters.Unreadable:N0} directories could not be read (denied {result.Counters.DirectoriesDenied:N0}, failed {result.Counters.DirectoriesFailed:N0}); the sizes are a lower bound.");
            foreach (var sample in result.ErrorSamples) error.WriteLine($"  failed: {sample}");
        }
        if (options.Benchmark) error.WriteLine(BenchmarkLine(result.Metrics));
    }

    static string BenchmarkLine(FsMetrics m) =>
        $"benchmark: workers={m.Workers}, large_fetch={(m.LargeFetch ? "on" : "off")}, open_ms={m.Open.TotalMilliseconds:N1}, walk_ms={m.Walk.TotalMilliseconds:N1}, " +
        $"aggregation_ms={m.Aggregation.TotalMilliseconds:N1}, finalize_ms={m.Finalize.TotalMilliseconds:N1}, other_ms={m.Other.TotalMilliseconds:N1}, " +
        $"total_ms={m.Total.TotalMilliseconds:N1}, phase_sum_ms={m.PhaseSum.TotalMilliseconds:N1}, enum_ms_total={m.EnumTotal.TotalMilliseconds:N1}, " +
        $"idle_ms_total={m.IdleTotal.TotalMilliseconds:N1}, peak_queued_dirs={m.PeakQueuedDirs}, entries_per_sec={m.EntriesPerSec:N0}, " +
        $"directories_per_sec={m.DirectoriesPerSec:N0}, logical_mib_per_sec={m.LogicalMibPerSec:N1}, managed_allocated={m.ManagedAllocatedBytes}, peak_working_set={m.PeakWorkingSetBytes}";

    public static JsonFsOutput ToJson(FsResult result, int top)
    {
        var c = result.Counters;
        var m = result.Metrics;
        return new JsonFsOutput(
            result.RootPath,
            top,
            "logical",
            new JsonFsItem(result.Root.Path, result.Root.Size),
            Items(result.RootChildren),
            Items(result.Directories),
            Items(result.Files),
            new JsonFsStatistics(
                c.DirectoriesScanned, c.DirectoriesDenied, c.DirectoriesFailed, c.ReparseSkipped, c.Directories, c.Files, c.Bytes,
                result.ErrorSamples,
                new JsonFsPerformance(
                    m.Open.TotalMilliseconds, m.Walk.TotalMilliseconds, m.Aggregation.TotalMilliseconds, m.Finalize.TotalMilliseconds,
                    m.Other.TotalMilliseconds, m.Total.TotalMilliseconds, m.PhaseSum.TotalMilliseconds,
                    m.EnumTotal.TotalMilliseconds, m.IdleTotal.TotalMilliseconds, m.Workers, m.LargeFetch, m.PeakQueuedDirs,
                    m.ManagedAllocatedBytes, m.PeakWorkingSetBytes, m.EntriesPerSec, m.DirectoriesPerSec, m.LogicalMibPerSec)),
            "win32-find");
    }

    static JsonFsItem[] Items(ResultItem[] items)
    {
        var result = new JsonFsItem[items.Length];
        for (var index = 0; index < items.Length; index++) result[index] = new JsonFsItem(items[index].Path, items[index].Size);
        return result;
    }
}

sealed record JsonFsOutput(string Volume, int Top, string SizeMode, JsonFsItem Root, JsonFsItem[] RootChildren, JsonFsItem[] Directories, JsonFsItem[] Files, JsonFsStatistics Statistics, string Reader);
sealed record JsonFsItem(string Path, long Size);
sealed record JsonFsStatistics(long DirectoriesScanned, long DirectoriesDenied, long DirectoriesFailed, long ReparseSkipped, long Directories, long Files, long Bytes, string[] ErrorSamples, JsonFsPerformance Performance);
sealed record JsonFsPerformance(double OpenMs, double WalkMs, double AggregationMs, double FinalizeMs, double OtherMs, double TotalMs, double PhaseSumMs, double EnumMsTotal, double IdleMsTotal, int Workers, bool LargeFetch, int PeakQueuedDirs, long ManagedAllocatedBytes, long PeakWorkingSetBytes, double EntriesPerSec, double DirectoriesPerSec, double LogicalMibPerSec);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonFsOutput))]
partial class FsJsonContext : JsonSerializerContext;
