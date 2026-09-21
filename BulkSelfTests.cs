using System.Buffers.Binary;

static class BulkSelfTests
{
    public static void Run()
    {
        ValidRecordIsFixedUp();
        WrongUpdateSequenceFails();
        TruncatedUsaFails();
        InvalidUsaOffsetFails();
        InvalidUsaSizeFails();
        ZeroedSlotIsUnused();
        WrongSignatureIsBadSignature();
        FreedRecordIsDeletedNotMalformed();
        FreedDirectoryRecordIsDeleted();
        DeletedRecordIsNotFixedUp();
        InUseRecordWithBadUsaIsFixupFailure();
        InUseRecordIsFixedUp();
        BlocksSplitAtExtentBoundaries();
        BlocksCoverEveryRecordOnce();
        UncoveredMftRangeFails();
        UnalignedOffsetKeepsItsPositionInsideTheCluster();
        UnalignedReadThatSpillsPastTheExtentIsRejected();
        ExtentMapContinuesAfterMoreData();
        ExtentMapInOneCallStillWorks();
        MoreDataWithoutProgressFails();
        OtherIoctlErrorFails();
        TruncatedExtentResponseFails();
        IdenticalLayoutsAreStable();
        DifferentVolumeIsDetected();
        GrowthInsideLastExtentIsGrowthNotRelocation();
        GrowthByNewExtentIsGrowthNotRelocation();
        ShrinkIsDetected();
        RelocatedExtentIsDetected();
        SplitExtentWithSamePhysicalClustersIsStable();
        Console.WriteLine("P3 USA self-tests passed.");
    }

    // 12 MiB extent followed by an 8 MiB extent: the 8 MiB block size would straddle the first boundary at 12 MiB.
    static List<BulkNative.MftExtent> TwoExtents() => [new(0, 3072, 1000), new(3072, 5120, 90000)];

    static void BlocksSplitAtExtentBoundaries()
    {
        var blocks = BulkScan.PlanBlocks(20L * 1024 * 1024, 1024, 4096, TwoExtents());
        Assert(blocks.Count == 3, $"expected 3 blocks, got {blocks.Count}");
        Assert(blocks[0] == (0L, 8 * 1024 * 1024), "first block should be a full 8 MiB block");
        Assert(blocks[1] == (8L * 1024 * 1024, 4 * 1024 * 1024), "second block should stop at the 12 MiB extent boundary");
        Assert(blocks[2] == (12L * 1024 * 1024, 8 * 1024 * 1024), "third block should start at the second extent");
    }

    static void BlocksCoverEveryRecordOnce()
    {
        var blocks = BulkScan.PlanBlocks(20L * 1024 * 1024, 1024, 4096, TwoExtents());
        var next = 0L;
        foreach (var block in blocks)
        {
            Assert(block.LogicalOffset == next, "blocks must be contiguous and non-overlapping");
            Assert(block.Length > 0 && block.Length % 1024 == 0, "blocks must be a whole number of records");
            next += block.Length;
        }
        Assert(next == 20L * 1024 * 1024, "blocks must cover the whole captured MFT range");
    }

    static void UncoveredMftRangeFails()
    {
        var threw = false;
        try { BulkScan.PlanBlocks(24L * 1024 * 1024, 1024, 4096, TwoExtents()); }
        catch (IOException) { threw = true; }
        Assert(threw, "an MFT range beyond the extent map must be rejected");
    }

