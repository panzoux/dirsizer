enum ReadOutcome { Complete, Denied, Failed }

readonly record struct ReadResult(ReadOutcome Outcome, int Error);

// Enumerates one directory with FindFirstFileExW / FindNextFileW and hands every entry to a sink. One reader is shared by all
// workers; it holds the LARGE_FETCH switch, which is one flag for the whole run.
sealed class DirectoryReader(FindFirstFn? findFirst = null)
{
    readonly FindFirstFn _findFirst = findFirst ?? Win32Find.FindFirst;
    volatile bool _largeFetch = true;

    public bool LargeFetch => _largeFetch;

    // directoryPath is the extended-length path of the directory. The buffer is the caller's, so no worker allocates per call.
    public ReadResult Read(string directoryPath, ref Win32FindData data, IEntrySink sink)
    {
        var pattern = directoryPath.EndsWith('\\') ? directoryPath + "*" : directoryPath + "\\*";
        var largeFetch = _largeFetch;
        var first = _findFirst(pattern, ref data, largeFetch);
        if (first.Handle == Win32Find.InvalidHandle && largeFetch && first.Error == Win32Find.ErrorInvalidParameter)
        {
            // Some filesystems reject the flag. Retry once without it; only if that works is the flag switched off for the run,
            // because the same error can also come from the path itself.
            first = _findFirst(pattern, ref data, false);
            if (first.Handle != Win32Find.InvalidHandle) _largeFetch = false;
        }
        if (first.Handle == Win32Find.InvalidHandle)
            return new ReadResult(first.Error == Win32Find.ErrorAccessDenied ? ReadOutcome.Denied : ReadOutcome.Failed, first.Error);

        try
        {
            while (true)
            {
                if (!Win32Find.IsDotEntry(in data)) sink.OnEntry(in data);
                if (Win32Find.TryFindNext(first.Handle, ref data, out var error)) continue;
                return error == Win32Find.ErrorNoMoreFiles ? new ReadResult(ReadOutcome.Complete, 0) : new ReadResult(ReadOutcome.Failed, error);
            }
        }
        finally
        {
            Win32Find.Close(first.Handle);
        }
    }
}
