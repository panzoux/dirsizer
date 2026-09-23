// dirsizer-index's options. EXPERIMENTAL tool; see docs\design_index.md.
sealed class IndexOptions
{
    public string Target { get; private set; } = "";
    public int Top { get; private set; } = 25;
    public bool Files { get; private set; }
    public bool Json { get; private set; }
    public bool Help { get; private set; }
    public bool SelfTest { get; private set; }
    public bool Benchmark { get; private set; }
    public bool NoSave { get; private set; }
    public bool Verify { get; private set; }
    public string IndexDirectory { get; private set; } = IndexFile.DefaultDirectory;

    public static IndexOptions Parse(string[] args)
    {
        var result = new IndexOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help") { result.Help = true; continue; }
            if (arg == "--self-test") { result.SelfTest = true; continue; }
            if (arg == "--benchmark") { result.Benchmark = true; continue; }
            if (arg == "--files") { result.Files = true; continue; }
            if (arg == "--json") { result.Json = true; continue; }
            if (arg == "--no-save") { result.NoSave = true; continue; }
            if (arg == "--verify") { result.Verify = true; continue; }
            if (arg.StartsWith("--index-dir=", StringComparison.Ordinal) && arg.Length > "--index-dir=".Length) { result.IndexDirectory = Path.GetFullPath(arg["--index-dir=".Length..]); continue; }
            if (arg.StartsWith("--index-dir", StringComparison.Ordinal)) throw new ArgumentException("Use --index-dir=DIRECTORY.");
            if (arg.StartsWith("--top=", StringComparison.Ordinal) && int.TryParse(arg[6..], out var top)) { result.Top = Math.Max(1, top); continue; }
            if (arg == "--top" && index + 1 < args.Length && int.TryParse(args[++index], out top)) { result.Top = Math.Max(1, top); continue; }
            if (arg.StartsWith("--top", StringComparison.Ordinal)) throw new ArgumentException("--top needs a whole number: --top=N or --top N.");
            if (arg.StartsWith('-')) throw new ArgumentException($"Unknown option: {arg}");
            if (result.Target.Length != 0) throw new ArgumentException("Only one path is supported.");
            // "C:" alone would mean the current directory on C:; it is taken as the drive root, as the other tools do.
            result.Target = arg.Length == 2 && arg[1] == ':' ? arg + "\\" : arg;
        }
        if (!result.Help && !result.SelfTest && result.Target.Length == 0) throw new ArgumentException("A drive root is required, for example C:\\.");
        if (result.Verify && result.NoSave) throw new ArgumentException("--verify loads the saved index back; it cannot be combined with --no-save.");
        return result;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            dirsizer-index - EXPERIMENTAL: folder sizes from a saved, per-volume NTFS index

            Usage: dirsizer-index C:\ [--top=N] [--files] [--json] [--verify] [--no-save] [--index-dir=DIR] [--benchmark]

            --top=N          Show the largest N results (default: 25)
            --files          Include largest files
            --json           Write machine-readable JSON to stdout
            --verify         Load the saved index back and compare it with the scan (exit code 2 if they differ)
            --no-save        Do not write the index
            --index-dir=DIR  Where index files are kept (default: %LOCALAPPDATA%\dirsizer\index)
            --benchmark      Print phase timings to stderr
            --self-test      Run the built-in tests
            -h               Show this help

            Reads the whole $MFT like dirsizer-mft and saves the records as <volume serial>.dsix.
            The index file lists every file and directory name on the volume. In the default location only
            the current user, SYSTEM and Administrators can read it.
            Requires Administrator: the executable asks for elevation when it starts.

            Exit codes: 0 ok; 1 error; 2 --verify found differences; 3 the MFT changed while it was read,
            so the index was not saved.
            """);
    }
}
