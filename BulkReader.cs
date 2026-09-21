using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

static class BulkScan
{
    const int SamplesPerReason = 10;

    // Reads the MFT in DESCENDING record order, like the FSCTL reference reader. RecordMerger appends names in arrival
    // order and name selection breaks equal-score ties by taking the first name, so the arrival order is part of the
    // result. Visiting blocks from the end and records from the end of each block reproduces the reference order exactly
    // (same dictionary insertion order, same tie-breaks). Blocks are still large, extent-aligned reads.
    public static BulkResult Read(SafeFileHandle volume, BulkNative.VolumeData metadata, List<BulkNative.MftExtent> extents, bool diagnose, InUseRecordHandler? onInUse = null, Dictionary<ulong, FileRecord>? records = null, IScanProgress? progress = null)
    {
        var rejectCounts = new int[Enum.GetValues<ParseReject>().Length];
        var rejectFlags = new Dictionary<(ParseReject Reason, ushort Flags), int>();
        var samples = new List<string>();
        var signatureMalformed = 0;
        var deleted = 0;
        var deletedDirectories = 0;
        var timer = Stopwatch.StartNew();
        var slots = metadata.MftValidDataLength / metadata.RecordSize;
        var buffer = new byte[BulkConstants.BlockSize];
        var recordSize = metadata.RecordSize;
        var inUse = 0;
        var unused = 0;
        var malformed = 0;
        var fixupFailures = 0;
        var parseSuccessful = 0;
        var extensionRecords = 0;
        var reads = 0;
        long bytes = 0;
        var readTimer = new Stopwatch();
        var classifyTimer = new Stopwatch();
        var parserTimer = new Stopwatch();
        var mergeTimer = new Stopwatch();
        var blocks = PlanBlocks(slots * recordSize, recordSize, metadata.BytesPerCluster, extents);
        long slotsDone = 0;
        progress?.Report(0, slots);

        for (var blockIndex = blocks.Count - 1; blockIndex >= 0; blockIndex--)
        {
            var logicalOffset = blocks[blockIndex].LogicalOffset;
            var extentIndex = 0;
            var physicalOffset = BulkNative.MapLogicalToPhysical(logicalOffset, blocks[blockIndex].Length, metadata.BytesPerCluster, extents, ref extentIndex);
            readTimer.Start();
            var read = BulkNative.ReadAt(volume, buffer, blocks[blockIndex].Length, physicalOffset);
            readTimer.Stop();
            reads++;
            bytes += read;
            for (var offset = read - recordSize; offset >= 0; offset -= recordSize)
            {
                var recordNumber = (ulong)(logicalOffset / recordSize) + (ulong)(offset / recordSize);
                var record = buffer.AsSpan(offset, recordSize);
                classifyTimer.Start();
                var kind = Classify(record, metadata.BytesPerSector);
                classifyTimer.Stop();
                if (kind == SlotKind.Unused)
                {
                    unused++;
                    continue;
                }
                if (kind == SlotKind.BadSignature)
                {
                    signatureMalformed++;
                    continue;
                }
                if (kind == SlotKind.Deleted)
                {
                    deleted++;
                    if ((BinaryPrimitives.ReadUInt16LittleEndian(record[22..]) & 2) != 0) deletedDirectories++;
                    continue;
                }
                if (kind == SlotKind.FixupFailed)
                {
                    fixupFailures++;
                    continue;
                }
                inUse++;
                onInUse?.Invoke(recordNumber, record);
                parserTimer.Start();
                var parsed = RecordParser.TryParse(recordNumber, record, out var reject, out var rejectOffset);
                parserTimer.Stop();
                if (parsed is null)
                {
                    malformed++;
                    rejectCounts[(int)reject]++;
                    var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
                    var key = (reject, flags);
                    rejectFlags[key] = rejectFlags.GetValueOrDefault(key) + 1;
                    if (diagnose && rejectCounts[(int)reject] <= SamplesPerReason) samples.Add(DescribeReject(recordNumber, record, reject, rejectOffset));
                    continue;
                }
                parseSuccessful++;
                if (parsed.BaseReference.RecordNumber != 0) extensionRecords++;
                if (records is not null)
                {
                    mergeTimer.Start();
                    RecordMerger.Merge(records, parsed);
                    mergeTimer.Stop();
                }
            }
            slotsDone += read / recordSize;
            progress?.Report(slotsDone, slots);
        }
        timer.Stop();
        // Every slot must land in exactly one bucket; an unexplained remainder means the classification is wrong.
        if (slots != unused + signatureMalformed + deleted + fixupFailures + inUse)
            throw new InvalidOperationException("Slot accounting does not sum to the MFT slot count.");
        if (inUse != parseSuccessful + malformed)
            throw new InvalidOperationException("In-use records do not equal parse-successful plus parser rejects.");
        return new BulkResult(slots, inUse, parseSuccessful, extensionRecords, unused, malformed, fixupFailures, reads, bytes, readTimer.Elapsed, timer.Elapsed, metadata.MftValidDataLength % metadata.RecordSize, signatureMalformed, rejectCounts, rejectFlags, samples, deleted, deletedDirectories, classifyTimer.Elapsed, parserTimer.Elapsed, mergeTimer.Elapsed);
    }