    // A fake FSCTL_GET_RETRIEVAL_POINTERS: each response holds StartingVcn and (NextVcn, Lcn) pairs.
    static BulkNative.RetrievalResponse Response(int error, long startVcn, params (long Next, long Lcn)[] extents)
    {
        var output = new byte[16 + extents.Length * 16];
        BinaryPrimitives.WriteUInt32LittleEndian(output, (uint)extents.Length);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(8), startVcn);
        for (var index = 0; index < extents.Length; index++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(16 + index * 16), extents[index].Next);
            BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(24 + index * 16), extents[index].Lcn);
        }
        return new BulkNative.RetrievalResponse(error, output, output.Length);
    }

    static void ExtentMapContinuesAfterMoreData()
    {
        var requested = new List<long>();
        BulkNative.RetrievalFetch fetch = start =>
        {
            requested.Add(start);
            return start switch
            {
                0 => Response(BulkConstants.ErrorMoreData, 0, (100, 1000), (200, 2000)),
                200 => Response(BulkConstants.ErrorMoreData, 200, (300, 3000)),
                300 => Response(0, 300, (400, 4000), (500, 5000)),
                _ => throw new InvalidOperationException($"unexpected start VCN {start}"),
            };
        };
        var extents = BulkNative.ReadMftExtents(fetch, 4096, 500L * 4096);
        Assert(extents.Count == 5, $"expected 5 extents, got {extents.Count}");
        Assert(extents[0] == new BulkNative.MftExtent(0, 100, 1000) && extents[4] == new BulkNative.MftExtent(400, 500, 5000), "extent boundaries were not preserved across calls");
        Assert(requested.Count == 3 && requested[1] == 200 && requested[2] == 300, "each call must continue at the previous NextVcn");
    }

    static void ExtentMapInOneCallStillWorks()
    {
        var extents = BulkNative.ReadMftExtents(start => Response(0, start, (100, 1000), (200, 2000)), 4096, 200L * 4096);
        Assert(extents.Count == 2, "a complete single response must be accepted");
    }

    static void MoreDataWithoutProgressFails()
    {
        var threw = false;
        try { BulkNative.ReadMftExtents(start => Response(BulkConstants.ErrorMoreData, start), 4096, 100L * 4096); }
        catch (IOException) { threw = true; }
        Assert(threw, "a MORE_DATA response with no extents must fail instead of looping forever");
    }

    static void OtherIoctlErrorFails()
    {
        var threw = false;
        try { BulkNative.ReadMftExtents(start => new BulkNative.RetrievalResponse(5, new byte[16], 0), 4096, 100L * 4096); }
        catch (System.ComponentModel.Win32Exception) { threw = true; }
        Assert(threw, "an unexpected IOCTL error must not be treated as a partial map");
    }

    static void TruncatedExtentResponseFails()
    {
        var threw = false;
        var response = Response(0, 0, (100, 1000), (200, 2000));
        try { BulkNative.ReadMftExtents(start => response with { Returned = 16 + 16 }, 4096, 200L * 4096); }
        catch (IOException) { threw = true; }
        Assert(threw, "a response shorter than its extent count must be rejected");
    }

    // MFT layout comparison: 4 KiB clusters, 1 KiB records, an MFT of 100 clusters in two extents.
    static MftLayout Layout(long validLength = 100 * 4096, long serial = 0x1234, params BulkNative.MftExtent[] extents) =>
        new(new BulkNative.VolumeData(validLength, 1024, 512, 4096, serial, extents.Length == 0 ? 1000 : extents[0].LcnStart),
            extents.Length == 0 ? [new(0, 60, 1000), new(60, 100, 5000)] : [.. extents]);

    static void IdenticalLayoutsAreStable() =>
        Assert(MftLayout.Compare(Layout(), Layout()) == LayoutChange.None, "identical layouts must be stable");

    static void DifferentVolumeIsDetected() =>
        Assert(MftLayout.Compare(Layout(), Layout(serial: 0x9999)).HasFlag(LayoutChange.Volume), "a different volume serial must be flagged");

    static void GrowthInsideLastExtentIsGrowthNotRelocation()
    {
        var end = Layout(104 * 4096, 0x1234, new(0, 60, 1000), new(60, 104, 5000));
        Assert(MftLayout.Compare(Layout(), end) == LayoutChange.Grew, "growth by extending the last extent must be reported as Grew only");
    }

    static void GrowthByNewExtentIsGrowthNotRelocation()
    {
        var end = Layout(120 * 4096, 0x1234, new(0, 60, 1000), new(60, 100, 5000), new(100, 120, 9000));
        Assert(MftLayout.Compare(Layout(), end) == LayoutChange.Grew, "growth by a new extent must be reported as Grew only");
    }

    static void ShrinkIsDetected() =>
        Assert(MftLayout.Compare(Layout(), Layout(90 * 4096, 0x1234, new(0, 60, 1000), new(60, 100, 5000))).HasFlag(LayoutChange.Shrank), "a shorter MFT must be flagged");

    static void RelocatedExtentIsDetected()
    {
        var end = Layout(100 * 4096, 0x1234, new(0, 60, 1000), new(60, 100, 7777));
        Assert(MftLayout.Compare(Layout(), end).HasFlag(LayoutChange.ExtentsMoved), "an extent that moved within the captured range must be flagged");
    }

    static void SplitExtentWithSamePhysicalClustersIsStable()
    {
        // The same clusters described as three extents (as after a merge or split) are not a relocation.
        var end = Layout(100 * 4096, 0x1234, new(0, 30, 1000), new(30, 60, 1030), new(60, 100, 5000));
        Assert(MftLayout.Compare(Layout(), end) == LayoutChange.None, "the same physical mapping split differently must be stable");
    }

    // Reading a single 1 KiB record puts the offset in the middle of a 4 KiB cluster. The physical offset must keep that
    // position (an aligned-only mapping silently returned the start of the cluster, i.e. a different record).
    static void UnalignedOffsetKeepsItsPositionInsideTheCluster()
    {
        var extents = new List<BulkNative.MftExtent> { new(0, 3072, 1000) };
        var index = 0;
        var physical = BulkNative.MapLogicalToPhysical(5 * 1024, 1024, 4096, extents, ref index);
        Assert(physical == (1000L + 1) * 4096 + 1024, $"record 5 (offset 5,120) must map to cluster 1001 plus 1,024 bytes, got {physical}");
        index = 0;
        Assert(BulkNative.MapLogicalToPhysical(8 * 4096, 4096, 4096, extents, ref index) == (1000L + 8) * 4096, "an aligned offset must still map to the start of its cluster");
    }

    static void UnalignedReadThatSpillsPastTheExtentIsRejected()
    {
        var extents = new List<BulkNative.MftExtent> { new(0, 2, 1000) };
        var index = 0;
        var threw = false;
        try { BulkNative.MapLogicalToPhysical(2 * 4096 - 512, 1024, 4096, extents, ref index); }
        catch (IOException) { threw = true; }
        Assert(threw, "a read that starts inside the last cluster of an extent and runs past its end must be rejected");
    }

    static void ZeroedSlotIsUnused() =>
        Assert(BulkScan.Classify(new byte[1024], 512) == SlotKind.Unused, "zeroed slot was not classified unused");

    static void WrongSignatureIsBadSignature()
    {
        var record = Fixture.CreateFile(flags: 1);
        record[0] = (byte)'B';
        Assert(BulkScan.Classify(record, 512) == SlotKind.BadSignature, "non-FILE record was not classified as bad signature");
    }

    static void FreedRecordIsDeletedNotMalformed() =>
        Assert(BulkScan.Classify(Fixture.CreateFile(flags: 0), 512) == SlotKind.Deleted, "freed file record was not classified deleted");

    static void FreedDirectoryRecordIsDeleted() =>
        Assert(BulkScan.Classify(Fixture.CreateFile(flags: 2), 512) == SlotKind.Deleted, "freed directory record was not classified deleted");

    static void DeletedRecordIsNotFixedUp()
    {
        // A freed slot with a stale update sequence must be deleted, not a USA failure, and must be left untouched.
        var record = Fixture.CreateFile(flags: 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), 0xBBBB);
        Assert(BulkScan.Classify(record, 512) == SlotKind.Deleted, "freed record with stale USA was not classified deleted");
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)) == 0xBBBB, "freed record was modified by fixup");
    }

    static void InUseRecordWithBadUsaIsFixupFailure()
    {
        var record = Fixture.CreateFile(flags: 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), 0xBBBB);
        Assert(BulkScan.Classify(record, 512) == SlotKind.FixupFailed, "in-use record with wrong USA was not a fixup failure");
    }

    static void InUseRecordIsFixedUp()
    {
        var record = Fixture.CreateFile(flags: 1);
        Assert(BulkScan.Classify(record, 512) == SlotKind.InUse, "valid in-use record was not classified in use");
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)) == Fixture.SavedFirst, "in-use record was not fixed up");
    }

    static void ValidRecordIsFixedUp()
    {
        var record = Fixture.Create();
        Assert(BulkScan.UsaFixup(record, 512), "valid USA record was rejected");
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)) == Fixture.SavedFirst, "first sector tail was not restored");
        Assert(BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022)) == Fixture.SavedSecond, "second sector tail was not restored");
    }

    static void WrongUpdateSequenceFails()
    {
        var record = Fixture.Create();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), 0xBBBB);
        Assert(!BulkScan.UsaFixup(record, 512), "wrong USA sequence was accepted");
    }

    static void TruncatedUsaFails()
    {
        var record = Fixture.Create();
        Assert(!BulkScan.UsaFixup(record.AsSpan(0, 40), 512), "truncated USA was accepted");
    }

    static void InvalidUsaOffsetFails()
    {
        var record = Fixture.Create();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 1020);
        Assert(!BulkScan.UsaFixup(record, 512), "invalid USA offset was accepted");
    }

    static void InvalidUsaSizeFails()
    {
        var record = Fixture.Create();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 2);
        Assert(!BulkScan.UsaFixup(record, 512), "invalid USA size was accepted");
    }

    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static class Fixture
    {
        public const ushort SavedFirst = 0x1111;
        public const ushort SavedSecond = 0x2222;

        public static byte[] CreateFile(ushort flags)
        {
            var record = Create();
            record[0] = (byte)'F';
            record[1] = (byte)'I';
            record[2] = (byte)'L';
            record[3] = (byte)'E';
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), flags);
            return record;
        }

        public static byte[] Create()
        {
            var record = new byte[1024];
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 40);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(40), 0xAAAA);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(42), SavedFirst);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(44), SavedSecond);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), 0xAAAA);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022), 0xAAAA);
            return record;
        }
    }
}
