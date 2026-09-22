using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;
using EngineMath = Csl.Engine.Math;

namespace Csl.Demo.Shaders;

/// <summary>
/// A lit sphere, with the engine's shaders used both ways. Math.RayIntersectsSphere and Math.PI are
/// static: called by name. Utilities.FresnelSchlick is not: Utilities is mixed in and the method is
/// called as the shader's own. The surface is DemoCommon's noise through its palette.
/// </summary>
[Shader, Mixin(typeof(Utilities))]
public partial class DemoSphere : DemoTile
{
    public override float3 Color(float2 p)
    {
        float3 eye = new float3(0.0f, 0.0f, -3.5f);
        float3 direction = normalize(new float3(p.x, -p.y, 1.0f));
        float3 color = lerp(new float3(0.02f, 0.02f, 0.05f), new float3(0.1f, 0.1f, 0.2f), p.y + 0.5f);
        float hit = 0.0f;
        if (EngineMath.RayIntersectsSphere(eye, direction, new float3(0.0f, 0.0f, 0.0f), 1.0f, out hit))
        {
            float3 normal = normalize(eye + direction * hit);
            float3 light = normalize(new float3(cos(Time), 0.7f, sin(Time) - 1.0f));
            float diffuse = saturate(dot(normal, light));
            // Longitude, turning with time, and latitude: the noise wraps around the sphere.
            float longitude = atan2(normal.z, normal.x) / (2.0f * EngineMath.PI) + Time * 0.05f;
            float3 albedo = DemoCommon.Palette(DemoCommon.Fbm(new float2(longitude * 12.0f, normal.y * 3.0f)) + 0.6f);
            float3 rim = FresnelSchlick(new float3(0.04f, 0.04f, 0.04f), -direction, normal, 1.0f);
            color = albedo * (0.1f + 0.9f * diffuse) + rim;
        }
        return color;
    }
}
