using Stride.Core.Mathematics;
using Stride.Graphics;
using Half = Stride.Core.Mathematics.Half;

namespace Csl.Tests;

public class RuntimeTests
{
    [Fact]
    public void GroupCountsRoundUp()
    {
        Assert.Equal(new Int3(8, 8, 8), ComputeEffect.GroupCount(new Int3(64), new Int3(8)));
        Assert.Equal(new Int3(9, 9, 9), ComputeEffect.GroupCount(new Int3(65), new Int3(8)));
        Assert.Equal(new Int3(1, 1, 1), ComputeEffect.GroupCount(new Int3(1), new Int3(8)));
        Assert.Equal(new Int3(3, 1, 2), ComputeEffect.GroupCount(new Int3(12, 4, 8), new Int3(4)));
    }

    [Fact]
    public void FormatsFollowTheElementType()
    {
        Assert.Equal(PixelFormat.R16_Float, Textures.FormatOf<Half>());
        Assert.Equal(PixelFormat.R32_UInt, Textures.FormatOf<uint>());
        Assert.Equal(PixelFormat.R32_Float, Textures.FormatOf<float>());
        Assert.Equal(PixelFormat.R8G8_UNorm, Textures.FormatOf<Texels.Rg8>());
        Assert.Equal(PixelFormat.R8G8B8A8_UNorm, Textures.FormatOf<Color>());
        Assert.Throws<NotSupportedException>(() => Textures.FormatOf<Int3>());
        Assert.True(Textures.IsTypedUavLoadElement<uint>());
        Assert.False(Textures.IsTypedUavLoadElement<Half>());
    }

    [Fact]
    public void FlagsFollowTheSlots()
    {
        var read = new ResourceSlot("S", "In", ResourceKind.Texture, ResourceAccess.Read, "Texture3D<float>", "float");
        var write = new ResourceSlot("S", "Out", ResourceKind.Texture, ResourceAccess.Write, "RWTexture3D<float>", "float");
        Assert.Equal(TextureFlags.ShaderResource, Textures.FlagsFor(new[] { read }));
        Assert.Equal(TextureFlags.ShaderResource | TextureFlags.UnorderedAccess, Textures.FlagsFor(new[] { read, write }));
        Assert.Equal(TextureFlags.ShaderResource, Textures.FlagsFor(ReadOnlySpan<ResourceSlot>.Empty));
        Assert.Equal(TextureFlags.UnorderedAccess | TextureFlags.RenderTarget, Textures.FlagsFor(new[] { write }, TextureFlags.RenderTarget));
    }

    [Fact]
    public void PingPongSwaps()
    {
        var pair = new PingPong<string>("a", "b");
        Assert.Equal("a", pair.Current);
        Assert.Equal("b", pair.Next);
        pair.Swap();
        Assert.Equal("b", pair.Current);
        Assert.Equal("a", pair.Next);
        Assert.Equal(1, pair.CurrentIndex);
        Assert.Equal("a", pair[0]);
    }
}
