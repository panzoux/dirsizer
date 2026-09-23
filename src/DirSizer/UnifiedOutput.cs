using System.Text.Json;
using System.Text.Json.Serialization;

static class UnifiedOutput
{
    public static void Write(UnifiedScanResult result, UnifiedCliOptions options, TextWriter output, TextWriter error)
    {
        if (options.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(ToJson(result, options.Top), UnifiedJsonContext.Default.JsonUnifiedOutput));
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
            output.WriteLine();
            output.WriteLine("Summary");
            // strategy and the scanned path first (what ran, on what); directories_scanned/unreadable grouped
            // together (unreadable counts directories -- for the filesystem strategy always; for mft/fsctl the
            // closest analogue, MFT records that could not be read/parsed -- see UnifiedScanResult's own doc
            // comment); no separate byte total, since it always equals the root's own size, already shown above
            // in the Directories table.
            output.WriteLine($"strategy={result.Strategy} path={result.Volume} directories_scanned={result.DirectoriesScanned} unreadable={result.Unreadable} files={result.FileCount}");
        }
        if (result.Unreadable > 0)
        {
            error.WriteLine($"warning: part of the scan could not be completed (unreadable={result.Unreadable}); the sizes are a lower bound.");
            foreach (var sample in result.ErrorSamples) error.WriteLine($"  failed: {sample}");
        }
        if (result.StrategyFallback is not null)
            error.WriteLine($"warning: strategy {result.Strategy} fell back to {result.StrategyFallback}: {result.StrategyFallbackReason}");
        if (options.Benchmark)
        {
            var fallback = result.StrategyFallback is null ? "" : $" (fallback: {result.StrategyFallback}, reason={result.StrategyFallbackReason})";
            error.WriteLine($"benchmark: strategy={result.Strategy}{fallback}, total_ms={result.TotalMs:F1}");
        }
    }

    static JsonUnifiedOutput ToJson(UnifiedScanResult result, int top) => new(
        result.Volume, top, "logical",
        new JsonUnifiedItem(result.Root.Path, result.Root.Size),
        Items(result.RootChildren), Items(result.Directories), Items(result.Files),
        new JsonUnifiedStatistics(result.DirectoriesScanned, result.Unreadable, result.FileCount, result.ErrorSamples,
            new JsonUnifiedPerformance(result.Strategy, result.StrategyFallback, result.StrategyFallbackReason, result.TotalMs)));

    static JsonUnifiedItem[] Items(UnifiedItem[] items)
    {
        var result = new JsonUnifiedItem[items.Length];
        for (var i = 0; i < items.Length; i++) result[i] = new JsonUnifiedItem(items[i].Path, items[i].Size);
        return result;
    }
}

sealed record JsonUnifiedOutput(string Volume, int Top, string SizeMode, JsonUnifiedItem Root, JsonUnifiedItem[] RootChildren, JsonUnifiedItem[] Directories, JsonUnifiedItem[] Files, JsonUnifiedStatistics Statistics);
sealed record JsonUnifiedItem(string Path, long Size);
sealed record JsonUnifiedStatistics(long DirectoriesScanned, long Unreadable, long FileCount, string[] ErrorSamples, JsonUnifiedPerformance Performance);
sealed record JsonUnifiedPerformance(string Strategy, string? StrategyFallback, string? StrategyFallbackReason, double TotalMs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonUnifiedOutput))]
partial class UnifiedJsonContext : JsonSerializerContext;
