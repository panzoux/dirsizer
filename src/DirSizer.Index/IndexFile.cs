using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

// The on-disk form of a VolumeIndex (docs\design_index.md). Two files, little-endian throughout.
//
// Base file <serial>.dsix, version 1, written by a full save:
//   u32 magic "DSIX", u32 version
//   i64 volume serial, i32 MFT record size, i64 bytes per cluster, i64 MFT start LCN
//   u64 USN journal id (0 = none), i64 next USN, i64 time written (UTC ticks)
//   i32 record count, then each record in dictionary order (a record: see WriteRecord)
//   32 bytes: SHA-256 of everything before them
// Records are written and read in dictionary order, so a loaded index enumerates exactly like the scan that wrote it.
//
// Delta file <serial>.dsix.delta, version 1, written by an incremental run instead of the whole base file:
//   u32 magic "DSXD", u32 version
//   32 bytes: the SHA-256 trailer of the base file it applies to
//   u64 USN journal id, i64 next USN, i64 time written (UTC ticks)   (replace the base file's)
//   i32 removed count, then each removed record number (u64), ascending
//   i32 record count, then each record, ascending by record number
//   32 bytes: SHA-256 of everything before them
// It holds the current state of every entry changed since the base file was written, so it replaces the previous delta
// instead of being appended to. A delta whose base hash is not the base file's is left over from before the last full
// save and is ignored.
static class IndexFile
{
    public const uint Magic = 0x58495344;        // "DSIX" read as a little-endian u32
    public const uint DeltaMagic = 0x44585344;   // "DSXD"
    public const uint Version = 1;
    public const uint DeltaVersion = 1;
    // A delta is written while it holds at most this many entries, or 5 % of the records if that is more; beyond that
    // a full save folds it into the base file, so loading never has to apply a large delta.
    public const int MinDeltaLimit = 1000;
    const int HashLength = 32;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dirsizer", "index");

    public static string PathFor(string directory, long serialNumber) => Path.Combine(directory, $"{serialNumber:X16}.dsix");

    public static string DeltaPathFor(string path) => path + ".delta";

    public static bool ShouldSaveDelta(VolumeIndex index) =>
        index.BaseHash is not null && index.Dirty.Count <= Math.Max(MinDeltaLimit, index.Records.Count / 20);

    // Writes a temporary file, flushes it to disk, then renames it over the old index: a crash leaves the old or the new
    // file. Then the delta is deleted (if a crash leaves it, it names the old base's hash and is ignored).
    public static void Save(VolumeIndex index, string path)
    {
        var bytes = Serialize(index);
        WriteAtomically(bytes, path);
        index.BaseHash = bytes[^HashLength..];
        index.Dirty.Clear();
        File.Delete(DeltaPathFor(path));
    }

    // Writes only the entries changed since the base file (index.Dirty); the base file is not touched.
    public static void SaveDelta(VolumeIndex index, string path)
    {
        if (index.BaseHash is null) throw new InvalidOperationException("A delta needs a base file.");
        WriteAtomically(SerializeDelta(index), DeltaPathFor(path));
    }

