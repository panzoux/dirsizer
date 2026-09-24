using System.Buffers.Binary;
using System.ComponentModel;
using System.Text;

static partial class IndexSelfTests
{
    // One USN_RECORD_V2 (60-byte header, UTF-16 name, padded to 8 bytes).
    static byte[] UsnRecord(ulong reference, long usn, uint reason, string name = "f.txt", ushort major = 2)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = (60 + nameBytes.Length + 7) & ~7;
        var bytes = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), major);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), reference);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), Ref(5).FullReference);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(58), 60);
        nameBytes.CopyTo(bytes, 60);
        return bytes;
    }

    // One FSCTL_READ_USN_JOURNAL output buffer: the USN to continue from, then the records.
    static UsnJournal.ReadResponse Page(long next, params byte[][] records)
    {
        var size = 8;
        foreach (var record in records) size += record.Length;
        var output = new byte[size];
        BinaryPrimitives.WriteInt64LittleEndian(output, next);
        var offset = 8;
        foreach (var record in records)
        {
            record.CopyTo(output, offset);
            offset += record.Length;
        }
        return new UsnJournal.ReadResponse(0, output, size);
    }

    static void UsnRecordsAreParsed()
    {
        var changes = new List<UsnChange>();
        var page = Page(0, UsnRecord(Ref(40, 3).FullReference, 1000, 0x100), UsnRecord(Ref(41).FullReference, 1100, 0x200, "a much longer file name.txt"));
        UsnJournal.ParseRecords(page.Output.AsSpan(8), changes);
        AssertEqual(2, changes.Count, "two records");
        AssertEqual(new UsnChange(40, 1000, 0x100), changes[0], "first record: the record number without its sequence");
        AssertEqual(new UsnChange(41, 1100, 0x200), changes[1], "second record, after a longer name");
    }

    static void DamagedUsnRecordsAreErrors()
    {
        var record = UsnRecord(Ref(40).FullReference, 1, 1);
        AssertThrows<IOException>(() => UsnJournal.ParseRecords(record.AsSpan(0, 40), new List<UsnChange>()), "a record cut short");
        var badLength = (byte[])record.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(badLength, 8);
        AssertThrows<IOException>(() => UsnJournal.ParseRecords(badLength, new List<UsnChange>()), "a record length below the header size");
        AssertThrows<IOException>(() => UsnJournal.ParseRecords(UsnRecord(Ref(40).FullReference, 1, 1, major: 3), new List<UsnChange>()), "a version 3 record");
    }

    static void ReadChangesFollowsTheJournalToItsEnd()
    {
        var pages = new Dictionary<long, UsnJournal.ReadResponse>
        {
            [100] = Page(200, UsnRecord(Ref(40).FullReference, 100, 1), UsnRecord(Ref(41).FullReference, 150, 1)),
            [200] = Page(300, UsnRecord(Ref(42).FullReference, 200, 1)),
            [300] = Page(300),
        };
        var changes = new List<UsnChange>();
        var next = UsnJournal.ReadChanges(start => pages[start], 100, changes);
        AssertEqual((long?)300, next, "continues from the USN after the last record");
        AssertEqual(3, changes.Count, "every record of every page");
        AssertEqual((long?)100, UsnJournal.ReadChanges(start => new UsnJournal.ReadResponse(UsnJournal.ErrorHandleEof, new byte[8], 0), 100, new List<UsnChange>()), "ERROR_HANDLE_EOF: nothing new");
    }

    static void ReadChangesReportsWrapsAndErrors()
    {
        AssertEqual((long?)null, UsnJournal.ReadChanges(start => new UsnJournal.ReadResponse(UsnJournal.ErrorJournalEntryDeleted, new byte[8], 0), 100, new List<UsnChange>()),
            "ERROR_JOURNAL_ENTRY_DELETED: the saved position is gone");
        AssertThrows<Win32Exception>(() => UsnJournal.ReadChanges(start => new UsnJournal.ReadResponse(5, new byte[8], 0), 100, new List<UsnChange>()), "any other error");
        AssertThrows<IOException>(() => UsnJournal.ReadChanges(start => Page(start, UsnRecord(Ref(40).FullReference, start, 1)), 100, new List<UsnChange>()), "records without progress");
    }
}
