using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
// Options, result types, and output shared by the usage-scan tools (dirsizer-fsctl and dirsizer-bulk).

sealed class Options
{
    public string Volume { get; private set; } = "";
    public int Top { get; private set; } = 25;
    public bool Files { get; private set; }
    public bool Dirs { get; private set; } = true;
    public bool Json { get; private set; }
    public bool Help { get; private set; }
    public bool SelfTest { get; private set; }
    public bool Benchmark { get; private set; }
    public bool Diagnostics { get; private set; }

    public static Options Parse(string[] args)
    {
        var result = new Options();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help") { result.Help = true; continue; }
            if (arg == "--self-test") { result.SelfTest = true; continue; }
            if (arg == "--benchmark") { result.Benchmark = true; continue; }
            if (arg == "--diagnostics") { result.Diagnostics = true; continue; }
            if (arg == "--files") { result.Files = true; continue; }
            if (arg == "--dirs") { result.Dirs = true; continue; }
            if (arg == "--json") { result.Json = true; continue; }
            if (arg.StartsWith("--top=", StringComparison.Ordinal) && int.TryParse(arg[6..], out var top)) { result.Top = Math.Max(1, top); continue; }
            if (arg == "--top" && index + 1 < args.Length && int.TryParse(args[++index], out top)) { result.Top = Math.Max(1, top); continue; }
            if (arg.StartsWith("--top", StringComparison.Ordinal)) throw new ArgumentException("Use --top N or --top=N.");
            if (arg.StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option: {arg}");
            if (result.Volume.Length != 0) throw new ArgumentException("Only one volume is supported.");
            result.Volume = arg;
        }
        if (!result.Help && !result.SelfTest && result.Volume.Length == 0) throw new ArgumentException("A volume root is required, for example C:\\.");
        return result;
    }

    // For a caller that already has validated values, not raw argv (the unified dirsizer.exe's MftStrategy /
    // FsctlStrategy). Does not go through Parse's validation -- the caller is responsible for a sane volume string.
    public static Options From(string volume, int top, bool files, bool dirs) =>
        new() { Volume = volume, Top = top, Files = files, Dirs = dirs };


    // The options are the same for both usage-scan tools; each tool supplies its own name and notes.
    public static void PrintHelp(string tool, string notes)
    {
        Console.WriteLine($"""
            {tool} - fast, read-only NTFS folder size scanner

            Usage: {tool} C:\ [--top=N] [--files] [--dirs] [--json]

            --top=N   Show the largest N results (default: 25)
            --files   Include largest files
            --dirs    Include largest directories (default)
            --json    Write machine-readable JSON to stdout
            --self-test Run correctness fixtures without opening a volume
            --benchmark Include phase timings and memory measurements
            --diagnostics Include unresolved-record details
            -h        Show this help
            """);
        Console.WriteLine();
        Console.WriteLine(notes);
        Console.WriteLine();
        Console.WriteLine("""
            Sizes are logical bytes from unnamed NTFS $DATA attributes. Directories,
            alternate data streams, reparse targets, and deleted records are excluded.
            Hard-linked file records are counted once, using one selected parent/name.
            Requires Administrator: the executable asks for elevation when it starts.
            """);
    }
}
sealed record ScanResult(string Volume, Dictionary<ulong, FileRecord> Records, FileRecord[] Directories, FileRecord[] Files, FileRecord Root, FileRecord[] RootChildren, UnresolvedRecord[] UnresolvedRecords, int Scanned, int Skipped, ScanMetrics Metrics, int ExitCode = 0, string Reader = "fsctl", IScanDetails? Details = null);

// Reader-specific extras that the shared output code prints without knowing which reader produced them.
interface IScanDetails
{
    void PrintBenchmark();
    JsonBulk? ToJson();
}


sealed record ScanMetrics(TimeSpan OpenTime, TimeSpan VolumeTime, TimeSpan QueryTime, TimeSpan ParserTime, TimeSpan MergeTime, TimeSpan RelationshipTime, TimeSpan AggregationTime, TimeSpan FinalizeTime, TimeSpan TotalTime, long ManagedAllocatedBytes, long PeakWorkingSetBytes, int QueryCount, int ReturnedRecords, int ParseSuccessfulRecords, int ExtensionRecords, int ExactRelationships, int FallbackZeroSequenceRelationships, int FallbackMismatchRelationships, int UnresolvedRelationships, string[] RelationshipSamples)
{
    public double QueryQueriesPerSecond => QueryTime.TotalSeconds == 0 ? 0 : QueryCount / QueryTime.TotalSeconds;
    public double OverallRecordsPerSecond => TotalTime.TotalSeconds == 0 ? 0 : QueryCount / TotalTime.TotalSeconds;
    public int LogicalRecords => ParseSuccessfulRecords - ExtensionRecords;
    public int FallbackRelationships => FallbackZeroSequenceRelationships + FallbackMismatchRelationships;
    public TimeSpan OtherTime => TimeSpan.FromTicks(Math.Max(0, TotalTime.Ticks - OpenTime.Ticks - VolumeTime.Ticks - QueryTime.Ticks - ParserTime.Ticks - MergeTime.Ticks - RelationshipTime.Ticks - AggregationTime.Ticks - FinalizeTime.Ticks));
    public TimeSpan PhaseSum => OpenTime + VolumeTime + QueryTime + ParserTime + MergeTime + RelationshipTime + AggregationTime + FinalizeTime + OtherTime;
}

