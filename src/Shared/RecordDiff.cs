using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Compares two copies of the same MFT record (the raw bulk read after USA fixup, and what FSCTL_GET_NTFS_FILE_RECORD
// returns). Shared by the DirSizer.Compare tool and by dirsizer-inspect --compare.
static class RecordDiff
{
    // Classifies every differing byte. The saved-sector-tail entries of the update sequence array (the array bytes after
    // the update sequence number itself) are not record content once fixup has been applied, and the reference parser
    // never reads them. Observed on T:: for records NTFS created or rewrote since the volume was mounted, FSCTL returns
    // those entries as zero while the on-disk copy read by the bulk reader holds the protected values; after a fresh
    // mount every entry matches. A difference confined to the entries is counted as information, and only if the FSCTL
    // side is zero. Any other difference inside the used region, or in the slack after it, is a mismatch.
    static void CompareBytes(ulong number, byte[] bulk, byte[] other, Tally tally)
    {
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(bulk.AsSpan(4));
        var usaSize = BinaryPrimitives.ReadUInt16LittleEndian(bulk.AsSpan(6));
        var entriesStart = usaOffset + 2;
        var entriesEnd = Math.Min(usaOffset + usaSize * 2, bulk.Length);
        var used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bulk.AsSpan(24)), (uint)bulk.Length);
        var usaEntriesDiffer = false;
        var fsctlEntryNonZero = -1;
        var contentDifference = -1;
        var slackDifference = -1;
        for (var index = 0; index < bulk.Length; index++)
        {
            if (bulk[index] == other[index]) continue;
            if (index >= entriesStart && index < entriesEnd)
            {
                usaEntriesDiffer = true;
                if (other[index] != 0 && fsctlEntryNonZero < 0) fsctlEntryNonZero = index;
            }
            else if (index < used) { if (contentDifference < 0) contentDifference = index; }
            else if (slackDifference < 0) slackDifference = index;
        }
        if (usaEntriesDiffer) tally.UsaEntriesDiffer++;
        if (fsctlEntryNonZero >= 0) tally.Mismatch("usa_entries_fsctl_nonzero", number, $"offset={fsctlEntryNonZero} bulk=0x{bulk[fsctlEntryNonZero]:X2} fsctl=0x{other[fsctlEntryNonZero]:X2}");
        if (contentDifference >= 0) tally.Mismatch("raw_bytes_within_used", number, $"first_difference_offset={contentDifference} used={used} bulk=0x{bulk[contentDifference]:X2} fsctl=0x{other[contentDifference]:X2}");
        if (slackDifference >= 0) tally.Mismatch("raw_bytes_slack_only", number, $"first_difference_offset={slackDifference} used={used} bulk=0x{bulk[slackDifference]:X2} fsctl=0x{other[slackDifference]:X2}");
    }

    static void CountNonZeroSavedEntries(byte[] bulk, byte[] other, Tally tally)
    {
        if (bulk.Length < 8 || other.Length != bulk.Length) return;
        var start = BinaryPrimitives.ReadUInt16LittleEndian(bulk.AsSpan(4)) + 2;
        var end = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(bulk.AsSpan(4)) + BinaryPrimitives.ReadUInt16LittleEndian(bulk.AsSpan(6)) * 2, other.Length);
        for (var index = start; index < end; index++)
        {
            if (other[index] == 0) continue;
            tally.FsctlUsaEntriesNonZero++;
            return;
        }
    }

    internal static void CompareRecord(ulong number, byte[] bulk, byte[] other, Tally tally)
    {
        tally.Compared++;
        CountNonZeroSavedEntries(bulk, other, tally);
        if (bulk.Length != other.Length) tally.Mismatch("record_length", number, $"bulk={bulk.Length} fsctl={other.Length}");
        if (bulk.Length == other.Length && bulk.AsSpan().CommonPrefixLength(other) != bulk.Length) CompareBytes(number, bulk, other, tally);

        var left = RecordParser.TryParse(number, bulk, out var leftReject, out _);
        var right = RecordParser.TryParse(number, other, out var rightReject, out _);
        if (left is null || right is null || leftReject != rightReject)
        {
            if (!(left is null && right is null && leftReject == rightReject)) tally.Mismatch("parse_result", number, $"bulk={leftReject} fsctl={rightReject}");
            return;
        }
        if (left.BaseReference.RecordNumber != 0) tally.Extension++;
        if (left.IsDirectory) tally.Directories++;
        if (left.Names.Count > 1) tally.MultiName++;
        if (left.LogicalSize > 0) tally.WithSize++;
        tally.Names += left.Names.Count;
        if (left.Reference != right.Reference) tally.Mismatch("identity", number, $"bulk=0x{left.Reference.FullReference:X} fsctl=0x{right.Reference.FullReference:X}");
        if (left.HeaderSequenceNumber != right.HeaderSequenceNumber) tally.Mismatch("sequence", number, $"bulk={left.HeaderSequenceNumber} fsctl={right.HeaderSequenceNumber}");
        if (left.BaseReference != right.BaseReference) tally.Mismatch("base_reference", number, $"bulk=0x{left.BaseReference.FullReference:X} fsctl=0x{right.BaseReference.FullReference:X}");
        if (left.IsDirectory != right.IsDirectory) tally.Mismatch("directory_flag", number, $"bulk={left.IsDirectory} fsctl={right.IsDirectory}");
        if (left.LogicalSize != right.LogicalSize) tally.Mismatch("logical_size", number, $"bulk={left.LogicalSize} fsctl={right.LogicalSize}");
        if (left.Names.Count != right.Names.Count) tally.Mismatch("filename_count", number, $"bulk={left.Names.Count} fsctl={right.Names.Count}");
        else
        {
            for (var index = 0; index < left.Names.Count; index++)
            {
                if (left.Names[index] != right.Names[index]) tally.Mismatch("filename", number, $"index={index} bulk='{left.Names[index].Name}' parent=0x{left.Names[index].Parent.FullReference:X} ns={left.Names[index].Namespace} fsctl='{right.Names[index].Name}' parent=0x{right.Names[index].Parent.FullReference:X} ns={right.Names[index].Namespace}");
            }
        }
    }
}

