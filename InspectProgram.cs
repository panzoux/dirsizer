using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

try
{
    return InspectCli.Run(args);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
{
    Console.Error.WriteLine($"error: {exception.Message}");
    if (exception is UnauthorizedAccessException || exception is Win32Exception win32 && win32.NativeErrorCode is 5 or 1314)
        Console.Error.WriteLine("Open the terminal as Administrator. The tool is read-only and requires NTFS volume access.");
    return 1;
}

enum InspectCommand { Volume, MftExtents, Slots, Record, Compare }

sealed record InspectOptions(string Volume, InspectCommand Command, ulong Record, bool Dump, bool Raw, bool Diagnose, bool Help, bool SelfTest);

static class InspectCli
{
    public static int Run(string[] args)
    {
        var options = Parse(args);
        if (options.Help) { PrintHelp(); return 0; }
        if (options.SelfTest) { InspectSelfTests.Run(); return 0; }

        var volume = DriveRoot.Validate(options.Volume);
        using var volumeHandle = BulkNative.OpenVolume($"\\\\.\\{volume}");
        var metadata = BulkNative.ReadVolumeData(volumeHandle, volume);
        using var mft = BulkNative.OpenMft(volume);
        var extents = BulkNative.ReadMftExtents(mft, metadata.BytesPerCluster, metadata.MftValidDataLength);
        return options.Command switch
        {
            InspectCommand.Volume => ShowVolume(volume, metadata, extents),
            InspectCommand.MftExtents => ShowExtents(metadata, extents),
            InspectCommand.Slots => ShowSlots(volumeHandle, volume, metadata, extents, options.Diagnose),
            InspectCommand.Record => ShowRecord(volumeHandle, metadata, extents, options),
            _ => CompareRecord(volumeHandle, metadata, extents, options.Record),
        };
    }

    static InspectOptions Parse(string[] args)
    {
        string? volume = null;
        InspectCommand? command = null;
        ulong record = 0;
        bool dump = false, raw = false, diagnose = false, help = false, selfTest = false;

        void SetCommand(InspectCommand value)
        {
            if (command is not null && command != value) throw new ArgumentException("Use only one of --volume, --mft-extents, --slots, --record, --compare.");
            command = value;
        }
        ulong ReadNumber(string text, string option)
        {
            try { return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt64(text[2..], 16) : ulong.Parse(text); }
            catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException) { throw new ArgumentException($"{option} needs a record number, decimal or 0x-prefixed hexadecimal (got '{text}')."); }
        }

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "-h" or "--help": help = true; break;
                case "--self-test": selfTest = true; break;
                case "--volume": SetCommand(InspectCommand.Volume); break;
                case "--mft-extents": SetCommand(InspectCommand.MftExtents); break;
                case "--slots": SetCommand(InspectCommand.Slots); break;
                case "--dump": dump = true; break;
                case "--raw": raw = true; break;
                case "--diagnose": diagnose = true; break;
                case "--record" or "--compare":
                    SetCommand(arg == "--record" ? InspectCommand.Record : InspectCommand.Compare);
                    if (index + 1 >= args.Length) throw new ArgumentException($"{arg} needs a record number.");
                    record = ReadNumber(args[++index], arg);
                    break;
                default:
                    if (arg.StartsWith("--record=", StringComparison.Ordinal)) { SetCommand(InspectCommand.Record); record = ReadNumber(arg["--record=".Length..], "--record"); break; }
                    if (arg.StartsWith("--compare=", StringComparison.Ordinal)) { SetCommand(InspectCommand.Compare); record = ReadNumber(arg["--compare=".Length..], "--compare"); break; }
                    if (arg.StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option: {arg}. Run with --help for the list.");
                    if (volume is not null) throw new ArgumentException("Only one volume is supported.");
                    volume = arg;
                    break;
            }
        }
        if (help || selfTest) return new InspectOptions(volume ?? "", command ?? InspectCommand.Volume, record, dump, raw, diagnose, help, selfTest);
        if (volume is null) throw new ArgumentException("A volume is required, for example: dirsizer-inspect C: --record 5   (run with --help for the list).");
        if ((dump || raw) && command != InspectCommand.Record) throw new ArgumentException("--dump and --raw only apply to --record.");
        if (diagnose && command != InspectCommand.Slots) throw new ArgumentException("--diagnose only applies to --slots.");
        return new InspectOptions(volume, command ?? InspectCommand.Volume, record, dump, raw, diagnose, false, false);
    }

    static int ShowVolume(string volume, BulkNative.VolumeData metadata, List<BulkNative.MftExtent> extents)
    {
        var slots = metadata.MftValidDataLength / metadata.RecordSize;
        Console.WriteLine($"volume            : {volume}");
        Console.WriteLine($"serial number     : 0x{metadata.SerialNumber:X16}");
        Console.WriteLine($"sector size       : {metadata.BytesPerSector:N0} bytes");
        Console.WriteLine($"cluster size      : {metadata.BytesPerCluster:N0} bytes");
        Console.WriteLine($"MFT record size   : {metadata.RecordSize:N0} bytes");
        Console.WriteLine($"MFT valid length  : {metadata.MftValidDataLength:N0} bytes = {slots:N0} record slots (numbered 0 to {slots - 1:N0})");
        Console.WriteLine($"MFT start LCN     : {metadata.MftStartLcn:N0}");
        Console.WriteLine($"MFT extents       : {extents.Count} ({(extents.Count == 1 ? "contiguous" : "fragmented")}); run --mft-extents to list them");
        return 0;
    }

    static int ShowExtents(BulkNative.VolumeData metadata, List<BulkNative.MftExtent> extents)
    {
        Console.WriteLine($"{extents.Count} extent(s) map the $MFT data stream ({metadata.MftValidDataLength:N0} valid bytes, cluster size {metadata.BytesPerCluster:N0})");
        Console.WriteLine($"{"#",3}  {"VCN start",12}  {"VCN end",12}  {"clusters",10}  {"LCN start",14}  {"bytes",16}  first/last record");
        long total = 0;
        for (var index = 0; index < extents.Count; index++)
        {
            var extent = extents[index];
            var clusters = extent.VcnEnd - extent.VcnStart;
            var firstRecord = extent.VcnStart * metadata.BytesPerCluster / metadata.RecordSize;
            var lastRecord = extent.VcnEnd * metadata.BytesPerCluster / metadata.RecordSize - 1;
            Console.WriteLine($"{index,3}  {extent.VcnStart,12:N0}  {extent.VcnEnd,12:N0}  {clusters,10:N0}  {extent.LcnStart,14:N0}  {clusters * metadata.BytesPerCluster,16:N0}  {firstRecord:N0} - {lastRecord:N0}");
            total += clusters;
        }
        Console.WriteLine($"total {total:N0} clusters = {total * metadata.BytesPerCluster:N0} bytes allocated; valid data length {metadata.MftValidDataLength:N0}");
        return 0;
    }

    static int ShowSlots(SafeFileHandle volumeHandle, string volume, BulkNative.VolumeData metadata, List<BulkNative.MftExtent> extents, bool diagnose)
    {
        var scan = BulkScan.Read(volumeHandle, metadata, extents, diagnose);
        BulkReport.PrintReaderSummary(volume, metadata, extents.Count, scan, diagnose, Console.Out);
        return 0;
    }

    // Reads one record exactly as it is on disk (before update-sequence fixup).
    static byte[] ReadRaw(SafeFileHandle volumeHandle, BulkNative.VolumeData metadata, List<BulkNative.MftExtent> extents, ulong record, out long physicalOffset)
    {
        var slots = (ulong)(metadata.MftValidDataLength / metadata.RecordSize);
        if (record >= slots) throw new ArgumentException($"record {record} is beyond the end of the MFT, which has {slots:N0} record slots (numbered 0 to {slots - 1:N0}).");
        var logicalOffset = checked((long)record * metadata.RecordSize);
        var extentIndex = 0;
        physicalOffset = BulkNative.MapLogicalToPhysical(logicalOffset, metadata.RecordSize, metadata.BytesPerCluster, extents, ref extentIndex);
        var buffer = new byte[metadata.RecordSize];
        BulkNative.ReadAt(volumeHandle, buffer, buffer.Length, physicalOffset);
        return buffer;
    }

    // Reads clusters straight from the volume; null if they cannot be read (the inspector then says so).
    static byte[]? ReadClusters(SafeFileHandle volumeHandle, long bytesPerCluster, long firstLcn, long clusters)
    {
        try
        {
            var buffer = new byte[checked(clusters * bytesPerCluster)];
            BulkNative.ReadAt(volumeHandle, buffer, buffer.Length, firstLcn * bytesPerCluster);
            return buffer;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or OverflowException) { return null; }
    }

    static int ShowRecord(SafeFileHandle volumeHandle, BulkNative.VolumeData metadata, List<BulkNative.MftExtent> extents, InspectOptions options)
    {
        var raw = ReadRaw(volumeHandle, metadata, extents, options.Record, out var physical);
        Console.Write(RecordInspector.Describe(options.Record, raw, metadata.BytesPerSector, physical, (lcn, clusters) => ReadClusters(volumeHandle, metadata.BytesPerCluster, lcn, clusters), (int)metadata.BytesPerCluster));
        if (options.Dump)
        {
            var normalized = (byte[])raw.Clone();
            BulkScan.UsaFixup(normalized, metadata.BytesPerSector);
            Console.WriteLine("  record bytes after update sequence fixup (what the readers parse)");
            Console.Write(RecordInspector.HexDump(normalized));
        }
        if (options.Raw)
        {
            Console.WriteLine("  record bytes as read from disk (before fixup)");
            Console.Write(RecordInspector.HexDump(raw));
        }
        return 0;
    }

    // One slot through both readers' acquisition paths: the raw read (after fixup) against FSCTL_GET_NTFS_FILE_RECORD.
    static int CompareRecord(SafeFileHandle volumeHandle, BulkNative.VolumeData metadata, List<BulkNative.MftExtent> extents, ulong record)
    {
        var raw = ReadRaw(volumeHandle, metadata, extents, record, out _);
        var normalized = (byte[])raw.Clone();
        var kind = BulkScan.Classify(normalized, metadata.BytesPerSector);
        var inUse = kind == SlotKind.InUse;
        Console.WriteLine($"record {record}: raw read classifies the slot as {kind}");

        var returned = FsctlRecords.Query(volumeHandle, record, new byte[8], new byte[Math.Max(4096, metadata.RecordSize + 16)], out var fsctlRecord);
        if (returned is null)
        {
            Console.WriteLine("FSCTL_GET_NTFS_FILE_RECORD returned no record");
            return inUse ? Different("the raw read says the slot is in use but FSCTL returned nothing") : Consistent("the slot is not in use, and FSCTL agrees");
        }
        if (returned != record)
        {
            Console.WriteLine($"FSCTL_GET_NTFS_FILE_RECORD returned record {returned} instead (the nearest lower record in use)");
            return inUse ? Different("the raw read says the slot is in use but FSCTL skipped it") : Consistent("the slot is not in use, and FSCTL skipped it");
        }
        if (!inUse) return Different($"FSCTL returned the record but the raw read classifies the slot as {kind} (a live volume may have changed in between)");

        var tally = new Tally();
        RecordDiff.CompareRecord(record, normalized, fsctlRecord, tally);
        if (tally.TotalMismatches == 0)
        {
            if (normalized.AsSpan().SequenceEqual(fsctlRecord))
                return Consistent("IDENTICAL: the two records are byte-for-byte equal, and the shared parser reads the same model from both");
            return Consistent("EQUIVALENT: equal except the saved entries of the update sequence array. FSCTL returns NTFS's in-memory copy, in which those entries can be zero for records changed since the volume was mounted; the shared parser never reads them and reads the same model from both");
        }
        foreach (var pair in tally.Counts) Console.WriteLine($"  difference {pair.Key}: {pair.Value}");
        foreach (var sample in tally.Samples) Console.WriteLine($"  {sample}");
        return Different("the records differ beyond the update sequence array");
    }

    static int Consistent(string message) { Console.WriteLine($"result: {message}"); return 0; }
    static int Different(string message) { Console.WriteLine($"result: DIFFERENT: {message}"); return 2; }

    static void PrintHelp() => Console.WriteLine("""
        dirsizer-inspect - read-only NTFS $MFT inspection tool

        Usage: dirsizer-inspect C: [command] [options]

        Shows what is actually in the NTFS master file table: one record's attributes, the
        $MFT's own layout, how many slots are in use or deleted, and whether the raw read
        used by dirsizer-bulk and FSCTL_GET_NTFS_FILE_RECORD (used by dirsizer-fsctl) give the
        same record. It is for investigating and developing; use dirsizer-bulk or
        dirsizer-fsctl to find out what is using the disk. Everything is read-only.

        Commands (one at a time; --volume is the default):
          --volume            volume geometry, MFT size and number of record slots, number of MFT extents
          --mft-extents       every extent of the $MFT data stream: VCN range, LCN, size, and the records it holds
          --slots             read every slot and count them: in use, deleted, unused, bad signature,
                              update sequence array failures, records the parser rejects (add --diagnose to
                              list the reasons and sample records)
          --record N          describe record N: classification, header (sequence number, flags, base record),
                              update sequence array against the sector tails, every attribute ($FILE_NAME
                              names with parent and namespace, $DATA streams and sizes, $ATTRIBUTE_LIST
                              entries and the extension records they name, ...), and the model the shared
                              parser builds from it
                              --dump   also print the record bytes after update sequence fixup
                              --raw    also print the record bytes as read from disk
          --compare N         read record N both ways (raw read and FSCTL) and compare the bytes and the
                              parsed model. Exit code 2 if they differ beyond the update sequence array

        N is a record number, decimal or 0x-prefixed hexadecimal. Record numbers are positions in the MFT:
        0 is $MFT, 5 is the root directory, 11 is $Extend. To find the record of a file, use
        `fsutil file queryFileID C:\path\to\file`; its low 48 bits are the record number.

        Other:
          --self-test         run the built-in tests of the record formatter (no volume needed)
          -h, --help          this text

        Terms:
          slot         one fixed-size position in the $MFT. Its number is the record number.
          in use       the header's IN_USE flag is set. A slot with a FILE signature and the flag clear is
                       deleted (its old contents stay until the slot is reused); a slot that starts with
                       zeros is unused.
          extension    a record that holds attributes of another (base) record because they did not fit;
                       its base record reference names the base record.
          update sequence array (USA)
                       NTFS replaces the last two bytes of every 512-byte sector of a record with a
                       sequence number and saves the originals in this array; readers restore them.

        Examples:
          dirsizer-inspect C: --record 5
          dirsizer-inspect C: --record 0x1A2B --dump
          dirsizer-inspect C: --mft-extents
          dirsizer-inspect C: --slots --diagnose
          dirsizer-inspect C: --compare 12345

        Exit codes: 0 ok, 1 error (bad arguments, no access, not NTFS, I/O failure), 2 --compare found a difference.
        Requires Administrator: the executable asks for elevation when it starts.
        """);
}
