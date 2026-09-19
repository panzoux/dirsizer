using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

var options = Options.Parse(args);
if (options.Help)
{
    Options.PrintHelp();
    return 0;
}

try
{
    var result = new Scanner(options).Run();
    Output.Write(result, options);
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    if (exception is UnauthorizedAccessException || exception is Win32Exception win32 && win32.NativeErrorCode is 5 or 1314)
        Console.Error.WriteLine("Open the terminal as Administrator. The scanner is read-only and requires NTFS volume access.");
    return 1;
}

sealed class Options
{
    public string Volume { get; private set; } = "";
    public int Top { get; private set; } = 25;
    public bool Files { get; private set; }
    public bool Dirs { get; private set; } = true;
    public bool Allocated { get; private set; }
    public bool Json { get; private set; }
    public bool Help { get; private set; }

    public static Options Parse(string[] args)
    {
        var result = new Options();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help") { result.Help = true; continue; }
            if (arg == "--files") { result.Files = true; continue; }
            if (arg == "--dirs") { result.Dirs = true; continue; }
            if (arg == "--allocated") { result.Allocated = true; continue; }
            if (arg == "--json") { result.Json = true; continue; }
            if (arg.StartsWith("--top=", StringComparison.Ordinal) && int.TryParse(arg[6..], out var top)) { result.Top = Math.Max(1, top); continue; }
            if (arg == "--top" && index + 1 < args.Length && int.TryParse(args[++index], out top)) { result.Top = Math.Max(1, top); continue; }
            if (arg.StartsWith("--top", StringComparison.Ordinal)) throw new ArgumentException("Use --top N or --top=N.");
            if (arg.StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option: {arg}");
            if (result.Volume.Length != 0) throw new ArgumentException("Only one volume is supported.");
            result.Volume = arg;
        }
        if (!result.Help && result.Volume.Length == 0) throw new ArgumentException("A volume root is required, for example C:\\.");
        return result;
    }

    public static void PrintHelp() => Console.WriteLine("""
        dirsizer - fast, read-only NTFS folder size scanner

        Usage: dirsizer C:\\ [--top=N] [--files] [--dirs] [--allocated] [--json]

        --top=N   Show the largest N results (default: 25)
        --files   Include largest files
        --dirs    Include largest directories (default)
        --allocated  Report NTFS allocated bytes instead of logical bytes
        --json    Write machine-readable JSON to stdout
        -h        Show this help

        Sizes are logical bytes from unnamed NTFS $DATA attributes by default;
        --allocated reports NTFS allocated bytes. Directories,
        alternate data streams, reparse targets, and deleted records are excluded.
        Hard-linked file records are counted once, on their first discovered name.
        Progress is written to stderr while the MFT is scanned.
        """);
}

sealed class Scanner(Options options)
{
    public ScanResult Run()
    {
        var displayVolume = options.Volume.Trim().TrimEnd('\\');
        var root = NormalizeVolume(displayVolume);
        using var handle = Native.OpenVolume(root);
        var volume = Native.ReadVolumeData(handle, displayVolume);
        var records = new Dictionary<ulong, FileRecord>();
        var scanned = 0;
        var skipped = 0;
        var total = volume.RecordSize == 0 ? 0 : volume.MftLength / volume.RecordSize;
        var input = new byte[8];
        var output = new byte[Math.Max(4096, volume.RecordSize + 16)];
        var lastPercent = -1;
        Progress(0, total, ref lastPercent);

        for (ulong number = 0; number < (ulong)total; number++)
        {
            scanned++;
            try
            {
            var fileRecord = Native.ReadRecord(handle, number, input, output);
            if (fileRecord is null) { skipped++; continue; }
            var record = RecordParser.Parse(fileRecord.Value.Reference, fileRecord.Value.Bytes);
                if (record is not null)
                {
                    record.Size = options.Allocated ? record.AllocatedSize : record.LogicalSize;
                    records[record.Reference] = record;
                }
            }
            catch { skipped++; }
            Progress(scanned, total, ref lastPercent);
        }
        Progress(scanned, total, ref lastPercent);
        Console.Error.WriteLine();

        var directories = records.Values.Where(record => record.IsDirectory).ToDictionary(record => record.Reference);
        var files = records.Values.Where(record => !record.IsDirectory && record.Size > 0).ToArray();
        var rootRecord = records.Values.FirstOrDefault(record => (record.Reference & 0x0000FFFFFFFFFFFFUL) == 5);
        if (rootRecord is null) throw new IOException("NTFS root directory record was not found.");

        foreach (var record in records.Values)
        {
            foreach (var name in record.Names)
            {
                if (directories.ContainsKey(name.Parent)) record.Parent = name.Parent;
                if (record.DisplayName is null || name.Namespace is 1 or 3) record.DisplayName = name.Name;
            }
        }

        foreach (var file in files)
        {
            var name = file.Names.FirstOrDefault(entry => directories.ContainsKey(entry.Parent));
            if (name is null) continue;
            file.Parent = name.Parent;
        }

        foreach (var record in records.Values.Where(record => !record.IsDirectory))
        {
            if (record.Parent != 0 && directories.TryGetValue(record.Parent, out var parent))
                parent.Size += record.Size;
        }

        foreach (var directory in directories.Values.OrderByDescending(record => DirectoryDepth(record, directories)))
        {
            if (directory.Parent != 0 && directories.TryGetValue(directory.Parent, out var parent))
                parent.Size += directory.Size;
        }

        return new ScanResult(displayVolume, records, directories.Values.OrderByDescending(record => record.Size).Take(options.Top).ToArray(), files.OrderByDescending(record => record.Size).Take(options.Top).ToArray(), scanned, skipped);
    }

