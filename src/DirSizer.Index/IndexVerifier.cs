// Compares an index with a fresh scan record by record: identity, logical size, every name (in order), and the derived
// parent, display name and directory size. Both dictionaries must be aggregated (VolumeIndex.Recompute, or a scan).
// Dictionary order is not compared: after an incremental update it differs from a scan's, and no size depends on it.
sealed record VerifyResult(int Differences, string[] Samples);

static class IndexVerifier
{
    const int MaxSamples = 20;

    public static VerifyResult Compare(Dictionary<ulong, FileRecord> index, Dictionary<ulong, FileRecord> fresh)
    {
        var differences = 0;
        var samples = new List<string>();
        void Add(string text)
        {
            differences++;
            if (samples.Count < MaxSamples) samples.Add(text);
        }

        foreach (var (number, expected) in fresh)
        {
            if (!index.TryGetValue(number, out var actual))
            {
                Add($"record {number} ({expected.DisplayName}): on the volume, missing from the index");
                continue;
            }
            var problem = Describe(expected, actual);
            if (problem is not null) Add($"record {number} ({expected.DisplayName}): {problem}");
        }
        foreach (var (number, actual) in index)
        {
            if (!fresh.ContainsKey(number)) Add($"record {number} ({actual.DisplayName}): in the index, no longer on the volume");
        }
        return new VerifyResult(differences, samples.ToArray());
    }

    static string? Describe(FileRecord expected, FileRecord actual)
    {
        if (expected.Reference != actual.Reference) return $"reference {Ref(expected.Reference)} on the volume, {Ref(actual.Reference)} in the index";
        if (expected.SequenceNumber != actual.SequenceNumber) return $"sequence {expected.SequenceNumber} on the volume, {actual.SequenceNumber} in the index";
        if (expected.IsDirectory != actual.IsDirectory) return $"directory={expected.IsDirectory} on the volume, {actual.IsDirectory} in the index";
        if (expected.LogicalSize != actual.LogicalSize) return $"logical size {expected.LogicalSize} on the volume, {actual.LogicalSize} in the index";
        if (expected.Names.Count != actual.Names.Count) return $"{expected.Names.Count} names on the volume, {actual.Names.Count} in the index";
        for (var i = 0; i < expected.Names.Count; i++)
        {
            if (expected.Names[i] != actual.Names[i])
                return $"name {i} is \"{expected.Names[i].Name}\" (parent {Ref(expected.Names[i].Parent)}) on the volume, \"{actual.Names[i].Name}\" (parent {Ref(actual.Names[i].Parent)}) in the index";
        }
        if (expected.Parent != actual.Parent) return $"parent {Ref(expected.Parent)} on the volume, {Ref(actual.Parent)} in the index";
        if (expected.DisplayName != actual.DisplayName) return $"display name \"{expected.DisplayName}\" on the volume, \"{actual.DisplayName}\" in the index";
        if (expected.Size != actual.Size) return $"directory size {expected.Size} on the volume, {actual.Size} in the index";
        return null;
    }

    static string Ref(FileRef reference) => $"{reference.RecordNumber}:{reference.SequenceNumber}";
}
