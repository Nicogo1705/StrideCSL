using System;
using Stride.Core.Mathematics;
using Stride.Graphics;

namespace Csl;

/// <summary>
/// One view per mip level of a texture. A compute pass that reads one level and writes the next
/// needs the two as distinct subresources, and Direct3D wants a view for each.
/// </summary>
public sealed class MipChain : IDisposable
{
    private readonly Texture[] levels;

    private MipChain(Texture texture)
    {
        Texture = texture;
        levels = new Texture[texture.MipLevelCount];
        for (int level = 0; level < levels.Length; level++)
        {
            levels[level] = texture.ToTextureView(new TextureViewDescription
            {
                Type = ViewType.Single,
                MipLevel = level,
                ArraySlice = 0,
                Flags = texture.Flags & (TextureFlags.ShaderResource | TextureFlags.UnorderedAccess),
                Format = texture.ViewFormat,
            });
        }
    }

    /// <summary>The whole texture, as it was given.</summary>
    public Texture Texture { get; }

    public int Count => levels.Length;

    /// <summary>The view of one level.</summary>
    public Texture this[int level] => levels[level];

    /// <summary>The size of one level, on each axis.</summary>
    public Int3 SizeAt(int level) => Textures.SizeOf(Texture, level);

    /// <summary>Views over every level of the texture; the texture itself is not owned.</summary>
    public static MipChain Of(Texture texture) => new(texture ?? throw new ArgumentNullException(nameof(texture)));

    public void Dispose()
    {
        foreach (var level in levels)
            level.Dispose();
    }
}

public static class MipChainExtensions
{
    /// <summary>Views over every level of the texture; dispose them with the texture.</summary>
    public static MipChain MipViews(this Texture texture) => MipChain.Of(texture);
}
