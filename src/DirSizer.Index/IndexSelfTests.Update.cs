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

    // Root 5; A (30) with B (31) holding b.bin (40, 100) and a.bin (41, 20); r.bin (43, 3) at the root.
    static FakeRecordSource SampleVolume()
    {
        var source = new FakeRecordSource();
        source.Records[5] = DirBytes(5, 5, ".");
        source.Records[30] = DirBytes(30, 5, "A");
        source.Records[31] = DirBytes(31, 30, "B");
        source.Records[40] = FileBytes(40, 31, "b.bin", 100);
        source.Records[41] = FileBytes(41, 30, "a.bin", 20);
        source.Records[43] = FileBytes(43, 5, "r.bin", 3);
        return source;
    }

    // Applies the changes, then checks the index against what a full scan of the changed volume produces.
    static UpdateResult ApplyAndCompare(FakeRecordSource source, Dictionary<ulong, FileRecord> records, params ulong[] changed)
    {
        var changes = new List<UsnChange>();
        foreach (var number in changed) changes.Add(new UsnChange(number, 0, 0));
        var result = IndexUpdater.Apply(records, changes, new SortedSet<ulong>(), source);
        AssertEqual((string?)null, result.RebuildReason, "no rebuild needed");
        var verify = IndexVerifier.Compare(Aggregated(records).Records, Aggregated(source.Scan()).Records);
        AssertEqual(0, verify.Differences, $"differences from a fresh scan ({string.Join("; ", verify.Samples)})");
        return result;
    }

    static void UpdateAppliesCreateModifyDeleteRenameAndMove()
    {
        var source = SampleVolume();
        var records = source.Scan();
        source.Records[41] = FileBytes(41, 30, "a.bin", 90);    // modified: 20 -> 90 bytes
        source.Records.Remove(43);                               // deleted
        source.Records[44] = FileBytes(44, 31, "new.bin", 5);    // created in B
        source.Records[32] = DirBytes(32, 5, "C");               // new directory C
        source.Records[31] = DirBytes(31, 32, "B-renamed");      // B renamed and moved from A into C, with its content
        var result = ApplyAndCompare(source, records, 41, 43, 44, 32, 31, 41);
        AssertEqual(6, result.Changes, "journal entries");
        AssertEqual(5, result.Reread, "distinct records read again (41 is in the journal twice)");
        AssertEqual(1, result.Removed, "r.bin removed");
        AssertEqual(4, result.Replaced, "41, 44, 32, 31 rewritten");
        var index = Aggregated(records);
        AssertEqual(195L, index.Records[5].Size, "root: a.bin 90 + b.bin 100 + new.bin 5");
        AssertEqual(105L, index.Records[32].Size, "C: the moved B with b.bin and new.bin");
        AssertEqual(90L, index.Records[30].Size, "A: only a.bin is left");
    }

    static void UpdateSeesAReusedRecordAsANewFile()
    {
        var source = SampleVolume();
        var records = source.Scan();
        source.Records[43] = FileBytes(43, 30, "other.bin", 9, sequence: 2);   // r.bin deleted, its record reused
        ApplyAndCompare(source, records, 43);
        AssertEqual((ushort)2, records[43].Reference.SequenceNumber, "the new sequence number");
    }

    static void UpdateReadsExtensionRecordsThroughTheAttributeList()
    {
        var source = SampleVolume();
        var baseRecord = FileBytes(50, 30, "big.bin", 0);
        AddAttributeList(baseRecord, 50, 51);
        source.Records[50] = baseRecord;
        source.Records[51] = ExtensionBytes(51, 50, 30, "BIG~1.BIN", 10);
        var records = source.Scan();
        source.Records[51] = ExtensionBytes(51, 50, 30, "BIG~1.BIN", 70);   // the data lives in the extension record
        var result = ApplyAndCompare(source, records, 50);                 // the journal names only the base record
        AssertEqual(1, result.ExtensionReads, "extension record 51 read through the attribute list");
        AssertEqual(70L, records[50].LogicalSize, "the size from the extension record");
    }

    static void UpdateDropsARecordThatBecameAnExtension()
    {
        var source = SampleVolume();
        var records = source.Scan();
        source.Records[43] = ExtensionBytes(43, 41, 30, "A~1.BIN", 0);   // record 43 is now an extension record of a.bin
        var baseRecord = FileBytes(41, 30, "a.bin", 20);
        AddAttributeList(baseRecord, 41, 43);
        source.Records[41] = baseRecord;
        ApplyAndCompare(source, records, 43, 41);
        Assert(!records.ContainsKey(43), "43 is no longer an entry of its own");
    }

    static void UnreadableExtensionAsksForARebuild()
    {
        var source = SampleVolume();
        var records = source.Scan();
        var baseRecord = FileBytes(50, 30, "big.bin", 0);
        AddAttributeList(baseRecord, 50, 51);
        source.Records[50] = baseRecord;   // record 51 does not exist
        var result = IndexUpdater.Apply(records, [new UsnChange(50, 0, 0)], new SortedSet<ulong>(), source);
        AssertContains(result.RebuildReason, "51", "the rebuild reason names the missing extension record");
    }

    static void MetadataRecordsAreAlwaysReread()
    {
        var source = SampleVolume();
        source.Records[0] = FileBytes(0, 5, "$MFT", 100);
        source.Records[11] = DirBytes(11, 5, "$Extend");
        source.Records[24] = DirBytes(24, 11, "$RmMetadata");
        source.Records[25] = FileBytes(25, 24, "$Repair", 1);
        var records = source.Scan();
        var metadata = IndexUpdater.MetadataRecords(records);
        Assert(metadata.Contains(0) && metadata.Contains(11) && metadata.Contains(23), "records 0-23 are metadata");
        Assert(metadata.Contains(24) && metadata.Contains(25), "the $Extend tree is metadata");
        Assert(!metadata.Contains(30) && !metadata.Contains(43), "user directories and files are not");
        source.Records[0] = FileBytes(0, 5, "$MFT", 200);   // $MFT grew; NTFS writes no USN record for that
        IndexUpdater.Apply(records, [], metadata, source);
        AssertEqual(200L, records[0].LogicalSize, "the $MFT size is current without a journal entry");
    }
}
