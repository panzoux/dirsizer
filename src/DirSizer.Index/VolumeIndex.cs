// The persistent index of one NTFS volume (docs\design_index.md). Records holds the merged MFT records exactly as a
// scan's RecordMerger leaves them -- the input of relationship resolution and aggregation -- so a loaded index goes
// through the same shared stages as a fresh scan. Size, Parent and DisplayName are derived and never stored.

// What must be unchanged for a saved index to describe this volume at all.
sealed record VolumeIdentity(long SerialNumber, int RecordSize, long BytesPerCluster, long MftStartLcn)
{
    public static VolumeIdentity From(BulkNative.VolumeData data) => new(data.SerialNumber, data.RecordSize, data.BytesPerCluster, data.MftStartLcn);
}

// JournalId 0 means the volume had no active USN journal when the index was written: it cannot be brought up to date
// incrementally. NextUsn is the journal position the index is known to be current up to; later changes may or may not
// be in it already, and applying them again is harmless (IndexUpdater reads the current state from the MFT).
sealed class VolumeIndex(VolumeIdentity identity, ulong journalId, long nextUsn, DateTime writtenUtc, Dictionary<ulong, FileRecord> records)
{
    public VolumeIdentity Identity { get; } = identity;
    public ulong JournalId { get; set; } = journalId;
    public long NextUsn { get; set; } = nextUsn;
    public DateTime WrittenUtc { get; set; } = writtenUtc;
    public Dictionary<ulong, FileRecord> Records { get; } = records;
    public RelationshipResult? Relationships { get; set; }

    // Resolves parents and names and aggregates directory sizes from scratch, with the shared stages a scan uses.
    public void Recompute()
    {
        foreach (var record in Records.Values)
        {
            record.Size = 0;
            record.Parent = default;
            record.DisplayName = null;
        }
        Relationships = RelationshipResolver.Resolve(Records);
        SizeAggregator.AddFileSizesToParents(Records);
        SizeAggregator.AggregateDirectories(Records);
    }
}
