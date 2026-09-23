using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Turns a path into its MFT record by asking Windows (GetFileInformationByHandle): exactly the object Windows opens for
// that path, following junctions and mount points as Windows does. The result must be on the indexed volume and in the
// index with the same sequence number (roadmap I4).
static class PathResolver
{
    const uint FileReadAttributes = 0x80;
    const uint FileShareAll = 7;   // read, write, delete
    const uint OpenExisting = 3;
    const uint FileFlagBackupSemantics = 0x02000000;   // needed to open a directory

    public static FileRecord Find(VolumeIndex index, string path)
    {
        var (serial, reference) = Identify(path);
        if (serial != (uint)index.Identity.SerialNumber)
            throw new ArgumentException($"{path} is not on the indexed volume (it may lead through a junction or mount point to another volume).");
        if (!index.Records.TryGetValue(reference.RecordNumber, out var record) || record.Reference != reference)
            throw new ArgumentException($"{path} is not in the index (MFT record {reference.RecordNumber}:{reference.SequenceNumber}); it may have been created after the journal was read. Run again.");
        if (!record.IsDirectory) throw new ArgumentException($"{path} is a file, not a directory.");
        return record;
    }

    // The volume serial number (low 32 bits of the NTFS serial) and the full file reference, as Windows reports them.
    internal static (uint Serial, FileRef Reference) Identify(string path)
    {
        using var handle = CreateFile(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Cannot open {path} (Win32 error {error})");
        }
        var info = new byte[52];   // BY_HANDLE_FILE_INFORMATION
        if (!GetFileInformationByHandle(handle, info))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Cannot read the file id of {path} (Win32 error {error})");
        }
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(28));
        var fileIndex = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(44)) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(48));
        return (serial, new FileRef(fileIndex));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, byte[] information);
}
