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
    public bool Rebuild { get; private set; }
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
            if (arg == "--rebuild") { result.Rebuild = true; continue; }
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
        return result;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            dirsizer-index - EXPERIMENTAL: folder sizes from a per-volume NTFS index kept up to date by the USN journal

            Usage: dirsizer-index C:\ [--top=N] [--files] [--json] [--rebuild] [--verify] [--no-save] [--index-dir=DIR] [--benchmark]

            --top=N          Show the largest N results (default: 25)
            --files          Include largest files
            --json           Write machine-readable JSON to stdout
            --rebuild        Ignore the saved index and scan the whole $MFT again
            --verify         Also scan the whole $MFT and compare it with the index, record by record
                             (exit code 2 if they differ; use on a volume nothing else is writing to)
            --no-save        Do not write the index back
            --index-dir=DIR  Where index files are kept (default: %LOCALAPPDATA%\dirsizer\index)
            --benchmark      Print phase timings to stderr
            --self-test      Run the built-in tests
            -h               Show this help

            The first run reads the whole $MFT (like dirsizer-mft) and saves an index of the volume. Later
            runs load it, read the USN change journal from where it left off, and read again only the MFT
            records the journal names. A full scan runs instead, with the reason on stderr, when there is no
            usable index: none yet, another volume, the journal was recreated, wrapped or disabled, or the
            file is damaged. Changes made while the volume was used by a system that does not write the USN
            journal (another operating system) are not seen: use --rebuild after that.

            The index file lists every file and directory name on the volume. In the default location only
            the current user, SYSTEM and Administrators can read it.
            Requires Administrator: the executable asks for elevation when it starts.

            Exit codes: 0 ok; 1 error; 2 --verify found differences; 3 the MFT changed while it was read,
            so the index was not saved.
            """);
    }
}
