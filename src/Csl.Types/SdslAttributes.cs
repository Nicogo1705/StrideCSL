using System;

namespace Csl;

// Attributes for what SDSL declares and C# has no keyword for. Each maps to one piece of SDSL syntax;
// the SDSL to C# conversion writes them, the generator reads them back.

/// <summary>A patch-constant stream of a tessellation stage: <c>patchstream T name : SEMANTIC;</c></summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class PatchStreamAttribute : Attribute
{
    public PatchStreamAttribute(string? semantic = null) => Semantic = semantic;

    public string? Semantic { get; }
}

/// <summary>A generic parameter of the shader: <c>shader X&lt;LinkType TTexture, int TCount&gt;</c>, one field each, in order.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class GenericAttribute : Attribute { }

/// <summary>The member is in a constant buffer: <c>cbuffer PerMaterial { ... }</c>.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Method)]
public sealed class CBufferAttribute : Attribute
{
    public CBufferAttribute(string? name = null) => Name = name;

    public string? Name { get; }

    /// <summary>The member opens another block of the same name: <c>cbuffer A { x } cbuffer A { y }</c> are two buffers.</summary>
    public bool NewBlock { get; set; }
}

/// <summary>The member is in a resource group: <c>rgroup PerView.Lighting { ... }</c>.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Method)]
public sealed class RGroupAttribute : Attribute
{
    public RGroupAttribute(string? name = null) => Name = name;

    public string? Name { get; }

    /// <summary>The member opens another group of the same name.</summary>
    public bool NewBlock { get; set; }
}

/// <summary>The member is in a texture buffer: <c>tbuffer Name { ... }</c>.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Method)]
public sealed class TBufferAttribute : Attribute
{
    public TBufferAttribute(string? name = null) => Name = name;

    public string? Name { get; }

    /// <summary>The member opens another buffer of the same name.</summary>
    public bool NewBlock { get; set; }
}

/// <summary>The sizes of an array, as SDSL writes them: <c>float4 Values[TCount];</c> is <c>[Size("TCount")] float4[] Values</c>.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SizeAttribute : Attribute
{
    public SizeAttribute(params string[] sizes) => Sizes = sizes;

    public string[] Sizes { get; }
}

/// <summary>An HLSL semantic: <c>float4 Position : SV_Position</c>, on a field, a parameter, or a return value.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue)]
public sealed class SemanticAttribute : Attribute
{
    public SemanticAttribute(string name) => Name = name;

    public string Name { get; }
}

/// <summary>HLSL keywords C# has no place for: <c>nointerpolation</c>, <c>const</c> on a parameter, <c>triangle</c> on a geometry shader input…</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Method, AllowMultiple = true)]
public sealed class ModifiersAttribute : Attribute
{
    public ModifiersAttribute(string modifiers) => Modifiers = modifiers;

    public string Modifiers { get; }
}

/// <summary>An SDSL type C# cannot name, on a member declared <c>dynamic</c>: <c>[Type("TriangleStream&lt;Output&gt;")]</c>.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class TypeAttribute : Attribute
{
    public TypeAttribute(string name) => Name = name;

    public string Name { get; }
}

/// <summary>An attribute written as is in the SDSL: <c>[Hlsl("maxvertexcount(3)")]</c> is <c>[maxvertexcount(3)]</c>.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Struct | AttributeTargets.Parameter, AllowMultiple = true)]
public sealed class HlslAttribute : Attribute
{
    public HlslAttribute(string text) => Text = text;

    public string Text { get; }
}

/// <summary>The member exists only when the preprocessor condition holds: <c>#if COND ... #endif</c> around it.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Method | AttributeTargets.Struct)]
public sealed class IfAttribute : Attribute
{
    public IfAttribute(string condition) => Condition = condition;

    public string Condition { get; }
}

/// <summary>
/// A macro the shader defines: <c>#define Name Value</c>, under <see cref="If"/> when set. The
/// engine's way of giving a macro a default is <c>If = "!defined(Name)"</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class DefineAttribute : Attribute
{
    public DefineAttribute(string name, string? value = null)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string? Value { get; }
    public string? If { get; set; }
}

/// <summary>
/// The method overrides one of a [Mixin] shader. C# cannot say <c>override</c> there (the method is
/// not in the C# base class), so the attribute says it: the SDSL gets <c>override</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OverrideAttribute : Attribute { }

/// <summary>
/// An abstract method declared again in a shader whose C# base already has it: C# writes
/// <c>abstract override</c>, SDSL just <c>abstract</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RedeclareAttribute : Attribute { }

/// <summary><c>clone</c>: the member is duplicated for each composition instance.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Method)]
public sealed class CloneAttribute : Attribute { }

/// <summary>A sampler state description: <c>SamplerState S { Filter = MIN_MAG_MIP_POINT; AddressU = Wrap; }</c>.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SamplerAttribute : Attribute
{
    public string? Filter { get; set; }
    public string? AddressU { get; set; }
    public string? AddressV { get; set; }
    public string? AddressW { get; set; }
    public string? MipLODBias { get; set; }
    public string? MaxAnisotropy { get; set; }
    public string? ComparisonFunc { get; set; }
    public string? BorderColor { get; set; }
    public string? MinLOD { get; set; }
    public string? MaxLOD { get; set; }
}
