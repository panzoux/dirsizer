// Whether a loaded index may be brought up to date from the journal. Null means yes; otherwise the reason a full scan
// is needed (shown on stderr and in JSON index.rebuild_reason). Order matters: the first failing rule is the reason.
static class IndexValidity
{
    public static string? Check(VolumeIndex index, VolumeIdentity identity, JournalState? journal)
    {
        if (index.Identity != identity) return "the volume is not the one the index was written for (serial number or geometry changed)";
        if (journal is null) return "the volume has no active USN journal";
        if (index.JournalId == 0) return "the index was written without a USN journal";
        if (index.JournalId != journal.Value.JournalId) return "the USN journal was recreated since the index was written";
        if (index.NextUsn < journal.Value.FirstUsn) return "the USN journal no longer holds the saved position (it wrapped)";
        if (index.NextUsn > journal.Value.NextUsn) return "the saved USN position is ahead of the journal";
        return null;
    }
}
