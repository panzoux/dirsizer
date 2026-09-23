// "What changed since the previous run" (roadmap I5). The baseline is the saved index, aggregated before the journal's
// changes are applied; only directories are kept. A directory is the same directory only if its record number *and*
// sequence number match, so a reused record counts as one directory gone and one new.
readonly record struct SnapshotEntry(FileRef Reference, long Size, ulong Parent, string? Name);

sealed class IndexSnapshot(DateTime writtenUtc, Dictionary<ulong, SnapshotEntry> directories)
{
    const int MaxDepth = 10000;

    public DateTime WrittenUtc { get; } = writtenUtc;
    public Dictionary<ulong, SnapshotEntry> Directories { get; } = directories;

    // The index must be aggregated (Recompute, or a scan).
    public static IndexSnapshot Capture(VolumeIndex index)
    {
        var directories = new Dictionary<ulong, SnapshotEntry>();
        foreach (var record in index.Records.Values)
        {
            if (record.IsDirectory)
                directories[record.Reference.RecordNumber] = new SnapshotEntry(record.Reference, record.Size, record.Parent.RecordNumber, record.DisplayName);
        }
        return new IndexSnapshot(index.WrittenUtc, directories);
    }

    public bool IsUnder(ulong number, ulong scope)
    {
        var current = number;
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (current == scope) return true;
            if (current == SubtreeQuery.RootRecord || !Directories.TryGetValue(current, out var entry) || entry.Name is null) return false;
            current = entry.Parent;
        }
        return false;
    }

    public string PathOf(string volume, ulong number)
    {
        var names = new Stack<string>();
        var current = number;
        for (var depth = 0; depth < MaxDepth && current != SubtreeQuery.RootRecord; depth++)
        {
            if (!Directories.TryGetValue(current, out var entry) || entry.Name is null) return $"{volume}\\[record {number}]";
            names.Push(entry.Name);
            current = entry.Parent;
        }
        return current == SubtreeQuery.RootRecord ? volume + "\\" + string.Join('\\', names) : $"{volume}\\[record {number}]";
    }
}

readonly record struct DirectoryChange(string Path, long Before, long After)
{
    public long Delta => After - Before;
}

sealed record ChangeReport(DateTime SinceUtc, long RootBefore, long RootAfter, DirectoryChange[] Shrunk, DirectoryChange[] Grew);

static class ChangeReporter
{
    readonly record struct Candidate(ulong Number, bool Gone, long Before, long After)
    {
        public long Delta => After - Before;
    }

    // Compares every directory at or below `scope` now with the same directory in the snapshot. A directory's change
    // includes everything below it, so the parents of a deleted folder are listed too.
    public static ChangeReport Compare(IndexSnapshot before, VolumeIndex after, string volume, FileRecord scope, int top)
    {
        var records = after.Records;
        var scopeNumber = scope.Reference.RecordNumber;
        var present = new HashSet<ulong>();
        var candidates = new List<Candidate>();
        var inScope = new List<FileRecord> { scope };
        foreach (var record in SubtreeQuery.Descendants(records, scopeNumber))
        {
            if (record.IsDirectory) inScope.Add(record);
        }
        foreach (var directory in inScope)
        {
            var number = directory.Reference.RecordNumber;
            present.Add(number);
            var old = SameDirectoryBefore(before, directory, scopeNumber) ? before.Directories[number].Size : 0;
            if (directory.Size != old) candidates.Add(new Candidate(number, false, old, directory.Size));
        }
        foreach (var (number, entry) in before.Directories)
        {
            if (entry.Size == 0 || !before.IsUnder(number, scopeNumber)) continue;
            if (present.Contains(number) && records[number].Reference == entry.Reference) continue;
            candidates.Add(new Candidate(number, true, entry.Size, 0));
        }
        var rootBefore = SameDirectoryBefore(before, scope, scopeNumber) ? before.Directories[scopeNumber].Size : 0;
        return new ChangeReport(before.WrittenUtc, rootBefore, scope.Size,
            Select(candidates, top, shrunk: true, before, records, volume),
            Select(candidates, top, shrunk: false, before, records, volume));
    }

    static bool SameDirectoryBefore(IndexSnapshot before, FileRecord directory, ulong scopeNumber) =>
        before.Directories.TryGetValue(directory.Reference.RecordNumber, out var entry)
        && entry.Reference == directory.Reference
        && before.IsUnder(directory.Reference.RecordNumber, scopeNumber);

    static DirectoryChange[] Select(List<Candidate> candidates, int top, bool shrunk, IndexSnapshot before, Dictionary<ulong, FileRecord> records, string volume)
    {
        var chosen = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            if (shrunk ? candidate.Delta < 0 : candidate.Delta > 0) chosen.Add(candidate);
        }
        // Largest change first; equal changes by record number, so the order is reproducible. Paths are built only for
        // the entries that are shown.
        chosen.Sort((left, right) =>
        {
            var byChange = shrunk ? left.Delta.CompareTo(right.Delta) : right.Delta.CompareTo(left.Delta);
            return byChange != 0 ? byChange : left.Number.CompareTo(right.Number);
        });
        var count = Math.Min(top, chosen.Count);
        var result = new DirectoryChange[count];
        for (var i = 0; i < count; i++)
        {
            var candidate = chosen[i];
            var path = candidate.Gone ? before.PathOf(volume, candidate.Number) : RecordPaths.Build(volume, records, records[candidate.Number]);
            result[i] = new DirectoryChange(path, candidate.Before, candidate.After);
        }
        return result;
    }
}
