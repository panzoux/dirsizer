using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// The on-disk form of a VolumeIndex (docs\design_index.md). Version 1, little-endian throughout:
//   u32 magic "DSIX", u32 version
//   i64 volume serial, i32 MFT record size, i64 bytes per cluster, i64 MFT start LCN
//   u64 USN journal id (0 = none), i64 next USN, i64 time written (UTC ticks)
//   i32 record count, then for each record, in dictionary order:
//     u64 reference, u16 header sequence, u8 flags (bit 0 = directory), i64 logical size, u16 name count,
//     and for each name: u64 parent reference, u8 namespace, u16 character count, UTF-16LE characters
//   32 bytes: SHA-256 of everything before them
// Records are written and read in dictionary order, so a loaded index enumerates exactly like the scan that wrote it.
static class IndexFile
{
    public const uint Magic = 0x58495344;   // "DSIX" read as a little-endian u32
    public const uint Version = 1;
    const int HashLength = 32;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dirsizer", "index");

    public static string PathFor(string directory, long serialNumber) => Path.Combine(directory, $"{serialNumber:X16}.dsix");

    // Writes a temporary file, flushes it to disk, then renames it over the old index: a crash leaves the old or the new file.
    public static void Save(VolumeIndex index, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var bytes = Serialize(index);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    // Null if there is no usable file; `problem` then says why (it becomes the reason for a full scan).
    public static VolumeIndex? TryLoad(string path, out string? problem)
    {
        if (!File.Exists(path))
        {
            problem = "no saved index";
            return null;
        }
        try
        {
            var index = Read(File.ReadAllBytes(path));
            problem = null;
            return index;
        }
        catch (InvalidDataException exception)
        {
            problem = $"the saved index cannot be used: {exception.Message}";
            return null;
        }
    }

    public static byte[] Serialize(VolumeIndex index)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(index.Identity.SerialNumber);
            writer.Write(index.Identity.RecordSize);
            writer.Write(index.Identity.BytesPerCluster);
            writer.Write(index.Identity.MftStartLcn);
            writer.Write(index.JournalId);
            writer.Write(index.NextUsn);
            writer.Write(index.WrittenUtc.Ticks);
            writer.Write(index.Records.Count);
            foreach (var record in index.Records.Values)
            {
                writer.Write(record.Reference.FullReference);
                writer.Write(record.SequenceNumber);
                writer.Write((byte)(record.IsDirectory ? 1 : 0));
                writer.Write(record.LogicalSize);
                writer.Write(checked((ushort)record.Names.Count));
                foreach (var name in record.Names)
                {
                    writer.Write(name.Parent.FullReference);
                    writer.Write(name.Namespace);
                    writer.Write(checked((ushort)name.Name.Length));
                    foreach (var character in name.Name) writer.Write((ushort)character);
                }
            }
        }
        var hash = SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        stream.Write(hash);
        return stream.ToArray();
    }

    public static VolumeIndex Read(byte[] bytes)
    {
        if (bytes.Length < HashLength + 8) throw new InvalidDataException("The index file is truncated.");
        var body = bytes.AsSpan(0, bytes.Length - HashLength);
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - HashLength)))
            throw new InvalidDataException("The index file checksum does not match.");
        var reader = new IndexReader(body);
        if (reader.U32() != Magic) throw new InvalidDataException("Not a dirsizer index file.");
        var version = reader.U32();
        if (version != Version) throw new InvalidDataException($"Index format version {version} is not supported (this build reads version {Version}).");
        var identity = new VolumeIdentity(reader.I64(), reader.I32(), reader.I64(), reader.I64());
        var journalId = reader.U64();
        var nextUsn = reader.I64();
        var written = new DateTime(reader.I64(), DateTimeKind.Utc);
        var count = reader.I32();
        if (count < 0) throw new InvalidDataException("The record count is negative.");
        var records = new Dictionary<ulong, FileRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var reference = new FileRef(reader.U64());
            var sequence = reader.U16();
            var flags = reader.U8();
            var record = new FileRecord(reference, sequence, (flags & 1) != 0) { LogicalSize = reader.I64() };
            var names = reader.U16();
            for (var n = 0; n < names; n++)
            {
                var parent = new FileRef(reader.U64());
                var nameSpace = reader.U8();
                var characters = reader.U16();
                record.Names.Add(new FileName(parent, reader.Utf16(characters), nameSpace));
            }
            if (!records.TryAdd(reference.RecordNumber, record)) throw new InvalidDataException($"Record {reference.RecordNumber} appears twice.");
        }
        if (reader.Position != body.Length) throw new InvalidDataException("There are bytes after the last record.");
        return new VolumeIndex(identity, journalId, nextUsn, written, records);
    }

    // Bounds-checked little-endian reads; running past the end is a damaged file, not an exception of another type.
    ref struct IndexReader
    {
        readonly ReadOnlySpan<byte> data;
        int position;

        public IndexReader(ReadOnlySpan<byte> data)
        {
            this.data = data;
            position = 0;
        }

        public readonly int Position => position;

        ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - position) throw new InvalidDataException("The index file is truncated.");
            var slice = data.Slice(position, count);
            position += count;
            return slice;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public string Utf16(int characters) => new(MemoryMarshal.Cast<byte, char>(Take(characters * 2)));
    }
}
