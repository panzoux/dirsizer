using Microsoft.Win32.SafeHandles;

// What can change about the MFT while a raw scan is running. A change means the scan read bytes through an extent map
// that no longer describes the file system, or that records appeared beyond the range the scan captured.
[Flags]
enum LayoutChange
{
    None = 0,
    Volume = 1,       // different serial number or geometry: not the volume that was opened
    Grew = 2,         // MftValidDataLength increased: records added after the scan captured its range
    Shrank = 4,       // MftValidDataLength decreased
    ExtentsMoved = 8, // the physical cluster mapping of the captured range changed (for example a defragmentation)
}

// The MFT layout at one instant: volume geometry plus the extent map that covers the captured valid data length.
readonly record struct MftLayout(BulkNative.VolumeData Volume, List<BulkNative.MftExtent> Extents)
{
    public static MftLayout Capture(SafeFileHandle volume, SafeFileHandle mft)
    {
        var metadata = BulkNative.ReadVolumeData(volume);
        return new MftLayout(metadata, BulkNative.ReadMftExtents(mft, metadata.BytesPerCluster, metadata.MftValidDataLength));
    }

    // Compares the layout captured when the scan started with the layout after it finished. Only the captured range is
    // compared, so growth (which appends or lengthens the last extent) is reported as growth, not as relocation.
    public static LayoutChange Compare(MftLayout start, MftLayout end)
    {
        var change = LayoutChange.None;
        var a = start.Volume;
        var b = end.Volume;
        if (a.SerialNumber != b.SerialNumber || a.BytesPerSector != b.BytesPerSector || a.BytesPerCluster != b.BytesPerCluster ||
            a.RecordSize != b.RecordSize || a.MftStartLcn != b.MftStartLcn)
            change |= LayoutChange.Volume;
        if (b.MftValidDataLength > a.MftValidDataLength) change |= LayoutChange.Grew;
        else if (b.MftValidDataLength < a.MftValidDataLength) change |= LayoutChange.Shrank;

        var capturedClusters = (a.MftValidDataLength + a.BytesPerCluster - 1) / a.BytesPerCluster;
        var before = Normalize(start.Extents, capturedClusters);
        var after = Normalize(end.Extents, capturedClusters);
        var same = before.Count == after.Count;
        for (var index = 0; same && index < before.Count; index++) same = before[index] == after[index];
        if (!same) change |= LayoutChange.ExtentsMoved;
        return change;
    }

    // Clips the extent map to the captured range and merges extents that are contiguous both virtually and physically,
    // so the same cluster mapping always compares equal however it happens to be split into extents.
    static List<BulkNative.MftExtent> Normalize(List<BulkNative.MftExtent> extents, long capturedClusters)
    {
        var result = new List<BulkNative.MftExtent>();
        foreach (var extent in extents)
        {
            if (extent.VcnStart >= capturedClusters) break;
            var end = Math.Min(extent.VcnEnd, capturedClusters);
            if (result.Count > 0)
            {
                var previous = result[^1];
                if (previous.VcnEnd == extent.VcnStart && previous.LcnStart + (previous.VcnEnd - previous.VcnStart) == extent.LcnStart)
                {
                    result[^1] = previous with { VcnEnd = end };
                    continue;
                }
            }
            result.Add(new BulkNative.MftExtent(extent.VcnStart, end, extent.LcnStart));
        }
        return result;
    }
}
