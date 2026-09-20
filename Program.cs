using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

Options options;
try
{
    options = Options.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}
if (options.Help)
{
    Options.PrintHelp();
    return 0;
}
if (options.SelfTest)
{
    SelfTests.Run();
    return 0;
}

try
{
    var result = options.Reader == "bulk" ? BulkIntegration.Run(options) : new Scanner(options).Run();
    Output.Write(result, options);
    return result.ExitCode;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    if (exception is UnauthorizedAccessException || exception is Win32Exception win32 && win32.NativeErrorCode is 5 or 1314)
        Console.Error.WriteLine("Open the terminal as Administrator. The scanner is read-only and requires NTFS volume access.");
    return 1;
}

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
    public string Reader { get; private set; } = "fsctl";

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
            if (arg.StartsWith("--reader=", StringComparison.Ordinal)) { result.Reader = ParseReader(arg[9..]); continue; }
            if (arg == "--reader" && index + 1 < args.Length) { result.Reader = ParseReader(args[++index]); continue; }
            if (arg.StartsWith("--reader", StringComparison.Ordinal)) throw new ArgumentException("Use --reader fsctl|bulk or --reader=fsctl|bulk.");
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

    static string ParseReader(string value) => value is "fsctl" or "bulk" ? value : throw new ArgumentException($"Unknown reader '{value}'. Use --reader=fsctl or --reader=bulk.");

    public static void PrintHelp() => Console.WriteLine("""
        dirsizer - fast, read-only NTFS folder size scanner

        Usage: dirsizer C:\\ [--top=N] [--files] [--dirs] [--json] [--reader=fsctl|bulk]

        --top=N   Show the largest N results (default: 25)
        --files   Include largest files
        --dirs    Include largest directories (default)
        --json    Write machine-readable JSON to stdout
        --self-test Run parser correctness fixtures without opening a volume
        --benchmark Include phase timings and memory measurements
        --diagnostics Include unresolved-record details
        --reader=fsctl  Read MFT records one at a time with FSCTL_GET_NTFS_FILE_RECORD (default; the reference implementation)
        --reader=bulk   EXPERIMENTAL. Read the raw $MFT in large blocks; about 1.4x faster in measurements (it varies). Never falls
                        back to fsctl. Exit code 3 means the result was produced but the MFT layout changed while it was
                        read (even after one rescan), so it is not a consistent snapshot; see the README.
        -h        Show this help

        Sizes are logical bytes from unnamed NTFS $DATA attributes. Directories,
        alternate data streams, reparse targets, and deleted records are excluded.
        Hard-linked file records are counted once, using one selected parent/name.
        Progress is written to stderr while the MFT is scanned.
        """);
}

