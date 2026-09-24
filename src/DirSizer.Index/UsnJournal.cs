using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// The volume's USN change journal. The index uses it only to learn *which* MFT records changed; their new state is
// always read from the MFT itself (docs\design_index.md, roadmap I3).
readonly record struct JournalState(ulong JournalId, long FirstUsn, long NextUsn);

// One USN record reduced to what the index needs: whose record changed. Usn and Reason are kept for diagnostics only.
readonly record struct UsnChange(ulong RecordNumber, long Usn, uint Reason);

static class UsnJournal
{
    const uint FsctlQueryUsnJournal = 0x000900F4;
    const uint FsctlReadUsnJournal = 0x000900BB;
    const int UsnRecordV2HeaderSize = 60;
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

    public readonly record struct ReadResponse(int Error, byte[] Output, int Returned);
    public delegate ReadResponse ReadFetch(long startUsn);

    // One FSCTL_READ_USN_JOURNAL call with READ_USN_JOURNAL_DATA_V0 (so the records are USN_RECORD_V2): every reason,
    // not only on close, and never waiting for new records.
    public static ReadResponse Fetch(SafeFileHandle volume, ulong journalId, long startUsn, byte[] output)
    {
        var input = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(input, startUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);   // ReasonMask: every reason
        // ReturnOnlyOnClose (12), Timeout (16) and BytesToWaitFor (24) stay 0.
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), journalId);
        var succeeded = DeviceIoControl(volume, FsctlReadUsnJournal, input, input.Length, output, output.Length, out var returned, IntPtr.Zero);
        return new ReadResponse(succeeded ? 0 : Marshal.GetLastWin32Error(), output, returned);
    }

    // Reads from startUsn to the end of the journal. Returns the USN to continue from next time, or null if the journal
    // no longer holds startUsn (it wrapped). Any other failure throws: the caller must not treat it as "nothing changed".
    public static long? ReadChanges(ReadFetch fetch, long startUsn, List<UsnChange> changes)
    {
        var usn = startUsn;
        while (true)
        {
            var response = fetch(usn);
            if (response.Error == ErrorJournalEntryDeleted) return null;
            if (response.Error == ErrorHandleEof) return usn;
            if (response.Error != 0) throw new Win32Exception(response.Error, $"Cannot read the USN journal (Win32 error {response.Error})");
            if (response.Returned < 8) throw new IOException("Invalid USN journal read response");
            var next = BinaryPrimitives.ReadInt64LittleEndian(response.Output);
            if (response.Returned == 8) return next;
            ParseRecords(response.Output.AsSpan(8, response.Returned - 8), changes);
            if (next <= usn) throw new IOException($"The USN journal read made no progress at USN {usn}");
            usn = next;
        }
    }

    // Only V2 records are expected (the V0 read request asks for them); anything else is an error, not something to skip.
    public static void ParseRecords(ReadOnlySpan<byte> data, List<UsnChange> changes)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < UsnRecordV2HeaderSize) throw new IOException("Truncated USN record");
            var length = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
            if (length < UsnRecordV2HeaderSize || length > data.Length - offset) throw new IOException($"Invalid USN record length {length}");
            var major = BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 4)..]);
            if (major != 2) throw new IOException($"Unsupported USN record version {major}");
            var reference = new FileRef(BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 8)..]));
            changes.Add(new UsnChange(reference.RecordNumber, BinaryPrimitives.ReadInt64LittleEndian(data[(offset + 24)..]), BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 40)..])));
            offset += length;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