    static string NormalizeVolume(string value)
    {
        var drive = value.Trim().TrimEnd('\\');
        if (drive.Length == 2 && drive[1] == ':') return $"\\\\.\\{drive}";
        throw new ArgumentException("The volume must be a drive root such as C:\\.");
    }

    static void Progress(int current, long total, ref int lastPercent)
    {
        var percent = total == 0 ? 0 : (int)(current * 100L / total);
        if (percent == lastPercent) return;
        lastPercent = percent;
        Console.Error.Write($"\rScanning MFT: {current}/{total} ({percent}%)");
        Console.Error.Flush();
    }

    static int DirectoryDepth(FileRecord directory, Dictionary<ulong, FileRecord> directories)
    {
        var depth = 0;
        var current = directory;
        var visited = new HashSet<ulong>();
        while (current.Parent != 0 && visited.Add(current.Reference) && directories.TryGetValue(current.Parent, out current!))
            depth++;
        return depth;
    }
}

sealed class FileRecord(ulong reference, bool isDirectory)
{
    public ulong Reference { get; } = reference;
    public bool IsDirectory { get; } = isDirectory;
    public long Size { get; set; }
    public long LogicalSize { get; set; }
    public long AllocatedSize { get; set; }
    public ulong Parent { get; set; }
    public string? DisplayName { get; set; }
    public List<FileName> Names { get; } = [];
}

sealed record FileName(ulong Parent, string Name, byte Namespace);
sealed record ScanResult(string Volume, Dictionary<ulong, FileRecord> Records, FileRecord[] Directories, FileRecord[] Files, int Scanned, int Skipped);

static class RecordParser
{
    public static FileRecord? Parse(ulong reference, byte[] record)
    {
        if (record.Length < 24 || Encoding.ASCII.GetString(record, 0, 4) != "FILE") return null;
        var flags = BitConverter.ToUInt16(record, 22);
        if ((flags & 1) == 0) return null;
        var result = new FileRecord(reference & 0x0000FFFFFFFFFFFFUL, (flags & 2) != 0);
        var offset = BitConverter.ToUInt16(record, 20);
        while (offset + 8 <= record.Length)
        {
            var type = BitConverter.ToUInt32(record, offset);
            if (type == uint.MaxValue) break;
            var length = BitConverter.ToUInt32(record, offset + 4);
            if (length < 8 || offset + length > record.Length) break;
            var nonResident = record[offset + 8] != 0;
            var nameLength = record[offset + 9];
            if (type == 0x30 && !nonResident && offset + 24 <= record.Length)
            {
                var valueLength = BitConverter.ToUInt32(record, offset + 16);
                var valueOffset = BitConverter.ToUInt16(record, offset + 20);
                var value = offset + valueOffset;
                if (valueLength >= 66 && value + valueLength <= record.Length)
                {
                    var parent = BitConverter.ToUInt64(record, value) & 0x0000FFFFFFFFFFFFUL;
                    var characters = record[value + 64];
                    var nameSpace = record[value + 65];
                    var name = Encoding.Unicode.GetString(record, value + 66, characters * 2);
                    result.Names.Add(new FileName(parent, name, nameSpace));
                }
            }
            else if (type == 0x80 && nameLength == 0)
            {
                var valueLength = BitConverter.ToUInt32(record, offset + 16);
                result.LogicalSize = nonResident ? BitConverter.ToInt64(record, offset + 48) : valueLength;
                result.AllocatedSize = nonResident ? BitConverter.ToInt64(record, offset + 40) : valueLength;
            }
            offset += (ushort)length;
        }
        return result;
    }
}