sealed class Scanner(Options options)
{
    public ScanResult Run()
    {
        var totalTimer = Stopwatch.StartNew();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var openTimer = Stopwatch.StartNew();
        var displayVolume = options.Volume.Trim().TrimEnd('\\');
        var root = NormalizeVolume(displayVolume);
        using var handle = Native.OpenVolume(root);
        openTimer.Stop();
        var volumeTimer = Stopwatch.StartNew();
        var volume = Native.ReadVolumeData(handle, displayVolume);
        volumeTimer.Stop();
        var queryTimer = new Stopwatch();
        var parserTimer = new Stopwatch();
        var mergeTimer = new Stopwatch();
        var returnedRecords = 0;
        var parseSuccessfulRecords = 0;
        var extensionRecords = 0;
        var records = new Dictionary<ulong, FileRecord>();
        var scanned = 0;
        var skipped = 0;
        var total = volume.RecordSize == 0 ? 0 : volume.MftLength / volume.RecordSize;
        var input = new byte[8];
        var output = new byte[Math.Max(4096, volume.RecordSize + 16)];
        var lastPercent = -1;
        Progress(0, total, ref lastPercent);

        var next = total == 0 ? 0UL : (ulong)total - 1;
        ulong previousReturned = ulong.MaxValue;
        while (total > 0)
        {
            scanned++;
            queryTimer.Start();
            var fileRecord = Native.ReadRecord(handle, next, input, output);
            queryTimer.Stop();
            if (fileRecord is null) break;
            if (fileRecord.Value.ReturnedRecordNumber >= previousReturned)
                throw new IOException($"FSCTL enumeration did not descend: returned {fileRecord.Value.ReturnedRecordNumber} after {previousReturned}.");
            previousReturned = fileRecord.Value.ReturnedRecordNumber;
            returnedRecords++;
            parserTimer.Start();
            var parsed = RecordParser.Parse(fileRecord.Value.ReturnedRecordNumber, fileRecord.Value.Buffer.AsSpan(fileRecord.Value.Offset, fileRecord.Value.Length));
            parserTimer.Stop();
            if (parsed is null) skipped++;
            else
            {
                parseSuccessfulRecords++;
                if (parsed.BaseReference.RecordNumber != 0) extensionRecords++;
                mergeTimer.Start();
                RecordMerger.Merge(records, parsed);
                mergeTimer.Stop();
            }
            var returnedNumber = parsed?.Reference.RecordNumber ?? fileRecord.Value.ReturnedRecordNumber;
            if (returnedNumber == 0) break;
            next = returnedNumber - 1;
            Progress(scanned, total, ref lastPercent);
        }
        Progress(total, total, ref lastPercent);
        Console.Error.WriteLine();

        var relationshipTimer = Stopwatch.StartNew();
        var relationships = RelationshipResolver.Resolve(records);
        var rootRecord = relationships.Root;

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
        var metrics = new ScanMetrics(
            openTimer.Elapsed,
            volumeTimer.Elapsed,
            queryTimer.Elapsed,
            parserTimer.Elapsed,
            mergeTimer.Elapsed,
            relationshipTimer.Elapsed,
            aggregationTimer.Elapsed,
            finalizeTimer.Elapsed,
            totalTimer.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            process.PeakWorkingSet64,
            scanned,
            returnedRecords,
            parseSuccessfulRecords,
            extensionRecords,
            relationships.Exact,
            relationships.FallbackZeroSequence,
            relationships.FallbackMismatch,
            relationships.Unresolved,
            relationships.Samples);
        if (metrics.PhaseSum != metrics.TotalTime)
            throw new InvalidOperationException("Benchmark phase accounting does not sum to total scan time.");
        return new ScanResult(displayVolume, records, ResultSelector.SelectTop(candidates.Directories, options.Top, record => record.Size), ResultSelector.SelectTop(candidates.Files, options.Top, record => record.LogicalSize), rootRecord, candidates.RootChildren.ToArray(), relationships.UnresolvedRecords, scanned, skipped, metrics);
    }

    internal static string NormalizeVolume(string value)
    {
        var drive = value.Trim().TrimEnd('\\');
        if (drive.Length == 2 && drive[1] == ':') return $"\\\\.\\{drive}";
        throw new ArgumentException("The volume must be a drive root such as C:\\.");
    }

    static void Progress(long current, long total, ref int lastPercent)
    {
        var percent = total == 0 ? 0 : (int)(current * 100L / total);
        if (percent == lastPercent) return;
        lastPercent = percent;
        Console.Error.Write($"\rScanning MFT: {current}/{total} ({percent}%)");
        Console.Error.Flush();
    }
}

sealed record ScanResult(string Volume, Dictionary<ulong, FileRecord> Records, FileRecord[] Directories, FileRecord[] Files, FileRecord Root, FileRecord[] RootChildren, UnresolvedRecord[] UnresolvedRecords, int Scanned, int Skipped, ScanMetrics Metrics, int ExitCode = 0, BulkScanOutcome? Bulk = null);

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
                result.Bulk is null ? "fsctl" : "bulk",
                result.Bulk is null ? null : JsonBulk.From(result.Bulk));
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
        if (result.Bulk is not null) BulkReport.PrintBenchmark(result.Bulk);
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

