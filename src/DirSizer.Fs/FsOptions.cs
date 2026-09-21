sealed class FsOptions
{
    public string Path { get; private set; } = "";
    public int Top { get; private set; } = 25;
    public bool Files { get; private set; }
    public bool Dirs { get; private set; } = true;
    public bool Json { get; private set; }
    public bool Help { get; private set; }
    public bool SelfTest { get; private set; }
    public bool Benchmark { get; private set; }
    public bool Strict { get; private set; }
    public int Workers { get; private set; }    // 0 = automatic

    public static int DefaultWorkers => Math.Min(Environment.ProcessorCount, 8);

    public static FsOptions Parse(string[] args)
    {
        var result = new FsOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help") { result.Help = true; continue; }
            if (arg == "--self-test") { result.SelfTest = true; continue; }
            if (arg == "--benchmark") { result.Benchmark = true; continue; }
            if (arg == "--strict") { result.Strict = true; continue; }
            if (arg == "--files") { result.Files = true; continue; }
            if (arg == "--dirs") { result.Dirs = true; continue; }
            if (arg == "--json") { result.Json = true; continue; }
            if (arg.StartsWith("--top=", StringComparison.Ordinal) && int.TryParse(arg[6..], out var top)) { result.Top = Math.Max(1, top); continue; }
            if (arg == "--top" && index + 1 < args.Length && int.TryParse(args[++index], out top)) { result.Top = Math.Max(1, top); continue; }
            if (arg.StartsWith("--top", StringComparison.Ordinal)) throw new ArgumentException("Use --top N or --top=N.");
            if (arg.StartsWith("--workers=", StringComparison.Ordinal) && int.TryParse(arg[10..], out var workers)) { result.Workers = CheckWorkers(workers); continue; }
            if (arg == "--workers" && index + 1 < args.Length && int.TryParse(args[++index], out workers)) { result.Workers = CheckWorkers(workers); continue; }
            if (arg.StartsWith("--workers", StringComparison.Ordinal)) throw new ArgumentException("Use --workers N or --workers=N.");
            if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option: {arg}");
            if (result.Path.Length != 0) throw new ArgumentException("Only one path is supported.");
            result.Path = arg;
        }
        if (!result.Help && !result.SelfTest && result.Path.Length == 0) throw new ArgumentException("A directory path is required, for example C:\\.");
        return result;
    }

    static int CheckWorkers(int workers) =>
        workers is >= 1 and <= 256 ? workers : throw new ArgumentException("--workers must be between 1 and 256.");

    public static void PrintHelp()
    {
        Console.WriteLine($"""
            dirsizer - fast, read-only folder size scanner for any Windows filesystem

            Usage: dirsizer <path> [--top=N] [--files] [--dirs] [--json] [--workers N] [--strict]

            <path>      Directory to scan: a drive (C:\), a share (\\server\share), or any folder.
            --top=N     Show the largest N results (default: 25)
            --files     Include largest files
            --dirs      Include largest directories (default)
            --json      Write machine-readable JSON to stdout
            --workers N Directories are read by N threads in parallel (default: {DefaultWorkers}; 1-256)
            --strict    Exit with code 3 if a directory could not be read (result is still written)
            --benchmark Print phase timings and memory measurements to stderr
            --self-test Run the built-in tests (uses a temporary folder)
            -h          Show this help

            Reads directories with FindFirstFileExW; never opens individual files. Runs without
            elevation: directories you cannot read are skipped and counted. Sizes are logical
            (the file size the directory listing reports). A hard-linked file counts in every
            directory that holds a name for it. Reparse-point directories (junctions, symbolic
            links, mount points) are not entered. Alternate data streams are not included.

            Exit codes: 0 result written; 1 error; 3 with --strict, a directory could not be read.
            """);
    }
}
