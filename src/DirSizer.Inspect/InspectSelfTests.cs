using System.Buffers.Binary;
using System.Text;

// Volume-independent tests of the record formatter used by dirsizer-inspect.
static class InspectSelfTests
{
    public static void Run()
    {
        InUseFileIsDescribed();
        DirectoryFlagIsDescribed();
        DeletedRecordIsDescribed();
        UnusedAndBadSignatureAreDescribed();
        BrokenUsaIsReportedNotHidden();
        ExtensionRecordIsDescribed();
        AttributeListShowsExtensionRecords();
        NonResidentNamedStreamIsDescribed();
        MalformedAttributeIsReportedNotThrown();
        RunListIsDecoded();
        DamagedRunListIsRejected();
        NonResidentAttributeListIsExpanded();
        NonResidentAttributeListWithoutReaderIsReportedNotRead();
        RandomRecordsNeverThrow();
        HexDumpHasSixteenBytesPerRow();
        Console.WriteLine("Inspect self-tests passed.");
    }

    static void InUseFileIsDescribed()
    {
        var text = RecordInspector.Describe(100, Fixture.Record(1, a => { a.Add(Fixture.StandardInformation(0x20)); a.Add(Fixture.FileName(5, 1, "a.txt", 1)); a.Add(Fixture.ResidentData(300)); }), 512);
        Contains(text, "in use, file", "classification");
        Contains(text, "$STANDARD_INFORMATION", "standard information attribute");
        Contains(text, "file attributes 0x20 (archive)", "file attribute flags");
        Contains(text, "name \"a.txt\"", "file name");
        Contains(text, "parent 5:1   namespace 1 (Win32)", "parent and namespace");
        Contains(text, "data size 300 bytes (stored in the record)", "resident data size");
        Contains(text, "update sequence number : 0x0007", "update sequence number");
        Contains(text, "(= sequence number, ok)", "valid sector tails");
        Contains(text, "identity 100:5", "parser identity");
        Contains(text, "logical size (unnamed $DATA) 300", "parser logical size");
    }

    static void DirectoryFlagIsDescribed() =>
        Contains(RecordInspector.Describe(5, Fixture.Record(3, a => a.Add(Fixture.FileName(5, 5, ".", 1))), 512), "in use, directory", "directory classification");

    static void DeletedRecordIsDescribed()
    {
        var text = RecordInspector.Describe(9, Fixture.Record(0, a => a.Add(Fixture.FileName(5, 1, "gone.txt", 1))), 512);
        Contains(text, "deleted (was a file", "deleted classification");
        Contains(text, "name \"gone.txt\"", "stale contents are still shown");
    }

    static void UnusedAndBadSignatureAreDescribed()
    {
        Contains(RecordInspector.Describe(1, new byte[1024], 512), "unused", "unused classification");
        var bad = Fixture.Record(1, _ => { });
        bad[0] = (byte)'B';
        Contains(RecordInspector.Describe(1, bad, 512), "bad signature", "bad signature classification");
    }

