namespace VramRipperdoc;

public static class Crc32
{
    static readonly uint[] Table = Build(0xEDB88320);
    static readonly uint[] TableC = Build(0x82F63B78);
    static uint[] Build(uint poly)
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
    public static uint Compute(ReadOnlySpan<byte> data, uint seed = 0) => Run(Table, data, seed);
    public static uint ComputeC(ReadOnlySpan<byte> data) => Run(TableC, data, 0);
    static uint Run(uint[] t, ReadOnlySpan<byte> data, uint seed)
    {
        uint c = ~seed;
        foreach (var b in data) c = t[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }
}

public static class Crc64
{
    static readonly ulong[] Ecma = Build(0xC96C5795D7870F42); // reflected ECMA-182 (CRC-64/XZ)
    static readonly ulong[] Iso = Build(0xD800000000000000);
    static ulong[] Build(ulong poly)
    {
        var t = new ulong[256];
        for (ulong i = 0; i < 256; i++)
        {
            ulong c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
    public static ulong ComputeEcma(ReadOnlySpan<byte> d, bool invert = true) => Run(Ecma, d, invert);
    public static ulong ComputeIso(ReadOnlySpan<byte> d, bool invert = true) => Run(Iso, d, invert);
    static ulong Run(ulong[] t, ReadOnlySpan<byte> d, bool invert)
    {
        ulong c = invert ? ulong.MaxValue : 0;
        foreach (var b in d) c = t[(c ^ b) & 0xFF] ^ (c >> 8);
        return invert ? ~c : c;
    }
}
