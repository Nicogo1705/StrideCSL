using System;
using Stride.Graphics;
using Buffer = Stride.Graphics.Buffer;

namespace Csl;

/// <summary>Buffers allocated from their element type and the shader slots that will bind them.</summary>
public static class Buffers
{
    /// <summary>The flags a buffer bound to all these slots needs. With no slot, a shader resource.</summary>
    public static BufferFlags FlagsFor(ReadOnlySpan<ResourceSlot> boundTo, BufferFlags extra = BufferFlags.None)
    {
        var flags = extra;
        foreach (var slot in boundTo)
            flags |= slot.BufferFlags;
        return flags == BufferFlags.None ? BufferFlags.ShaderResource : flags;
    }

    /// <summary>A typed buffer (Buffer&lt;T&gt; / RWBuffer&lt;T&gt; in the shader) of this many elements.</summary>
    public static Buffer NewTyped<T>(GraphicsDevice device, int count, params ReadOnlySpan<ResourceSlot> boundTo) where T : unmanaged
    {
        var format = Textures.FormatOf<T>();
        foreach (var slot in boundTo)
        {
            if (slot.NeedsTypedUavLoad && !ResourceSlot.IsTypedUavLoadFormat(format))
                throw new ArgumentException($"{slot} is read and written through its RW view, which needs R32_Float, R32_UInt or R32_SInt; allocate it with float, uint or int rather than {typeof(T).Name}");
        }
        return Buffer.New(device, count * System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), FlagsFor(boundTo), format, GraphicsResourceUsage.Default);
    }

    /// <summary>A structured buffer (StructuredBuffer&lt;T&gt; / RWStructuredBuffer&lt;T&gt;) of this many elements.</summary>
    public static Buffer NewStructured<T>(GraphicsDevice device, int count, params ReadOnlySpan<ResourceSlot> boundTo) where T : unmanaged
        => Buffer.New(device, count * System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), System.Runtime.CompilerServices.Unsafe.SizeOf<T>(),
            FlagsFor(boundTo) | BufferFlags.StructuredBuffer, PixelFormat.None, GraphicsResourceUsage.Default);

    /// <summary>A raw buffer (ByteAddressBuffer / RWByteAddressBuffer) of this many bytes.</summary>
    public static Buffer NewRaw(GraphicsDevice device, int bytes, params ReadOnlySpan<ResourceSlot> boundTo)
        => Buffer.New(device, bytes, 4, FlagsFor(boundTo) | BufferFlags.RawBuffer, PixelFormat.None, GraphicsResourceUsage.Default);
}
