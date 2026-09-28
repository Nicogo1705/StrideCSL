using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;
using EngineMath = Csl.Engine.Math;

namespace Csl.Demo.MeshShaders;

/// <summary>
/// A mesh lit by one directional light with the engine's GGX terms (BRDFMicrofacet mixed in): Albedo
/// sampled with its mip level from the derivatives, Roughness and Metalness. MeshScene draws it;
/// Ctrl+click runs a pixel of it on the CPU, vertex shader, rasterizer and pixel shader.
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

        float3 albedo = Albedo.Sample(AlbedoSampler, streams.TexCoord).rgb;
        float alpha = Roughness * Roughness;
        float3 f0 = lerp(new float3(0.04f, 0.04f, 0.04f), albedo, Metalness);
        float3 specular = FresnelSchlick(f0, lDotH) * NormalDistributionGGX(alpha, nDotH) * VisibilitySmithSchlickGGX(alpha, nDotL, nDotV);
        float3 diffuse = albedo * (1.0f - Metalness) / EngineMath.PI;
        float3 color = (diffuse + specular) * LightColor * nDotL + albedo * 0.05f;
        streams.ColorTarget = new float4(color, 1.0f);
    }
}