sealed class Tally
{
    readonly Dictionary<string, int> counts = new();
    readonly List<string> samples = new();
    public int Compared;
    public int Extension;
    public int Directories;
    public int MultiName;
    public int WithSize;
    public int Names;
    public int UsaEntriesDiffer;
    public int FsctlUsaEntriesNonZero;
    public IReadOnlyDictionary<string, int> Counts => counts;
    public IReadOnlyList<string> Samples => samples;
    public int TotalMismatches
    {
        get
        {
            var total = 0;
            foreach (var pair in counts) total += pair.Value;
            return total;
        }
    }

    public void Mismatch(string category, ulong record, string detail)
    {
        counts[category] = counts.GetValueOrDefault(category) + 1;
        if (counts[category] <= 5) samples.Add($"{category} record={record} {detail}");
    }
}

static class FsctlRecords
{
    const uint FsctlGetNtfsFileRecord = 0x00090068;

    // Returns the record number FSCTL_GET_NTFS_FILE_RECORD reports for the request (the requested slot if it is in use,
    // otherwise the nearest lower in-use record), or null if there is none. The record bytes come back already USA-fixed.
    public static ulong? Query(SafeFileHandle volume, ulong requested, byte[] input, byte[] output, out byte[] record)
    {
        record = [];
        BinaryPrimitives.WriteUInt64LittleEndian(input, requested);
        if (!DeviceIoControl(volume, FsctlGetNtfsFileRecord, input, input.Length, output, output.Length, out var bytesReturned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 18) return null; // same "no record" errors the reference reader accepts
            throw new Win32Exception(error, $"FSCTL_GET_NTFS_FILE_RECORD failed for slot {requested} (Win32 error {error})");
        }
        const int recordOffset = 12;
        var returned = BinaryPrimitives.ReadUInt64LittleEndian(output) & 0x0000FFFFFFFFFFFFUL;
        var length = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8));
        if (length <= 0 || length > bytesReturned - recordOffset) return null;
        if (returned > requested) throw new IOException($"FSCTL returned record {returned} above requested slot {requested}.");
        record = output.AsSpan(recordOffset, length).ToArray();
        return returned;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}

