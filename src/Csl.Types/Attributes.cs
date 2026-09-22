using System;

namespace Csl;

/// <summary>
/// Marks a partial class as a shader: the generator translates it to SDSL, emits its *Keys class
/// and its wrapper. The class's base is the shader's first base; more come from <see cref="MixinAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ShaderAttribute : Attribute
{
    /// <summary>The SDSL class name, when it differs from the C# one.</summary>
    public string? Name { get; set; }

    /// <summary>The shader exists in the engine (or in a .sdsl file): the class only describes it, nothing is generated.</summary>
    public bool External { get; set; }
}

/// <summary>Further base shaders, in order, after the C# base: <c>shader X : Base, A, B</c>. Their members are callable through the stubs the generator adds.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MixinAttribute : Attribute
{
    public MixinAttribute(params Type[] shaders) => Shaders = shaders;

    public Type[] Shaders { get; }
}

/// <summary>A composed shader slot: <c>compose T name;</c></summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ComposeAttribute : Attribute { }

/// <summary>A stage member: shared by every mixin of the stage, and a shader parameter.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class StageAttribute : Attribute { }

/// <summary>A stream: a value that flows between shader stages, read as <c>streams.Name</c> in SDSL.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class StreamAttribute : Attribute
{
    public StreamAttribute(string? semantic = null) => Semantic = semantic;

    /// <summary>The HLSL semantic, SV_DispatchThreadID say, or null.</summary>
    public string? Semantic { get; }
}

/// <summary>Memory shared by the threads of a group: <c>groupshared</c>.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class GroupSharedAttribute : Attribute { }

/// <summary>Links a member to a parameter key of another name: <c>[Link("Shader.Name")]</c>.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class LinkAttribute : Attribute
{
    public LinkAttribute(string key) => Key = key;

    public string Key { get; }
}

/// <summary>A float vector parameter that holds a colour, keyed as Color3/Color4 rather than Vector3/Vector4.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ColorAttribute : Attribute { }

/// <summary>The thread group size the shader is written for. The wrapper uses it by default.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NumThreadsAttribute : Attribute
{
    public NumThreadsAttribute(int x, int y = 1, int z = 1)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public int X { get; }
    public int Y { get; }
    public int Z { get; }
}
