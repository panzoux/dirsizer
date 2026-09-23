// The directory model: one node per directory, ids handed out at discovery, sizes rolled up after the walk. No file entries are
// kept here; files only enter the bounded top-N heaps.

sealed class DirNode(int id, int parentId, string name)
{
    public int Id { get; } = id;                 // dense, 0 is the root
    public int ParentId { get; } = parentId;     // -1 for the root; for every other node ParentId < Id
    public string Name { get; } = name;          // the root: the root path as displayed
    public long OwnFileSize;                     // files directly in this directory; written by the one worker that enumerates it
    public long Total;                           // OwnFileSize plus everything below; filled by DirTable.Aggregate
}

readonly record struct FileHit(int DirId, string Name, long Size);

// Keeps the `limit` entries with the largest size. Order among equal sizes is unspecified. Not thread-safe: every worker has its own
// instance and they are merged after the join. Which sizes are eligible at all (for example only files larger than 0) is the
// caller's decision; the item and its size are not tied together, because the caller creates the item (a name string) only after
// WouldAccept has said that it will be kept.
sealed class BoundedTop<T>(int limit)
{
    readonly PriorityQueue<T, long> _queue = new();

    public int Count => _queue.Count;

    // True if an entry of this size would be kept, so the caller can avoid creating the item (for example a name string) if not.
    public bool WouldAccept(long size)
    {
        if (_queue.Count < limit) return true;
        return _queue.TryPeek(out _, out var smallest) && size > smallest;
    }

    public void Add(T item, long size)
    {
        if (!WouldAccept(size)) return;
        _queue.Enqueue(item, size);
        if (_queue.Count > limit) _queue.Dequeue();
    }

    public void AddAll(BoundedTop<T> other)
    {
        if (ReferenceEquals(other, this)) return;   // Add would change the queue while it is being enumerated
        foreach (var (item, size) in other._queue.UnorderedItems) Add(item, size);
    }

    // Largest first. Empties the heap.
    public T[] ToDescendingArray()
    {
        var result = new T[_queue.Count];
        for (var index = result.Length - 1; index >= 0; index--) result[index] = _queue.Dequeue();
        return result;
    }
}

static class DirTable
{
    // Puts the root and the nodes each worker created into one array indexed by id. Ids are dense and unique by construction; a
    // violation is an internal error.
    public static DirNode[] Build(DirNode root, List<List<DirNode>> created)
    {
        var count = 1;
        foreach (var list in created) count += list.Count;
        var nodes = new DirNode[count];
        nodes[0] = root;
        foreach (var list in created)
        {
            foreach (var node in list)
            {
                if (node.Id <= 0 || node.Id >= count || nodes[node.Id] is not null)
                    throw new InvalidOperationException($"directory id {node.Id} is out of range or used twice (table of {count})");
                nodes[node.Id] = node;
            }
        }
        return nodes;
    }

    // Fills Total for every node. Relies on one property only: every node's parent has a smaller id, so walking the ids downwards
    // visits every child before its parent. Linear, no queue, no recursion.
    public static void Aggregate(DirNode[] nodes)
    {
        if (nodes.Length == 0 || nodes[0].Id != 0 || nodes[0].ParentId != -1) throw new InvalidOperationException("directory table has no root at id 0");
        foreach (var node in nodes) node.Total = node.OwnFileSize;
        for (var id = nodes.Length - 1; id >= 1; id--)
        {
            var node = nodes[id];
            if (node.Id != id || node.ParentId < 0 || node.ParentId >= id)
                throw new InvalidOperationException($"directory table invariant violated at id {id}: parent id {node.ParentId}");
            nodes[node.ParentId].Total += node.Total;
        }
    }

    public static string Combine(string directory, string name) => directory.EndsWith('\\') ? directory + name : directory + "\\" + name;

    // Full display path. Only called for the few directories that are reported.
    public static string PathOf(DirNode[] nodes, int id)
    {
        if (id == 0) return nodes[0].Name;
        return Combine(nodes[0].Name, RelativePath(nodes, id));
    }

    // Names below the root joined with backslashes; empty for the root.
    public static string RelativePath(DirNode[] nodes, int id)
    {
        var names = new Stack<string>();
        for (var current = id; current != 0; current = nodes[current].ParentId)
        {
            // Aggregate has already validated every table that reaches the output; this turns a corrupt table into an error
            // instead of an endless loop.
            if (nodes[current].ParentId < 0 || nodes[current].ParentId >= current)
                throw new InvalidOperationException($"directory table invariant violated at id {current}: parent id {nodes[current].ParentId}");
            names.Push(nodes[current].Name);
        }
        return string.Join('\\', names);
    }
}
