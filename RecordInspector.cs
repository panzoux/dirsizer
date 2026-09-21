using System.Buffers.Binary;
using System.Text;

// A readable description of one raw MFT record. Pure text formatting with no I/O, so it can be tested with synthetic
// records. Every read is bounds-checked: a damaged or stale record produces a note, never an exception.
static class RecordInspector
{
    static readonly (uint Type, string Name)[] AttributeNames =
    [
        (0x10, "$STANDARD_INFORMATION"), (0x20, "$ATTRIBUTE_LIST"), (0x30, "$FILE_NAME"), (0x40, "$OBJECT_ID"),
        (0x50, "$SECURITY_DESCRIPTOR"), (0x60, "$VOLUME_NAME"), (0x70, "$VOLUME_INFORMATION"), (0x80, "$DATA"),
        (0x90, "$INDEX_ROOT"), (0xA0, "$INDEX_ALLOCATION"), (0xB0, "$BITMAP"), (0xC0, "$REPARSE_POINT"),
        (0xD0, "$EA_INFORMATION"), (0xE0, "$EA"), (0x100, "$LOGGED_UTILITY_STREAM"),
    ];

    static string AttributeName(uint type)
    {
        foreach (var (candidate, name) in AttributeNames) if (candidate == type) return name;
        return $"0x{type:X}";
    }

    // raw: the record exactly as read from disk (before update-sequence fixup).
    // readClusters(firstLcn, clusterCount) returns those clusters from the volume, or null if they cannot be read. It is only
    // needed to expand a non-resident $ATTRIBUTE_LIST; without it the entries are reported as not read.
    public static string Describe(ulong number, byte[] raw, int bytesPerSector, long? physicalOffset = null, Func<long, long, byte[]?>? readClusters = null, int bytesPerCluster = 0)
    {
        var text = new StringBuilder();
        var slotOffset = (long)number * raw.Length;
        text.Append($"record {number} (0x{number:X})   offset in $MFT: {slotOffset:N0} (0x{slotOffset:X})");
        if (physicalOffset is not null) text.Append($"   physical offset on the volume: {physicalOffset:N0} (0x{physicalOffset:X})");
        text.AppendLine();

        var kind = BulkScan.Classify((byte[])raw.Clone(), bytesPerSector);
        text.AppendLine($"  classification : {DescribeKind(kind, raw)}");
        if (kind is SlotKind.Unused) return text.ToString();
        if (kind is SlotKind.BadSignature)
        {
            text.AppendLine($"  first bytes    : {Convert.ToHexString(raw, 0, Math.Min(16, raw.Length))}");
            return text.ToString();
        }

        var normalized = (byte[])raw.Clone();
        var usaValid = BulkScan.UsaFixup(normalized, bytesPerSector);
        if (!usaValid) normalized = (byte[])raw.Clone();
        AppendHeader(text, normalized);
        AppendUsa(text, raw, bytesPerSector);
        if (!usaValid) text.AppendLine("  (the attributes below are read without fixup because the update sequence array does not validate)");
        AppendAttributes(text, normalized, number, readClusters, bytesPerCluster);
        AppendParserView(text, number, normalized);
        return text.ToString();
    }

    static string DescribeKind(SlotKind kind, byte[] raw)
    {
        var directory = raw.Length >= 24 && (U16(raw, 22) & 2) != 0;
        return kind switch
        {
            SlotKind.Unused => "unused (first byte and sequence number are 0; no FILE signature)",
            SlotKind.BadSignature => "bad signature (neither unused nor a FILE record)",
            SlotKind.Deleted => $"deleted ({(directory ? "was a directory" : "was a file")}; the IN_USE flag is clear and the contents are stale)",
            SlotKind.FixupFailed => "in use, but the update sequence array does not validate (counted as a fixup failure by the readers)",
            _ => directory ? "in use, directory" : "in use, file",
        };
    }

