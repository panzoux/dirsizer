// NotRead is the default value on purpose: a result that was never filled in must not look like a successful read.
enum ReadOutcome { NotRead, Complete, Denied, Failed }

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
        // A directory with no entries at all. The root of a FAT or exFAT volume has no "." or ".." entries, so listing an empty one
        // fails with ERROR_FILE_NOT_FOUND: that is an empty directory, not a failure. (A path that does not exist gives ERROR_PATH_NOT_FOUND.)
        if (first.Handle == Win32Find.InvalidHandle && first.Error == Win32Find.ErrorFileNotFound)
            return new ReadResult(ReadOutcome.Complete, 0);
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
