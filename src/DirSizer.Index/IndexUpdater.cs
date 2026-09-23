// Brings a loaded index up to date: every record the USN journal names (plus the NTFS metadata records, which change
// without journal entries) is read again from the MFT and replaces or removes its index entry. The journal only says
// *which* records to look at; their new state always comes from the MFT (docs\design_index.md, roadmap I3). Applying a
// change twice is therefore harmless.
static class IndexUpdater
{
    const ulong FirstUserRecord = 24;   // records 0-23 are reserved for NTFS metadata
    const ulong ExtendDirectory = 11;

    public static UpdateResult Apply(Dictionary<ulong, FileRecord> records, List<UsnChange> changes, SortedSet<ulong> alwaysReread, IRecordSource source)
    {
        var numbers = new SortedSet<ulong>(alwaysReread);
        foreach (var change in changes) numbers.Add(change.RecordNumber);
        var replaced = 0;
        var removed = 0;
        var extensionReads = 0;
        // Descending, like a scan. Each entry depends only on its own MFT records, so the order does not change the result.
        foreach (var number in numbers.Reverse())
        {
            var bytes = source.Read(number);
            var parsed = bytes is null ? null : RecordParser.Parse(number, bytes);
            if (parsed is null || parsed.BaseReference.RecordNumber != 0)
            {
                // Not in use any more, not parseable (a scan skips it too), or now an extension record of another file
                // (that file's own journal entry brings it in): in every case no longer an entry of its own.
                if (records.Remove(number)) removed++;
                continue;
            }
            var extensions = AttributeList.ExtensionRecords(bytes!, number, source);
            if (extensions is null) return Failed($"the attribute list of MFT record {number} could not be read");
            var parts = new List<ParsedRecord> { parsed };
            foreach (var extension in extensions)
            {
                extensionReads++;
                var extensionBytes = source.Read(extension);
                var extensionParsed = extensionBytes is null ? null : RecordParser.Parse(extension, extensionBytes);
                if (extensionParsed is null || extensionParsed.BaseReference.RecordNumber != number)
                    return Failed($"extension record {extension} of MFT record {number} could not be read");
                parts.Add(extensionParsed);
            }
            // The order a full scan meets them in (descending record number), so the names end up in the same order.
            parts.Sort((left, right) => right.Reference.RecordNumber.CompareTo(left.Reference.RecordNumber));
            var merged = new Dictionary<ulong, FileRecord>();
            foreach (var part in parts) RecordMerger.Merge(merged, part);
            records[number] = merged[number];
            replaced++;
        }
        return new UpdateResult(changes.Count, numbers.Count, replaced, removed, extensionReads, null);

        UpdateResult Failed(string reason) => new(changes.Count, numbers.Count, replaced, removed, extensionReads, reason);
    }

    // Records 0-23 and every record in the $Extend tree ($ObjId, $Quota, $Reparse, $UsnJrnl, $RmMetadata...). Their
    // changes, such as $MFT growing, are not written to the USN journal, so they are read again on every update.
    public static SortedSet<ulong> MetadataRecords(Dictionary<ulong, FileRecord> records)
    {
        var result = new SortedSet<ulong>();
        for (ulong number = 0; number < FirstUserRecord; number++) result.Add(number);
        bool added;
        do
        {
            added = false;
            foreach (var record in records.Values)
            {
                if (result.Contains(record.Reference.RecordNumber)) continue;
                foreach (var name in record.Names)
                {
                    var parent = name.Parent.RecordNumber;
                    if (parent == ExtendDirectory || (parent >= FirstUserRecord && result.Contains(parent)))
                    {
                        result.Add(record.Reference.RecordNumber);
                        added = true;
                        break;
                    }
                }
            }
        } while (added);
        return result;
    }
}
