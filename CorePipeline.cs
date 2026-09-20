// Reader-independent processing stages shared by the FSCTL reference reader and the raw bulk reader.
// These were extracted from the reference scanner without changing behaviour.

public static class RecordMerger
{
    // Folds one parsed MFT record into the record dictionary. Extension records are merged into their base record.
    public static void Merge(Dictionary<ulong, FileRecord> records, ParsedRecord parsed)
    {
        var key = parsed.BaseReference.RecordNumber == 0 ? parsed.Reference.RecordNumber : parsed.BaseReference.RecordNumber;
        if (!records.TryGetValue(key, out var record))
        {
            record = new FileRecord(parsed.BaseReference.RecordNumber == 0 ? parsed.Reference : parsed.BaseReference, parsed.HeaderSequenceNumber, parsed.IsDirectory);
            records.Add(key, record);
        }
        if (parsed.BaseReference.RecordNumber == 0 || parsed.Reference.RecordNumber == key)
        {
            record.Reference = parsed.Reference;
            record.SequenceNumber = parsed.HeaderSequenceNumber;
            record.IsDirectory = parsed.IsDirectory;
        }
        record.LogicalSize = Math.Max(record.LogicalSize, parsed.LogicalSize);
        record.Names.AddRange(parsed.Names);
    }
}

public enum RelationshipKind { Exact, FallbackZeroSequence, FallbackMismatch, Unresolved }

public sealed record UnresolvedRecord(FileRef Reference, bool IsDirectory, long LogicalSize, int NameCount, FileRef Parent);

public sealed record RelationshipResult(FileRecord Root, int Exact, int FallbackZeroSequence, int FallbackMismatch, int Unresolved, string[] Samples, UnresolvedRecord[] UnresolvedRecords)
{
    public int Fallback => FallbackZeroSequence + FallbackMismatch;
}

public static class RecordLookup
{
    public static bool TryGetDirectory(Dictionary<ulong, FileRecord> records, FileRef reference, out FileRecord directory) =>
        records.TryGetValue(reference.RecordNumber, out directory!) &&
        directory.IsDirectory;
}

public static class RelationshipResolver
{
    // Selects one parent/name pair per record (from the same $FILE_NAME) and sets Parent and DisplayName.
    // Record 5 is the root and is never its own child.
    public static RelationshipResult Resolve(Dictionary<ulong, FileRecord> records)
    {
        var exactRelationships = 0;
        var fallbackZeroSequenceRelationships = 0;
        var fallbackMismatchRelationships = 0;
        var unresolvedRelationships = 0;
        var relationshipSamples = new List<string>();
        var unresolvedRecords = new List<UnresolvedRecord>();

        FileRecord? rootRecord = null;
        foreach (var record in records.Values)
        {
            if (record.Reference.RecordNumber == 5)
            {
                rootRecord = record;
                break;
            }
        }
        if (rootRecord is null) throw new IOException("NTFS root directory record was not found.");

        foreach (var record in records.Values)
        {
            if (record.Reference.RecordNumber == 5)
            {
                record.Parent = default;
                continue;
            }
            var name = SelectName(record.Reference, records, record.Names, out var relationshipKind, out var mismatchSample);
            if (mismatchSample is not null && relationshipSamples.Count < 20) relationshipSamples.Add(mismatchSample);
            if (name is not null)
            {
                record.Parent = name.Parent;
                record.DisplayName = name.Name;
                if (relationshipKind == RelationshipKind.Exact) exactRelationships++;
                else if (relationshipKind == RelationshipKind.FallbackZeroSequence) fallbackZeroSequenceRelationships++;
                else fallbackMismatchRelationships++;
            }
            else
            {
                unresolvedRelationships++;
                unresolvedRecords.Add(new UnresolvedRecord(record.Reference, record.IsDirectory, record.LogicalSize, record.Names.Count, record.Names.Count == 0 ? default : record.Names[0].Parent));
            }
        }
        return new RelationshipResult(rootRecord, exactRelationships, fallbackZeroSequenceRelationships, fallbackMismatchRelationships, unresolvedRelationships, relationshipSamples.ToArray(), unresolvedRecords.ToArray());
    }

    static int NamePriority(byte nameSpace) => nameSpace is 1 or 3 ? 2 : nameSpace == 0 ? 1 : 0;

    static FileName? SelectName(FileRef childReference, Dictionary<ulong, FileRecord> records, List<FileName> names, out RelationshipKind kind, out string? mismatchSample)
    {
        FileName? best = null;
        var bestScore = -1;
        kind = RelationshipKind.Unresolved;
        mismatchSample = null;
        foreach (var name in names)
        {
            if (!RecordLookup.TryGetDirectory(records, name.Parent, out var parent)) continue;
            var exact = name.Parent.SequenceNumber != 0 && name.Parent.SequenceNumber == parent.SequenceNumber;
            if (!exact && mismatchSample is null)
                mismatchSample = $"child={name.Name} child_ref={childReference.RecordNumber}:{childReference.SequenceNumber} parent_ref={name.Parent.RecordNumber}:{name.Parent.SequenceNumber} parent_a={name.Parent.SequenceNumber} parent_b={parent.SequenceNumber}";
            var score = (exact ? 2 : 0) + NamePriority(name.Namespace);
            if (score <= bestScore) continue;
            best = name;
            bestScore = score;
            kind = exact
                ? RelationshipKind.Exact
                : name.Parent.SequenceNumber == 0
                    ? RelationshipKind.FallbackZeroSequence
                    : RelationshipKind.FallbackMismatch;
        }
        return best;
    }
}

