using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

try
{
    var volume = "T:";
    var maxSlots = 65536;
    foreach (var arg in args)
    {
        if (arg is "-h" or "--help")
        {
            Console.WriteLine("dirsizer-compare T: [--max-slots=N] [--dump-usa]");
            Console.WriteLine("Compares raw-bulk and FSCTL records for every MFT slot on a QUIESCENT NTFS volume.");
            Console.WriteLine("Exit code 0 = no mismatches and the volume did not change during the run; 2 = mismatch or unstable.");
            return 0;
        }
        if (arg == "--self-test") return CompareSelfTests.Run();
        if (arg == "--dump-usa") { RecordComparison.DumpUsa = true; continue; }
        if (arg.StartsWith("--max-slots=", StringComparison.Ordinal)) maxSlots = int.Parse(arg["--max-slots=".Length..]);
        else volume = arg.Trim().TrimEnd('\\');
    }
    return RecordComparison.Run(volume, maxSlots);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or FormatException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    return 1;
}

static class RecordComparison
{
    const int SamplesPerCategory = 5;
    public static bool DumpUsa;

    public static int Run(string volume, int maxSlots)
    {
        using var handle = BulkNative.OpenVolume($"\\\\.\\{volume}");
        var metadata = BulkNative.ReadVolumeData(handle);
        using var mft = BulkNative.OpenMft(volume);
        var extents = BulkNative.ReadMftExtents(mft, metadata.BytesPerCluster, metadata.MftValidDataLength);
        var extentBufferMismatches = CheckExtentMapWithSmallBuffers(mft, metadata, extents);
        var slots = metadata.MftValidDataLength / metadata.RecordSize;
        if (slots > maxSlots) throw new ArgumentException($"{volume} has {slots} MFT slots; this tool holds every in-use record in memory and is limited to --max-slots={maxSlots}. Use a small test volume.");

        var bulkFirst = new Dictionary<ulong, byte[]>();
        var summaryFirst = BulkScan.Read(handle, metadata, extents, false, (number, record) => bulkFirst[number] = record.ToArray());

        var fsctl = new Dictionary<ulong, byte[]>();
        var fsctlOutput = new byte[Math.Max(4096, metadata.RecordSize + 16)];
        var fsctlInput = new byte[8];
        var fsctlReturnedEarlier = 0;
        for (ulong number = 0; number < (ulong)slots; number++)
        {
            var returned = FsctlRecords.Query(handle, number, fsctlInput, fsctlOutput, out var record);
            if (returned is null) continue;
            if (returned.Value == number) fsctl[number] = record;
            else fsctlReturnedEarlier++;
        }

        var bulkSecond = new Dictionary<ulong, byte[]>();
        BulkScan.Read(handle, metadata, extents, false, (number, record) => bulkSecond[number] = record.ToArray());

        var tally = new Tally();
        var stable = bulkFirst.Count == bulkSecond.Count;
        foreach (var pair in bulkFirst)
        {
            if (!bulkSecond.TryGetValue(pair.Key, out var again) || !pair.Value.AsSpan().SequenceEqual(again))
            {
                stable = false;
                tally.Mismatch("bulk_unstable_between_passes", pair.Key, "record changed or disappeared between the two bulk passes");
            }
        }

        foreach (var pair in bulkFirst)
        {
            if (fsctl.TryGetValue(pair.Key, out var other)) CompareRecord(pair.Key, pair.Value, other, tally);
            else tally.Mismatch("only_in_bulk", pair.Key, "bulk reports in-use; FSCTL did not return this slot");
        }
        foreach (var pair in fsctl)
        {
            if (!bulkFirst.ContainsKey(pair.Key)) tally.Mismatch("only_in_fsctl", pair.Key, "FSCTL returned this slot; bulk did not classify it in-use");
        }
        if (bulkFirst.Count != fsctl.Count) tally.Mismatch("record_count", 0, $"bulk_in_use={bulkFirst.Count} fsctl_in_use={fsctl.Count}");

        Console.WriteLine($"comparison volume={volume} record_size={metadata.RecordSize} mft_slots={slots} extents={extents.Count}");
        Console.WriteLine($"bulk_in_use={bulkFirst.Count} fsctl_in_use={fsctl.Count} bulk_deleted={summaryFirst.DeletedSlots} bulk_unused={summaryFirst.UnusedSlots} fsctl_returned_earlier_slot={fsctlReturnedEarlier}");
        Console.WriteLine($"volume_stable_during_run={(stable ? "true" : "false")}");
        Console.WriteLine($"compared_records={tally.Compared} compared_extension_records={tally.Extension} compared_directories={tally.Directories} compared_multi_name_records={tally.MultiName} compared_records_with_data_size={tally.WithSize} compared_names={tally.Names}");
        Console.WriteLine($"info usa_saved_entries_differ_records={tally.UsaEntriesDiffer} fsctl_records_with_nonzero_saved_usa_entries={tally.FsctlUsaEntriesNonZero} (bulk keeps the on-disk saved sector-tail values; a difference confined to them is information only when the FSCTL side is zero)");
        foreach (var pair in tally.Counts) Console.WriteLine($"mismatch {pair.Key}={pair.Value}");
        foreach (var sample in tally.Samples) Console.WriteLine($"sample {sample}");
        if (DumpUsa) DumpUsaEntries(bulkFirst, fsctl);
        var total = tally.TotalMismatches + extentBufferMismatches;
        Console.WriteLine($"result={(total == 0 && stable ? "EQUAL" : "DIFFERENT")} mismatches={total}");
        return total == 0 && stable ? 0 : 2;
    }

