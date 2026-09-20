using System.Buffers.Binary;
using System.Text;

public sealed class FileRecord(FileRef reference, ushort sequenceNumber, bool isDirectory)
{
    public FileRef Reference { get; set; } = reference;
    public ushort SequenceNumber { get; set; } = sequenceNumber;
    public bool IsDirectory { get; set; } = isDirectory;
    public long Size { get; set; }
    public long LogicalSize { get; set; }
    public FileRef Parent { get; set; }
    public string? DisplayName { get; set; }
    public List<FileName> Names { get; } = [];
}

public readonly record struct FileRef(ulong FullReference)
{
    public ulong RecordNumber => FullReference & 0x0000FFFFFFFFFFFFUL;
    public ushort SequenceNumber => (ushort)(FullReference >> 48);
}

public sealed record ParsedRecord(FileRef Reference, ushort HeaderSequenceNumber, FileRef BaseReference, bool IsDirectory, long LogicalSize, List<FileName> Names);
public sealed record FileName(FileRef Parent, string Name, byte Namespace);

public enum ParseReject
{
    None,
    TooShort,
    InvalidSignature,
    NotInUse,
    AttributeLengthInvalid,
    AttributeOutOfBounds,
    AttributeHeaderTruncated,
}

public static class RecordParser
{
    public static ParsedRecord? Parse(ulong recordNumber, ReadOnlySpan<byte> record) => TryParse(recordNumber, record, out _, out _);

    // rejectOffset is the offset of the offending attribute header for attribute-level rejects, otherwise 0.
    public static ParsedRecord? TryParse(ulong recordNumber, ReadOnlySpan<byte> record, out ParseReject reject, out int rejectOffset)
    {
        reject = ParseReject.None;
        rejectOffset = 0;
        if (record.Length < 24) { reject = ParseReject.TooShort; return null; }
        if (record[0] != 'F' || record[1] != 'I' || record[2] != 'L' || record[3] != 'E') { reject = ParseReject.InvalidSignature; return null; }
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        if ((flags & 1) == 0) { reject = ParseReject.NotInUse; return null; }
        var headerSequenceNumber = record.Length >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(record[16..]) : (ushort)0;
        var reference = new FileRef(recordNumber | ((ulong)headerSequenceNumber << 48));
        var baseReference = record.Length >= 40 ? new FileRef(BinaryPrimitives.ReadUInt64LittleEndian(record[32..])) : default;
        var names = new List<FileName>();
        long logicalSize = 0;
        var offset = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        while (offset + 8 <= record.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == uint.MaxValue) break;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 8) { reject = ParseReject.AttributeLengthInvalid; rejectOffset = offset; return null; }
            if (length > int.MaxValue || offset + (int)length > record.Length) { reject = ParseReject.AttributeOutOfBounds; rejectOffset = offset; return null; }
            if (offset + 10 > record.Length) { reject = ParseReject.AttributeHeaderTruncated; rejectOffset = offset; return null; }
            var nonResident = record[offset + 8] != 0;
            var nameLength = record[offset + 9];
            if (type == 0x30 && !nonResident && offset + 24 <= record.Length)
            {
                var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 16)..]);
                var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(offset + 20)..]);
                var value = offset + valueOffset;
                var nameBytes = valueLength >= 66 && value <= record.Length - (int)Math.Min(valueLength, int.MaxValue)
                    ? record.Slice(value, (int)valueLength)
                    : ReadOnlySpan<byte>.Empty;
                if (nameBytes.Length >= 66)
                {
                    var characters = nameBytes[64];
                    var nameByteLength = characters * 2;
                    if (nameByteLength <= nameBytes.Length - 66)
                    {
                        var parent = new FileRef(BinaryPrimitives.ReadUInt64LittleEndian(nameBytes));
                        names.Add(new FileName(parent, Encoding.Unicode.GetString(nameBytes.Slice(66, nameByteLength)), nameBytes[65]));
                    }
                }
            }
            else if (type == 0x80 && nameLength == 0 && offset + 24 <= record.Length)
            {
                var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 16)..]);
                logicalSize = nonResident
                    ? offset + 56 <= record.Length ? BinaryPrimitives.ReadInt64LittleEndian(record[(offset + 48)..]) : throw new FormatException("Truncated nonresident DATA attribute")
                    : valueLength;
            }
            offset += (int)length;
        }
        return new ParsedRecord(reference, headerSequenceNumber, baseReference, (flags & 2) != 0, Math.Max(0, logicalSize), names);
    }
}
