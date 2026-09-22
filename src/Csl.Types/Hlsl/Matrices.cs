using System.Runtime.InteropServices;

namespace Csl.Hlsl;

/// <summary>HLSL float4x4, row-major as written. Only what the translator needs to type-check; the maths runs on the GPU.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct float4x4
{
    public float4 r0, r1, r2, r3;

    public float4x4(float4 r0, float4 r1, float4 r2, float4 r3)
    {
        this.r0 = r0;
        this.r1 = r1;
        this.r2 = r2;
        this.r3 = r3;
    }

    public float4 this[int row]
    {
        get => row switch { 0 => r0, 1 => r1, 2 => r2, _ => r3 };
        set { switch (row) { case 0: r0 = value; break; case 1: r1 = value; break; case 2: r2 = value; break; default: r3 = value; break; } }
    }
}

/// <summary>HLSL float3x3.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct float3x3
{
    public float3 r0, r1, r2;

    public float3x3(float3 r0, float3 r1, float3 r2)
    {
        this.r0 = r0;
        this.r1 = r1;
        this.r2 = r2;
    }

    public float3 this[int row]
    {
        get => row switch { 0 => r0, 1 => r1, _ => r2 };
        set { switch (row) { case 0: r0 = value; break; case 1: r1 = value; break; default: r2 = value; break; } }
    }
}

public static partial class Intrinsics
{
    public static float4 mul(float4x4 m, float4 v) => new float4(dot(m.r0, v), dot(m.r1, v), dot(m.r2, v), dot(m.r3, v));
    public static float4 mul(float4 v, float4x4 m) => v.x * m.r0 + v.y * m.r1 + v.z * m.r2 + v.w * m.r3;
    public static float3 mul(float3x3 m, float3 v) => new float3(dot(m.r0, v), dot(m.r1, v), dot(m.r2, v));
    public static float3 mul(float3 v, float3x3 m) => v.x * m.r0 + v.y * m.r1 + v.z * m.r2;
    public static float mul(float a, float b) => a * b;
    public static float3 mul(float3 v, float s) => v * s;
    public static float4x4 transpose(float4x4 m) => new float4x4(
        new float4(m.r0.x, m.r1.x, m.r2.x, m.r3.x), new float4(m.r0.y, m.r1.y, m.r2.y, m.r3.y),
        new float4(m.r0.z, m.r1.z, m.r2.z, m.r3.z), new float4(m.r0.w, m.r1.w, m.r2.w, m.r3.w));
}