    // Reads the MFT extent map again with tiny output buffers so FSCTL_GET_RETRIEVAL_POINTERS really returns
    // ERROR_MORE_DATA (a partial map) and must be continued; every variant must equal the normal read.
    static int CheckExtentMapWithSmallBuffers(Microsoft.Win32.SafeHandles.SafeFileHandle mft, BulkNative.VolumeData metadata, List<BulkNative.MftExtent> expected)
    {
        var mismatches = 0;
        foreach (var size in new[] { 32, 48, 64, 100, 4096 })
        {
            var calls = 0;
            var moreData = 0;
            List<BulkNative.MftExtent> actual;
            try
            {
                actual = BulkNative.ReadMftExtents(startVcn =>
                {
                    var response = BulkNative.FetchRetrievalPointers(mft, startVcn, size);
                    calls++;
                    if (response.Error == BulkConstants.ErrorMoreData) moreData++;
                    return response;
                }, metadata.BytesPerCluster, metadata.MftValidDataLength);
            }
            catch (Exception exception) when (exception is IOException or Win32Exception)
            {
                Console.WriteLine($"extent_map buffer={size} FAILED: {exception.Message}");
                mismatches++;
                continue;
            }
            var equal = actual.Count == expected.Count;
            for (var index = 0; equal && index < actual.Count; index++) equal = actual[index] == expected[index];
            Console.WriteLine($"extent_map buffer={size} calls={calls} more_data_responses={moreData} extents={actual.Count} equal_to_normal_read={(equal ? "true" : "false")}");
            if (!equal) mismatches++;
        }
        return mismatches;
    }

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

    // Diagnostic: per-record saved USA entries on both sides, for records where either side is non-zero.
    static void DumpUsaEntries(Dictionary<ulong, byte[]> bulk, Dictionary<ulong, byte[]> fsctl)
    {
        var numbers = new List<ulong>(bulk.Keys);
        numbers.Sort();
        foreach (var number in numbers)
        {
            if (!fsctl.TryGetValue(number, out var other)) continue;
            var left = bulk[number];
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(4));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(6)) - 1;
            var used = BinaryPrimitives.ReadUInt32LittleEndian(left.AsSpan(24));
            var leftEntries = new List<string>();
            var rightEntries = new List<string>();
            var interesting = false;
            for (var entry = 0; entry < count; entry++)
            {
                var position = offset + 2 + entry * 2;
                var leftValue = BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(position));
                var rightValue = BinaryPrimitives.ReadUInt16LittleEndian(other.AsSpan(position));
                leftEntries.Add($"{leftValue:X4}");
                rightEntries.Add($"{rightValue:X4}");
                if (leftValue != 0 || rightValue != 0) interesting = true;
            }
            if (!interesting) continue;
            var tails = $"tail510={BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(510)):X4}/{BinaryPrimitives.ReadUInt16LittleEndian(other.AsSpan(510)):X4} tail1022={BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(1022)):X4}/{BinaryPrimitives.ReadUInt16LittleEndian(other.AsSpan(1022)):X4}";
            Console.WriteLine($"usa record={number} ext={(BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(32)) != 0 ? "y" : "n")} flags=0x{BinaryPrimitives.ReadUInt16LittleEndian(left.AsSpan(22)):X4} used={used} bulk=[{string.Join(",", leftEntries)}] fsctl=[{string.Join(",", rightEntries)}] {tails} {(leftEntries.SequenceEqual(rightEntries) ? "same" : "DIFF")}");
        }
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

