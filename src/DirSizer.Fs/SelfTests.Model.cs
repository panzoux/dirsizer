// The directory model on synthetic data: no file system involved.
static partial class FsSelfTests
{
    static partial void AddModelTests(List<SelfTest> tests)
    {
        tests.Add(new("aggregation rolls sizes up to the root", AggregationRollsUp));
        tests.Add(new("aggregation rejects a parent id that is not smaller", AggregationRejectsBadParent));
        tests.Add(new("table build rejects a duplicated id", TableRejectsDuplicateId));
        tests.Add(new("bounded top-N keeps the largest, and merges", BoundedTopKeepsLargest));
        tests.Add(new("paths are built from the root name and node names", PathsFromNodes));
    }

    // 0 root(own 1) -> 1 a(own 2) -> 2 b(own 4);  0 -> 3 c(own 8)
    static DirNode[] SyntheticNodes()
    {
        var nodes = new DirNode[]
        {
            new(0, -1, @"C:\r"),
            new(1, 0, "a"),
            new(2, 1, "b"),
            new(3, 0, "c"),
        };
        nodes[0].OwnFileSize = 1;
        nodes[1].OwnFileSize = 2;
        nodes[2].OwnFileSize = 4;
        nodes[3].OwnFileSize = 8;
        return nodes;
    }

    static void AggregationRollsUp()
    {
        var nodes = SyntheticNodes();
        DirTable.Aggregate(nodes);
        AssertEqual(4L, nodes[2].Total, "b");
        AssertEqual(6L, nodes[1].Total, "a = own 2 + b 4");
        AssertEqual(8L, nodes[3].Total, "c");
        AssertEqual(15L, nodes[0].Total, "root = own 1 + a 6 + c 8");

        // The commonest real input: a table with only the root (an empty directory), through Build and then Aggregate.
        var alone = DirTable.Build(new DirNode(0, -1, @"C:\empty"), []);
        AssertEqual(1, alone.Length, "Build with no other nodes");
        alone[0].OwnFileSize = 7;
        DirTable.Aggregate(alone);
        AssertEqual(7L, alone[0].Total, "a table with only the root");
    }

    static void AggregationRejectsBadParent()
    {
        var nodes = SyntheticNodes();
        nodes[2] = new DirNode(2, 3, "b");   // parent id 3 is larger than the node's own id 2
        AssertThrows<InvalidOperationException>(() => DirTable.Aggregate(nodes), "parent id larger than id");
        var selfParent = SyntheticNodes();
        selfParent[1] = new DirNode(1, 1, "a");
        AssertThrows<InvalidOperationException>(() => DirTable.Aggregate(selfParent), "node that is its own parent");
        AssertThrows<InvalidOperationException>(() => DirTable.Aggregate([]), "an empty table");
        var rootWithParent = SyntheticNodes();
        rootWithParent[0] = new DirNode(0, 3, @"C:\r");
        AssertThrows<InvalidOperationException>(() => DirTable.Aggregate(rootWithParent), "a root that has a parent");
        var wrongId = SyntheticNodes();
        wrongId[2] = new DirNode(9, 1, "b");
        AssertThrows<InvalidOperationException>(() => DirTable.Aggregate(wrongId), "a node whose id is not its index");
    }

    static void TableRejectsDuplicateId()
    {
        var root = new DirNode(0, -1, @"C:\r");
        var first = new List<DirNode> { new(1, 0, "a") };
        var second = new List<DirNode> { new(1, 0, "b") };
        AssertThrows<InvalidOperationException>(() => DirTable.Build(root, [first, second]), "duplicate id");
        var outOfRange = new List<DirNode> { new(5, 0, "a") };
        AssertThrows<InvalidOperationException>(() => DirTable.Build(root, [outOfRange]), "id beyond the table");
        var secondRoot = new List<DirNode> { new(0, 0, "x") };
        AssertThrows<InvalidOperationException>(() => DirTable.Build(root, [secondRoot]), "a second node with id 0");
        var ok = DirTable.Build(root, [new List<DirNode> { new(2, 0, "b") }, new List<DirNode> { new(1, 0, "a") }]);
        AssertEqual("a", ok[1].Name, "nodes are placed by id, whatever list they came from");
        AssertEqual("b", ok[2].Name, "nodes are placed by id, whatever list they came from");
    }