static class Output
{
    public static void Write(ScanResult result, Options options)
    {
        if (options.Json)
        {
            var rootChildren = JsonItems(result, result.RootChildren, record => record.IsDirectory ? record.Size : record.LogicalSize);
            var directories = JsonItems(result, result.Directories, record => record.Size);
            var files = JsonItems(result, result.Files, record => record.LogicalSize);
            var fileCount = 0;
            var directoryCount = 0;
            foreach (var record in result.Records.Values)
            {
                if (record.IsDirectory) directoryCount++;
                else fileCount++;
            }
            var document = new JsonOutput(
                result.Volume,
                options.Top,
                "logical",
                new JsonItem(Path(result, result.Root), result.Root.Size),
                rootChildren,
                directories,
                files,
                new JsonStatistics(result.Scanned, result.Records.Count, result.Skipped,
                    fileCount,
                    directoryCount,
                    new JsonPerformance(
                        result.Metrics.OpenTime.TotalMilliseconds,
                        result.Metrics.VolumeTime.TotalMilliseconds,
                        result.Metrics.QueryTime.TotalMilliseconds,
                        result.Metrics.ParserTime.TotalMilliseconds,
                        result.Metrics.MergeTime.TotalMilliseconds,
                        result.Metrics.RelationshipTime.TotalMilliseconds,
                        result.Metrics.AggregationTime.TotalMilliseconds,
                        result.Metrics.FinalizeTime.TotalMilliseconds,
                        result.Metrics.OtherTime.TotalMilliseconds,
                        result.Metrics.TotalTime.TotalMilliseconds,
                        result.Metrics.PhaseSum.TotalMilliseconds,
                        result.Metrics.ManagedAllocatedBytes,
                        result.Metrics.PeakWorkingSetBytes,
                        result.Metrics.QueryCount,
                        result.Metrics.QueryQueriesPerSecond,
                        result.Metrics.OverallRecordsPerSecond,
                        result.Metrics.ReturnedRecords,
                        result.Metrics.ParseSuccessfulRecords,
                        result.Metrics.ExtensionRecords,
                        result.Metrics.LogicalRecords,
                        result.Metrics.ExactRelationships,
                        result.Metrics.FallbackRelationships,
                        result.Metrics.FallbackZeroSequenceRelationships,
                        result.Metrics.FallbackMismatchRelationships,
                        result.Metrics.UnresolvedRelationships,
                        result.Metrics.RelationshipSamples)),
                result.Reader,
                result.Details?.ToJson());
            Console.WriteLine(JsonSerializer.Serialize(document, JsonContext.Default.JsonOutput));
            if (options.Benchmark) PrintBenchmarkFor(result);
            if (options.Diagnostics) PrintDiagnostics(result.UnresolvedRecords);
            return;
        }
        if (options.Benchmark) PrintBenchmarkFor(result);
        if (options.Diagnostics) PrintDiagnostics(result.UnresolvedRecords);
        if (options.Dirs) Console.WriteLine($"Showing up to {options.Top} largest directories by logical size");
        if (options.Files) Console.WriteLine($"Showing up to {options.Top} largest files by logical size");
        if (options.Dirs)
        {
            Console.WriteLine();
            Console.WriteLine($"Directories (largest {options.Top})");
            Console.WriteLine("Size\tPath");
            foreach (var record in result.Directories) Console.WriteLine($"{record.Size,12:N0}\t{Path(result, record)}");
        }
        if (options.Files)
        {
            Console.WriteLine();
            Console.WriteLine($"Files (largest {options.Top})");
            Console.WriteLine("Size\tPath");
            foreach (var record in result.Files) Console.WriteLine($"{record.LogicalSize,12:N0}\t{Path(result, record)}");
        }
    }

    static JsonItem[] JsonItems(ScanResult result, FileRecord[] records, Func<FileRecord, long> size)
    {
        var items = new JsonItem[records.Length];
        for (var index = 0; index < records.Length; index++)
            items[index] = new JsonItem(Path(result, records[index]), size(records[index]));
        return items;
    }

