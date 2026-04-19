using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BackupsterAgent.Services.Backup;

public sealed class ContentDefinedChunker
{
    public const int MinSize = 1 * 1024 * 1024;
    public const int AvgSize = 4 * 1024 * 1024;
    public const int MaxSize = 8 * 1024 * 1024;

    private const ulong MaskS = (1UL << 24) - 1;
    private const ulong MaskL = (1UL << 20) - 1;

    private static readonly ulong[] Gear = BuildGearTable();

    public IEnumerable<byte[]> Split(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var buffer = new byte[MaxSize];
        int filled = 0;

        while (true)
        {
            while (filled < MaxSize)
            {
                int read = input.Read(buffer, filled, MaxSize - filled);
                if (read == 0) break;
                filled += read;
            }

            if (filled == 0) yield break;

            int size = FindBoundary(buffer, filled);

            var chunk = new byte[size];
            Buffer.BlockCopy(buffer, 0, chunk, 0, size);
            yield return chunk;

            int remaining = filled - size;
            if (remaining > 0)
                Buffer.BlockCopy(buffer, size, buffer, 0, remaining);
            filled = remaining;
        }
    }

    private static int FindBoundary(byte[] src, int length)
    {
        if (length <= MinSize) return length;

        int n = Math.Min(length, MaxSize);
        int nAvg = Math.Min(n, AvgSize);

        ulong fp = 0;
        int i = MinSize;

        while (i < nAvg)
        {
            fp = (fp << 1) + Gear[src[i]];
            i++;
            if ((fp & MaskS) == 0) return i;
        }

        while (i < n)
        {
            fp = (fp << 1) + Gear[src[i]];
            i++;
            if ((fp & MaskL) == 0) return i;
        }

        return n;
    }

    private static ulong[] BuildGearTable()
    {
        var table = new ulong[256];
        for (int i = 0; i < 256; i++)
        {
            var input = new byte[] { (byte)i };
            var hash = SHA256.HashData(input);
            table[i] = BinaryPrimitives.ReadUInt64LittleEndian(hash);
        }
        return table;
    }
}
