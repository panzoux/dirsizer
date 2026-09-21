using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// WIN32_FIND_DATAW, field for field (592 bytes). Every field is an integer, so the struct is blittable and is passed to the API by
// reference with no marshalling code and no unsafe code. The file name is an inline array of ushort rather than char: a char
// field would make the marshaller depend on the struct's CharSet. The FILETIMEs are pairs of uints on purpose: a long would be
// aligned to 8 bytes and shift every field after it.
[InlineArray(260)]
struct FileNameBuffer
{
    ushort _element0;
}

[InlineArray(14)]
struct AlternateNameBuffer
{
    ushort _element0;
}

struct Win32FindData
{
    public uint FileAttributes;
    public uint CreationTimeLow, CreationTimeHigh;
    public uint LastAccessTimeLow, LastAccessTimeHigh;
    public uint LastWriteTimeLow, LastWriteTimeHigh;
    public uint FileSizeHigh, FileSizeLow;
    public uint Reserved0, Reserved1;
    public FileNameBuffer FileName;
    public AlternateNameBuffer AlternateFileName;
}

readonly record struct FindFirstResult(nint Handle, int Error);

// The one call that the self-test replaces to inject failures (see DirectoryReader). Not an enumerator abstraction.
delegate FindFirstResult FindFirstFn(string pattern, ref Win32FindData data, bool largeFetch);

interface IEntrySink
{
    // Called for every entry except "." and "..". The reference is only valid during the call.
    void OnEntry(in Win32FindData entry);
}

static class Win32Find
{
    public const uint DirectoryAttribute = 0x10;
    public const uint ReparsePointAttribute = 0x400;
    public const int ErrorFileNotFound = 2;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNoMoreFiles = 18;
    public const int ErrorInvalidParameter = 87;
    public const nint InvalidHandle = -1;

    const int FindExInfoBasic = 1;          // no 8.3 short-name lookup
    const int FindExSearchNameMatch = 0;
    const int FindFirstExLargeFetch = 2;    // larger directory query buffer

    public static FindFirstResult FindFirst(string pattern, ref Win32FindData data, bool largeFetch)
    {
        var handle = FindFirstFileExW(pattern, FindExInfoBasic, ref data, FindExSearchNameMatch, 0, largeFetch ? FindFirstExLargeFetch : 0);
        return handle == InvalidHandle ? new FindFirstResult(handle, Marshal.GetLastPInvokeError()) : new FindFirstResult(handle, 0);
    }

    public static bool TryFindNext(nint handle, ref Win32FindData data, out int error)
    {
        if (FindNextFileW(handle, ref data))
        {
            error = 0;
            return true;
        }
        error = Marshal.GetLastPInvokeError();
        return false;
    }

    public static void Close(nint handle) => FindClose(handle);

    public static bool IsDotEntry(in Win32FindData entry)
    {
        ReadOnlySpan<char> name = MemoryMarshal.Cast<ushort, char>((ReadOnlySpan<ushort>)entry.FileName);
        return name[0] == '.' && (name[1] == '\0' || name[1] == '.' && name[2] == '\0');
    }

    public static string NameString(in Win32FindData entry)
    {
        ReadOnlySpan<char> name = MemoryMarshal.Cast<ushort, char>((ReadOnlySpan<ushort>)entry.FileName);
        var length = name.IndexOf('\0');
        return new string(length < 0 ? name : name[..length]);
    }

    public static long FileSize(in Win32FindData entry) => ((long)entry.FileSizeHigh << 32) | entry.FileSizeLow;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint FindFirstFileExW(string lpFileName, int fInfoLevelId, ref Win32FindData lpFindFileData, int fSearchOp, nint lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindNextFileW(nint hFindFile, ref Win32FindData lpFindFileData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FindClose(nint hFindFile);
}
