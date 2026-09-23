// Answers a size query for one directory from an aggregated index: the directory itself, its direct children, and the
// largest directories and files at or below it (roadmap I4). The whole volume (record 5) uses the scan's own
// ResultSelector.Collect, so that answer is exactly dirsizer-mft's; any other directory is a walk down the selected parents.
static class SubtreeQuery
{
    public const ulong RootRecord = 5;

    public static UnifiedScanResult Query(Dictionary<ulong, FileRecord> records, string volume, FileRecord root, int top)
    {
        List<FileRecord> directories;
        List<FileRecord> files;
        List<FileRecord> rootChildren;
        long directoryCount = 0;
        long fileCount = 0;
        if (root.Reference.RecordNumber == RootRecord)
        {
            var candidates = ResultSelector.Collect(records);
            (directories, files, rootChildren) = (candidates.Directories, candidates.Files, candidates.RootChildren);
            foreach (var record in records.Values)
            {
                if (record.IsDirectory) directoryCount++;
                else fileCount++;
            }
        }
        else
        {
            directories = [root];
            files = [];
            rootChildren = [];
            directoryCount = 1;
            foreach (var record in Descendants(records, root.Reference.RecordNumber))
            {
                if (record.IsDirectory)
                {
                    directories.Add(record);
                    directoryCount++;
                }
                else
                {
                    fileCount++;
                    if (record.LogicalSize > 0) files.Add(record);
                }
                if (record.Parent.RecordNumber == root.Reference.RecordNumber) rootChildren.Add(record);
            }
            rootChildren.Sort((left, right) => ResultSelector.SizeOf(right).CompareTo(ResultSelector.SizeOf(left)));
        }
        var topDirectories = ResultSelector.SelectTop(directories, top, record => record.Size);
        var topFiles = ResultSelector.SelectTop(files, top, record => record.LogicalSize);
        return new UnifiedScanResult(
            RecordPaths.Build(volume, records, root), top,
            Item(volume, records, root), Items(volume, records, rootChildren), Items(volume, records, topDirectories), Items(volume, records, topFiles),
            directoryCount, 0, fileCount, [], "index", null, null, 0);
    }

    // Every record below `root` (not including it), following each record's selected parent. Unresolved records have
    // no parent and are below nothing. O(records) to build the child lists, then O(subtree).
    public static List<FileRecord> Descendants(Dictionary<ulong, FileRecord> records, ulong root)
    {
        var children = new Dictionary<ulong, List<FileRecord>>();
        foreach (var record in records.Values)
        {
            if (record.Reference.RecordNumber == RootRecord || record.DisplayName is null) continue;
            if (!RecordLookup.TryGetDirectory(records, record.Parent, out _)) continue;
            if (!children.TryGetValue(record.Parent.RecordNumber, out var list)) children[record.Parent.RecordNumber] = list = [];
            list.Add(record);
        }
        var result = new List<FileRecord>();
        var pending = new Stack<ulong>();
        var visited = new HashSet<ulong> { root };
        pending.Push(root);
        while (pending.Count > 0)
        {
            if (!children.TryGetValue(pending.Pop(), out var list)) continue;
            foreach (var child in list)
            {
                result.Add(child);
                if (child.IsDirectory && visited.Add(child.Reference.RecordNumber)) pending.Push(child.Reference.RecordNumber);
            }
        }
        return result;
    }

    static UnifiedItem Item(string volume, Dictionary<ulong, FileRecord> records, FileRecord record) =>
        new(RecordPaths.Build(volume, records, record), ResultSelector.SizeOf(record));

    static UnifiedItem[] Items(string volume, Dictionary<ulong, FileRecord> records, IReadOnlyList<FileRecord> list)
    {
        var items = new UnifiedItem[list.Count];
        for (var i = 0; i < list.Count; i++) items[i] = Item(volume, records, list[i]);
        return items;
    }
}
