using System.Runtime.InteropServices;

// Shared by all workers of a run: the LARGE_FETCH switch. It only ever goes from on to off, so a plain volatile flag is enough.
sealed class LargeFetchState(bool on)
{
    volatile bool _on = on;

    public bool On => _on;

    public void TurnOff() => _on = false;
}

// A: FindFirstFileExW / FindNextFileW, the enumerator of the first version and the baseline of every comparison.
sealed class FindFirstEnumerator(LargeFetchState largeFetch, FindFirstFn findFirst) : IDirectoryEnumerator
{
    Win32FindData _data;    // this worker's buffer

    public ReadResult Read(string directoryPath, IEntrySink sink)
    {
        var pattern = directoryPath.EndsWith('\\') ? directoryPath + "*" : directoryPath + "\\*";
        var useLargeFetch = largeFetch.On;
        var first = findFirst(pattern, ref _data, useLargeFetch);
        if (first.Handle == Win32Find.InvalidHandle && useLargeFetch && first.Error == Win32Find.ErrorInvalidParameter)
        {
            // Some filesystems reject the flag. Retry once without it; only if that works is the flag switched off for the run,
            // because the same error can also come from the path itself.
            first = findFirst(pattern, ref _data, false);
            if (first.Handle != Win32Find.InvalidHandle) largeFetch.TurnOff();
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
                ReadOnlySpan<char> all = MemoryMarshal.Cast<ushort, char>((ReadOnlySpan<ushort>)_data.FileName);
                var length = all.IndexOf('\0');
                var name = length < 0 ? all : all[..length];
                if (!EntryNames.IsDot(name)) sink.OnEntry(_data.FileAttributes, Win32Find.FileSize(in _data), name);
                if (Win32Find.TryFindNext(first.Handle, ref _data, out var error)) continue;
                return error == Win32Find.ErrorNoMoreFiles ? new ReadResult(ReadOutcome.Complete, 0) : new ReadResult(ReadOutcome.Failed, error);
            }
        }
        finally
        {
            Win32Find.Close(first.Handle);
        }
    }
}

sealed class FindFirstFactory(bool largeFetch = true, FindFirstFn? findFirst = null) : IEnumeratorFactory
{
    readonly LargeFetchState _state = new(largeFetch);
    readonly FindFirstFn _findFirst = findFirst ?? Win32Find.FindFirst;

    public string Name { get; } = largeFetch ? "find" : "find:nolarge";

    public bool LargeFetch => _state.On;

    public IDirectoryEnumerator Create() => new FindFirstEnumerator(_state, _findFirst);
}