    static void BoundedTopKeepsLargest()
    {
        var top = new BoundedTop<string>(3);
        var sizes = new long[] { 5, 1, 9, 7, 3, 8 };
        foreach (var size in sizes) top.Add("n" + size, size);
        AssertEqual(3, top.Count, "bounded");
        Assert(!top.WouldAccept(7), "a size equal to the smallest kept is not accepted");
        Assert(top.WouldAccept(8), "a size above the smallest kept is accepted");
        var other = new BoundedTop<string>(3);
        other.Add("n10", 10);
        other.Add("n2", 2);
        top.AddAll(other);
        AssertEqual("n10,n9,n8", string.Join(',', top.ToDescendingArray()), "merged, largest first");
        AssertEqual(0, top.Count, "ToDescendingArray empties the heap");

        var roomy = new BoundedTop<string>(5);
        roomy.Add("zero", 0);
        Assert(roomy.WouldAccept(0), "a heap that is not full accepts any size");
        var none = new BoundedTop<string>(0);
        none.Add("x", 100);
        AssertEqual(0, none.Count, "a limit of 0 keeps nothing");
        var same = new BoundedTop<string>(2);
        same.Add("a", 1);
        same.Add("b", 2);
        same.AddAll(same);
        AssertEqual("b,a", string.Join(',', same.ToDescendingArray()), "merging a heap into itself changes nothing");

        // Against a sorted-list oracle, with a fixed seed and many ties (sizes 0-5).
        var random = new Random(12345);
        for (var round = 0; round < 500; round++)
        {
            var limit = random.Next(1, 8);
            var count = random.Next(0, 30);
            var heap = new BoundedTop<FileHit>(limit);
            var all = new List<long>();
            for (var index = 0; index < count; index++)
            {
                var size = (long)random.Next(0, 6);
                all.Add(size);
                heap.Add(new FileHit(0, "n" + index, size), size);
            }
            all.Sort();
            all.Reverse();
            var expected = new List<long>();
            for (var index = 0; index < Math.Min(limit, all.Count); index++) expected.Add(all[index]);
            var actual = new List<long>();
            foreach (var hit in heap.ToDescendingArray()) actual.Add(hit.Size);
            AssertEqual(string.Join(',', expected), string.Join(',', actual), $"round {round} (limit {limit}, {count} entries): kept sizes, largest first");
        }
    }

    static void PathsFromNodes()
    {
        var nodes = SyntheticNodes();
        AssertEqual(@"C:\r", DirTable.PathOf(nodes, 0), "root");
        AssertEqual(@"C:\r\a\b", DirTable.PathOf(nodes, 2), "nested");
        AssertEqual(@"a\b", DirTable.RelativePath(nodes, 2), "relative");
        AssertEqual("", DirTable.RelativePath(nodes, 0), "relative root");
        var driveRoot = new DirNode[] { new(0, -1, @"C:\"), new(1, 0, "x") };
        AssertEqual(@"C:\x", DirTable.PathOf(driveRoot, 1), "child of a drive root has no doubled backslash");
        AssertEqual(@"C:\x\f.bin", DirTable.Combine(DirTable.PathOf(driveRoot, 1), "f.bin"), "file path");
        var share = new DirNode[] { new(0, -1, @"\\server\share"), new(1, 0, "x") };
        AssertEqual(@"\\server\share\x", DirTable.PathOf(share, 1), "child of a share root");
        var corrupt = SyntheticNodes();
        corrupt[1] = new DirNode(1, 2, "a");   // parent id 2 is larger than 1: a loop between 1 and 2
        AssertThrows<InvalidOperationException>(() => DirTable.RelativePath(corrupt, 2), "a corrupt table gives an error, not an endless loop");
    }
}