public static class SizeAggregator
{
    // Adds each file's logical size to the directory named by its selected parent.
    public static void AddFileSizesToParents(Dictionary<ulong, FileRecord> records)
    {
        foreach (var record in records.Values)
        {
            if (record.IsDirectory) continue;
            if (RecordLookup.TryGetDirectory(records, record.Parent, out var parent))
                parent.Size += record.LogicalSize;
        }
    }

    // Iterative leaf-to-root aggregation: each directory's size (its files plus its subdirectories) is added to its parent.
    public static void AggregateDirectories(Dictionary<ulong, FileRecord> records)
    {
        var remainingChildren = new Dictionary<ulong, int>();
        foreach (var record in records.Values)
        {
            if (record.IsDirectory) remainingChildren.Add(record.Reference.RecordNumber, 0);
        }
        foreach (var directory in records.Values)
        {
            if (!directory.IsDirectory || directory.Parent.RecordNumber == 0) continue;
            if (RecordLookup.TryGetDirectory(records, directory.Parent, out var parent))
                remainingChildren[parent.Reference.RecordNumber]++;
        }
        var leaves = new Queue<FileRecord>();
        foreach (var record in records.Values)
        {
            if (record.IsDirectory && remainingChildren[record.Reference.RecordNumber] == 0) leaves.Enqueue(record);
        }
        while (leaves.Count > 0)
        {
            var directory = leaves.Dequeue();
            if (directory.Parent.RecordNumber == 0 || directory.Parent == directory.Reference) continue;
            if (!RecordLookup.TryGetDirectory(records, directory.Parent, out var parent)) continue;
            parent.Size += directory.Size;
            if (--remainingChildren[parent.Reference.RecordNumber] == 0) leaves.Enqueue(parent);
        }
    }
}

public sealed record ResultCandidates(List<FileRecord> RootChildren, List<FileRecord> Directories, List<FileRecord> Files);

public static class ResultSelector
{
    // Collects the root's direct children (largest first), every directory, and every file with data.
    public static ResultCandidates Collect(Dictionary<ulong, FileRecord> records)
    {
        var rootChildren = new List<FileRecord>();
        foreach (var record in records.Values)
        {
            if (record.Reference.RecordNumber == 5 || record.Parent.RecordNumber != 5) continue;
            rootChildren.Add(record);
        }
        rootChildren.Sort((left, right) => SizeOf(right).CompareTo(SizeOf(left)));
        var directories = new List<FileRecord>();
        var files = new List<FileRecord>();
        foreach (var record in records.Values)
        {
            if (record.IsDirectory) directories.Add(record);
            else if (record.LogicalSize > 0) files.Add(record);
        }
        return new ResultCandidates(rootChildren, directories, files);
    }

    public static long SizeOf(FileRecord record) => record.IsDirectory ? record.Size : record.LogicalSize;

    // Bounded top-N: only the requested number of records is retained, returned largest first.
    public static FileRecord[] SelectTop(IEnumerable<FileRecord> records, int limit, Func<FileRecord, long> size)
    {
        var queue = new PriorityQueue<FileRecord, long>();
        foreach (var record in records)
        {
            queue.Enqueue(record, size(record));
            if (queue.Count > limit) queue.Dequeue();
        }
        var result = new FileRecord[queue.Count];
        for (var index = result.Length - 1; index >= 0; index--)
            result[index] = queue.Dequeue();
        return result;
    }
}

public static class RecordPaths
{
    public static string Build(string volume, Dictionary<ulong, FileRecord> records, FileRecord record)
    {
        var recordNumber = record.Reference.RecordNumber;
        if (recordNumber == 5)
            return volume + "\\";
        if (record.DisplayName is null)
            return NtfsSystemPath(volume, recordNumber) ?? $"{volume}\\[unresolved record {recordNumber}]";

        var names = new Stack<string>();
        var current = record;
        var visited = new HashSet<ulong>();
        while (current.DisplayName is not null && current.Parent.RecordNumber != current.Reference.RecordNumber && visited.Add(current.Reference.RecordNumber))
        {
            names.Push(current.DisplayName);
            if (!records.TryGetValue(current.Parent.RecordNumber, out current!)) break;
        }
        return names.Count == 0
            ? NtfsSystemPath(volume, recordNumber) ?? $"{volume}\\[unresolved record {recordNumber}]"
            : volume + "\\" + string.Join('\\', names);
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
}
