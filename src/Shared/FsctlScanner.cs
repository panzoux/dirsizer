using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
// The FSCTL reader: the reference implementation. One FSCTL_GET_NTFS_FILE_RECORD call per MFT record.

sealed class Scanner(Options options)
{
    public ScanResult Run()
    {
        var totalTimer = Stopwatch.StartNew();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var openTimer = Stopwatch.StartNew();
        var displayVolume = options.Volume.Trim().TrimEnd('\\');
        var root = NormalizeVolume(displayVolume);
        using var handle = Native.OpenVolume(root);
        openTimer.Stop();
        var volumeTimer = Stopwatch.StartNew();
        var volume = Native.ReadVolumeData(handle, displayVolume);
        volumeTimer.Stop();
        var queryTimer = new Stopwatch();
        var parserTimer = new Stopwatch();
        var mergeTimer = new Stopwatch();
        var returnedRecords = 0;
        var parseSuccessfulRecords = 0;
        var extensionRecords = 0;
        var records = new Dictionary<ulong, FileRecord>();
        var scanned = 0;
        var skipped = 0;
        var total = volume.RecordSize == 0 ? 0 : volume.MftLength / volume.RecordSize;
        var input = new byte[8];
        var output = new byte[Math.Max(4096, volume.RecordSize + 16)];
        var lastPercent = -1;
        Progress(0, total, ref lastPercent);

        var next = total == 0 ? 0UL : (ulong)total - 1;
        ulong previousReturned = ulong.MaxValue;
        while (total > 0)
        {
            scanned++;
            queryTimer.Start();
            var fileRecord = Native.ReadRecord(handle, next, input, output);
            queryTimer.Stop();
            if (fileRecord is null) break;
            if (fileRecord.Value.ReturnedRecordNumber >= previousReturned)
                throw new IOException($"FSCTL enumeration did not descend: returned {fileRecord.Value.ReturnedRecordNumber} after {previousReturned}.");
            previousReturned = fileRecord.Value.ReturnedRecordNumber;
            returnedRecords++;
            parserTimer.Start();
            var parsed = RecordParser.Parse(fileRecord.Value.ReturnedRecordNumber, fileRecord.Value.Buffer.AsSpan(fileRecord.Value.Offset, fileRecord.Value.Length));
            parserTimer.Stop();
            if (parsed is null) skipped++;
            else
            {
                parseSuccessfulRecords++;
                if (parsed.BaseReference.RecordNumber != 0) extensionRecords++;
                mergeTimer.Start();
                RecordMerger.Merge(records, parsed);
                mergeTimer.Stop();
            }
            var returnedNumber = parsed?.Reference.RecordNumber ?? fileRecord.Value.ReturnedRecordNumber;
            if (returnedNumber == 0) break;
            next = returnedNumber - 1;
            Progress(scanned, total, ref lastPercent);
        }
        Progress(total, total, ref lastPercent);
        Console.Error.WriteLine();

        var relationshipTimer = Stopwatch.StartNew();
        var relationships = RelationshipResolver.Resolve(records);
        var rootRecord = relationships.Root;

        SizeAggregator.AddFileSizesToParents(records);
        relationshipTimer.Stop();

        var aggregationTimer = Stopwatch.StartNew();
        SizeAggregator.AggregateDirectories(records);
        aggregationTimer.Stop();

        var finalizeTimer = Stopwatch.StartNew();
        var candidates = ResultSelector.Collect(records);
        finalizeTimer.Stop();
        totalTimer.Stop();
        var process = Process.GetCurrentProcess();
        var metrics = new ScanMetrics(
            openTimer.Elapsed,
            volumeTimer.Elapsed,
            queryTimer.Elapsed,
            parserTimer.Elapsed,
            mergeTimer.Elapsed,
            relationshipTimer.Elapsed,
            aggregationTimer.Elapsed,
            finalizeTimer.Elapsed,
            totalTimer.Elapsed,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
            process.PeakWorkingSet64,
            scanned,
            returnedRecords,
            parseSuccessfulRecords,
            extensionRecords,
            relationships.Exact,
            relationships.FallbackZeroSequence,
            relationships.FallbackMismatch,
            relationships.Unresolved,
            relationships.Samples);
        if (metrics.PhaseSum != metrics.TotalTime)
            throw new InvalidOperationException("Benchmark phase accounting does not sum to total scan time.");
        return new ScanResult(displayVolume, records, ResultSelector.SelectTop(candidates.Directories, options.Top, record => record.Size), ResultSelector.SelectTop(candidates.Files, options.Top, record => record.LogicalSize), rootRecord, candidates.RootChildren.ToArray(), relationships.UnresolvedRecords, scanned, skipped, metrics);
    }

