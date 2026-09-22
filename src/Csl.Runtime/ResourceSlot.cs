using System;
using Stride.Graphics;

namespace Csl;

/// <summary>How a shader touches a resource: what view it needs, and whether it reads back what it wrote.</summary>
[Flags]
public enum ResourceAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
}

public enum ResourceKind
{
    Texture = 0,
    TypedBuffer = 1,
    StructuredBuffer = 2,
    RawBuffer = 3,
    Sampler = 4,
}

/// <summary>
/// One resource of one shader, as the generator read it from the .sdsl: enough to allocate something
/// that can be bound there, and to reject what cannot.
/// </summary>
public readonly struct ResourceSlot
{
    public ResourceSlot(string shaderName, string name, ResourceKind kind, ResourceAccess access, string hlslType, string? elementType)
    {
        ShaderName = shaderName;
        Name = name;
        Kind = kind;
        Access = access;
        HlslType = hlslType;
        ElementType = elementType;
    }

    public string ShaderName { get; }
    public string Name { get; }
    public ResourceKind Kind { get; }
    public ResourceAccess Access { get; }

    /// <summary>The declaration: RWTexture3D&lt;uint&gt;.</summary>
    public string HlslType { get; }

    /// <summary>The element type of a texture or typed buffer: uint, float2.</summary>
    public string? ElementType { get; }

    /// <summary>The texture flags this slot needs: a shader resource view to read, an unordered access view to write.</summary>
    public TextureFlags TextureFlags =>
        ((Access & ResourceAccess.Read) != 0 ? TextureFlags.ShaderResource : TextureFlags.None)
        | ((Access & ResourceAccess.Write) != 0 ? TextureFlags.UnorderedAccess : TextureFlags.None);

    public BufferFlags BufferFlags
    {
        get
        {
            var flags = (Access & ResourceAccess.Read) != 0 ? BufferFlags.ShaderResource : BufferFlags.None;
            if ((Access & ResourceAccess.Write) != 0)
                flags |= BufferFlags.UnorderedAccess;
            switch (Kind)
            {
                case ResourceKind.StructuredBuffer: flags |= BufferFlags.StructuredBuffer; break;
                case ResourceKind.RawBuffer: flags |= BufferFlags.RawBuffer; break;
            }
            return flags;
        }
    }

    /// <summary>
    /// Whether the shader loads from the resource through its RW view, which Direct3D 11 allows only on
    /// R32_Float, R32_UInt and R32_SInt.
    /// </summary>
    public bool NeedsTypedUavLoad => Access == ResourceAccess.ReadWrite && (Kind == ResourceKind.Texture || Kind == ResourceKind.TypedBuffer);

    public static bool IsTypedUavLoadFormat(PixelFormat format) => format is PixelFormat.R32_Float or PixelFormat.R32_UInt or PixelFormat.R32_SInt;

    public override string ToString() => $"{ShaderName}.{Name} ({HlslType}, {Access})";
}

/// <summary>Marks a generated slot field with what the analyzer needs without loading the runtime.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SlotAttribute : Attribute
{
    public SlotAttribute(ResourceAccess access, ResourceKind kind)
    {
        Access = access;
        Kind = kind;
    }

    public ResourceAccess Access { get; }
    public ResourceKind Kind { get; }
}
