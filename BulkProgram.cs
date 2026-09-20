using System.ComponentModel;

try
{
    var options = BulkOptions.Parse(args);
    if (options.Help)
    {
        BulkOptions.PrintHelp();
        return 0;
    }
    if (options.SelfTest)
    {
        BulkSelfTests.Run();
        return 0;
    }
    return BulkRunner.Run(options);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

sealed class BulkOptions
{
    public string Volume { get; private set; } = "C:";
    public int Top { get; private set; } = 25;
    public bool Files { get; private set; }
    public bool Dirs { get; private set; } = true;
    public bool RootChildren { get; private set; }
    public bool Help { get; private set; }
    public bool SelfTest { get; private set; }
    public bool Benchmark { get; private set; }
    public bool Diagnostics { get; private set; }
    public bool Diagnose { get; private set; }
    public bool NoRetry { get; private set; }
    public int TestDelayAfterScanMs { get; private set; }

    public static BulkOptions Parse(string[] args)
    {
        var result = new BulkOptions();
        var volumeSeen = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help") { result.Help = true; continue; }
            if (arg == "--self-test") { result.SelfTest = true; continue; }
            if (arg == "--benchmark") { result.Benchmark = true; continue; }
            if (arg == "--diagnostics") { result.Diagnostics = true; continue; }
            if (arg == "--diagnose") { result.Diagnose = true; continue; }
            if (arg == "--files") { result.Files = true; continue; }
            if (arg == "--dirs") { result.Dirs = true; continue; }
            if (arg == "--root-children") { result.RootChildren = true; continue; }
            if (arg == "--no-retry") { result.NoRetry = true; continue; }
            if (arg.StartsWith("--test-delay-after-scan-ms=", StringComparison.Ordinal) && int.TryParse(arg["--test-delay-after-scan-ms=".Length..], out var delay)) { result.TestDelayAfterScanMs = Math.Max(0, delay); continue; }
            if (arg.StartsWith("--top=", StringComparison.Ordinal) && int.TryParse(arg[6..], out var top)) { result.Top = Math.Max(1, top); continue; }
            if (arg == "--top" && index + 1 < args.Length && int.TryParse(args[++index], out top)) { result.Top = Math.Max(1, top); continue; }
            if (arg.StartsWith("--top", StringComparison.Ordinal)) throw new ArgumentException("Use --top N or --top=N.");
            if (arg.StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option: {arg}");
            if (volumeSeen) throw new ArgumentException("Only one volume is supported.");
            result.Volume = arg.Trim().TrimEnd('\\');
            volumeSeen = true;
        }
        return result;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("dirsizer-bulk C: [--top=N] [--files] [--dirs] [--root-children] [--benchmark] [--diagnostics] [--diagnose]");
        Console.WriteLine("Experimental raw $MFT bulk reader. Read-only; requires NTFS and Administrator access.");
        Console.WriteLine("It runs the same merge, relationship, and aggregation stages as the FSCTL reader and prints the same listing.");
        Console.WriteLine("--root-children  list the root's direct children (the FSCTL reader reports these in --json)");
        Console.WriteLine("--benchmark      phase timings on stderr; --diagnostics unresolved records on stderr");
        Console.WriteLine("--diagnose       classify parser-rejected records by reason and print samples on stderr");
        Console.WriteLine("Exit code 0 = MFT layout unchanged during the scan; 3 = layout changed even after one retry (results are not a consistent snapshot).");
        Console.WriteLine("--no-retry       report an unstable scan instead of rescanning once (test aid); --test-delay-after-scan-ms=N widens the re-check window (test aid)");
    }
}

static class BulkRunner
{
    // Exit codes: 0 = the MFT layout did not change during the scan; 3 = results were produced, but the layout changed during
    // the scan even after one retry, so they are not a consistent snapshot. The check covers the MFT layout (volume,
    // valid data length, extent map); file changes inside existing records during the scan are not detected.
    public static int Run(BulkOptions options)
    {
        var outcome = BulkScanner.Scan(new BulkScanSettings(options.Volume, options.Diagnose, options.NoRetry, options.TestDelayAfterScanMs));
        var pipeline = outcome.Pipeline;
        var volume = options.Volume;
        var records = pipeline.Records;
        // Like the reference reader, top-N selection happens after the total is taken and is not part of any timed phase.
        var directories = ResultSelector.SelectTop(pipeline.Candidates.Directories, options.Top, record => record.Size);
        var files = ResultSelector.SelectTop(pipeline.Candidates.Files, options.Top, record => record.LogicalSize);

        BulkReport.PrintReaderSummary(volume, pipeline.Metadata, pipeline.ExtentCount, pipeline.Scan, options.Diagnose);
        BulkReport.PrintStability(outcome);
        if (options.Benchmark) BulkReport.PrintBenchmark(outcome);
        if (options.Diagnostics) BulkReport.PrintDiagnostics(pipeline.Relationships);

        if (options.Dirs) Console.WriteLine($"Showing up to {options.Top} largest directories by logical size");
        if (options.Files) Console.WriteLine($"Showing up to {options.Top} largest files by logical size");
        if (options.Dirs) PrintListing($"Directories (largest {options.Top})", volume, records, directories, record => record.Size);
        if (options.Files) PrintListing($"Files (largest {options.Top})", volume, records, files, record => record.LogicalSize);
        if (options.RootChildren) PrintListing("Root children", volume, records, pipeline.Candidates.RootChildren.ToArray(), ResultSelector.SizeOf);
        return outcome.IsStable ? 0 : 3;
    }

    static void PrintListing(string title, string volume, Dictionary<ulong, FileRecord> records, FileRecord[] items, Func<FileRecord, long> size)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine("Size\tPath");
        foreach (var record in items) Console.WriteLine($"{size(record),12:N0}\t{RecordPaths.Build(volume, records, record)}");
    }
}
