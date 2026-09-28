using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Csl.Cpu;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;
using CslTypes = Csl.Types;

namespace Csl;

/// <summary>
/// What an effect really had when the GPU ran it, handed to a CPU run of the same shader (Csl.Cpu):
/// every value of its <see cref="ParameterCollection"/> by the member its key names, the textures and
/// buffers read back from the GPU, the samplers' descriptions. The CPU then runs the shader on the
/// inputs the game gave it, not on made-up ones.
/// </summary>
public static class CpuCapture
{
    /// <summary>
    /// The CPU run of an image effect as it was last drawn: its C# class (found by the effect's shader
    /// name), its parameters and inputs, over <paramref name="viewport"/> of its output.
    /// </summary>
    public static CpuImageEffect ImageEffect(ImageEffectShader effect, CommandList commandList, Viewport viewport, Type? shaderType = null)
    {
        var type = shaderType ?? ShaderTypes.Find(effect.EffectName)
            ?? throw new InvalidOperationException($"No C# [Shader] class for {effect.EffectName}");
        int left = (int)MathF.Floor(viewport.X), top = (int)MathF.Floor(viewport.Y);
        int width = (int)MathF.Ceiling(viewport.X + viewport.Width) - left, height = (int)MathF.Ceiling(viewport.Y + viewport.Height) - top;
        var run = new CpuImageEffect(type, width, height)
        {
            Left = left,
            Top = top,
            Viewport = (viewport.X, viewport.Y, viewport.Width, viewport.Height),
        };
        Apply(effect.Parameters, run, commandList);
        return run;
    }

    /// <summary>The CPU run of a compute shader with the parameters and resources its wrapper had.</summary>
    public static CpuComputeShader Compute(ComputeEffect effect, CommandList commandList, Type? shaderType = null)
    {
        var type = shaderType ?? ShaderTypes.Find(effect.Name)
            ?? throw new InvalidOperationException($"No C# [Shader] class for {effect.Name}");
        var run = new CpuComputeShader(type);
        Apply(effect.Parameters, run, commandList);
        return run;
    }

    /// <summary>
    /// Every parameter the collection holds that the shader has a member for (the key's last name:
    /// <c>Global.Time</c> is <c>Time</c>), converted to the member's type. Returns how many were set.
    /// </summary>
    public static int Apply(ParameterCollection parameters, Run run, CommandList commandList)
    {
        var fields = Members.InstanceFields(run.Shader.GetType());
        int set = 0;
        foreach (var info in parameters.ParameterKeyInfos)
        {
            var name = info.Key.Name;
            var member = name.Substring(name.LastIndexOf('.') + 1);
            if (!fields.TryGetValue(member, out var field))
                continue;
            try
            {
                object? value = info.IsValueParameter
                    ? ValueOf(parameters.DataValues, info.Offset, info.Count, field.FieldType)
                    : info.IsResourceParameter ? ResourceOf(parameters.ObjectValues[info.BindingSlot], field.FieldType, commandList) : null;
                if (value == null)
                    continue;
                field.SetValue(run.Shader, value);
                set++;
            }
            catch (Exception e) when (e is NotSupportedException or ArgumentException)
            {
                // A parameter the CPU cannot hold (a format it does not decode): the member keeps its default.
            }
        }
        return set;
    }

    /// <summary>A value parameter's bytes as the member's type: Stride's and Csl's types have the same layout.</summary>
    private static object? ValueOf(byte[] data, int offset, int count, Type type)
    {
        if (type == typeof(bool))
            return BitConverter.ToInt32(data, offset) != 0;
        if (type.IsArray)
        {
            var element = type.GetElementType()!;
            int size = Marshal.SizeOf(element);
            var array = Array.CreateInstance(element, count);
            for (int i = 0; i < count && offset + (i + 1) * size <= data.Length; i++)
                array.SetValue(FromBytes(data, offset + i * size, element), i);
            return array;
        }
        if (!type.IsValueType || type.IsPrimitive == false && type.Namespace != "Csl.Types")
            return type.IsPrimitive ? FromBytes(data, offset, type) : null;
        return offset + Marshal.SizeOf(type) <= data.Length ? FromBytes(data, offset, type) : null;
    }