    static void BrokenUsaIsReportedNotHidden()
    {
        var raw = Fixture.Record(1, a => a.Add(Fixture.FileName(5, 1, "a.txt", 1)));
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(510), 0xBBBB);
        var text = RecordInspector.Describe(1, raw, 512);
        Contains(text, "update sequence array does not validate", "classification of a broken array");
        Contains(text, "MISMATCH", "the mismatching sector tail is pointed out");
        Contains(text, "without fixup", "the caller is told the attributes were read without fixup");
    }

    static void ExtensionRecordIsDescribed()
    {
        var raw = Fixture.Record(1, a => a.Add(Fixture.ResidentData(8)), baseReference: 100UL | (5UL << 48));
        Contains(RecordInspector.Describe(101, raw, 512), "100:5  (this is an extension record)", "extension record base reference");
    }

    static void AttributeListShowsExtensionRecords()
    {
        var text = RecordInspector.Describe(100, Fixture.Record(1, a => a.Add(Fixture.AttributeList((0x30, 100), (0x80, 101), (0x80, 102)))), 512);
        Contains(text, "$ATTRIBUTE_LIST", "attribute list attribute");
        Contains(text, "in record 101:5", "entry pointing at an extension record");
        Contains(text, "(this record)", "entry pointing at the record itself");
        Contains(text, "extension records referenced by the attribute list: 101, 102", "summary of extension records");
    }

    static void NonResidentNamedStreamIsDescribed()
    {
        var text = RecordInspector.Describe(100, Fixture.Record(1, a => a.Add(Fixture.NonResidentStream("extra", 5000, 8192))), 512);
        Contains(text, "non-resident", "non-resident marker");
        Contains(text, "name \"extra\"", "stream name");
        Contains(text, "size 5,000   allocated 8,192", "sizes of a non-resident stream");
    }

    static void MalformedAttributeIsReportedNotThrown()
    {
        var raw = Fixture.Record(1, a => a.Add(Fixture.FileName(5, 1, "a.txt", 1)), protect: false);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(56 + 4), 0x00FFFFF0);   // attribute length far beyond the record
        Fixture.Protect(raw);
        Contains(RecordInspector.Describe(1, raw, 512), "malformed attribute header", "malformed attribute note");
    }

    // Runs: 4 clusters at LCN 100; 2 at +50 (150); 3 sparse; 5 at -16 (134, the offset is signed); end.
    static readonly byte[] SampleRuns = [0x21, 0x04, 0x64, 0x00, 0x11, 0x02, 0x32, 0x01, 0x03, 0x11, 0x05, 0xF0, 0x00];

    static void RunListIsDecoded()
    {
        var record = new byte[1024];
        var attribute = Fixture.NonResidentWithRuns(0x80, 4096, SampleRuns);
        attribute.CopyTo(record, 56);
        var runs = RunList.Decode(record, 56);
        Assert(runs is not null && runs.Count == 4, $"expected 4 runs, got {runs?.Count}");
        Assert(runs![0] == (100, 4) && runs[1] == (150, 2) && runs[2] == (-1, 3) && runs[3] == (134, 5), $"runs decoded wrongly: {string.Join(" ", runs)}");
    }

    static void DamagedRunListIsRejected()
    {
        var record = new byte[1024];
        Fixture.NonResidentWithRuns(0x80, 4096, [0x44, 0x01, 0x02, 0x03, 0x04, 0x05, 0x00]).CopyTo(record, 56);   // 4+4 bytes announced, the attribute ends first
        Assert(RunList.Decode(record, 56) is null, "a run whose bytes run past the attribute must be rejected");
        Assert(RunList.Decode(record, 1020) is null, "an attribute offset near the end of the record must be rejected");
    }

    static void NonResidentAttributeListIsExpanded()
    {
        var entries = Fixture.AttributeListValue((0x80, 101), (0x80, 102), (0x30, 100));
        var record = Fixture.Record(1, a => a.Add(Fixture.NonResidentWithRuns(0x20, entries.Length, [0x21, 0x01, 0xF4, 0x01, 0x00])));   // one cluster at LCN 500
        long? requested = null;
        var text = RecordInspector.Describe(100, record, 512, null, (lcn, clusters) =>
        {
            requested = lcn * 1000 + clusters;
            var data = new byte[clusters * 4096];
            entries.CopyTo(data, 0);
            return data;
        }, 4096);
        Assert(requested == 500 * 1000 + 1, $"the run list must lead to cluster 500, one cluster; requested {requested}");
        Contains(text, $"read {entries.Length} bytes from 1 run(s)", "the size read from the run list");
        Contains(text, "extension records referenced by the attribute list: 101, 102", "extension records named by a non-resident list");
    }

    static void NonResidentAttributeListWithoutReaderIsReportedNotRead()
    {
        var record = Fixture.Record(1, a => a.Add(Fixture.NonResidentWithRuns(0x20, 96, [0x21, 0x01, 0xF4, 0x01, 0x00])));
        Contains(RecordInspector.Describe(100, record, 512), "were not read", "a non-resident list is reported as not read when no reader is given");
    }

    // Damaged or stale records are exactly what an inspection tool is pointed at; it must describe them, not crash.
    static void RandomRecordsNeverThrow()
    {
        var random = new Random(12345);
        for (var index = 0; index < 1000; index++)
        {
            var raw = new byte[1024];
            random.NextBytes(raw);
            if (index % 2 == 0) { "FILE"u8.CopyTo(raw); raw[22] = (byte)(random.Next(4)); }
            if (index % 5 == 0) BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(20), (ushort)random.Next(0, 1200));
            try { RecordInspector.Describe((ulong)index, raw, 512); }
            catch (Exception exception) { throw new InvalidOperationException($"the formatter threw on random record {index}: {exception.GetType().Name}: {exception.Message}"); }
        }
    }

    static void HexDumpHasSixteenBytesPerRow()
    {
        var dump = RecordInspector.HexDump("FILE0123456789ABCDEFGHIJKLMNOPQR"u8.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert(dump.Length == 2, $"32 bytes must be two rows, got {dump.Length}");
        Assert(dump[0].StartsWith("    0000  46 49 4C 45 30 31", StringComparison.Ordinal), "first row must start at offset 0 with the hex bytes");
        Assert(dump[0].Contains("|FILE0123456789AB|"), "first row must show the printable characters");
        Assert(dump[1].StartsWith("    0010  ", StringComparison.Ordinal), "second row must start at offset 0x10");
    }

    static void Contains(string text, string expected, string what) =>
        Assert(text.Contains(expected, StringComparison.Ordinal), $"{what}: expected \"{expected}\" in:\n{text}");

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Builds a 1 KiB record with an update sequence array (sequence number 7, 512-byte sectors) and protects it the way
    // NTFS writes it: the last two bytes of each sector are replaced by the sequence number and saved in the array.
    static class Fixture
    {
        public static byte[] Record(ushort flags, Action<List<byte[]>> attributes, ulong baseReference = 0, bool protect = true)
        {
            var record = new byte[1024];
            "FILE"u8.CopyTo(record);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 48);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 5);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 56);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), flags);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), 1024);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), baseReference);
            var list = new List<byte[]>();
            attributes(list);
            var offset = 56;
            foreach (var attribute in list) { attribute.CopyTo(record, offset); offset += attribute.Length; }
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), uint.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)(offset + 8));
            if (protect) Protect(record);
            return record;
        }

        public static void Protect(byte[] record)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(48), 7);
            for (var sector = 1; sector <= 2; sector++)
            {
                var tail = sector * 512 - 2;
                record[48 + sector * 2] = record[tail];
                record[48 + sector * 2 + 1] = record[tail + 1];
                BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(tail), 7);
            }
        }

        static byte[] Resident(uint type, byte[] value)
        {
            var attribute = new byte[(24 + value.Length + 7) & ~7];
            BinaryPrimitives.WriteUInt32LittleEndian(attribute, type);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), (uint)attribute.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(16), (uint)value.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(20), 24);
            value.CopyTo(attribute, 24);
            return attribute;
        }

        public static byte[] StandardInformation(uint fileAttributes)
        {
            var value = new byte[48];
            BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(32), fileAttributes);
            return Resident(0x10, value);
        }

        public static byte[] FileName(ulong parent, ushort parentSequence, string name, byte nameSpace)
        {
            var characters = Encoding.Unicode.GetBytes(name);
            var value = new byte[66 + characters.Length];
            BinaryPrimitives.WriteUInt64LittleEndian(value, parent | ((ulong)parentSequence << 48));
            value[64] = (byte)name.Length;
            value[65] = nameSpace;
            characters.CopyTo(value, 66);
            return Resident(0x30, value);
        }

        public static byte[] ResidentData(int size) => Resident(0x80, new byte[size]);

        public static byte[] AttributeListValue(params (uint Type, ulong Record)[] entries)
        {
            var value = new byte[entries.Length * 32];
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = value.AsSpan(index * 32);
                BinaryPrimitives.WriteUInt32LittleEndian(entry, entries[index].Type);
                BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 32);
                BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], entries[index].Record | (5UL << 48));
            }
            return value;
        }

        public static byte[] AttributeList(params (uint Type, ulong Record)[] entries) => Resident(0x20, AttributeListValue(entries));

        // A non-resident attribute whose run list starts right after the 64-byte header.
        public static byte[] NonResidentWithRuns(uint type, long size, byte[] runs)
        {
            var attribute = new byte[(64 + runs.Length + 7) & ~7];
            BinaryPrimitives.WriteUInt32LittleEndian(attribute, type);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), (uint)attribute.Length);
            attribute[8] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(32), 64);
            BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(40), (size + 4095) & ~4095L);
            BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(48), size);
            BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(56), size);
            runs.CopyTo(attribute, 64);
            return attribute;
        }

        public static byte[] NonResidentStream(string name, long size, long allocated)
        {
            var characters = Encoding.Unicode.GetBytes(name);
            var attribute = new byte[(64 + characters.Length + 7) & ~7];
            BinaryPrimitives.WriteUInt32LittleEndian(attribute, 0x80);
            BinaryPrimitives.WriteUInt32LittleEndian(attribute.AsSpan(4), (uint)attribute.Length);
            attribute[8] = 1;
            attribute[9] = (byte)name.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(attribute.AsSpan(10), 64);
            BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(24), 1);
            BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(40), allocated);
            BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(48), size);
            BinaryPrimitives.WriteInt64LittleEndian(attribute.AsSpan(56), size);
            characters.CopyTo(attribute, 64);
            return attribute;
        }
    }
}