    // Splits the captured MFT range into record-aligned blocks that never cross an extent boundary. Pure planning, no I/O.
    internal static List<(long LogicalOffset, int Length)> PlanBlocks(long mftBytes, int recordSize, long bytesPerCluster, List<BulkNative.MftExtent> extents)
    {
        var blocks = new List<(long, int)>();
        var logicalOffset = 0L;
        var extentIndex = 0;
        while (logicalOffset < mftBytes)
        {
            var remaining = mftBytes - logicalOffset;
            var length = (int)Math.Min(BulkConstants.BlockSize, remaining);
            length -= length % recordSize;
            if (length == 0) break;
            var logicalCluster = logicalOffset / bytesPerCluster;
            while (extentIndex < extents.Count && logicalCluster >= extents[extentIndex].VcnEnd) extentIndex++;
            if (extentIndex >= extents.Count) throw new IOException("MFT logical range is not covered by extents");
            var extentEnd = extents[extentIndex].VcnEnd * bytesPerCluster;
            var available = extentEnd - logicalOffset;
            if (length > available)
            {
                length = (int)(available - available % recordSize);
                if (length == 0) throw new IOException("MFT extent boundary is not record-aligned");
            }
            blocks.Add((logicalOffset, length));
            logicalOffset += length;
        }
        return blocks;
    }

    static string DescribeReject(ulong recordNumber, ReadOnlySpan<byte> record, ParseReject reject, int rejectOffset)
    {
        var text = $"sample reason={reject} record={recordNumber} sequence={BinaryPrimitives.ReadUInt16LittleEndian(record[16..])} flags=0x{BinaryPrimitives.ReadUInt16LittleEndian(record[22..]):X4}"
            + $" link_count={BinaryPrimitives.ReadUInt16LittleEndian(record[18..])} first_attr_offset={BinaryPrimitives.ReadUInt16LittleEndian(record[20..])}"
            + $" used={BinaryPrimitives.ReadUInt32LittleEndian(record[24..])} allocated={BinaryPrimitives.ReadUInt32LittleEndian(record[28..])}"
            + $" base=0x{BinaryPrimitives.ReadUInt64LittleEndian(record[32..]):X}";
        if (rejectOffset != 0)
            text += $" attr_offset={rejectOffset} attr_type=0x{BinaryPrimitives.ReadUInt32LittleEndian(record[rejectOffset..]):X} attr_length={BinaryPrimitives.ReadUInt32LittleEndian(record[(rejectOffset + 4)..])}";
        return text;
    }

    // Order matters: a freed slot keeps its FILE signature and stale contents, so the in-use flag (offset 22, inside the
    // first sector and unaffected by USA fixup) is checked before fixup and before the shared parser sees the record.
    internal static SlotKind Classify(Span<byte> record, int bytesPerSector)
    {
        if (IsUnused(record)) return SlotKind.Unused;
        if (record.Length < 24 || !HasFileSignature(record)) return SlotKind.BadSignature;
        if ((BinaryPrimitives.ReadUInt16LittleEndian(record[22..]) & 1) == 0) return SlotKind.Deleted;
        return UsaFixup(record, bytesPerSector) ? SlotKind.InUse : SlotKind.FixupFailed;
    }