    private static object FromBytes(byte[] data, int offset, Type type)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure(handle.AddrOfPinnedObject() + offset, type)!;
        }
        finally
        {
            handle.Free();
        }
    }

    private static object? ResourceOf(object? value, Type type, CommandList commandList)
    {
        switch (value)
        {
            case Texture texture when type.Name.Contains("Texture"):
                // Texture2D, Texture2D<T>, RWTexture2D<T>, Texture3D…: each takes the CPU texture.
                return Activator.CreateInstance(type, ReadTexture(texture, commandList));
            case SamplerState sampler when type == typeof(CslTypes.SamplerState):
                return new CslTypes.SamplerState(Describe(sampler.Description));
            case SamplerState sampler when type == typeof(CslTypes.SamplerComparisonState):
                return new CslTypes.SamplerComparisonState(Describe(sampler.Description));
            case Stride.Graphics.Buffer buffer when type.IsGenericType:
                return Activator.CreateInstance(type, ReadBuffer(buffer, type.GetGenericArguments()[0], commandList));
            case Stride.Graphics.Buffer buffer when type.Name.Contains("ByteAddress"):
                return Activator.CreateInstance(type, ReadBuffer(buffer, typeof(uint), commandList));
            default:
                return null;
        }
    }

    /// <summary>A sampler state as the CPU filters with it.</summary>
    public static SamplerDescription Describe(SamplerStateDescription description)
    {
        var result = new SamplerDescription
        {
            AddressU = SamplerDescription.ParseAddress(description.AddressU.ToString()),
            AddressV = SamplerDescription.ParseAddress(description.AddressV.ToString()),
            AddressW = SamplerDescription.ParseAddress(description.AddressW.ToString()),
            BorderColor = new CslTypes.float4(description.BorderColor.R, description.BorderColor.G, description.BorderColor.B, description.BorderColor.A),
            MipLodBias = description.MipMapLevelOfDetailBias,
            MinLod = description.MinMipLevel,
            MaxLod = description.MaxMipLevel,
            Compare = SamplerDescription.ParseCompare(description.CompareFunction.ToString()),
        };
        SamplerDescription.ParseFilter(description.Filter.ToString(), result);
        return result;
    }

    /// <summary>A GPU texture read back, every level and slice, decoded to what a shader reads.</summary>
    public static CpuTexture ReadTexture(Texture texture, CommandList commandList)
    {
        var format = texture.Format;
        var cpuFormat = TexelFormatOf(format);
        bool volume = texture.Dimension == TextureDimension.Texture3D;
        int slices = volume ? texture.Depth : texture.ArraySize;
        var result = new CpuTexture(texture.Width, texture.Height, slices, texture.MipLevelCount, cpuFormat) { IsVolume = volume };
        for (int level = 0; level < texture.MipLevelCount; level++)
        {
            int width = result.LevelWidth(level), height = result.LevelHeight(level);
            int depth = volume ? result.LevelDepth(level) : 1;
            for (int slice = 0; slice < (volume ? 1 : slices); slice++)
            {
                var bytes = texture.GetData<byte>(commandList, slice, level);
                // The bytes of a texel, from what came back (the rows are packed).
                int texel = Math.Max(1, bytes.Length / (width * height * depth));
                for (int z = 0; z < depth; z++)
                for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int at = ((z * height + y) * width + x) * texel;
                    if (at + texel > bytes.Length)
                        continue;
                    if (result.IsInteger)
                        result.WriteInteger(level, x, y, volume ? z : slice, DecodeInteger(format, bytes, at));
                    else
                        result.Write(level, x, y, volume ? z : slice, Decode(format, bytes, at));
                }
            }
        }
        return result;
    }

    /// <summary>A GPU buffer read back as an array of the shader's element type.</summary>
    public static Array ReadBuffer(Stride.Graphics.Buffer buffer, Type element, CommandList commandList)
    {
        var bytes = buffer.GetData<byte>(commandList);
        int size = Marshal.SizeOf(element);
        var array = Array.CreateInstance(element, bytes.Length / size);
        for (int i = 0; i < array.Length; i++)
            array.SetValue(FromBytes(bytes, i * size, element), i);
        return array;
    }

    private static TexelFormat TexelFormatOf(PixelFormat format) => format switch
    {
        PixelFormat.R8G8B8A8_UNorm or PixelFormat.B8G8R8A8_UNorm or PixelFormat.R8G8B8A8_Typeless => TexelFormat.Rgba8UNorm,
        PixelFormat.R8G8B8A8_UNorm_SRgb or PixelFormat.B8G8R8A8_UNorm_SRgb => TexelFormat.Rgba8UNormSrgb,
        PixelFormat.R16G16B16A16_Float => TexelFormat.Rgba16Float,
        PixelFormat.R32_Float => TexelFormat.R32Float,
        PixelFormat.R16_Float => TexelFormat.R16Float,
        PixelFormat.R32G32_Float => TexelFormat.Rg32Float,
        PixelFormat.R8_UNorm => TexelFormat.R8UNorm,
        PixelFormat.R32_UInt => TexelFormat.R32UInt,
        PixelFormat.R32_SInt => TexelFormat.R32SInt,
        PixelFormat.R32G32B32A32_UInt => TexelFormat.Rgba32UInt,
        // Anything else kept exactly, as 32-bit floats.
        _ => TexelFormat.Rgba32Float,
    };

    private static CslTypes.float4 Decode(PixelFormat format, byte[] b, int at)
    {
        float U8(int i) => b[at + i] / 255f;
        float F16(int i) => (float)BitConverter.ToHalf(b, at + i * 2);
        float F32(int i) => BitConverter.ToSingle(b, at + i * 4);
        switch (format)
        {
            case PixelFormat.R8G8B8A8_UNorm or PixelFormat.R8G8B8A8_Typeless:
                return new CslTypes.float4(U8(0), U8(1), U8(2), U8(3));
            case PixelFormat.R8G8B8A8_UNorm_SRgb:
                return new CslTypes.float4(CpuTexture.SrgbToLinear(U8(0)), CpuTexture.SrgbToLinear(U8(1)), CpuTexture.SrgbToLinear(U8(2)), U8(3));
            case PixelFormat.B8G8R8A8_UNorm:
                return new CslTypes.float4(U8(2), U8(1), U8(0), U8(3));
            case PixelFormat.B8G8R8A8_UNorm_SRgb:
                return new CslTypes.float4(CpuTexture.SrgbToLinear(U8(2)), CpuTexture.SrgbToLinear(U8(1)), CpuTexture.SrgbToLinear(U8(0)), U8(3));
            case PixelFormat.R16G16B16A16_Float:
                return new CslTypes.float4(F16(0), F16(1), F16(2), F16(3));
            case PixelFormat.R16G16_Float:
                return new CslTypes.float4(F16(0), F16(1), 0f, 1f);
            case PixelFormat.R16_Float:
                return new CslTypes.float4(F16(0), 0f, 0f, 1f);
            case PixelFormat.R32G32B32A32_Float:
                return new CslTypes.float4(F32(0), F32(1), F32(2), F32(3));
            case PixelFormat.R32G32B32_Float:
                return new CslTypes.float4(F32(0), F32(1), F32(2), 1f);
            case PixelFormat.R32G32_Float:
                return new CslTypes.float4(F32(0), F32(1), 0f, 1f);
            case PixelFormat.R32_Float:
                return new CslTypes.float4(F32(0), 0f, 0f, 1f);
            case PixelFormat.R8_UNorm:
                return new CslTypes.float4(U8(0), 0f, 0f, 1f);
            case PixelFormat.R8G8_UNorm:
                return new CslTypes.float4(U8(0), U8(1), 0f, 1f);
            case PixelFormat.R16_UNorm:
                return new CslTypes.float4(BitConverter.ToUInt16(b, at) / 65535f, 0f, 0f, 1f);
            case PixelFormat.R10G10B10A2_UNorm:
            {
                uint v = BitConverter.ToUInt32(b, at);
                return new CslTypes.float4((v & 1023) / 1023f, ((v >> 10) & 1023) / 1023f, ((v >> 20) & 1023) / 1023f, (v >> 30) / 3f);
            }
            case PixelFormat.R11G11B10_Float:
            {
                uint v = BitConverter.ToUInt32(b, at);
                return new CslTypes.float4(Small(v & 0x7FF, 6), Small((v >> 11) & 0x7FF, 6), Small((v >> 22) & 0x3FF, 5), 1f);
            }
            default:
                throw new NotSupportedException($"Reading {format} back for the CPU is not supported yet");
        }
    }

    /// <summary>An unsigned small float (R11G11B10): 5 exponent bits, <paramref name="mantissa"/> bits.</summary>
    private static float Small(uint bits, int mantissa)
    {
        uint exponent = bits >> mantissa, fraction = bits & ((1u << mantissa) - 1);
        if (exponent == 0)
            return fraction / (float)(1 << mantissa) * MathF.Pow(2f, -14f);
        if (exponent == 31)
            return fraction == 0 ? float.PositiveInfinity : float.NaN;
        return (1f + fraction / (float)(1 << mantissa)) * MathF.Pow(2f, exponent - 15f);
    }

    private static CslTypes.uint4 DecodeInteger(PixelFormat format, byte[] b, int at) => format switch
    {
        PixelFormat.R32_UInt or PixelFormat.R32_SInt => new CslTypes.uint4(BitConverter.ToUInt32(b, at), 0, 0, 1),
        PixelFormat.R32G32B32A32_UInt => new CslTypes.uint4(BitConverter.ToUInt32(b, at), BitConverter.ToUInt32(b, at + 4), BitConverter.ToUInt32(b, at + 8), BitConverter.ToUInt32(b, at + 12)),
        _ => throw new NotSupportedException($"Reading {format} back for the CPU is not supported yet"),
    };
}

/// <summary>The C# class of a shader, by its SDSL name: a [Shader] class of the app, or the engine's in Csl.Engine.</summary>
public static class ShaderTypes
{
    private static readonly ConcurrentDictionary<string, Type?> Cache = new(StringComparer.Ordinal);

    /// <summary>The class whose generated <c>ShaderName</c> is <paramref name="shaderName"/>; a live-reload name (<c>DemoTile_3</c>) finds <c>DemoTile</c>.</summary>
    public static Type? Find(string shaderName) => Cache.GetOrAdd(shaderName, name =>
    {
        foreach (var candidate in new[] { name, System.Text.RegularExpressions.Regex.Replace(name, @"_\d+$", string.Empty) })
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray()!;
                }
                foreach (var type in types)
                {
                    if (type.GetCustomAttribute<ShaderAttribute>() == null)
                        continue;
                    var declared = type.GetField("ShaderName", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)?.GetRawConstantValue() as string ?? type.Name;
                    if (declared == candidate)
                        return type;
                }
            }
        }
        return null;
    });
}