// Present only for --reader=bulk. For that reader statistics.performance.query_ms is the whole acquisition phase
// (extents + raw read + USA fixup + layout re-check) and query_count is the number of raw read operations; the phases
// are listed separately here, together with the live-scan stability result.
sealed record JsonBulk(string ScanStability, int Attempts, string LayoutChanges, double DiscardedAttemptMs, double ExtentsMs, double RawReadMs, double FixupMs, double StabilityMs, int RawReadOperations, double RawReadMbPerSec, long MftSlots, int InUseSlots, int DeletedSlots, int UnusedSlots, int Malformed, int FixupFailures, int SignatureMalformed)
{
    public static JsonBulk From(BulkScanOutcome outcome)
    {
        var scan = outcome.Pipeline.Scan;
        var metrics = outcome.Pipeline.Metrics;
        return new JsonBulk(outcome.IsStable ? "stable" : "unstable", outcome.Attempts, outcome.Change.ToString(), outcome.DiscardedTime.TotalMilliseconds,
            metrics.ExtentsTime.TotalMilliseconds, metrics.RawReadTime.TotalMilliseconds, metrics.FixupTime.TotalMilliseconds, metrics.StabilityTime.TotalMilliseconds,
            scan.ReadOperations, scan.MegabytesPerSecond, scan.Slots, scan.InUseSlots, scan.DeletedSlots, scan.UnusedSlots, scan.Malformed, scan.FixupFailures, scan.SignatureMalformed);
    }
}
sealed record JsonItem(string Path, long Size);
sealed record JsonStatistics(int RecordsScanned, int RecordsAccepted, int RecordsSkipped, int Files, int Directories, JsonPerformance Performance);
sealed record JsonPerformance(double OpenMs, double VolumeMetadataMs, double QueryMs, double ParserMs, double MergeMs, double RelationshipsMs, double AggregationMs, double FinalizeMs, double OtherMs, double TotalMs, double PhaseSumMs, long ManagedAllocatedBytes, long PeakWorkingSetBytes, int QueryCount, double QueryQueriesPerSecond, double OverallRecordsPerSecond, int ReturnedRecords, int ParseSuccessfulRecords, int ExtensionRecords, int LogicalRecords, int ExactRelationships, int FallbackRelationships, int FallbackZeroSequenceRelationships, int FallbackMismatchRelationships, int UnresolvedRelationships, string[] RelationshipSamples);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(JsonOutput))]
partial class JsonContext : JsonSerializerContext;

static class Native
{
    const uint GenericRead = 0x80000000;
    const uint FileShareRead = 1;
    const uint FileShareWrite = 2;
    const uint OpenExisting = 3;
    const uint FsctlGetNtfsVolumeData = 0x00090064;
    const uint FsctlGetNtfsFileRecords = 0x00090068;

    public static SafeFileHandle OpenVolume(string path)
    {
        var handle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Cannot open {path} (Win32 error {error}).");
        }
        return handle;
    }

    public static VolumeData ReadVolumeData(SafeFileHandle handle, string displayVolume)
    {
        var output = new byte[128];
        if (!DeviceIoControl(handle, FsctlGetNtfsVolumeData, null, 0, output, output.Length, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            var filesystem = GetFilesystemName(displayVolume);
            if (!string.Equals(filesystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"{displayVolume}: filesystem is {filesystem ?? "unknown"}; only NTFS volumes are supported.");
            throw new Win32Exception(error, $"Cannot read NTFS volume metadata from {displayVolume} (Win32 error {error}).");
        }
        return new VolumeData(BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(56)), BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(48)));
    }

    static string? GetFilesystemName(string volume)
    {
        var name = new StringBuilder(32);
        return GetVolumeInformation($"\\\\?\\{volume.TrimEnd('\\')}\\", null, 0, out _, out _, out _, name, name.Capacity)
            ? name.ToString()
            : null;
    }

    public static RecordBuffer? ReadRecord(SafeFileHandle handle, ulong number, byte[] input, byte[] output)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(input, number);
        if (!DeviceIoControl(handle, FsctlGetNtfsFileRecords, input, input.Length, output, output.Length, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 18) return null;
            throw new Win32Exception(error);
        }
        var returnedRecordNumber = BinaryPrimitives.ReadUInt64LittleEndian(output) & 0x0000FFFFFFFFFFFFUL;
        var length = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8));
        const int recordOffset = 12;
        if (length <= 0 || length > returned - recordOffset) return null;
        if (returnedRecordNumber > number)
            throw new IOException($"FSCTL returned record {returnedRecordNumber} above requested ordinal {number}.");
        return new RecordBuffer(returnedRecordNumber, output, recordOffset, length);
    }

    public readonly record struct RecordBuffer(ulong ReturnedRecordNumber, byte[] Buffer, int Offset, int Length);
    public readonly record struct VolumeData(long MftLength, int RecordSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string rootPath, StringBuilder? volumeName, int volumeNameSize, out uint serialNumber, out uint maximumComponentLength, out uint filesystemFlags, StringBuilder filesystemName, int filesystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}