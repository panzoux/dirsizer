using System.Buffers.Binary;

// Decodes the mapping pairs (run list) of a non-resident attribute. Pure and bounds-checked: a damaged run list yields
// null instead of an exception. Used by dirsizer-inspect to read a non-resident $ATTRIBUTE_LIST.
static class RunList
{
    // Returns the runs in order as (first LCN, cluster count); LCN is -1 for a sparse run (no clusters on disk).
    public static List<(long Lcn, long Clusters)>? Decode(byte[] record, int attributeOffset)
    {
        if (attributeOffset < 0 || attributeOffset + 34 > record.Length) return null;
        var attributeLength = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attributeOffset + 4));
        var end = (int)Math.Min(record.Length, attributeOffset + (long)attributeLength);
        var position = attributeOffset + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attributeOffset + 32));
        var runs = new List<(long, long)>();
        long lcn = 0;
        while (position < end && record[position] != 0)
        {
            var lengthBytes = record[position] & 0x0F;
            var offsetBytes = record[position] >> 4;
            if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8 || position + 1 + lengthBytes + offsetBytes > end) return null;
            long clusters = 0;
            for (var i = 0; i < lengthBytes; i++) clusters |= (long)record[position + 1 + i] << (8 * i);
            if (clusters <= 0) return null;
            if (offsetBytes == 0) runs.Add((-1, clusters));
            else
            {
                long delta = 0;
                for (var i = 0; i < offsetBytes; i++) delta |= (long)record[position + 1 + lengthBytes + i] << (8 * i);
                if ((record[position + lengthBytes + offsetBytes] & 0x80) != 0 && offsetBytes < 8) delta -= 1L << (8 * offsetBytes);   // the offset is signed
                lcn += delta;
                if (lcn < 0) return null;
                runs.Add((lcn, clusters));
            }
            position += 1 + lengthBytes + offsetBytes;
            if (runs.Count > 4096) return null;
        }
        return runs;
    }
}
