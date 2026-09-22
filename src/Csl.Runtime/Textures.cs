using System;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Half = Stride.Core.Mathematics.Half;

namespace Csl;

/// <summary>
/// Textures allocated from what will be stored in them and which shader slots will bind them: the
/// format follows the element type, the views follow the slots.
/// </summary>
public static class Textures
{
    /// <summary>The pixel format a texture of these elements has.</summary>
    public static PixelFormat FormatOf<T>() where T : unmanaged
    {
        if (typeof(T) == typeof(float)) return PixelFormat.R32_Float;
        if (typeof(T) == typeof(Half)) return PixelFormat.R16_Float;
        if (typeof(T) == typeof(uint)) return PixelFormat.R32_UInt;
        if (typeof(T) == typeof(int)) return PixelFormat.R32_SInt;
        if (typeof(T) == typeof(ushort) || typeof(T) == typeof(Texels.R16)) return PixelFormat.R16_UNorm;
        if (typeof(T) == typeof(byte) || typeof(T) == typeof(Texels.R8)) return PixelFormat.R8_UNorm;
        if (typeof(T) == typeof(Texels.Rg8)) return PixelFormat.R8G8_UNorm;
        if (typeof(T) == typeof(Vector2)) return PixelFormat.R32G32_Float;
        if (typeof(T) == typeof(Half2)) return PixelFormat.R16G16_Float;
        if (typeof(T) == typeof(Int2)) return PixelFormat.R32G32_SInt;
        if (typeof(T) == typeof(Vector4)) return PixelFormat.R32G32B32A32_Float;
        if (typeof(T) == typeof(Half4)) return PixelFormat.R16G16B16A16_Float;
        if (typeof(T) == typeof(Int4)) return PixelFormat.R32G32B32A32_SInt;
        if (typeof(T) == typeof(Color)) return PixelFormat.R8G8B8A8_UNorm;
        if (typeof(T) == typeof(Color4)) return PixelFormat.R32G32B32A32_Float;
        throw new NotSupportedException($"No pixel format for elements of type {typeof(T).Name}; use float, Half, uint, int, byte, ushort, Vector2/4, Half2/4, Int2/4, Color or Csl.Texels.*");
    }

    /// <summary>Whether a 32-bit single channel texture of this element type can be loaded through a RW view.</summary>
    public static bool IsTypedUavLoadElement<T>() where T : unmanaged => ResourceSlot.IsTypedUavLoadFormat(FormatOf<T>());

    /// <summary>The flags a texture bound to all these slots needs. With no slot, a shader resource.</summary>
    public static TextureFlags FlagsFor(ReadOnlySpan<ResourceSlot> boundTo, TextureFlags extra = TextureFlags.None)
    {
        var flags = extra;
        foreach (var slot in boundTo)
            flags |= slot.TextureFlags;
        return flags == TextureFlags.None ? TextureFlags.ShaderResource : flags;
    }

    private static void CheckTypedUavLoad<T>(ReadOnlySpan<ResourceSlot> boundTo) where T : unmanaged
    {
        foreach (var slot in boundTo)
        {
            if (slot.NeedsTypedUavLoad && !IsTypedUavLoadElement<T>())
                throw new ArgumentException($"{slot} is read and written through its RW view, which needs R32_Float, R32_UInt or R32_SInt; allocate it with float, uint or int rather than {typeof(T).Name}");
        }
    }

    /// <summary>A 3D texture of these elements, one mip, with the views its slots need.</summary>
    public static Texture New3D<T>(GraphicsDevice device, Int3 size, params ReadOnlySpan<ResourceSlot> boundTo) where T : unmanaged
        => New3D<T>(device, size, 1, TextureFlags.None, boundTo);

    /// <summary>A 3D texture of these elements with this many mips (MipMapCount.Auto for the whole chain).</summary>
    public static Texture New3D<T>(GraphicsDevice device, Int3 size, MipMapCount mips, params ReadOnlySpan<ResourceSlot> boundTo) where T : unmanaged
        => New3D<T>(device, size, mips, TextureFlags.None, boundTo);

    /// <summary>A 3D texture of these elements, with flags beyond what the slots ask for (a render target, say).</summary>
    public static Texture New3D<T>(GraphicsDevice device, Int3 size, MipMapCount mips, TextureFlags extraFlags, params ReadOnlySpan<ResourceSlot> boundTo) where T : unmanaged
    {
        CheckTypedUavLoad<T>(boundTo);
        return Texture.New3D(device, size.X, size.Y, size.Z, mips, FormatOf<T>(), FlagsFor(boundTo, extraFlags), GraphicsResourceUsage.Default);
    }

    /// <summary>A 2D texture of these elements, one mip, with the views its slots need.</summary>
    public static Texture New2D<T>(GraphicsDevice device, Int2 size, params ReadOnlySpan<ResourceSlot> boundTo) where T : unmanaged
        => New2D<T>(device, size, 1, TextureFlags.None, boundTo);

    public static Texture New2D<T>(GraphicsDevice device, Int2 size, MipMapCount mips, params ReadOnlySpan<ResourceSlot> boundTo) where T : unmanaged
        => New2D<T>(device, size, mips, TextureFlags.None, boundTo);

    public static Texture New2D<T>(GraphicsDevice device, Int2 size, MipMapCount mips, TextureFlags extraFlags, params ReadOnlySpan<ResourceSlot> boundTo) where T : unmanaged
    {
        CheckTypedUavLoad<T>(boundTo);
        return Texture.New2D(device, size.X, size.Y, mips, FormatOf<T>(), FlagsFor(boundTo, extraFlags), 1, GraphicsResourceUsage.Default);
    }

    /// <summary>The size of a texture on each axis, at a mip level.</summary>
    public static Int3 SizeOf(Texture texture, int mipLevel = 0) => new(
        Texture.CalculateMipSize(texture.Width, mipLevel),
        Texture.CalculateMipSize(texture.Height, mipLevel),
        Texture.CalculateMipSize(texture.Depth, mipLevel));
}
