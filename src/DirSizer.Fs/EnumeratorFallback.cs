using System.Threading;

// Shared by all workers of a run: once B is found unsupported, every Read that STARTS after that point uses
// A instead. A B.Read() already in progress when the flag flips is not interrupted — it is allowed to finish
// (the check is made once, at the start of Read, not partway through). One-way, like LargeFetchState in
// FindFirstEnumerator.cs: never re-probes B afterwards.
sealed class FallbackState
{
    int _reasonError;   // 0 = not triggered; Interlocked, first writer wins

    public bool Triggered => Volatile.Read(ref _reasonError) != 0;

    public int Reason => Volatile.Read(ref _reasonError);

    // Returns true if this call is the one that triggered the fallback (only the first caller gets true; the
    // return value is not needed by FallbackEnumerator, every caller falls through to the secondary either way).
    public bool TryTrigger(int error) => Interlocked.CompareExchange(ref _reasonError, error, 0) == 0;
}

// auto: B (handle:full:64) with a fallback to A (find) when B's information class is not supported on this
// file system. One instance per worker, wrapping one primary and one secondary enumerator instance.
sealed class FallbackEnumerator(IDirectoryEnumerator primary, IDirectoryEnumerator secondary, FallbackState state) : IDirectoryEnumerator
{
    public ReadResult Read(string directoryPath, IEntrySink sink)
    {
        if (state.Triggered) return secondary.Read(directoryPath, sink);
        var result = primary.Read(directoryPath, sink);
        // Only the first query of a directory, only this one error: GetFileInformationByHandleEx (and
        // NtQueryDirectoryFileEx, mapped the same way through RtlNtStatusToDosError) report an unsupported
        // information class this way. A later query failing with the same code is a real, unrelated error.
        if (result.Outcome == ReadOutcome.Failed && result.FirstQuery && result.Error == Win32Find.ErrorInvalidParameter)
        {
            state.TryTrigger(result.Error);
            return secondary.Read(directoryPath, sink);
        }
        return result;
    }
}
