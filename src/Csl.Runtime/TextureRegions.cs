using System;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Csl;

/// <summary>Uploads to part of a texture: a box of a larger array, or one value over a box.</summary>
public static class TextureRegions
{
    /// <summary>
    /// Uploads the box [lo, hi], inclusive, of a whole-texture array laid out x fastest, then y, then z.
    /// </summary>
    public static void UploadRegion<T>(this Texture texture, CommandList commandList, ReadOnlySpan<T> whole, Int3 wholeSize, Int3 lo, Int3 hi, int mipLevel = 0)
        where T : unmanaged
    {
        if (whole.Length < wholeSize.X * wholeSize.Y * wholeSize.Z)
            throw new ArgumentException($"The array holds {whole.Length} elements, fewer than {wholeSize} needs", nameof(whole));
        lo = Int3.Max(lo, Int3.Zero);
        hi = Int3.Min(hi, wholeSize - Int3.One);
        var extent = hi - lo + Int3.One;
        if (extent.X <= 0 || extent.Y <= 0 || extent.Z <= 0)
            return;

        // The whole array straight through when the box is everything: no copy.
        if (lo == Int3.Zero && extent == wholeSize)
        {
            texture.SetData(commandList, whole, 0, mipLevel);
            return;
        }

        var block = new T[extent.X * extent.Y * extent.Z];
        for (int z = 0; z < extent.Z; z++)
        {
            for (int y = 0; y < extent.Y; y++)
            {
                int source = ((lo.Z + z) * wholeSize.Y + (lo.Y + y)) * wholeSize.X + lo.X;
                whole.Slice(source, extent.X).CopyTo(block.AsSpan((z * extent.Y + y) * extent.X, extent.X));
            }
        }
        texture.SetData(commandList, block, 0, mipLevel, new ResourceRegion(lo.X, lo.Y, lo.Z, hi.X + 1, hi.Y + 1, hi.Z + 1));
    }

    /// <summary>Uploads the box [lo, hi], inclusive, of a whole-texture array that has the texture's own size.</summary>
    public static void UploadRegion<T>(this Texture texture, CommandList commandList, ReadOnlySpan<T> whole, Int3 lo, Int3 hi)
        where T : unmanaged
        => UploadRegion(texture, commandList, whole, Textures.SizeOf(texture), lo, hi);

    /// <summary>Writes one value to every texel of the box [lo, hi], inclusive, clipped to the texture.</summary>
    public static void FillRegion<T>(this Texture texture, CommandList commandList, T value, Int3 lo, Int3 hi, int mipLevel = 0)
        where T : unmanaged
    {
        var size = Textures.SizeOf(texture, mipLevel);
        lo = Int3.Max(lo, Int3.Zero);
        hi = Int3.Min(hi, size - Int3.One);
        var extent = hi - lo + Int3.One;
        if (extent.X <= 0 || extent.Y <= 0 || extent.Z <= 0)
            return;
        var block = new T[extent.X * extent.Y * extent.Z];
        Array.Fill(block, value);
        texture.SetData(commandList, block, 0, mipLevel, new ResourceRegion(lo.X, lo.Y, lo.Z, hi.X + 1, hi.Y + 1, hi.Z + 1));
    }

    /// <summary>Writes one value to the whole texture.</summary>
    public static void Fill<T>(this Texture texture, CommandList commandList, T value, int mipLevel = 0) where T : unmanaged
        => FillRegion(texture, commandList, value, Int3.Zero, Textures.SizeOf(texture, mipLevel) - Int3.One, mipLevel);
}