    static void AppendHeader(StringBuilder text, byte[] r)
    {
        var flags = U16(r, 22);
        var baseReference = U64(r, 32);
        text.AppendLine("  header");
        text.AppendLine($"    sequence number         : {U16(r, 16)}");
        text.AppendLine($"    hard link count         : {U16(r, 18)}");
        text.AppendLine($"    flags                   : 0x{flags:X4} ({HeaderFlags(flags)})");
        text.AppendLine($"    used / allocated bytes  : {U32(r, 24):N0} / {U32(r, 28):N0}   (record is {r.Length:N0} bytes)");
        text.AppendLine($"    base record reference   : {(baseReference == 0 ? "none (this is a base record)" : $"{baseReference & 0x0000FFFFFFFFFFFF}:{baseReference >> 48}  (this is an extension record)")}");
        text.AppendLine($"    first attribute offset  : {U16(r, 20)}   next attribute id: {U16(r, 40)}   record number field: {U32(r, 44)}");
        text.AppendLine($"    log sequence number     : 0x{U64(r, 8):X}");
    }

    static string HeaderFlags(ushort flags)
    {
        var parts = new List<string>();
        if ((flags & 1) != 0) parts.Add("in use");
        if ((flags & 2) != 0) parts.Add("directory");
        if ((flags & 4) != 0) parts.Add("extend");
        if ((flags & 8) != 0) parts.Add("view index");
        return parts.Count == 0 ? "not in use" : string.Join(", ", parts);
    }

    // Shows the update sequence array against the sector tails of the raw (unfixed) record.
    static void AppendUsa(StringBuilder text, byte[] raw, int bytesPerSector)
    {
        var offset = U16(raw, 4);
        var count = U16(raw, 6);
        text.AppendLine($"  update sequence array   : at offset {offset}, {count} entries (1 sequence number + {Math.Max(0, count - 1)} sector tails)");
        if (count < 2 || offset + count * 2 > raw.Length || bytesPerSector <= 0)
        {
            text.AppendLine("    (invalid: cannot be read)");
            return;
        }
        var sequence = U16(raw, offset);
        text.AppendLine($"    update sequence number : 0x{sequence:X4}");
        for (var sector = 1; sector < count; sector++)
        {
            var tail = sector * bytesPerSector - 2;
            if (tail + 2 > raw.Length) { text.AppendLine($"    sector {sector}: tail offset {tail} is outside the record"); continue; }
            var onDisk = U16(raw, tail);
            var saved = U16(raw, offset + sector * 2);
            text.AppendLine($"    sector {sector}: tail at {tail} holds 0x{onDisk:X4} {(onDisk == sequence ? "(= sequence number, ok)" : "(MISMATCH with the sequence number)")}; saved original bytes 0x{saved:X4}");
        }
    }

    static void AppendAttributes(StringBuilder text, byte[] r, ulong number, Func<long, long, byte[]?>? readClusters, int bytesPerCluster)
    {
        text.AppendLine("  attributes");
        var offset = (int)U16(r, 20);
        var extensionRecords = new SortedSet<ulong>();
        for (var count = 0; count < 128; count++)
        {
            if (offset + 4 > r.Length) { text.AppendLine($"    (the record ends at offset {offset} without an end marker)"); break; }
            var type = U32(r, offset);
            if (type == 0xFFFFFFFF) { text.AppendLine($"    end marker at offset {offset}"); break; }
            var length = U32(r, offset + 4);
            if (offset + 16 > r.Length || length < 16 || offset + (long)length > r.Length)
            {
                text.AppendLine($"    malformed attribute header at offset {offset}: type 0x{type:X}, length {length}");
                break;
            }
            AppendAttribute(text, r, offset, (int)length, type, number, extensionRecords, readClusters, bytesPerCluster);
            offset += (int)length;
        }
        if (extensionRecords.Count > 0)
            text.AppendLine($"  extension records referenced by the attribute list: {string.Join(", ", extensionRecords)}");
    }

    static void AppendAttribute(StringBuilder text, byte[] r, int offset, int length, uint type, ulong number, SortedSet<ulong> extensionRecords, Func<long, long, byte[]?>? readClusters, int bytesPerCluster)
    {
        var nonResident = r[offset + 8] != 0;
        var nameLength = r[offset + 9];
        var nameOffset = U16(r, offset + 10);
        var flags = U16(r, offset + 12);
        var name = nameLength == 0 ? "" : Utf16(r, offset + nameOffset, nameLength * 2, offset + length);
        var flagText = new List<string>();
        if ((flags & 0x0001) != 0) flagText.Add("compressed");
        if ((flags & 0x4000) != 0) flagText.Add("encrypted");
        if ((flags & 0x8000) != 0) flagText.Add("sparse");
        text.AppendLine($"    @{offset,-5} {AttributeName(type),-22} {(nonResident ? "non-resident" : "resident    ")} length {length,-5} id {U16(r, offset + 14)}{(name.Length > 0 ? $"  name \"{name}\"" : "")}{(flagText.Count > 0 ? $"  [{string.Join(", ", flagText)}]" : "")}");

