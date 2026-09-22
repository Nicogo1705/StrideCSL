using System.Collections.Generic;

namespace Csl.Generators.Sdsl;

/// <summary>What a shader does with a resource, found by reading its method bodies.</summary>
public enum SdslAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    ReadWrite = Read | Write,
}

/// <summary>A parsed .sdsl file: its namespace and the shaders it declares.</summary>
public sealed class SdslFile
{
    public SdslFile(string path)
    {
        Path = path;
    }

    public string Path { get; }
    public string? Namespace { get; set; }
    public List<SdslShader> Shaders { get; } = new List<SdslShader>();
    public List<SdslError> Errors { get; } = new List<SdslError>();
}

public sealed class SdslError
{
    public SdslError(string message, int line, int column)
    {
        Message = message;
        Line = line;
        Column = column;
    }

    public string Message { get; }
    public int Line { get; }
    public int Column { get; }
}

public sealed class SdslShader
{
    public SdslShader(string name, int line)
    {
        Name = name;
        Line = line;
    }

    public string Name { get; }
    public int Line { get; }
    public List<string> Doc { get; } = new List<string>();

    /// <summary>Base shaders, in declaration order, generic arguments stripped.</summary>
    public List<string> Bases { get; } = new List<string>();

    /// <summary>Generic parameters of the shader itself, if any. A generic shader gets no wrapper.</summary>
    public List<string> GenericParameters { get; } = new List<string>();

    public List<SdslMember> Members { get; } = new List<SdslMember>();
    public List<SdslMethod> Methods { get; } = new List<SdslMethod>();

    /// <summary>Resource members by name with what the bodies do to them.</summary>
    public Dictionary<string, SdslAccess> Usage { get; } = new Dictionary<string, SdslAccess>();

    /// <summary>The thread group size the shader was written for ([NumThreads] on a C# shader), or null.</summary>
    public (int X, int Y, int Z)? DefaultThreads { get; set; }
}

public sealed class SdslMember
{
    public SdslMember(string type, string name, int line, int column)
    {
        Type = type;
        Name = name;
        Line = line;
        Column = column;
    }

    /// <summary>The type name without generic arguments: Texture3D, float3, int.</summary>
    public string Type { get; }

    /// <summary>The text inside the type's angle brackets, if any: float2 for Texture3D&lt;float2&gt;.</summary>
    public string? GenericArgument { get; set; }

    public string Name { get; }
    public int Line { get; }
    public int Column { get; }
    public bool IsArray { get; set; }
    public bool IsStage { get; set; }
    public bool IsStream { get; set; }
    public bool IsCompose { get; set; }
    public bool IsConst { get; set; }
    public bool IsStatic { get; set; }
    public bool IsGroupShared { get; set; }
    public List<string> Doc { get; } = new List<string>();

    /// <summary>Attribute names, with the first string argument where there is one: Color, Type("X"), Link("Y").</summary>
    public List<(string Name, string? Argument)> Attributes { get; } = new List<(string, string?)>();

    /// <summary>The initializer as written, or null.</summary>
    public string? Initializer { get; set; }

    /// <summary>The HLSL semantic after the name (SV_DispatchThreadID), or null.</summary>
    public string? Semantic { get; set; }
}

public sealed class SdslMethod
{
    public SdslMethod(string name, int line)
    {
        Name = name;
        Line = line;
    }

    public string Name { get; }
    public int Line { get; }
    public bool IsAbstract { get; set; }
    public bool IsOverride { get; set; }
    public List<string> Doc { get; } = new List<string>();
    public SdslSignature? Signature { get; set; }
    public List<SdslToken> Body { get; } = new List<SdslToken>();
}

/// <summary>Return type and parameters of a method, for stubs.</summary>
public sealed class SdslSignature
{
    public SdslSignature(string returnType, string? returnGeneric)
    {
        ReturnType = returnType;
        ReturnGeneric = returnGeneric;
    }

    public string ReturnType { get; }
    public string? ReturnGeneric { get; }
    public List<SdslParameter> Parameters { get; } = new List<SdslParameter>();
}

public sealed class SdslParameter
{
    public SdslParameter(string type, string? generic, string name, string? modifier)
    {
        Type = type;
        Generic = generic;
        Name = name;
        Modifier = modifier;
    }

    public string Type { get; }
    public string? Generic { get; }
    public string Name { get; }

    /// <summary>in, out, inout, or null.</summary>
    public string? Modifier { get; }
}
