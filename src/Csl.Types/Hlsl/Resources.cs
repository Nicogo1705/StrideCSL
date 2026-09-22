using System;

namespace Csl.Hlsl;

/// <summary>Shader resources: opaque handles, as on the GPU. They exist so shader code type-checks; every member runs on the GPU only.</summary>
internal static class GpuOnly
{
    public static Exception Exception() => new NotSupportedException("Shader resources are only accessed on the GPU");
}

public readonly struct SamplerState { }
public readonly struct SamplerComparisonState { }

public readonly struct Texture1D<T> where T : struct
{
    public T this[int location] => throw GpuOnly.Exception();
    public T Load(int2 location) => throw GpuOnly.Exception();
    public T Sample(SamplerState sampler, float location) => throw GpuOnly.Exception();
    public T SampleLevel(SamplerState sampler, float location, float lod) => throw GpuOnly.Exception();
    public void GetDimensions(out uint width) => throw GpuOnly.Exception();
}

public readonly struct Texture2D<T> where T : struct
{
    public T this[int2 location] => throw GpuOnly.Exception();
    public T Load(int3 location) => throw GpuOnly.Exception();
    public T Sample(SamplerState sampler, float2 location) => throw GpuOnly.Exception();
    public T SampleLevel(SamplerState sampler, float2 location, float lod) => throw GpuOnly.Exception();
    public T SampleGrad(SamplerState sampler, float2 location, float2 ddx, float2 ddy) => throw GpuOnly.Exception();
    public void GetDimensions(out uint width, out uint height) => throw GpuOnly.Exception();
    public void GetDimensions(uint mipLevel, out uint width, out uint height, out uint levels) => throw GpuOnly.Exception();
}

public readonly struct Texture3D<T> where T : struct
{
    public T this[int3 location] => throw GpuOnly.Exception();
    public T Load(int4 location) => throw GpuOnly.Exception();
    public T Sample(SamplerState sampler, float3 location) => throw GpuOnly.Exception();
    public T SampleLevel(SamplerState sampler, float3 location, float lod) => throw GpuOnly.Exception();
    public void GetDimensions(out uint width, out uint height, out uint depth) => throw GpuOnly.Exception();
    public void GetDimensions(uint mipLevel, out uint width, out uint height, out uint depth, out uint levels) => throw GpuOnly.Exception();
}

public readonly struct TextureCube<T> where T : struct
{
    public T Sample(SamplerState sampler, float3 direction) => throw GpuOnly.Exception();
    public T SampleLevel(SamplerState sampler, float3 direction, float lod) => throw GpuOnly.Exception();
}

public readonly struct RWTexture1D<T> where T : struct
{
    public T this[int location] { get => throw GpuOnly.Exception(); set => throw GpuOnly.Exception(); }
    public T Load(int location) => throw GpuOnly.Exception();
    public void GetDimensions(out uint width) => throw GpuOnly.Exception();
}

public readonly struct RWTexture2D<T> where T : struct
{
    public T this[int2 location] { get => throw GpuOnly.Exception(); set => throw GpuOnly.Exception(); }
    public T Load(int2 location) => throw GpuOnly.Exception();
    public void GetDimensions(out uint width, out uint height) => throw GpuOnly.Exception();
}

public readonly struct RWTexture3D<T> where T : struct
{
    public T this[int3 location] { get => throw GpuOnly.Exception(); set => throw GpuOnly.Exception(); }
    public T Load(int3 location) => throw GpuOnly.Exception();
    public void GetDimensions(out uint width, out uint height, out uint depth) => throw GpuOnly.Exception();
}

public readonly struct Buffer<T> where T : struct
{
    public T this[int index] => throw GpuOnly.Exception();
    public T Load(int index) => throw GpuOnly.Exception();
    public void GetDimensions(out uint count) => throw GpuOnly.Exception();
}

public readonly struct RWBuffer<T> where T : struct
{
    public T this[int index] { get => throw GpuOnly.Exception(); set => throw GpuOnly.Exception(); }
    public T Load(int index) => throw GpuOnly.Exception();
    public void GetDimensions(out uint count) => throw GpuOnly.Exception();
}

public readonly struct StructuredBuffer<T> where T : struct
{
    public T this[int index] => throw GpuOnly.Exception();
    public T Load(int index) => throw GpuOnly.Exception();
    public void GetDimensions(out uint count, out uint stride) => throw GpuOnly.Exception();
}

public readonly struct RWStructuredBuffer<T> where T : struct
{
    public T this[int index] { get => throw GpuOnly.Exception(); set => throw GpuOnly.Exception(); }
    public T Load(int index) => throw GpuOnly.Exception();
    public void GetDimensions(out uint count, out uint stride) => throw GpuOnly.Exception();
    public uint IncrementCounter() => throw GpuOnly.Exception();
    public uint DecrementCounter() => throw GpuOnly.Exception();
}

public readonly struct AppendStructuredBuffer<T> where T : struct
{
    public void Append(T value) => throw GpuOnly.Exception();
}

public readonly struct ConsumeStructuredBuffer<T> where T : struct
{
    public T Consume() => throw GpuOnly.Exception();
}

public readonly struct ByteAddressBuffer
{
    public uint Load(int address) => throw GpuOnly.Exception();
    public uint2 Load2(int address) => throw GpuOnly.Exception();
    public uint3 Load3(int address) => throw GpuOnly.Exception();
    public uint4 Load4(int address) => throw GpuOnly.Exception();
    public void GetDimensions(out uint bytes) => throw GpuOnly.Exception();
}

public readonly struct RWByteAddressBuffer
{
    public uint Load(int address) => throw GpuOnly.Exception();
    public uint2 Load2(int address) => throw GpuOnly.Exception();
    public uint3 Load3(int address) => throw GpuOnly.Exception();
    public uint4 Load4(int address) => throw GpuOnly.Exception();
    public void Store(int address, uint value) => throw GpuOnly.Exception();
    public void Store2(int address, uint2 value) => throw GpuOnly.Exception();
    public void Store3(int address, uint3 value) => throw GpuOnly.Exception();
    public void Store4(int address, uint4 value) => throw GpuOnly.Exception();
    public void GetDimensions(out uint bytes) => throw GpuOnly.Exception();
}
