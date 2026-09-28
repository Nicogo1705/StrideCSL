using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;
using EngineMath = Csl.Engine.Math;

namespace Csl.Demo.MeshShaders;

/// <summary>
/// A mesh shaded physically: the engine's GGX terms (BRDFMicrofacet mixed in) for one directional light,
/// and a procedural sky for the ambient light, diffuse and specular (split-sum, with the usual analytic
/// fit of the environment BRDF). Albedo is sRGB, sampled with its mip level from the derivatives; the
/// result is tone mapped (ACES) and written as sRGB. MeshScene draws it; Ctrl+click runs a pixel of it
/// on the CPU, vertex shader, rasterizer and pixel shader.
/// </summary>
[Shader, Mixin(typeof(BRDFMicrofacet))]
public partial class DemoLitMesh : ShaderBase
{
    [Stage, Stream("POSITION")] public float4 Position;
    [Stage, Stream("NORMAL")] public float3 Normal;
    [Stage, Stream("TEXCOORD0")] public float2 TexCoord;
    [Stage, Stream] public float3 PositionWS;
    [Stage, Stream] public float3 NormalWS;

    [Stage] public float4x4 WorldViewProjection;
    [Stage] public float4x4 World;
    [Stage] public float3 Eye;
    [Stage] public float3 LightDirection;
    [Stage] public float3 LightColor;
    [Stage] public float Roughness;
    [Stage] public float Metalness;
    [Stage] public Texture2D Albedo;
    [Stage, Sampler(Filter = "MIN_MAG_MIP_LINEAR", AddressU = "Wrap", AddressV = "Wrap")] public SamplerState AlbedoSampler;

    [Stage]
    public override void VSMain()
    {
        streams.ShadingPosition = mul(streams.Position, WorldViewProjection);
        streams.PositionWS = mul(streams.Position, World).xyz;
        streams.NormalWS = mul(new float4(streams.Normal, 0.0f), World).xyz;
    }

    /// <summary>The sky in a direction: ground, horizon and zenith colours, blurrier as the roughness grows.</summary>
    public float3 Sky(float3 direction, float roughness)
    {
        float up = direction.y;
        float3 ground = new float3(0.08f, 0.07f, 0.06f);
        float3 horizon = new float3(0.75f, 0.8f, 0.9f);
        float3 zenith = new float3(0.25f, 0.45f, 0.85f);
        float3 sky = up > 0.0f ? lerp(horizon, zenith, pow(up, 0.6f)) : lerp(horizon, ground, saturate(-up * 4.0f));
        // A rough surface sees the average of the sky, not its details.
        float3 average = new float3(0.4f, 0.45f, 0.5f);
        return lerp(sky, average, roughness * roughness);
    }

    /// <summary>The environment BRDF's scale and bias for F0 (the analytic fit of the split-sum lookup table).</summary>
    public float3 EnvironmentBrdf(float3 f0, float roughness, float nDotV)
    {
        float4 c0 = new float4(-1.0f, -0.0275f, -0.572f, 0.022f);
        float4 c1 = new float4(1.0f, 0.0425f, 1.04f, -0.04f);
        float4 r = roughness * c0 + c1;
        float a004 = min(r.x * r.x, exp2(-9.28f * nDotV)) * r.x + r.y;
        float2 ab = new float2(-1.04f, 1.04f) * a004 + r.zw;
        return f0 * ab.x + ab.y;
    }

    [Stage]
    public override void PSMain()
    {
        float3 n = normalize(streams.NormalWS);
        float3 v = normalize(Eye - streams.PositionWS);
        float3 l = normalize(-LightDirection);
        float3 h = normalize(l + v);
        float nDotL = saturate(dot(n, l));
        float nDotV = max(dot(n, v), 0.0001f);
        float nDotH = saturate(dot(n, h));
        float lDotH = saturate(dot(l, h));

        // The texture is sRGB: its colours in linear light.
        float3 albedo = pow(Albedo.Sample(AlbedoSampler, streams.TexCoord).rgb, 2.2f);
        float alpha = Roughness * Roughness;
        float3 f0 = lerp(new float3(0.04f, 0.04f, 0.04f), albedo, Metalness);
        float3 diffuseColor = albedo * (1.0f - Metalness);

        // The light: Lambert and the engine's GGX.
        // The visibility term divides by n.l: kept above zero, or an unlit face is inf * 0, NaN, black.
        float3 specular = FresnelSchlick(f0, lDotH) * NormalDistributionGGX(alpha, nDotH) * VisibilitySmithSchlickGGX(alpha, max(nDotL, 0.0001f), nDotV);
        float3 direct = (diffuseColor / EngineMath.PI + specular) * LightColor * nDotL;

        // The sky: what the normal sees for the diffuse part, what the reflection sees for the specular.
        float3 ambientDiffuse = diffuseColor * Sky(n, 1.0f);
        float3 ambientSpecular = Sky(reflect(-v, n), Roughness) * EnvironmentBrdf(f0, Roughness, nDotV);
        float3 color = direct + ambientDiffuse + ambientSpecular;

        // ACES filmic tone mapping (Narkowicz's fit), then sRGB.
        color = saturate(color * (2.51f * color + 0.03f) / (color * (2.43f * color + 0.59f) + 0.14f));
        streams.ColorTarget = new float4(pow(color, 1.0f / 2.2f), 1.0f);
    }
}
