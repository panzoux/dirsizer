using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// The volume's USN change journal. The index uses it only to learn *which* MFT records changed; their new state is
// always read from the MFT itself (docs\design_index.md, roadmap I3).
readonly record struct JournalState(ulong JournalId, long FirstUsn, long NextUsn);

static class UsnJournal
{
    const uint FsctlQueryUsnJournal = 0x000900F4;
    public const int ErrorHandleEof = 38;
    public const int ErrorJournalDeleteInProgress = 1178;
    public const int ErrorJournalNotActive = 1179;
    public const int ErrorJournalEntryDeleted = 1181;

    // Null if the volume has no active journal (or it is being deleted). The first three fields are the same in every
    // version of USN_JOURNAL_DATA: journal id, first USN still in the journal, next USN to be written.
    public static JournalState? Query(SafeFileHandle volume)
    {
        var output = new byte[80];
        if (!DeviceIoControl(volume, FsctlQueryUsnJournal, null, 0, output, output.Length, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorJournalNotActive or ErrorJournalDeleteInProgress) return null;
            throw new Win32Exception(error, $"Cannot query the USN journal (Win32 error {error})");
        }
        if (returned < 24) throw new IOException("Invalid USN journal query response");
        return new JournalState(BinaryPrimitives.ReadUInt64LittleEndian(output), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16)));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
