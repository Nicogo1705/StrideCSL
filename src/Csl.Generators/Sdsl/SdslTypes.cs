using System;
using System.Text.RegularExpressions;

namespace Csl.Generators.Sdsl;

/// <summary>What kind of thing an HLSL type declares, and the C# the engine's key generator maps it to.</summary>
public enum SdslResourceKind
{
    None,
    Texture,
    TypedBuffer,
    StructuredBuffer,
    RawBuffer,
    Sampler,
}

public static class SdslTypes
{
    private static readonly Regex VectorPattern = new Regex(@"^(float|half|int|uint|double|bool)([1-4])$", RegexOptions.Compiled);
    private static readonly Regex MatrixPattern = new Regex(@"^(float|half|int|uint|double|bool)([1-4])x([1-4])$", RegexOptions.Compiled);

    public static bool IsResource(string type) => ResourceKind(type) != SdslResourceKind.None;

    public static bool IsReadWriteType(string type) => type.StartsWith("RW", StringComparison.Ordinal)
        || type.StartsWith("Append", StringComparison.Ordinal) || type.StartsWith("Consume", StringComparison.Ordinal)
        || type.StartsWith("RasterizerOrdered", StringComparison.Ordinal);

    public static SdslResourceKind ResourceKind(string type)
    {
        var bare = type.StartsWith("RW", StringComparison.Ordinal) ? type.Substring(2)
            : type.StartsWith("RasterizerOrdered", StringComparison.Ordinal) ? type.Substring("RasterizerOrdered".Length)
            : type;
        if (bare.StartsWith("Texture", StringComparison.Ordinal) || bare == "TextureCube" || bare == "TextureCubeArray")
            return SdslResourceKind.Texture;
        switch (bare)
        {
            case "Buffer":
                return SdslResourceKind.TypedBuffer;
            case "StructuredBuffer":
            case "AppendStructuredBuffer":
            case "ConsumeStructuredBuffer":
                return SdslResourceKind.StructuredBuffer;
            case "ByteAddressBuffer":
                return SdslResourceKind.RawBuffer;
            case "SamplerState":
            case "SamplerComparisonState":
                return SdslResourceKind.Sampler;
        }
        return SdslResourceKind.None;
    }

    /// <summary>
    /// The C# type the engine's key generator gives a member of this type, or null when it has no
    /// mapping we know (a struct, a matrix other than float4x4). Mirrors Stride.Shaders.Generators.
    /// </summary>
    public static string? ToCSharp(SdslMember member)
    {
        foreach (var (name, argument) in member.Attributes)
        {
            if (name == "Type" && argument != null)
                return argument;
        }
        bool isColor = member.Attributes.Exists(a => a.Name == "Color");
        var element = ToCSharpElement(member.Type, isColor);
        if (element == null)
            return null;
        return member.IsArray ? element + "[]" : element;
    }

    private static string? ToCSharpElement(string type, bool isColor)
    {
        switch (ResourceKind(type))
        {
            case SdslResourceKind.Texture: return "Texture";
            case SdslResourceKind.TypedBuffer:
            case SdslResourceKind.StructuredBuffer:
            case SdslResourceKind.RawBuffer: return "Buffer";
            case SdslResourceKind.Sampler: return "SamplerState";
        }
        switch (type)
        {
            case "bool": return "bool";
            case "int": return "int";
            case "uint": return "uint";
            case "int64_t": return "long";
            case "uint64_t": return "ulong";
            case "half":
            case "float": return "float";
            case "double": return "double";
        }
        var vector = VectorPattern.Match(type);
        if (vector.Success)
        {
            var scalar = vector.Groups[1].Value;
            var size = vector.Groups[2].Value;
            switch (scalar)
            {
                case "float":
                case "half":
                    return isColor && (size == "3" || size == "4") ? "Color" + size : "Vector" + size;
                case "double": return "Double" + size;
                case "int":
                case "uint": return "Int" + size;
            }
            return null;
        }
        var matrix = MatrixPattern.Match(type);
        if (matrix.Success)
            return matrix.Groups[1].Value == "float" && matrix.Groups[2].Value == "4" && matrix.Groups[3].Value == "4" ? "Matrix" : null;
        return null;
    }

    /// <summary>Whether the engine registers the key as an object key (resources) rather than a value key.</summary>
    public static bool IsObjectKey(SdslMember member) => IsResource(member.Type);

    /// <summary>
    /// Formats that Direct3D 11 allows a typed UAV to be loaded from. A resource a shader both reads and
    /// writes through a RW view must use one of these, or the read silently returns zero on some drivers
    /// and fails validation on others.
    /// </summary>
    public static bool IsTypedUavLoadElement(string csharpElementType) => csharpElementType is "float" or "uint" or "int";
}