// Proves the comparison can actually see differences: an identical pair must produce zero mismatches and each
// mutation must be reported under the expected category.
static class CompareSelfTests
{
    public static int Run()
    {
        var failures = 0;
        failures += Expect("identical records", Mutate(_ => { }), []);
        failures += Expect("sequence number", Mutate(record => record[16] ^= 1), ["raw_bytes_within_used", "identity", "sequence"]);
        failures += Expect("directory flag", Mutate(record => record[22] ^= 2), ["raw_bytes_within_used", "directory_flag"]);
        failures += Expect("base reference", Mutate(record => record[32] ^= 1), ["raw_bytes_within_used", "base_reference"]);
        failures += Expect("unnamed data size", Mutate(record => record[Attribute(record, 0x80) + 16] ^= 1), ["raw_bytes_within_used", "logical_size"]);
        failures += Expect("file name character", Mutate(record => record[Attribute(record, 0x30) + 24 + 66] ^= 1), ["raw_bytes_within_used", "filename"]);
        failures += Expect("file name parent", Mutate(record => record[Attribute(record, 0x30) + 24] ^= 1), ["raw_bytes_within_used", "filename"]);
        failures += Expect("slack after used size only", Mutate(record => record[(int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24)) + 4] ^= 1), ["raw_bytes_slack_only"]);
        failures += Expect("record length", Truncated(), ["record_length"]);
        failures += Expect("saved USA entries zero on the FSCTL side (information only)", Mutate(record => { record[50] = 0; record[51] = 0; record[52] = 0; record[53] = 0; }), [], usaEntriesDiffer: 1);
        failures += Expect("update sequence number itself differs", Mutate(record => record[48] ^= 1), ["raw_bytes_within_used"]);
        failures += Expect("FSCTL saved USA entry non-zero and different", Mutate(record => record[50] = 0x10), ["usa_entries_fsctl_nonzero"], usaEntriesDiffer: 1);
        failures += Expect("sector tail differs (fixup not applied)", Mutate(record => record[510] ^= 1), ["raw_bytes_slack_only"]);
        Console.WriteLine(failures == 0 ? "Compare self-tests passed." : $"Compare self-tests FAILED: {failures}");
        return failures == 0 ? 0 : 1;
    }

    static int Expect(string name, (byte[] Left, byte[] Right) pair, string[] expected, int usaEntriesDiffer = 0)
    {
        var tally = new Tally();
        RecordComparison.CompareRecord(7, pair.Left, pair.Right, tally);
        var ok = tally.Counts.Count == expected.Length && tally.UsaEntriesDiffer == usaEntriesDiffer;
        foreach (var category in expected) ok &= tally.Counts.ContainsKey(category);
        if (!ok)
        {
            var seen = new List<string>();
            foreach (var pairCount in tally.Counts) seen.Add($"{pairCount.Key}={pairCount.Value}");
            Console.WriteLine($"FAIL {name}: expected [{string.Join(", ", expected)}] got [{string.Join(", ", seen)}]");
        }
        return ok ? 0 : 1;
    }

    static (byte[], byte[]) Mutate(Action<byte[]> change)
    {
        var left = Record();
        var right = (byte[])left.Clone();
        change(right);
        return (left, right);
    }

    static (byte[], byte[]) Truncated()
    {
        var left = Record();
        return (left, left[..1000]);
    }

    static int Attribute(byte[] record, uint type)
    {
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
        while (BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset)) != uint.MaxValue)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset)) == type) return offset;
            offset += (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(offset + 4));
        }
        throw new InvalidOperationException($"attribute 0x{type:X} missing from fixture");
    }

    // A 1 KiB in-use file record with one $FILE_NAME ("a.txt", parent 5) and a 1234-byte resident unnamed $DATA.
    static byte[] Record()
    {
        var record = new byte[1024];
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(48), 0x0007);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(50), 0x1111);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(52), 0x2222);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), 0x1111);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022), 0x2222);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), 1024);
        var offset = 56;
        var name = System.Text.Encoding.Unicode.GetBytes("a.txt");
        var nameValue = 66 + name.Length;
        var nameAttribute = (24 + nameValue + 7) & ~7;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x30);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), (uint)nameAttribute);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 16), (uint)nameValue);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20), 24);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(offset + 24), 5 | (1UL << 48));
        record[offset + 24 + 64] = 5;
        record[offset + 24 + 65] = 1;
        name.CopyTo(record, offset + 24 + 66);
        offset += nameAttribute;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 16), 1234);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20), 24);
        offset += 24;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)(offset + 8));
        return record;
    }
}