        if (nonResident)
        {
            if (offset + 64 > r.Length || length < 64) { text.AppendLine("           (header too short for the non-resident fields)"); return; }
            text.AppendLine($"           vcn {U64(r, offset + 16)}-{U64(r, offset + 24)}   size {U64(r, offset + 48):N0}   allocated {U64(r, offset + 40):N0}   initialized {U64(r, offset + 56):N0}");
            if (type == 0x20) AppendNonResidentAttributeList(text, r, offset, number, extensionRecords, readClusters, bytesPerCluster);
            return;
        }

        var valueLength = U32(r, offset + 16);
        var valueOffset = offset + U16(r, offset + 20);
        if (valueOffset < 0 || valueOffset + (long)valueLength > offset + length) { text.AppendLine($"           (resident value of {valueLength} bytes does not fit the attribute)"); return; }
        switch (type)
        {
            case 0x10 when valueLength >= 36:
                text.AppendLine($"           file attributes 0x{U32(r, valueOffset + 32):X} ({FileAttributes(U32(r, valueOffset + 32))})");
                break;
            case 0x30 when valueLength >= 66:
            {
                var parent = U64(r, valueOffset);
                var characters = r[valueOffset + 64];
                var space = r[valueOffset + 65];
                text.AppendLine($"           parent {parent & 0x0000FFFFFFFFFFFF}:{parent >> 48}   namespace {space} ({NamespaceName(space)})   name \"{Utf16(r, valueOffset + 66, characters * 2, valueOffset + (int)valueLength)}\"");
                break;
            }
            case 0x80:
                text.AppendLine($"           data size {valueLength:N0} bytes (stored in the record)");
                break;
            case 0xC0 when valueLength >= 4:
                text.AppendLine($"           reparse tag 0x{U32(r, valueOffset):X8}");
                break;
            case 0x20:
                AppendAttributeList(text, r, valueOffset, (int)valueLength, number, extensionRecords);
                break;
        }
    }

    // A big attribute list (for example a file with many hard links) is stored outside the record; read it through its run list.
    static void AppendNonResidentAttributeList(StringBuilder text, byte[] r, int attributeOffset, ulong number, SortedSet<ulong> extensionRecords, Func<long, long, byte[]?>? readClusters, int bytesPerCluster)
    {
        if (readClusters is null || bytesPerCluster <= 0) { text.AppendLine("           (the entries are in a non-resident stream and were not read)"); return; }
        var size = U64(r, attributeOffset + 48);
        if (size == 0 || size > 4 * 1024 * 1024) { text.AppendLine($"           (the attribute list of {size:N0} bytes was not read)"); return; }
        var runs = RunList.Decode(r, attributeOffset);
        if (runs is null) { text.AppendLine("           (the run list is damaged; the entries cannot be read)"); return; }
        var stream = new byte[size];
        var filled = 0;
        foreach (var (lcn, clusters) in runs)
        {
            var take = (int)Math.Min(clusters * bytesPerCluster, (long)stream.Length - filled);
            if (take <= 0) break;
            if (lcn >= 0)
            {
                var data = readClusters(lcn, (take + bytesPerCluster - 1) / bytesPerCluster);
                if (data is null || data.Length < take) { text.AppendLine("           (the clusters of the attribute list could not be read)"); return; }
                Array.Copy(data, 0, stream, filled, take);
            }
            filled += take;
        }
        text.AppendLine($"           read {filled:N0} bytes from {runs.Count} run(s)");
        AppendAttributeList(text, stream, 0, filled, number, extensionRecords);
    }

    static void AppendAttributeList(StringBuilder text, byte[] r, int start, int length, ulong number, SortedSet<ulong> extensionRecords)
    {
        var position = start;
        var end = start + length;
        while (position + 26 <= end)
        {
            var type = U32(r, position);
            var entryLength = U16(r, position + 4);
            var nameLength = r[position + 6];
            var nameOffset = r[position + 7];
            var reference = U64(r, position + 16);
            var record = reference & 0x0000FFFFFFFFFFFF;
            var name = nameLength == 0 ? "" : Utf16(r, position + nameOffset, nameLength * 2, end);
            text.AppendLine($"           entry: {AttributeName(type),-20} start vcn {U64(r, position + 8),-4} in record {record}:{reference >> 48}{(record == number ? " (this record)" : "")}  id {U16(r, position + 24)}{(name.Length > 0 ? $"  name \"{name}\"" : "")}");
            if (record != number) extensionRecords.Add(record);
            if (entryLength < 26) break;
            position += entryLength;
        }
    }

    // The record as the shared parser sees it: the same model both readers feed to merge, relationships, and aggregation.
    static void AppendParserView(StringBuilder text, ulong number, byte[] normalized)
    {
        text.AppendLine("  shared parser view");
        var parsed = RecordParser.TryParse(number, normalized, out var reject, out var rejectOffset);
        if (parsed is null)
        {
            text.AppendLine($"    rejected: {reject}{(rejectOffset != 0 ? $" at attribute offset {rejectOffset}" : "")}");
            return;
        }
        text.AppendLine($"    identity {parsed.Reference.RecordNumber}:{parsed.Reference.SequenceNumber}   {(parsed.IsDirectory ? "directory" : "file")}   base {(parsed.BaseReference.RecordNumber == 0 ? "none" : $"{parsed.BaseReference.RecordNumber}:{parsed.BaseReference.SequenceNumber}")}   logical size (unnamed $DATA) {parsed.LogicalSize:N0}");
        foreach (var name in parsed.Names)
            text.AppendLine($"    name \"{name.Name}\"   parent {name.Parent.RecordNumber}:{name.Parent.SequenceNumber}   namespace {name.Namespace} ({NamespaceName(name.Namespace)})");
        if (parsed.Names.Count == 0) text.AppendLine("    no $FILE_NAME in this record");
    }

    static string NamespaceName(byte space) => space switch { 0 => "POSIX", 1 => "Win32", 2 => "DOS", 3 => "Win32 and DOS", _ => "unknown" };

    static string FileAttributes(uint value)
    {
        (uint Bit, string Name)[] names = [(1, "read-only"), (2, "hidden"), (4, "system"), (0x10, "directory"), (0x20, "archive"), (0x100, "temporary"), (0x200, "sparse"), (0x400, "reparse point"), (0x800, "compressed"), (0x1000, "offline"), (0x2000, "not indexed"), (0x4000, "encrypted")];
        var parts = new List<string>();
        foreach (var (bit, name) in names) if ((value & bit) != 0) parts.Add(name);
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    // 16 bytes per row: offset, hex, and printable characters.
    public static string HexDump(byte[] data)
    {
        var text = new StringBuilder();
        for (var row = 0; row < data.Length; row += 16)
        {
            var count = Math.Min(16, data.Length - row);
            text.Append($"    {row:X4}  ");
            for (var i = 0; i < 16; i++) text.Append(i < count ? $"{data[row + i]:X2} " : "   ");
            text.Append(" |");
            for (var i = 0; i < count; i++) text.Append(data[row + i] is >= 0x20 and < 0x7F ? (char)data[row + i] : '.');
            text.AppendLine("|");
        }
        return text.ToString();
    }

    static string Utf16(byte[] r, int offset, int bytes, int limit)
    {
        if (offset < 0 || bytes < 0 || offset + bytes > Math.Min(limit, r.Length)) return "?";
        return Encoding.Unicode.GetString(r, offset, bytes);
    }

    static ushort U16(byte[] r, int offset) => offset >= 0 && offset + 2 <= r.Length ? BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(offset)) : (ushort)0;
    static uint U32(byte[] r, int offset) => offset >= 0 && offset + 4 <= r.Length ? BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(offset)) : 0;
    static ulong U64(byte[] r, int offset) => offset >= 0 && offset + 8 <= r.Length ? BinaryPrimitives.ReadUInt64LittleEndian(r.AsSpan(offset)) : 0;
}
