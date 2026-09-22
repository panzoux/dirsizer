// The contract between the walk and a directory-enumeration API. The walk asks an enumerator to read one directory and receives
// every entry through a sink. Nothing API-specific crosses this boundary: an API may return more (reparse tag, file id, allocation
// size, times, short name), but only what the walk needs is passed on.

// NotRead is the default value on purpose: a result that was never filled in must not look like a successful read.
enum ReadOutcome { NotRead, Complete, Denied, Failed }

// FirstQuery is true only when a Failed result came from the very first Query() call BufferedEnumerator.Read()
// issues for a directory (before any entry from that call could have reached the sink). It says nothing about
// *why* the query failed — that is for the caller (see FallbackEnumerator) to decide. Meaningless (always
// false) for an enumerator with no such internal query structure, i.e. FindFirstEnumerator.
readonly record struct ReadResult(ReadOutcome Outcome, int Error, bool FirstQuery = false);

interface IEntrySink
{
    // Called for every entry except "." and "..". `attributes` are the Win32 file attributes (the walk looks at
    // FILE_ATTRIBUTE_DIRECTORY and FILE_ATTRIBUTE_REPARSE_POINT), `size` is the logical size (end of file). The name is only
    // valid during the call.
    void OnEntry(uint attributes, long size, ReadOnlySpan<char> name);
}

interface IDirectoryEnumerator
{
    // directoryPath is the extended-length path of the directory. A file-system problem is not thrown: the result says what
    // happened. An enumerator instance belongs to one worker; it owns that worker's buffers and is not thread-safe.
    ReadResult Read(string directoryPath, IEntrySink sink);
}

static class EntryNames
{
    public static bool IsDot(ReadOnlySpan<char> name) => name.Length is 1 or 2 && name[0] == '.' && (name.Length == 1 || name[1] == '.');
}

interface IEnumeratorFactory
{
    // The canonical name, for example "find" or "handle:idextd:64"; it is what the benchmark line and the JSON report.
    string Name { get; }

    // find only: whether FIND_FIRST_EX_LARGE_FETCH is still in use (it is switched off for the rest of a run when the file system
    // rejects it). False for every other enumerator.
    bool LargeFetch { get; }

    // Non-null once a fallback has actually triggered during this run: the enumerator that was used instead of
    // Name, and why. Null for every factory that has no fallback (every one except AutoFactory).
    string? FallbackEnumerator { get; }
    string? FallbackReason { get; }

    // One instance per worker.
    IDirectoryEnumerator Create();
}
