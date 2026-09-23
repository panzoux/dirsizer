// dirsizer.exe's own option set -- see the design spec, "Common CLI options". Deliberately smaller than
// FsOptions: no --enumerator, no --strategy, nothing that names an implementation.
sealed class UnifiedCliOptions
{
    public string Root { get; private set; } = "";
    public int Top { get; private set; } = 25;
    public bool Files { get; private set; }
    public bool Dirs { get; private set; } = true;
    public bool Json { get; private set; }
    public bool Help { get; private set; }
    public bool SelfTest { get; private set; }
    public bool Benchmark { get; private set; }
    public bool Strict { get; private set; }
    public int Workers { get; private set; }   // 0 = automatic

    public static UnifiedCliOptions Parse(string[] args)
    {
        var result = new UnifiedCliOptions();
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
            if (arg.StartsWith("--top", StringComparison.Ordinal)) throw new ArgumentException("--top needs a whole number: --top=N or --top N.");
            if (arg.StartsWith("--workers=", StringComparison.Ordinal) && int.TryParse(arg[10..], out var workers)) { result.Workers = CheckWorkers(workers); continue; }
            if (arg == "--workers" && index + 1 < args.Length && int.TryParse(args[++index], out workers)) { result.Workers = CheckWorkers(workers); continue; }
            if (arg.StartsWith("--workers", StringComparison.Ordinal)) throw new ArgumentException("--workers needs a whole number from 1 to 256: --workers=N or --workers N.");
            if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option: {arg}");
            if (result.Root.Length != 0) throw new ArgumentException("Only one path is supported.");
            result.Root = arg;
        }
        if (!result.Help && !result.SelfTest && result.Root.Length == 0) throw new ArgumentException("A directory path is required, for example C:\\.");
        return result;
    }

    static int CheckWorkers(int workers) =>
        workers is >= 1 and <= 256 ? workers : throw new ArgumentException("--workers must be between 1 and 256.");

    public UnifiedScanOptions ToUnifiedScanOptions() => new(Top, Files, Dirs, Strict, Workers);

    public static void PrintHelp()
    {
        Console.WriteLine($"""
            dirsizer - fast, read-only folder size scanner: automatically picks the best available scan method

            Usage: dirsizer <path> [--top=N] [--files] [--dirs] [--json] [--workers N] [--strict]

            <path>      Directory to scan: a drive (C:\), a share (\\server\share), or any folder.
            --top=N     Show the largest N results (default: 25)
            --files     Include largest files
            --dirs      Include largest directories (default)
            --json      Write machine-readable JSON to stdout
            --workers N Only meaningful when the generic filesystem strategy runs (default: automatic; 1-256)
            --strict    Exit with code 3 if part of the scan could not be completed
            --benchmark Print total time and the strategy used to stderr
            --self-test Run the built-in tests (uses a temporary folder)
            -h          Show this help

            Reads the whole drive's $MFT directly when NTFS and an administrator token are both available
            (the fastest method); otherwise reads NTFS through FSCTL_GET_NTFS_FILE_RECORD if that is
            available; otherwise walks the directory tree, which works on any file system without elevation.
            The strategy used is reported with --benchmark or --json, never required to understand the result.
            To force one specific method (for comparison, diagnosis, or an automated script that must not
            depend on which one ran), use dirsizer-fs.exe, dirsizer-mft.exe or dirsizer-fsctl.exe directly.

            A path that is not a whole drive always uses the filesystem strategy (the other two only read an
            entire volume).

            Exit codes: 0 result written; 1 error; 3 with --strict, part of the scan could not be completed.
            """);
    }
}