    static bool IsUnused(ReadOnlySpan<byte> record) => record.Length >= 18 && record[0] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(record[16..]) == 0;
    static bool HasFileSignature(ReadOnlySpan<byte> record) => record[0] == 'F' && record[1] == 'I' && record[2] == 'L' && record[3] == 'E';

    internal static bool UsaFixup(Span<byte> record, int bytesPerSector)
    {
        if (record.Length < 24 || bytesPerSector <= 0) return false;
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var usaSize = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaSize < 2 || usaOffset > record.Length - usaSize) return false;
        var protectedSectors = (record.Length + bytesPerSector - 1) / bytesPerSector;
        if (usaSize != protectedSectors + 1) return false;
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (var sector = 1; sector <= protectedSectors; sector++)
        {
            var tail = sector * bytesPerSector - 2;
            if (tail < 0 || tail + 2 > record.Length) return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[tail..]) != sequence) return false;
        }
        for (var sector = 1; sector <= protectedSectors; sector++)
        {
            var tail = sector * bytesPerSector - 2;
            var saved = usaOffset + sector * 2;
            record[tail] = record[saved];
            record[tail + 1] = record[saved + 1];
        }
        return true;
    }
}

enum SlotKind { Unused, BadSignature, Deleted, FixupFailed, InUse }

// Progress of a scan, reported to the person running the tool. Report is called after every block with the number of MFT
// slots processed so far; Finish ends the progress line; Message prints a line of its own.
interface IScanProgress
{
    void Report(long current, long total);
    void Finish();
    void Message(string text);
}

// Receives each in-use slot after USA fixup and before parsing. The record span is only valid for the duration of the call.
delegate void InUseRecordHandler(ulong recordNumber, ReadOnlySpan<byte> record);

readonly record struct BulkResult(long Slots, int InUseSlots, int ParseSuccessful, int ExtensionRecords, int UnusedSlots, int Malformed, int FixupFailures, int ReadOperations, long BytesRead, TimeSpan ReadTime, TimeSpan TotalTime, long TailBytes, int SignatureMalformed, int[] RejectCounts, Dictionary<(ParseReject Reason, ushort Flags), int> RejectFlags, List<string> Samples, int DeletedSlots, int DeletedDirectories, TimeSpan ClassifyTime, TimeSpan ParseTime, TimeSpan MergeTime)
{
    public double MegabytesPerSecond => ReadTime.TotalSeconds == 0 ? 0 : BytesRead / 1024d / 1024d / ReadTime.TotalSeconds;
    public int LogicalRecords => ParseSuccessful - ExtensionRecords;
}

static class BulkConstants
{
    public const int BlockSize = 8 * 1024 * 1024;
    public const uint FileReadAttributes = 0x00000080;
    public const uint GenericRead = 0x80000000;
    public const uint FileShareRead = 1;
    public const uint FileShareWrite = 2;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FsctlGetNtfsVolumeData = 0x00090064;
    public const uint FsctlGetRetrievalPointers = 0x00090073;
    public const int ErrorMoreData = 234;
}

static class BulkNative
{
    public readonly record struct VolumeData(long MftValidDataLength, int RecordSize, int BytesPerSector, long BytesPerCluster, long SerialNumber = 0, long MftStartLcn = 0);
    public readonly record struct MftExtent(long VcnStart, long VcnEnd, long LcnStart);
    public readonly record struct Retrieval(long NextVcn, long Lcn);
    public readonly record struct RecordBuffer(ulong ReturnedRecordNumber, byte[] Buffer, int Offset, int Length);

    public static SafeFileHandle OpenVolume(string path)
    {
        var handle = CreateFile(path, BulkConstants.GenericRead, BulkConstants.FileShareRead | BulkConstants.FileShareWrite, IntPtr.Zero, BulkConstants.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) ThrowLastError($"Cannot open volume {path}");
        return handle;
    }

