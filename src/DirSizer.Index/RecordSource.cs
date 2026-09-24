using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

// Reads one MFT record as it is now. Read returns the record's bytes (update-sequence fixup already applied), or null if
// that record number is not in use. Behind an interface so IndexUpdater can be tested without a volume.
interface IRecordSource
{
    byte[]? Read(ulong recordNumber);
    byte[]? ReadClusters(long lcn, long clusters);
    long BytesPerCluster { get; }
}

// The real source: FSCTL_GET_NTFS_FILE_RECORD (the reference reader's call) for records, raw volume reads for clusters.
sealed class FsctlRecordSource(SafeFileHandle volume, int recordSize, long bytesPerCluster) : IRecordSource
{
    readonly byte[] input = new byte[8];
    readonly byte[] output = new byte[Math.Max(4096, recordSize + 16)];

    public long BytesPerCluster => bytesPerCluster;

    // The FSCTL returns the nearest in-use record at or below the requested number, so any other number means "not in use".
    public byte[]? Read(ulong recordNumber)
    {
        var record = Native.ReadRecord(volume, recordNumber, input, output);
        if (record is null || record.Value.ReturnedRecordNumber != recordNumber) return null;
        return record.Value.Buffer.AsSpan(record.Value.Offset, record.Value.Length).ToArray();
    }

    public byte[]? ReadClusters(long lcn, long clusters)
    {
        try
        {
            var buffer = new byte[checked(clusters * bytesPerCluster)];
            BulkNative.ReadAt(volume, buffer, buffer.Length, checked(lcn * bytesPerCluster));
            return buffer;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or OverflowException) { return null; }
    }
}

// Finds the extension records of a base record from its $ATTRIBUTE_LIST (type 0x20). A record without an attribute list
// has none. A non-resident list is read through its run list (RunList.cs, shared with dirsizer-inspect). Returns null
// if the list cannot be read or is damaged: the caller then falls back to a full scan instead of guessing.
static class AttributeList
{
    const int MaxNonResidentBytes = 4 * 1024 * 1024;
    const int MinEntryLength = 26;

    public static SortedSet<ulong>? ExtensionRecords(byte[] record, ulong baseRecord, IRecordSource source)
    {
        var result = new SortedSet<ulong>();
        if (record.Length < 24) return null;
        var offset = (int)BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
        while (offset + 4 <= record.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset));
            if (type == uint.MaxValue) return result;
            if (offset + 16 > record.Length) return null;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4));
            if (length < 16 || offset + (long)length > record.Length) return null;
            if (type == 0x20)
            {
                if (record[offset + 8] == 0)
                {
                    if (length < 24) return null;
                    var valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 16));
                    var valueStart = offset + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(offset + 20));
                    if (valueLength < 0 || valueStart + (long)valueLength > offset + length) return null;
                    if (!AddEntries(record, valueStart, valueLength, baseRecord, result)) return null;
                }
                else
                {
                    var list = ReadNonResident(record, offset, source);
                    if (list is null || !AddEntries(list, 0, list.Length, baseRecord, result)) return null;
                }
            }
            offset += (int)length;
        }
        return null;   // no end marker
    }

    static bool AddEntries(byte[] list, int start, int size, ulong baseRecord, SortedSet<ulong> result)
    {
        var position = start;
        var end = start + size;
        while (position < end)
        {
            if (end - position < MinEntryLength) return false;
            var entryLength = BinaryPrimitives.ReadUInt16LittleEndian(list.AsSpan(position + 4));
            if (entryLength < MinEntryLength || position + entryLength > end) return false;
            var record = BinaryPrimitives.ReadUInt64LittleEndian(list.AsSpan(position + 16)) & 0x0000FFFFFFFFFFFFUL;
            if (record != baseRecord) result.Add(record);
            position += entryLength;
        }
        return true;
    }

    static byte[]? ReadNonResident(byte[] record, int attributeOffset, IRecordSource source)
    {
        if (attributeOffset + 64 > record.Length) return null;
        var size = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(attributeOffset + 48));
        if (size <= 0 || size > MaxNonResidentBytes) return null;
        var runs = RunList.Decode(record, attributeOffset);
        if (runs is null) return null;
        var stream = new byte[size];
        var filled = 0;
        foreach (var (lcn, clusters) in runs)
        {
            var take = (int)Math.Min(clusters * source.BytesPerCluster, stream.Length - filled);
            if (take <= 0) break;
            if (lcn < 0) return null;   // an attribute list is never sparse
            var data = source.ReadClusters(lcn, (take + source.BytesPerCluster - 1) / source.BytesPerCluster);
            if (data is null || data.Length < take) return null;
            Array.Copy(data, 0, stream, filled, take);
            filled += take;
        }
        return filled == stream.Length ? stream : null;
    }
}
