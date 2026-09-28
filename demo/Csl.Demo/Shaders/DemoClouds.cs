using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Demo.Shaders;

/// <summary>
/// Clouds drifting over a sky: DemoCommon's fractal noise (shared by call) on DemoTile (shared by
/// inheritance). The noise is warped by itself, which makes the shapes curl.
/// </summary>
[Shader]
public partial class DemoClouds : DemoTile
{
    public override float3 Color(float2 p)
    {
        float2 q = p * 4.0f + new float2(Time * 0.3f, 0.0f);
        float warp = DemoCommon.Fbm(q + Time * 0.1f);
        float density = smoothstep(0.4f, 0.75f, DemoCommon.Fbm(q + warp));
        float3 sky = lerp(new float3(0.2f, 0.4f, 0.85f), new float3(0.7f, 0.85f, 1.0f), p.y + 0.5f);
        return lerp(sky, new float3(0.5f, 2.0f, 1.0f), density);
    }
}
