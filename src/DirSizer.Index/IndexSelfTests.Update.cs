using System.Buffers.Binary;

// A volume in memory: MFT records by number (bytes as FSCTL_GET_NTFS_FILE_RECORD returns them) and clusters by LCN.
sealed class FakeRecordSource : IRecordSource
{
    public Dictionary<ulong, byte[]> Records { get; } = new();
    public Dictionary<long, byte[]> Clusters { get; } = new();
    public long BytesPerCluster => 4096;

    public byte[]? Read(ulong recordNumber) => Records.TryGetValue(recordNumber, out var bytes) ? (byte[])bytes.Clone() : null;

    public byte[]? ReadClusters(long lcn, long clusters) => clusters == 1 && Clusters.TryGetValue(lcn, out var bytes) ? bytes : null;

    // What a full scan of these records produces: every record parsed and merged in descending record order.
    public Dictionary<ulong, FileRecord> Scan()
    {
        var numbers = new List<ulong>(Records.Keys);
        numbers.Sort();
        numbers.Reverse();
        var records = new Dictionary<ulong, FileRecord>();
        foreach (var number in numbers)
        {
            var parsed = RecordParser.Parse(number, Records[number]);
            if (parsed is not null) RecordMerger.Merge(records, parsed);
        }
        return records;
    }
}

static partial class IndexSelfTests
{
    static void SetSequence(byte[] record, ushort sequence) => BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), sequence);

    static byte[] DirBytes(ulong number, ulong parent, string name, ushort sequence = 1)
    {
        var record = RecordFixture.Record(number, directory: true);
        SetSequence(record, sequence);
        RecordFixture.AddName(record, Ref(parent).FullReference, name, 1);
        return record;
    }

    static byte[] FileBytes(ulong number, ulong parent, string name, long size, ushort sequence = 1)
    {
        var record = RecordFixture.Record(number);
        SetSequence(record, sequence);
        RecordFixture.AddName(record, Ref(parent).FullReference, name, 1);
        RecordFixture.AddData(record, size);
        return record;
    }

    static byte[] ExtensionBytes(ulong number, ulong baseNumber, ulong parent, string name, long size)
    {
        var record = RecordFixture.Record(number, baseReference: Ref(baseNumber).FullReference);
        RecordFixture.AddName(record, Ref(parent).FullReference, name, 2);
        RecordFixture.AddData(record, size);
        return record;
    }

    // $ATTRIBUTE_LIST entries (32 bytes each, for $DATA), naming the given MFT records.
    static void WriteListEntries(byte[] target, int start, ulong[] segments)
    {
        for (var i = 0; i < segments.Length; i++)
        {
            var entry = start + 32 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(entry), 0x80);
            BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(entry + 4), 32);
            target[entry + 7] = 26;
            BinaryPrimitives.WriteUInt64LittleEndian(target.AsSpan(entry + 16), Ref(segments[i]).FullReference);
        }
    }

    static void AddAttributeList(byte[] record, params ulong[] segments)
    {
        var offset = RecordFixture.NextAttribute(record);
        var valueLength = 32 * segments.Length;
        var length = RecordFixture.Align8(24 + valueLength);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 16), (uint)valueLength);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20), 24);
        WriteListEntries(record, offset + 24, segments);
        RecordFixture.EndAttribute(record, offset + length);
    }

    // A non-resident $ATTRIBUTE_LIST in one cluster at `lcn` (below 128, so its one-byte signed run offset is positive).
    static void AddNonResidentAttributeList(byte[] record, FakeRecordSource source, long lcn, params ulong[] segments)
    {
        var offset = RecordFixture.NextAttribute(record);
        var dataSize = 32 * segments.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), 72);
        record[offset + 8] = 1;                                                                        // non-resident
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 32), 64);                      // run list offset
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 40), source.BytesPerCluster);   // allocated size
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 48), dataSize);                 // data size
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(offset + 56), dataSize);                 // initialized size
        record[offset + 64] = 0x11;         // one length byte, one offset byte
        record[offset + 65] = 1;            // one cluster
        record[offset + 66] = (byte)lcn;
        RecordFixture.EndAttribute(record, offset + 72);
        var cluster = new byte[source.BytesPerCluster];
        WriteListEntries(cluster, 0, segments);
        source.Clusters[lcn] = cluster;
    }

    static void ResidentAttributeListNamesExtensionRecords()
    {
        var source = new FakeRecordSource();
        var record = RecordFixture.Record(50);
        AddAttributeList(record, 50, 51, 52, 51);
        RecordFixture.AddName(record, Ref(5).FullReference, "big.bin", 1);
        AssertEqual("51,52", string.Join(',', AttributeList.ExtensionRecords(record, 50, source)!), "the other records, once each, without the base record");
        AssertEqual("", string.Join(',', AttributeList.ExtensionRecords(FileBytes(60, 5, "plain.bin", 1), 60, source)!), "no attribute list: none");
    }

    static void DamagedAttributeListIsNotGuessed()
    {
        var record = RecordFixture.Record(50);
        AddAttributeList(record, 51);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(48 + 24 + 4), 8);   // first entry's length below the minimum
        Assert(AttributeList.ExtensionRecords(record, 50, new FakeRecordSource()) is null, "a damaged entry gives null");
    }

    static void NonResidentAttributeListIsReadFromItsClusters()
    {
        var source = new FakeRecordSource();
        var record = RecordFixture.Record(50);
        AddNonResidentAttributeList(record, source, 10, 50, 53);
        AssertEqual("53", string.Join(',', AttributeList.ExtensionRecords(record, 50, source)!), "entries read from LCN 10");
        source.Clusters.Clear();
        Assert(AttributeList.ExtensionRecords(record, 50, source) is null, "unreadable clusters give null");
    }
}
