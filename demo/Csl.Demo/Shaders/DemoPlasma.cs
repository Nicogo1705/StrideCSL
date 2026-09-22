using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>
/// A plasma: a few sines summed, coloured by DemoCommon.Palette. It inherits DemoTile, so it only
/// writes Color; Time comes from DemoTile's Global.
/// </summary>
[Shader]
public partial class DemoPlasma : DemoTile
{
    public override float3 Color(float2 p)
    {
        float2 q = p * 8.0f;
        float t = Time;
        float v = sin(q.x + t)
                + sin((q.y + t) * 0.5f)
                + sin((q.x + q.y + t) * 0.5f)
                + sin(length(q) - t * 1.5f);
        return DemoCommon.Palette(v * 0.25f + t * 0.1f);
    }
}