    static void WriteAtomically(byte[] bytes, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
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
        VolumeIndex index;
        try
        {
            index = ReadFile(path);
        }
        catch (InvalidDataException exception)
        {
            problem = $"the saved index cannot be used: {exception.Message}";
            return null;
        }
        var deltaPath = DeltaPathFor(path);
        if (File.Exists(deltaPath))
        {
            try
            {
                ApplyDelta(index, File.ReadAllBytes(deltaPath));
            }
            catch (InvalidDataException exception)
            {
                // The base file alone is older than the delta's journal position says; it is not used either.
                problem = $"the saved index delta cannot be used: {exception.Message}";
                return null;
            }
        }
        problem = null;
        return index;
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
            foreach (var record in index.Records.Values) WriteRecord(writer, record);
        }
        return WithHash(stream);
    }

    public static byte[] SerializeDelta(VolumeIndex index)
    {
        var removed = new List<ulong>();
        var present = new List<FileRecord>();
        foreach (var number in index.Dirty)
        {
            if (index.Records.TryGetValue(number, out var record)) present.Add(record);
            else removed.Add(number);
        }
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(DeltaMagic);
            writer.Write(DeltaVersion);
            writer.Write(index.BaseHash!);
            writer.Write(index.JournalId);
            writer.Write(index.NextUsn);
            writer.Write(index.WrittenUtc.Ticks);
            writer.Write(removed.Count);
            foreach (var number in removed) writer.Write(number);
            writer.Write(present.Count);
            foreach (var record in present) WriteRecord(writer, record);
        }
        return WithHash(stream);
    }

    static byte[] WithHash(MemoryStream stream)
    {
        var hash = SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
        stream.Write(hash);
        return stream.ToArray();
    }

    //   u64 reference, u16 header sequence, u8 flags (bit 0 = directory), i64 logical size, u16 name count,
    //   and for each name: u64 parent reference, u8 namespace, u16 character count, UTF-16LE characters
    static void WriteRecord(BinaryWriter writer, FileRecord record)
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

    static FileRecord ReadRecord(ref IndexReader reader)
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
        return record;
    }

    static ReadOnlySpan<byte> CheckedBody(byte[] bytes, string what)
    {
        if (bytes.Length < HashLength + 8) throw new InvalidDataException($"The {what} is truncated.");
        var body = bytes.AsSpan(0, bytes.Length - HashLength);
        if (!SHA256.HashData(body).AsSpan().SequenceEqual(bytes.AsSpan(bytes.Length - HashLength)))
            throw new InvalidDataException($"The {what} checksum does not match.");
        return body;
    }

    // The SHA-256 of a large base file is computed on another thread while this one parses (on C:, hashing 112 MiB
    // alone took about 600 ms). The index is returned only if the checksum matches, and a wrong checksum is reported
    // even if parsing failed first, so parsing must survive any damage: it throws only InvalidDataException (anything
    // else is wrapped), and every count is bounded by the bytes that could hold it before anything is allocated for it.
    public static VolumeIndex Read(byte[] bytes) => Read(bytes, null);

    // Reads a base file from disk in 4 MiB pieces; each piece is hashed as soon as it is in memory, so reading the file
    // and hashing it overlap (the array is not zeroed first: only bytes already read are hashed or parsed).
    public static VolumeIndex ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.SequentialScan);
        var length = stream.Length;
        if (length > Array.MaxLength) throw new InvalidDataException("The index file is too large.");
        var bytes = GC.AllocateUninitializedArray<byte>((int)length);
        return Read(bytes, report =>
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, Math.Min(ReadPiece, bytes.Length - offset));
                if (read == 0) throw new InvalidDataException("The index file is truncated.");
                offset += read;
                report(offset);
            }
        });
    }

    const int ReadPiece = 4 << 20;

    // `fill` (null: the bytes are all there) puts the bytes in place in order and reports how many leading bytes are.
    static VolumeIndex Read(byte[] bytes, Action<Action<int>>? fill)
    {
        if (bytes.Length < HashLength + 8) throw new InvalidDataException("The index file is truncated.");
        var bodyLength = bytes.Length - HashLength;
        var filled = new StrongBox<int>(fill is null ? bytes.Length : 0);
        var failed = new StrongBox<bool>();
        using var progress = new SemaphoreSlim(0);
        var hashing = Task.Run(() =>
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var done = 0;
            while (done < bodyLength)
            {
                if (Volatile.Read(ref failed.Value)) return [];
                var available = Math.Min(Volatile.Read(ref filled.Value), bodyLength);
                if (available == done)
                {
                    progress.Wait();
                    continue;
                }
                hash.AppendData(bytes, done, available - done);
                done = available;
            }
            return hash.GetHashAndReset();
        });
        if (fill is not null)
        {
            try
            {
                fill(count =>
                {
                    Volatile.Write(ref filled.Value, count);
                    progress.Release();
                });
            }
            catch
            {
                Volatile.Write(ref failed.Value, true);
                progress.Release();
                hashing.Wait();
                throw;
            }
        }
        VolumeIndex? index = null;
        Exception? parseError = null;
        try
        {
            index = Parse(bytes.AsSpan(0, bodyLength));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            parseError = exception;
        }
        if (!hashing.Result.AsSpan().SequenceEqual(bytes.AsSpan(bodyLength))) throw new InvalidDataException("The index file checksum does not match.");
        if (parseError is InvalidDataException invalid) throw invalid;
        if (parseError is not null) throw new InvalidDataException($"The index file cannot be read: {parseError.Message}", parseError);
        index!.BaseHash = bytes[^HashLength..];
        return index;
    }

    const int MinRecordBytes = 8 + 2 + 1 + 8 + 2;   // a record without names

    static VolumeIndex Parse(ReadOnlySpan<byte> body)
    {
        var reader = new IndexReader(body);
        if (reader.U32() != Magic) throw new InvalidDataException("Not a dirsizer index file.");
        var version = reader.U32();
        if (version != Version) throw new InvalidDataException($"Index format version {version} is not supported (this build reads version {Version}).");
        var identity = new VolumeIdentity(reader.I64(), reader.I32(), reader.I64(), reader.I64());
        var journalId = reader.U64();
        var nextUsn = reader.I64();
        var written = reader.Time();
        var count = reader.I32();
        if (count < 0 || count > (body.Length - reader.Position) / MinRecordBytes) throw new InvalidDataException($"The record count {count} does not fit the file.");
        var records = new Dictionary<ulong, FileRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var record = ReadRecord(ref reader);
            if (!records.TryAdd(record.Reference.RecordNumber, record)) throw new InvalidDataException($"Record {record.Reference.RecordNumber} appears twice.");
        }
        if (reader.Position != body.Length) throw new InvalidDataException("There are bytes after the last record.");
        return new VolumeIndex(identity, journalId, nextUsn, written, records);
    }

    // Applies a delta to the index loaded from its base file. Removals first, then records in ascending order, so a
    // given base and delta always give the same dictionary. A delta of another base changes nothing.
    public static void ApplyDelta(VolumeIndex index, byte[] bytes)
    {
        var body = CheckedBody(bytes, "delta file");
        var reader = new IndexReader(body);
        if (reader.U32() != DeltaMagic) throw new InvalidDataException("Not a dirsizer index delta file.");
        var version = reader.U32();
        if (version != DeltaVersion) throw new InvalidDataException($"Delta format version {version} is not supported (this build reads version {DeltaVersion}).");
        if (!reader.Bytes(HashLength).SequenceEqual(index.BaseHash)) return;
        var journalId = reader.U64();
        var nextUsn = reader.I64();
        var written = reader.Time();
        var dirty = new SortedSet<ulong>();
        var removed = reader.I32();
        if (removed < 0 || removed > (body.Length - reader.Position) / 8) throw new InvalidDataException($"The removed count {removed} does not fit the delta file.");
        var removals = new List<ulong>(removed);
        for (var i = 0; i < removed; i++)
        {
            var number = reader.U64();
            if (!dirty.Add(number)) throw new InvalidDataException($"Record {number} appears twice in the delta.");
            removals.Add(number);
        }
        var count = reader.I32();
        if (count < 0 || count > (body.Length - reader.Position) / MinRecordBytes) throw new InvalidDataException($"The record count {count} does not fit the delta file.");
        var records = new List<FileRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var record = ReadRecord(ref reader);
            if (!dirty.Add(record.Reference.RecordNumber)) throw new InvalidDataException($"Record {record.Reference.RecordNumber} appears twice in the delta.");
            records.Add(record);
        }
        if (reader.Position != body.Length) throw new InvalidDataException("There are bytes after the last record of the delta.");
        foreach (var number in removals) index.Records.Remove(number);
        foreach (var record in records) index.Records[record.Reference.RecordNumber] = record;
        index.JournalId = journalId;
        index.NextUsn = nextUsn;
        index.WrittenUtc = written;
        index.Dirty.UnionWith(dirty);
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
        public ReadOnlySpan<byte> Bytes(int count) => Take(count);

        public DateTime Time()
        {
            var ticks = I64();
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw new InvalidDataException($"The time written ({ticks} ticks) is out of range.");
            return new DateTime(ticks, DateTimeKind.Utc);
        }
        public string Utf16(int characters) => new(MemoryMarshal.Cast<byte, char>(Take(characters * 2)));
    }
}