    static string NormalizeVolume(string value) => $"\\\\.\\{DriveRoot.Validate(value)}";

    static void Progress(long current, long total, ref int lastPercent)
    {
        var percent = total == 0 ? 0 : (int)(current * 100L / total);
        if (percent == lastPercent) return;
        lastPercent = percent;
        Console.Error.Write($"\rScanning MFT: {current}/{total} ({percent}%)");
        Console.Error.Flush();
    }
}

static class Native
{
    const uint GenericRead = 0x80000000;
    const uint FileShareRead = 1;
    const uint FileShareWrite = 2;
    const uint OpenExisting = 3;
    const uint FsctlGetNtfsVolumeData = 0x00090064;
    const uint FsctlGetNtfsFileRecords = 0x00090068;

    public static SafeFileHandle OpenVolume(string path)
    {
        var handle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Cannot open {path} (Win32 error {error}).");
        }
        return handle;
    }

    public static VolumeData ReadVolumeData(SafeFileHandle handle, string displayVolume)
    {
        var output = new byte[128];
        if (!DeviceIoControl(handle, FsctlGetNtfsVolumeData, null, 0, output, output.Length, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            var filesystem = GetFilesystemName(displayVolume);
            if (!string.Equals(filesystem, "NTFS", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"{displayVolume}: filesystem is {filesystem ?? "unknown"}; only NTFS volumes are supported.");
            throw new Win32Exception(error, $"Cannot read NTFS volume metadata from {displayVolume} (Win32 error {error}).");
        }
        return new VolumeData(BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(56)), BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(48)));
    }

    static string? GetFilesystemName(string volume)
    {
        var name = new StringBuilder(32);
        return GetVolumeInformation($"\\\\?\\{volume.TrimEnd('\\')}\\", null, 0, out _, out _, out _, name, name.Capacity)
            ? name.ToString()
            : null;
    }

    public static RecordBuffer? ReadRecord(SafeFileHandle handle, ulong number, byte[] input, byte[] output)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(input, number);
        if (!DeviceIoControl(handle, FsctlGetNtfsFileRecords, input, input.Length, output, output.Length, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 18) return null;
            throw new Win32Exception(error);
        }
        var returnedRecordNumber = BinaryPrimitives.ReadUInt64LittleEndian(output) & 0x0000FFFFFFFFFFFFUL;
        var length = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(8));
        const int recordOffset = 12;
        if (length <= 0 || length > returned - recordOffset) return null;
        if (returnedRecordNumber > number)
            throw new IOException($"FSCTL returned record {returnedRecordNumber} above requested ordinal {number}.");
        return new RecordBuffer(returnedRecordNumber, output, recordOffset, length);
    }

    public readonly record struct RecordBuffer(ulong ReturnedRecordNumber, byte[] Buffer, int Offset, int Length);
    public readonly record struct VolumeData(long MftLength, int RecordSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string rootPath, StringBuilder? volumeName, int volumeNameSize, out uint serialNumber, out uint maximumComponentLength, out uint filesystemFlags, StringBuilder filesystemName, int filesystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}