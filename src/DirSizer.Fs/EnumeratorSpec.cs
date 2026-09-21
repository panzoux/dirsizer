enum EnumeratorKind { Find, Handle, Nt }

// The information class of a handle-based enumerator: what each directory entry contains. Only what the walk needs is read from it.
enum EntryClass { None, Dir, Full, IdExtd }

// --enumerator=<name>[:<class>[:<KiB>]]: which API reads the directories, and how. `find` is the default and the baseline.
sealed record EnumeratorSpec(EnumeratorKind Kind, EntryClass Class, bool LargeFetch, int BufferKiB)
{
    public const int DefaultBufferKiB = 64;
    public const int MinBufferKiB = 4;
    public const int MaxBufferKiB = 1024;

    public static EnumeratorSpec Default { get; } = new(EnumeratorKind.Find, EntryClass.None, true, 0);

    // The canonical form, which is what the benchmark line and the JSON report: find, find:nolarge, handle:idextd:64, nt:dir:4.
    public string Canonical => Kind switch
    {
        EnumeratorKind.Find => LargeFetch ? "find" : "find:nolarge",
        EnumeratorKind.Handle => $"handle:{ClassName(Class)}:{BufferKiB}",
        _ => $"nt:{ClassName(Class)}:{BufferKiB}",
    };

    // Throws ArgumentException with a message that says what is accepted.
    public static EnumeratorSpec Parse(string text)
    {
        var parts = text.Trim().ToLowerInvariant().Split(':');
        switch (parts[0])
        {
            case "find":
                if (parts.Length == 1) return Default;
                if (parts.Length == 2 && parts[1] == "nolarge") return Default with { LargeFetch = false };
                throw new ArgumentException("--enumerator find takes no class or buffer size; use find or find:nolarge.");
            case "handle":
                return ParseBuffered(EnumeratorKind.Handle, parts, [EntryClass.Full, EntryClass.IdExtd], "handle: classes are full and idextd");
            case "nt":
                return ParseBuffered(EnumeratorKind.Nt, parts, [EntryClass.Dir, EntryClass.Full, EntryClass.IdExtd], "nt: classes are dir, full and idextd");
            default:
                throw new ArgumentException($"Unknown enumerator '{text}'. Use find, find:nolarge, handle[:class[:KiB]] or nt[:class[:KiB]].");
        }
    }

    static EnumeratorSpec ParseBuffered(EnumeratorKind kind, string[] parts, EntryClass[] accepted, string message)
    {
        if (parts.Length > 3) throw new ArgumentException($"--enumerator {kind.ToString().ToLowerInvariant()} takes at most a class and a buffer size in KiB.");

        // The default is the lightest class that also works on exFAT (idextd does not: it fails with ERROR_INVALID_PARAMETER there).
        var entryClass = kind == EnumeratorKind.Handle ? EntryClass.Full : EntryClass.Dir;
        if (parts.Length >= 2)
        {
            entryClass = parts[1] switch { "dir" => EntryClass.Dir, "full" => EntryClass.Full, "idextd" => EntryClass.IdExtd, _ => EntryClass.None };
            if (Array.IndexOf(accepted, entryClass) < 0) throw new ArgumentException($"--enumerator {message}.");
        }
        var bufferKiB = DefaultBufferKiB;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[2], out bufferKiB) || bufferKiB < MinBufferKiB || bufferKiB > MaxBufferKiB)
                throw new ArgumentException($"--enumerator buffer size must be a whole number of KiB from {MinBufferKiB} to {MaxBufferKiB}.");
        }
        return new EnumeratorSpec(kind, entryClass, false, bufferKiB);
    }

    static string ClassName(EntryClass entryClass) => entryClass switch
    {
        EntryClass.Dir => "dir",
        EntryClass.Full => "full",
        _ => "idextd",
    };

    // The find-first delegate is the self-test's injection point and only means something for `find`.
    public IEnumeratorFactory CreateFactory(FindFirstFn? findFirst = null) =>
        Kind == EnumeratorKind.Find ? new FindFirstFactory(LargeFetch, findFirst) : new BufferedFactory(this);
}