    public static SafeFileHandle OpenMft(string volume)
    {
        EnableBackupPrivilege();
        var path = $"{volume.TrimEnd('\\')}\\$MFT::$DATA";
        var handle = CreateFile(path, BulkConstants.FileReadAttributes, BulkConstants.FileShareRead | BulkConstants.FileShareWrite, IntPtr.Zero, BulkConstants.OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) ThrowLastError($"Cannot open $MFT {path}");
        return handle;
    }

    // displayVolume (for example "D:") is only used to explain a failure: on a volume that is not NTFS the IOCTL fails with
    // an unhelpful error, so the file system name is looked up and reported instead, as the FSCTL reader does.
    public static VolumeData ReadVolumeData(SafeFileHandle handle, string? displayVolume = null)
    {
        var output = new byte[128];
        if (!DeviceIoControl(handle, BulkConstants.FsctlGetNtfsVolumeData, null, 0, output, output.Length, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (displayVolume is not null)
            {
                var filesystem = GetFilesystemName(displayVolume);
                if (!string.Equals(filesystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"{displayVolume} filesystem is {filesystem ?? "unknown"}; only NTFS volumes are supported.");
            }
            throw new Win32Exception(error, $"Cannot read NTFS volume metadata (Win32 error {error})");
        }
        var bytesPerSector = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(40));
        var bytesPerCluster = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(44));
        var mftLength = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(56));
        var recordSize = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(48));
        if (bytesPerSector <= 0 || bytesPerCluster <= 0 || mftLength < 0 || recordSize <= 0) throw new IOException("Invalid NTFS volume geometry");
        return new VolumeData(mftLength, recordSize, bytesPerSector, bytesPerCluster, BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(0)), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(64)));
    }

    static string? GetFilesystemName(string volume)
    {
        var name = new StringBuilder(32);
        return GetVolumeInformation($"\\\\?\\{volume.TrimEnd('\\')}\\", null, 0, out _, out _, out _, name, name.Capacity)
            ? name.ToString()
            : null;
    }

    public readonly record struct RetrievalResponse(int Error, byte[] Output, int Returned);
    public delegate RetrievalResponse RetrievalFetch(long startVcn);

    // outputBufferSize exists so tests can force ERROR_MORE_DATA on a real volume with a tiny buffer.
    public static List<MftExtent> ReadMftExtents(SafeFileHandle mft, long bytesPerCluster, long mftLength, int outputBufferSize = 64 * 1024) =>
        ReadMftExtents(startVcn => FetchRetrievalPointers(mft, startVcn, outputBufferSize), bytesPerCluster, mftLength);

    internal static RetrievalResponse FetchRetrievalPointers(SafeFileHandle mft, long startVcn, int outputBufferSize)
    {
        var input = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(input, startVcn);
        var output = new byte[outputBufferSize];
        var succeeded = DeviceIoControl(mft, BulkConstants.FsctlGetRetrievalPointers, input, input.Length, output, output.Length, out var returned, IntPtr.Zero);
        return new RetrievalResponse(succeeded ? 0 : Marshal.GetLastWin32Error(), output, returned);
    }

    // Follows NextVcn until the captured MFT length is covered. ERROR_MORE_DATA is not a failure: the buffer held only
    // part of the map, the extents that did fit are valid, and the next call continues where they ended.
    public static List<MftExtent> ReadMftExtents(RetrievalFetch fetch, long bytesPerCluster, long mftLength)
    {
        var result = new List<MftExtent>();
        var startVcn = 0L;
        while (startVcn * bytesPerCluster < mftLength)
        {
            var response = fetch(startVcn);
            if (response.Error != 0 && response.Error != BulkConstants.ErrorMoreData)
                throw new Win32Exception(response.Error, $"Cannot obtain MFT retrieval pointers (Win32 error {response.Error})");
            var output = response.Output;
            var returned = response.Returned;
            if (returned < 16) throw new IOException("Invalid MFT retrieval pointer response");
            var count = BinaryPrimitives.ReadUInt32LittleEndian(output);
            var currentVcn = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8));
            var offset = 16;
            for (var index = 0; index < count; index++)
            {
                if (offset + 16 > returned) throw new IOException("Truncated MFT extent map");
                var nextVcn = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(offset));
                var lcn = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(offset + 8));
                if (nextVcn <= currentVcn || lcn < 0) throw new IOException("Invalid MFT extent range");
                result.Add(new MftExtent(currentVcn, nextVcn, lcn));
                currentVcn = nextVcn;
                offset += 16;
            }
            if (count == 0 || currentVcn <= startVcn) throw new IOException("MFT extent map made no progress");
            startVcn = currentVcn;
        }
        return result;
    }

    public static long MapLogicalToPhysical(long logicalOffset, int length, long bytesPerCluster, List<MftExtent> extents, ref int extentIndex)
    {
        var logicalCluster = logicalOffset / bytesPerCluster;
        while (extentIndex < extents.Count && logicalCluster >= extents[extentIndex].VcnEnd) extentIndex++;
        if (extentIndex >= extents.Count || logicalCluster < extents[extentIndex].VcnStart) throw new IOException("MFT logical range is not covered by extents");
        var extent = extents[extentIndex];
        // The offset need not be cluster-aligned (dirsizer-inspect reads single 1 KiB records, four to a 4 KiB cluster): keep the
        // position inside the cluster, and count the clusters the read actually touches.
        var offsetInCluster = logicalOffset % bytesPerCluster;
        var requestedClusters = (offsetInCluster + length + bytesPerCluster - 1) / bytesPerCluster;
        if (logicalCluster + requestedClusters > extent.VcnEnd) throw new IOException("MFT block crosses an extent boundary");
        return (extent.LcnStart + logicalCluster - extent.VcnStart) * bytesPerCluster + offsetInCluster;
    }

    public static int ReadAt(SafeFileHandle handle, byte[] buffer, int length, long offset)
    {
        if (!SetFilePointerEx(handle, offset, out _, 0)) ThrowLastError("Cannot seek raw volume");
        if (!ReadFile(handle, buffer, length, out var read, IntPtr.Zero) || read != length) ThrowLastError("Cannot read raw MFT bytes");
        return read;
    }

    static void ThrowLastError(string message)
    {
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{message} (Win32 error {error})");
    }

    static void EnableBackupPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) ThrowLastError("Cannot open process token");
        using (token)
        {
            if (!LookupPrivilegeValue(null, "SeBackupPrivilege", out var luid)) ThrowLastError("Cannot locate SeBackupPrivilege");
            var privileges = new TokenPrivileges(1, new LuidAndAttributes(luid, SePrivilegeEnabled));
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero)) ThrowLastError("Cannot enable SeBackupPrivilege");
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error, "Cannot enable SeBackupPrivilege");
        }
    }

    const uint TokenAdjustPrivileges = 0x20;
    const uint TokenQuery = 0x8;
    const uint SePrivilegeEnabled = 0x2;
    [StructLayout(LayoutKind.Sequential)] readonly struct Luid
    {
        public readonly uint LowPart;
        public readonly int HighPart;
        public Luid(uint lowPart, int highPart) { LowPart = lowPart; HighPart = highPart; }
    }
    [StructLayout(LayoutKind.Sequential)] readonly struct LuidAndAttributes
    {
        public readonly Luid Luid;
        public readonly uint Attributes;
        public LuidAndAttributes(Luid luid, uint attributes) { Luid = luid; Attributes = attributes; }
    }
    [StructLayout(LayoutKind.Sequential)] readonly struct TokenPrivileges
    {
        public readonly uint PrivilegeCount;
        public readonly LuidAndAttributes Privileges;
        public TokenPrivileges(uint privilegeCount, LuidAndAttributes privileges) { PrivilegeCount = privilegeCount; Privileges = privileges; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out SafeFileHandle token);
    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(SafeFileHandle token, bool disableAll, ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string rootPath, StringBuilder? volumeName, int volumeNameSize, out uint serialNumber, out uint maximumComponentLength, out uint filesystemFlags, StringBuilder filesystemName, int filesystemNameSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long moved, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle file, byte[] buffer, int count, out int read, IntPtr overlapped);
}
