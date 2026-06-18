using System.Runtime.CompilerServices;

namespace WTreeModern.Tree;

/// <summary>
/// Einfacher Bloom-Filter mit double-hashed Bit-Array.
/// Seriallisierbar in einen IBlockStore-Block.
/// </summary>
internal sealed class BloomFilter
{
    private readonly byte[] _bits;
    private readonly int _bitCount;
    private readonly int _hashCount;
    private bool _isDirty;

    public bool IsDirty => _isDirty;
    public int BitCount => _bitCount;
    public int HashCount => _hashCount;
    public byte[] GetBits() => _bits;

    public BloomFilter(int expectedItems, double falsePositiveRate)
    {
        if (expectedItems <= 0) throw new ArgumentOutOfRangeException(nameof(expectedItems));
        if (falsePositiveRate <= 0 || falsePositiveRate >= 1) throw new ArgumentOutOfRangeException(nameof(falsePositiveRate));

        double ln2 = Math.Log(2);
        _bitCount = Math.Max(64, (int)Math.Ceiling(-expectedItems * Math.Log(falsePositiveRate) / (ln2 * ln2)));
        _hashCount = Math.Max(1, (int)Math.Round(_bitCount / (double)expectedItems * ln2));
        _bits = new byte[(_bitCount + 7) / 8];
    }

    public BloomFilter(byte[] bits, int bitCount, int hashCount)
    {
        _bits = bits;
        _bitCount = bitCount;
        _hashCount = hashCount;
    }

    public void Add(ReadOnlySpan<byte> item)
    {
        var (h1, h2) = ComputeHashes(item);
        for (int i = 0; i < _hashCount; i++)
        {
            int bit = (int)((h1 + (uint)i * h2) % (uint)_bitCount);
            _bits[bit >> 3] |= (byte)(1 << (bit & 7));
        }
        _isDirty = true;
    }

    public bool MayContain(ReadOnlySpan<byte> item)
    {
        var (h1, h2) = ComputeHashes(item);
        for (int i = 0; i < _hashCount; i++)
        {
            int bit = (int)((h1 + (uint)i * h2) % (uint)_bitCount);
            if ((_bits[bit >> 3] & (byte)(1 << (bit & 7))) == 0)
                return false;
        }
        return true;
    }

    public void MarkClean() => _isDirty = false;

    public void Reset()
    {
        Array.Clear(_bits);
        _isDirty = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (uint h1, uint h2) ComputeHashes(ReadOnlySpan<byte> data)
    {
        // FNV-1a 32-bit mit zwei unterschiedlichen Offsets für double-hashing
        const uint fnvPrime = 16777619;

        uint h1 = 2166136261;
        uint h2 = 0x811C9DC5;

        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            h1 = (h1 ^ b) * fnvPrime;
            h2 = (h2 ^ b) * fnvPrime;
        }

        if (h2 == h1) h2 = ~h1;
        return (h1, h2);
    }
}
