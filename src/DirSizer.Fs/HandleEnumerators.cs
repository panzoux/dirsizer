using System.Buffers.Binary;
using System.Runtime.InteropServices;

// The two enumerators that read a directory through a handle (B: GetFileInformationByHandleEx, C: NtQueryDirectoryFileEx). They
// share everything except the one call that fills the buffer: open the directory once, ask for as many entries as fit in the
// worker's buffer, follow the NextEntryOffset chain, close the handle. No file is ever opened.

static class DirectoryHandle
{
    const uint FileListDirectory = 0x1;
    const uint ShareAll = 0x7;                  // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    const uint OpenExisting = 3;
    const uint BackupSemantics = 0x02000000;    // FILE_FLAG_BACKUP_SEMANTICS, required to open a directory

    public const int ErrorInvalidData = 13;
    public const int ErrorInsufficientBuffer = 122;

    // Returns the handle, or InvalidHandle with the Win32 error.
    public static nint Open(string extendedPath, out int error)
    {
        var handle = CreateFileW(extendedPath, FileListDirectory, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        error = handle == Win32Find.InvalidHandle ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    public static void Close(nint handle) => CloseHandle(handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint hObject);
}

abstract class BufferedEnumerator(int bufferKiB, int nameOffset) : IDirectoryEnumerator
{
    // This worker's buffer, allocated once.
    protected readonly byte[] Buffer = new byte[bufferKiB * 1024];

    // The offset of the file name inside an entry: 64 for FileDirectoryInformation, 68 for FileFullDirectoryInformation and
    // 88 for FileIdExtdDirectoryInformation (the Win32 structures of B have the same layout).
    protected static int NameOffset(EntryClass entryClass) => entryClass switch
    {
        EntryClass.Dir => 64,
        EntryClass.Full => 68,
        _ => 88,
    };

    // One query for as many entries as fit in the buffer. `first` is true for the first query of a directory. Returns 0 when the
    // buffer holds entries (`bytes` of it are valid), ERROR_NO_MORE_FILES at the end of the directory, or another Win32 error.
    protected abstract int Query(nint handle, bool first, out int bytes);

    public ReadResult Read(string directoryPath, IEntrySink sink)
    {
        var handle = DirectoryHandle.Open(directoryPath, out var openError);
        // For find, ERROR_FILE_NOT_FOUND means "no entries"; here it would mean that the directory does not exist, so only
        // access denied is singled out and every other open error is a failure.
        if (handle == Win32Find.InvalidHandle)
            return new ReadResult(openError == Win32Find.ErrorAccessDenied ? ReadOutcome.Denied : ReadOutcome.Failed, openError);
        try
        {
            var first = true;
            while (true)
            {
                var error = Query(handle, first, out var bytes);
                first = false;
                if (error == Win32Find.ErrorNoMoreFiles) return new ReadResult(ReadOutcome.Complete, 0);
                if (error != 0) return new ReadResult(ReadOutcome.Failed, error);
                // A query that succeeds without an entry means that the buffer is too small, not that the directory is finished.
                if (bytes <= 0) return new ReadResult(ReadOutcome.Failed, DirectoryHandle.ErrorInsufficientBuffer);
                if (!Parse(bytes, sink)) return new ReadResult(ReadOutcome.Failed, DirectoryHandle.ErrorInvalidData);
            }
        }
        finally
        {
            DirectoryHandle.Close(handle);
        }
    }

    // Follows the chain of entries (NextEntryOffset 0 marks the last one). Layout of the part that all used classes share:
    // NextEntryOffset (0), EndOfFile (40), FileAttributes (56), FileNameLength in bytes (60); the name starts at nameOffset.
    // Returns false if an entry does not fit in the bytes that the query returned.
    bool Parse(int bytes, IEntrySink sink)
    {
        var data = new ReadOnlySpan<byte>(Buffer, 0, bytes);
        var offset = 0;
        while (true)
        {
            if (offset < 0 || offset > data.Length - nameOffset) return false;
            var entry = data[offset..];
            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            var size = BinaryPrimitives.ReadInt64LittleEndian(entry[40..]);
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            var nameBytes = BinaryPrimitives.ReadUInt32LittleEndian(entry[60..]);
            if (nameBytes > entry.Length - nameOffset || (nameBytes & 1) != 0) return false;
            var name = MemoryMarshal.Cast<byte, char>(entry.Slice(nameOffset, (int)nameBytes));
            if (!EntryNames.IsDot(name)) sink.OnEntry(attributes, size, name);
            if (next == 0) return true;
            if (next > (uint)data.Length) return false;
            offset += (int)next;
        }
    }
}

// B: GetFileInformationByHandleEx. The first query of a directory uses the Restart class, every later query the plain class.
sealed class HandleInfoEnumerator(EntryClass entryClass, int bufferKiB) : BufferedEnumerator(bufferKiB, NameOffset(entryClass))
{
    // FILE_INFO_BY_HANDLE_CLASS: FileFullDirectoryInfo 14, FileFullDirectoryRestartInfo 15,
    // FileIdExtdDirectoryInfo 19, FileIdExtdDirectoryRestartInfo 20.
    readonly int _restartClass = entryClass == EntryClass.IdExtd ? 20 : 15;
    readonly int _nextClass = entryClass == EntryClass.IdExtd ? 19 : 14;

    protected override int Query(nint handle, bool first, out int bytes)
    {
        // The call does not say how many bytes it wrote; the chain of entries ends with NextEntryOffset 0.
        bytes = Buffer.Length;
        return GetFileInformationByHandleEx(handle, first ? _restartClass : _nextClass, ref Buffer[0], (uint)Buffer.Length) ? 0 : Marshal.GetLastPInvokeError();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(nint hFile, int fileInformationClass, ref byte lpFileInformation, uint dwBufferSize);
}

// C: NtQueryDirectoryFileEx (ntdll.dll), a documented WDK Native System Service, Windows 10 version 1709 or later.
sealed class NtQueryEnumerator(EntryClass entryClass, int bufferKiB) : BufferedEnumerator(bufferKiB, NameOffset(entryClass))
{
    // FILE_INFORMATION_CLASS: FileDirectoryInformation 1, FileFullDirectoryInformation 2, FileIdExtdDirectoryInformation 60.
    // QueryFlags: SL_RESTART_SCAN (0x1) on the first query of a directory only. SL_RETURN_SINGLE_ENTRY (0x2) is never used: it
    // makes the file system return one entry per query.
    const uint RestartScan = 0x1;
    const int StatusNoMoreFiles = unchecked((int)0x80000006);

    readonly int _class = entryClass switch { EntryClass.Dir => 1, EntryClass.Full => 2, _ => 60 };
    IoStatusBlock _status;

    protected override int Query(nint handle, bool first, out int bytes)
    {
        bytes = 0;
        var status = NtQueryDirectoryFileEx(handle, 0, 0, 0, ref _status, ref Buffer[0], (uint)Buffer.Length, _class, first ? RestartScan : 0, 0);
        if (status == StatusNoMoreFiles) return Win32Find.ErrorNoMoreFiles;
        if (status < 0)
        {
            var error = (int)RtlNtStatusToDosError(status);
            return error == 0 ? DirectoryHandle.ErrorInvalidData : error;
        }
        bytes = (int)_status.Information;
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IoStatusBlock
    {
        public nint Status;
        public nint Information;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryDirectoryFileEx(nint fileHandle, nint evt, nint apcRoutine, nint apcContext, ref IoStatusBlock ioStatusBlock, ref byte fileInformation, uint length, int fileInformationClass, uint queryFlags, nint fileName);

    [DllImport("ntdll.dll")]
    static extern uint RtlNtStatusToDosError(int status);
}

sealed class BufferedFactory(EnumeratorSpec spec) : IEnumeratorFactory
{
    public string Name => spec.Canonical;

    public bool LargeFetch => false;

    public IDirectoryEnumerator Create() => spec.Kind == EnumeratorKind.Handle
        ? new HandleInfoEnumerator(spec.Class, spec.BufferKiB)
        : new NtQueryEnumerator(spec.Class, spec.BufferKiB);
}
