namespace WTreeModern.Storage;

internal static class Crc32
{
    private static readonly uint[] Table = new uint[256];
    private const uint Polynomial = 0xEDB88320;

    static Crc32()
    {
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            Table[i] = crc;
        }
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return ~crc;
    }
}