    static void PrintDiagnostics(UnresolvedRecord[] records)
    {
        Console.Error.WriteLine($"unresolved_records={records.Length}");
        foreach (var record in records)
            Console.Error.WriteLine($"unresolved: ref={record.Reference.RecordNumber}:{record.Reference.SequenceNumber} directory={record.IsDirectory} size={record.LogicalSize} names={record.NameCount} parent={record.Parent.RecordNumber}:{record.Parent.SequenceNumber}");
    }

    static string Path(ScanResult result, FileRecord record) => RecordPaths.Build(result.Volume, result.Records, record);
    static void PrintBenchmarkFor(ScanResult result)
    {
        if (result.Details is not null) result.Details.PrintBenchmark();
        else PrintBenchmark(result.Metrics);
    }

    static void PrintBenchmark(ScanMetrics metrics) => Console.Error.WriteLine(
        $"benchmark: queries={metrics.QueryCount}, query_qps={metrics.QueryQueriesPerSecond:N1}, overall_records_per_sec={metrics.OverallRecordsPerSecond:N1}, " +
        $"open_ms={metrics.OpenTime.TotalMilliseconds:N1}, volume_ms={metrics.VolumeTime.TotalMilliseconds:N1}, query_ms={metrics.QueryTime.TotalMilliseconds:N1}, " +
        $"parser_ms={metrics.ParserTime.TotalMilliseconds:N1}, merge_ms={metrics.MergeTime.TotalMilliseconds:N1}, relationships_ms={metrics.RelationshipTime.TotalMilliseconds:N1}, " +
        $"aggregation_ms={metrics.AggregationTime.TotalMilliseconds:N1}, finalize_ms={metrics.FinalizeTime.TotalMilliseconds:N1}, other_ms={metrics.OtherTime.TotalMilliseconds:N1}, " +
        $"total_ms={metrics.TotalTime.TotalMilliseconds:N1}, phase_sum_ms={metrics.PhaseSum.TotalMilliseconds:N1}, managed_allocated={metrics.ManagedAllocatedBytes}, " +
        $"peak_working_set={metrics.PeakWorkingSetBytes}, returned={metrics.ReturnedRecords}, parsed={metrics.ParseSuccessfulRecords}, " +
        $"extensions={metrics.ExtensionRecords}, logical_records={metrics.LogicalRecords}, exact={metrics.ExactRelationships}, " +
        $"fallback={metrics.FallbackRelationships}, fallback_zero={metrics.FallbackZeroSequenceRelationships}, fallback_mismatch={metrics.FallbackMismatchRelationships}, " +
        $"unresolved={metrics.UnresolvedRelationships}");

}

sealed record JsonOutput(string Volume, int Top, string SizeMode, JsonItem Root, JsonItem[] RootChildren, JsonItem[] Directories, JsonItem[] Files, JsonStatistics Statistics, string Reader, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonBulk? Bulk = null);

// Present only in the output of dirsizer-bulk. For that reader statistics.performance.query_ms is the whole acquisition phase
// (extents + raw read + USA fixup + layout re-check) and query_count is the number of raw read operations; the phases
// are listed separately here, together with the live-scan stability result.
sealed record JsonBulk(string ScanStability, int Attempts, string LayoutChanges, double DiscardedAttemptMs, double ExtentsMs, double RawReadMs, double FixupMs, double StabilityMs, int RawReadOperations, double RawReadMbPerSec, long MftSlots, int InUseSlots, int DeletedSlots, int UnusedSlots, int Malformed, int FixupFailures, int SignatureMalformed);

sealed record JsonItem(string Path, long Size);
sealed record JsonStatistics(int RecordsScanned, int RecordsAccepted, int RecordsSkipped, int Files, int Directories, JsonPerformance Performance);
sealed record JsonPerformance(double OpenMs, double VolumeMetadataMs, double QueryMs, double ParserMs, double MergeMs, double RelationshipsMs, double AggregationMs, double FinalizeMs, double OtherMs, double TotalMs, double PhaseSumMs, long ManagedAllocatedBytes, long PeakWorkingSetBytes, int QueryCount, double QueryQueriesPerSecond, double OverallRecordsPerSecond, int ReturnedRecords, int ParseSuccessfulRecords, int ExtensionRecords, int LogicalRecords, int ExactRelationships, int FallbackRelationships, int FallbackZeroSequenceRelationships, int FallbackMismatchRelationships, int UnresolvedRelationships, string[] RelationshipSamples);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonOutput))]
partial class JsonContext : JsonSerializerContext;

