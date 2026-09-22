using System.Runtime.InteropServices;

namespace Csl;

/// <summary>
/// Element types for the formats that have no C# struct of their own. Allocate a texture with one of
/// these as the element type and the format follows; upload with arrays of them.
/// </summary>
public static class Texels
{
    /// <summary>Two unsigned bytes, R8G8_UNorm: a density and a material id, say.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Rg8
    {
        public byte R;
        public byte G;

        public Rg8(byte r, byte g)
        {
            R = r;
            G = g;
        }
    }

    /// <summary>One unsigned byte, R8_UNorm.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct R8
    {
        public byte R;

        public R8(byte r) => R = r;
    }

    /// <summary>One unsigned short, R16_UNorm.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct R16
    {
        public ushort R;

        public R16(ushort r) => R = r;
    }
}
