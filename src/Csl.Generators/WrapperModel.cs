using System.Collections.Generic;
using Csl.Generators.Sdsl;

namespace Csl.Generators;

/// <summary>What one generated <c>&lt;Shader&gt;Effect</c> class contains.</summary>
public sealed class WrapperModel
{
    public WrapperModel(string shaderName, string? ns, string keysNamespace)
    {
        ShaderName = shaderName;
        Namespace = ns;
        KeysNamespace = keysNamespace;
    }

    public string ShaderName { get; }
    public string? Namespace { get; }

    /// <summary>Where the engine's generator put the *Keys class: the file's namespace, or Stride.Rendering for none.</summary>
    public string KeysNamespace { get; }

    public string ClassName => ShaderName + "Effect";

    /// <summary>The wrapper of the first project base, in the shader's own namespace, or null for Csl.ComputeEffect.</summary>
    public string? BaseClassName { get; set; }
    public string? BaseClassNamespace { get; set; }

    /// <summary>Whether the base wrapper is itself a runnable compute shader, so its members must be hidden with new.</summary>
    public bool BaseIsCompute { get; set; }

    /// <summary>Whether the base wrapper declares a Slots class this one hides.</summary>
    public bool BaseHasSlots { get; set; }

    /// <summary>Inherits ComputeShaderBase, so it can be dispatched; otherwise it is a mixin, and abstract.</summary>
    public bool IsCompute { get; set; }

    /// <summary>[NumThreads] of a C# shader: the wrapper gets a constructor that uses it.</summary>
    public (int X, int Y, int Z)? DefaultThreads { get; set; }

    public List<string> Doc { get; } = new List<string>();
    public List<PropertyModel> Properties { get; } = new List<PropertyModel>();
}

public sealed class PropertyModel
{
    public PropertyModel(string name, string csharpType, string keysType)
    {
        Name = name;
        CSharpType = csharpType;
        KeysType = keysType;
    }

    public string Name { get; }

    /// <summary>The C# type of the key: float, Int3, Texture, Buffer, SamplerState.</summary>
    public string CSharpType { get; }

    /// <summary>Fully qualified *Keys class the key lives on: the shader that declares the member.</summary>
    public string KeysType { get; }

    public List<string> Doc { get; } = new List<string>();

    /// <summary>Set for resources: the HLSL declaration and how the shader uses it.</summary>
    public SlotModel? Slot { get; set; }
}

public sealed class SlotModel
{
    public SlotModel(string shaderName, string hlslType, string? elementType, SdslResourceKind kind, SdslAccess access)
    {
        ShaderName = shaderName;
        HlslType = hlslType;
        ElementType = elementType;
        Kind = kind;
        Access = access;
    }

    public string ShaderName { get; }
    public string HlslType { get; }
    public string? ElementType { get; }
    public SdslResourceKind Kind { get; }
    public SdslAccess Access { get; }
}