static class Output
{
    public static void Write(ScanResult result, Options options)
    {
        if (options.Json)
        {
            var directories = string.Join(',', result.Directories.Select(record => $"{{\"path\":\"{Escape(Path(result, record))}\",\"size\":{record.Size}}}"));
            var files = string.Join(',', result.Files.Select(record => $"{{\"path\":\"{Escape(Path(result, record))}\",\"size\":{record.Size}}}"));
            var jsonSizeMode = options.Allocated ? "allocated" : "logical";
            Console.WriteLine($"{{\"volume\":\"{Escape(result.Volume)}\",\"top\":{options.Top},\"size_mode\":\"{jsonSizeMode}\",\"directories\":[{directories}],\"files\":[{files}],\"statistics\":{{\"records_scanned\":{result.Scanned},\"records_accepted\":{result.Records.Count},\"records_skipped\":{result.Skipped},\"files\":{result.Records.Values.Count(record => !record.IsDirectory)},\"directories\":{result.Records.Values.Count(record => record.IsDirectory)}}}}}");
            return;
        }
        var sizeMode = options.Allocated ? "allocated" : "logical";
        if (options.Dirs) Console.WriteLine($"Showing up to {options.Top} largest directories by {sizeMode} size");
        if (options.Files) Console.WriteLine($"Showing up to {options.Top} largest files by {sizeMode} size");
        if (options.Dirs)
        {
            Console.WriteLine();
            Console.WriteLine($"Directories (largest {options.Top})");
            Console.WriteLine("Size\tPath");
            foreach (var record in result.Directories) Console.WriteLine($"{record.Size,12:N0}\t{Path(result, record)}");
        }
        if (options.Files)
        {
            Console.WriteLine();
            Console.WriteLine($"Files (largest {options.Top})");
            Console.WriteLine("Size\tPath");
            foreach (var record in result.Files) Console.WriteLine($"{record.Size,12:N0}\t{Path(result, record)}");
        }
    }

    static string Path(ScanResult result, FileRecord record)
    {
        var recordNumber = record.Reference & 0x0000FFFFFFFFFFFFUL;
        if (recordNumber == 5)
            return result.Volume + "\\";
        if (record.DisplayName is null)
            return NtfsSystemPath(result.Volume, recordNumber) ?? $"{result.Volume}\\[unresolved record {recordNumber}]";

        var names = new Stack<string>();
        var current = record;
        var visited = new HashSet<ulong>();
        while (current.DisplayName is not null && current.Parent != current.Reference && visited.Add(current.Reference))
        {
            names.Push(current.DisplayName);
            if (!result.Records.TryGetValue(current.Parent, out current!)) break;
        }
        return names.Count == 0
            ? NtfsSystemPath(result.Volume, recordNumber) ?? $"{result.Volume}\\[unresolved record {recordNumber}]"
            : result.Volume + "\\" + string.Join('\\', names);
    }

    static string? NtfsSystemPath(string volume, ulong recordNumber)
    {
        var name = recordNumber switch
        {
            0 => "$MFT",
            1 => "$MFTMirr",
            2 => "$LogFile",
            3 => "$Volume",
            4 => "$AttrDef",
            6 => "$Bitmap",
            7 => "$Boot",
            8 => "$BadClus",
            9 => "$Secure",
            10 => "$UpCase",
            11 => "$Extend",
            12 => "$Quota",
            13 => "$ObjId",
            14 => "$Reparse",
            15 => "$UsnJrnl",
            _ => null
        };
        return name is null ? null : $"{volume}\\[NTFS metadata]\\{name}";
    }

    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
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
        return new VolumeData(BitConverter.ToInt64(output, 56), BitConverter.ToInt32(output, 48));
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
        BitConverter.GetBytes(number).CopyTo(input, 0);
        if (!DeviceIoControl(handle, FsctlGetNtfsFileRecords, input, input.Length, output, output.Length, out var returned, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 18) return null;
            throw new Win32Exception(error);
        }
        var reference = BitConverter.ToUInt64(output, 0);
        var length = BitConverter.ToInt32(output, 8);
        const int recordOffset = 12;
        if (length <= 0 || length > returned - recordOffset) return null;
        var record = new byte[length];
        Buffer.BlockCopy(output, recordOffset, record, 0, length);
        return new RecordBuffer(reference, record);
    }

    public readonly record struct RecordBuffer(ulong Reference, byte[] Bytes);
    public readonly record struct VolumeData(long MftLength, int RecordSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformation(string rootPath, StringBuilder? volumeName, int volumeNameSize, out uint serialNumber, out uint maximumComponentLength, out uint filesystemFlags, StringBuilder filesystemName, int filesystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}