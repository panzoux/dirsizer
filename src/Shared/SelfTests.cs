using System.Buffers.Binary;
using System.Text;

static class SelfTests
{
    public static void Run()
    {
        MalformedRecordIsRejected();
        ExtensionRecordPreservesBaseReference();
        HardLinksPreserveEachParentName();
        RootSelfReferenceIsParsed();
        MergeIsIndependentOfArrivalOrder();
        ResolverPrefersExactWin32Name();
        AggregationRollsUpToRoot();
        TopSelectionReturnsLargestFirst();
        VolumeInfoDetectsNtfs();
        Console.WriteLine("P0 self-tests passed.");
    }

    // VolumeInfo.IsNtfs (DirSizer.Core), used by the unified dirsizer.exe's strategy selector: unlike the rest
    // of this file, this is a real Win32 call against a real drive, not a synthetic byte-array fixture.
    static void VolumeInfoDetectsNtfs()
    {
        Assert(VolumeInfo.IsNtfs("C:"), "C: is NTFS on this development machine");
        Assert(!VolumeInfo.IsNtfs("ZZ:"), "a nonexistent drive letter must not throw and must report false");
    }

    static void MalformedRecordIsRejected()
    {
        var record = Fixture.Record(100);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(48, 4), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52, 4), 0xFFFFFFF0);
        Assert(RecordParser.Parse(100, record) is null, "malformed attribute should be rejected");
    }

    static void ExtensionRecordPreservesBaseReference()
    {
        var record = Fixture.Record(101, baseReference: 100);
        Fixture.AddData(record, 77);
        var parsed = RecordParser.Parse(101, record);
        Assert(parsed is not null, "extension record should parse");
        Assert(parsed!.HeaderSequenceNumber == 1, "record header sequence was not parsed");
        Assert(parsed!.BaseReference.RecordNumber == 100, "extension base reference was lost");
        Assert(parsed.LogicalSize == 77, "extension data size was lost");
    }

    static void HardLinksPreserveEachParentName()
    {
        var record = Fixture.Record(200);
        Fixture.AddName(record, 5, "first.txt", 1);
        Fixture.AddName(record, 6, "second.txt", 1);
        var parsed = RecordParser.Parse(200, record);
        Assert(parsed is not null && parsed.Names.Count == 2, "hard-link names were not preserved");
        var firstFound = false;
        var secondFound = false;
        foreach (var name in parsed!.Names)
        {
            if (name.Parent.RecordNumber == 5 && name.Name == "first.txt") firstFound = true;
            if (name.Parent.RecordNumber == 6 && name.Name == "second.txt") secondFound = true;
        }
        Assert(firstFound, "first hard link missing");
        Assert(secondFound, "second hard link missing");
    }

    static void RootSelfReferenceIsParsed()
    {
        var record = Fixture.Record(5);
        Fixture.AddName(record, 5, ".", 1);
        var parsed = RecordParser.Parse(5, record);
        Assert(parsed is not null && parsed.Names.Count == 1 && parsed.Names[0].Parent.RecordNumber == 5, "root self-reference was not parsed");
    }

    static ParsedRecord Parsed(ulong number, bool directory, long size, ulong baseNumber = 0, params FileName[] names) =>
        new(new FileRef(number | (1UL << 48)), 1, baseNumber == 0 ? default : new FileRef(baseNumber | (1UL << 48)), directory, size, [.. names]);

    static FileName Name(string name, byte nameSpace, ulong parent = 5) => new(new FileRef(parent | (1UL << 48)), name, nameSpace);

    static void MergeIsIndependentOfArrivalOrder()
    {
        var baseRecord = Parsed(100, false, 0, 0, Name("base.bin", 1));
        var extension = Parsed(101, false, 70, 100, Name("ext.bin", 1));
        foreach (var order in new[] { new[] { baseRecord, extension }, new[] { extension, baseRecord } })
        {
            var records = new Dictionary<ulong, FileRecord>();
            foreach (var parsed in order) RecordMerger.Merge(records, parsed);
            Assert(records.Count == 1 && records.ContainsKey(100), "extension record must merge into its base record");
            Assert(records[100].LogicalSize == 70, "merged logical size must be the largest part");
            Assert(records[100].Names.Count == 2, "names from base and extension must be combined");
            Assert(records[100].Reference.RecordNumber == 100 && !records[100].IsDirectory, "base record identity must win");
        }
    }

    static void ResolverPrefersExactWin32Name()
    {
        var records = new Dictionary<ulong, FileRecord>();
        RecordMerger.Merge(records, Parsed(5, true, 0));
        RecordMerger.Merge(records, Parsed(100, false, 10, 0, Name("LONGNA~1.TXT", 2), Name("Long name.txt", 1)));
        var result = RelationshipResolver.Resolve(records);
        Assert(records[100].DisplayName == "Long name.txt", "the Win32 name must beat the DOS name");
        Assert(result.Exact == 1 && result.Unresolved == 0, "the relationship must be exact");
    }

    static void AggregationRollsUpToRoot()
    {
        var records = new Dictionary<ulong, FileRecord>();
        RecordMerger.Merge(records, Parsed(5, true, 0));
        RecordMerger.Merge(records, Parsed(6, true, 0, 0, Name("A", 1)));
        RecordMerger.Merge(records, Parsed(7, false, 100, 0, Name("inner.bin", 1, 6)));
        RecordMerger.Merge(records, Parsed(8, false, 50, 0, Name("top.bin", 1)));
        RelationshipResolver.Resolve(records);
        SizeAggregator.AddFileSizesToParents(records);
        SizeAggregator.AggregateDirectories(records);
        Assert(records[6].Size == 100, "directory A must contain its file");
        Assert(records[5].Size == 150, "the root must contain its file and directory A");
    }

    static void TopSelectionReturnsLargestFirst()
    {
        var sizes = new long[] { 5, 90, 20, 70, 1 };
        var items = new List<FileRecord>();
        for (var index = 0; index < sizes.Length; index++)
            items.Add(new FileRecord(new FileRef((ulong)(10 + index)), 1, false) { LogicalSize = sizes[index] });
        var top = ResultSelector.SelectTop(items, 3, record => record.LogicalSize);
        Assert(top.Length == 3 && top[0].LogicalSize == 90 && top[1].LogicalSize == 70 && top[2].LogicalSize == 20, "top-N must return the largest items, largest first");
    }
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static class Fixture
    {
        public static byte[] Record(ulong number, ulong baseReference = 0)
        {
            var record = new byte[512];
            record[0] = (byte)'F';
            record[1] = (byte)'I';
            record[2] = (byte)'L';
            record[3] = (byte)'E';
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 48);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), 1);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), baseReference);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(48), uint.MaxValue);
            return record;
        }

        public static void AddName(byte[] record, ulong parent, string name, byte nameSpace)
        {
            var offset = NextAttribute(record);
            var nameBytes = Encoding.Unicode.GetBytes(name);
            var valueLength = 66 + nameBytes.Length;
            var attributeLength = Align8(24 + valueLength);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x30);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), (uint)attributeLength);
            record[offset + 16] = (byte)valueLength;
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20), 24);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(offset + 24), parent);
            record[offset + 24 + 64] = (byte)name.Length;
            record[offset + 24 + 65] = nameSpace;
            nameBytes.CopyTo(record, offset + 24 + 66);
            EndAttribute(record, offset + attributeLength);
        }

        public static void AddData(byte[] record, long size)
        {
            var offset = NextAttribute(record);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), 24);
            record[offset + 16] = (byte)size;
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20), 24);
            EndAttribute(record, offset + 24);
        }

        static int NextAttribute(byte[] record)
        {
            var offset = 48;
            while (BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset)) != uint.MaxValue)
                offset += (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4));
            return offset;
        }

        static void EndAttribute(byte[] record, int offset) => BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), uint.MaxValue);
        static int Align8(int value) => (value + 7) & ~7;
    }
}
