using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>
/// Shared by inheritance: the base of the tiles that only compute a colour. Shading is written once,
/// here, and calls Color; a tile inherits DemoTile and overrides Color. Global is mixed in for Time,
/// which every tile inheriting this one sees too.
/// </summary>
[Shader, Mixin(typeof(Global))]
public abstract partial class DemoTile : ImageEffectShader
{
    /// <summary>The tile's width over its height, set by the app: circles stay round.</summary>
    [Stage] public float Aspect;

    /// <summary>
    /// The colour at p: (0, 0) at the centre of the tile, y from -0.5 at the top to 0.5 at the bottom,
    /// x scaled by Aspect.
    /// </summary>
    public virtual float3 Color(float2 p)
    {
        return new float3(p + 0.5f, 0.5f);
    }

    [Stage]
    public override float4 Shading()
    {
        float2 p = streams.TexCoord - 0.5f;
        p.x *= Aspect;
        return new float4(Color(p), 1.0f);
    }
}
