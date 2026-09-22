using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Csl.Generators.Sdsl.Syntax;

namespace Csl.Generators.Conversion;

public sealed class SdslToCSharpOptions
{
    /// <summary>Only the declarations, bodies left out: a [Shader(External = true)] description of a shader that exists elsewhere.</summary>
    public bool DeclarationsOnly { get; set; }

    /// <summary>The C# namespace; null keeps the shader's own (none when it has none).</summary>
    public string? Namespace { get; set; }

    /// <summary>More namespaces to import, where the shaders this one names are (Csl.Engine, say).</summary>
    public List<string> Usings { get; } = new List<string>();
}

/// <summary>
/// Writes a shader as a C# [Shader] class: the reverse of what the generator does. The C# is typed
/// against Csl.Types, so what the SDSL leaves implicit (a float3 narrowed to a float2, an int used as
/// a condition) may not compile as written; <see cref="CSharpFixer"/> makes those explicit afterwards.
/// </summary>
public sealed class SdslToCSharp
{
    private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };

    private static readonly Regex VectorOrMatrix = new Regex(@"^(bool|int|uint|half|float|double)([1-4])(x[1-4])?$", RegexOptions.Compiled);

    private static readonly HashSet<string> DynamicTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Streams", "Input", "Output", "Input2", "Output2", "Constants", "InputPatch", "OutputPatch", "TriangleStream", "LineStream", "PointStream",
    };

    private static readonly Dictionary<string, string> LoopMarkers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["unroll"] = "Unroll",
        ["loop"] = "Loop",
        ["branch"] = "Branch",
        ["flatten"] = "Flatten",
    };

    private readonly SdslShaderDeclaration shader;
    private readonly SdslShaderIndex index;
    private readonly SdslToCSharpOptions options;
    private readonly List<SdslDiagnostic> diagnostics;
    private readonly StringBuilder sb = new StringBuilder();
    private readonly HashSet<string> memberNames = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> genericKinds = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly HashSet<string> structs = new HashSet<string>(StringComparer.Ordinal);
    private int indent;

    /// <summary>Locals and parameters in scope, innermost last: they shadow shader names.</summary>
    private readonly List<HashSet<string>> scopes = new List<HashSet<string>>();

    private SdslToCSharp(SdslShaderDeclaration shader, SdslShaderIndex index, SdslToCSharpOptions options, List<SdslDiagnostic> diagnostics)
    {
        this.shader = shader;
        this.index = index;
        this.options = options;
        this.diagnostics = diagnostics;
        foreach (var parameter in shader.GenericParameters)
            genericKinds[parameter.Name] = parameter.Type;
        foreach (var member in shader.Members)
        {
            if (member is SdslStruct declaration)
                structs.Add(declaration.Name);
        }
    }

    /// <summary>The C# file of one shader. Problems the conversion cannot express are added to <paramref name="diagnostics"/>.</summary>
    public static string Convert(SdslShaderDeclaration shader, SdslShaderIndex index, SdslToCSharpOptions options, List<SdslDiagnostic> diagnostics, IEnumerable<string>? fileComments = null)
    {
        var converter = new SdslToCSharp(shader, index, options, diagnostics);
        converter.EmitFile(fileComments);
        return converter.sb.ToString();
    }

    public static string EscapeIdentifier(string name) => CSharpKeywords.Contains(name) && name != "base" && name != "this" ? "@" + name : name;

    private void Error(string message, SdslPosition at) => diagnostics.Add(new SdslDiagnostic(message, at));

    // -- file ----------------------------------------------------------------------------------------

    private void EmitFile(IEnumerable<string>? fileComments)
    {
        // Comments before the shader that are not its documentation: the file header.
        var header = new List<string>();
        if (fileComments != null)
            header.AddRange(fileComments.Where(c => !IsDoc(c)));
        header.AddRange(shader.Comments.Where(c => !IsDoc(c)));
        while (header.Count > 0 && header[header.Count - 1].Length == 0)
            header.RemoveAt(header.Count - 1);
        foreach (var comment in header)
            CommentLine(comment);
        if (header.Count > 0)
            Line(string.Empty);

        Line("using Csl;");
        Line("using Csl.Hlsl;");
        var ownNamespace = options.Namespace ?? shader.Namespace;
        var usings = new SortedSet<string>(options.Usings, StringComparer.Ordinal);
        if (options.Namespace == null)
        {
            // The shaders this one names may be in any namespace of the set: import them all.
            foreach (var other in index.Shaders)
                if (other.Namespace != null && other.Namespace != ownNamespace && !index.IsExternal(other.Name))
                    usings.Add(other.Namespace);
        }
        foreach (var ns in usings)
            Line("using " + ns + ";");
        Line("using static Csl.Hlsl.Intrinsics;");
        Line(string.Empty);

        var ns2 = options.Namespace ?? shader.Namespace;
        if (ns2 != null)
        {
            Line("namespace " + ns2 + ";");
            Line(string.Empty);
        }

        foreach (var comment in shader.Comments.Where(IsDoc))
            Line(comment.Trim());
        EmitClassAttributes();

        var baseClause = string.Empty;
        if (shader.Bases.Count > 0)
            baseClause = " : " + TypeName(shader.Bases[0].Name);
        Line("public abstract partial class " + EscapeIdentifier(shader.Name) + baseClause);
        Line("{");
        indent++;

        bool first = true;
        foreach (var parameter in shader.GenericParameters)
        {
            Line("[Generic] public static " + GenericParameterType(parameter.Type) + " " + EscapeIdentifier(parameter.Name) + ";");
            memberNames.Add(parameter.Name);
            first = false;
        }

        foreach (var member in shader.Members)
        {
            switch (member)
            {
                case SdslVariable variable:
                    memberNames.Add(variable.Name);
                    break;
                case SdslMethod method:
                    memberNames.Add(method.Name);
                    break;
            }
        }

        // A variable declared more than once, under different #if: the last one is the C# field, the
        // others its variants.
        var lastByName = new Dictionary<string, SdslVariable>(StringComparer.Ordinal);
        foreach (var member in shader.Members)
            if (member is SdslVariable variable)
                lastByName[variable.Name] = variable;
        foreach (var member in shader.Members)
        {
            if (member is SdslVariable variable && lastByName[variable.Name] != variable)
            {
                if (variable.Conditions.Count == 0)
                    Error("Member " + variable.Name + " is declared twice", variable.Position);
                variants.TryGetValue(lastByName[variable.Name], out var list);
                variants[lastByName[variable.Name]] = (list ?? new List<SdslVariable>()).Concat(new[] { variable }).ToList();
            }
        }

        foreach (var member in shader.Members)
        {
            if (member is SdslVariable repeated && lastByName[repeated.Name] != repeated)
                continue;
            if (!first && (member.BlankLineBefore || member is SdslMethod || member is SdslStruct))
                Line(string.Empty);
            first = false;
            switch (member)
            {
                case SdslVariable variable:
                    EmitVariable(variable);
                    previousGroup = variable.Group;
                    break;
                case SdslMethod method:
                    EmitMethod(method);
                    break;
                case SdslStruct declaration:
                    EmitStruct(declaration);
                    break;
                case SdslTypedef typedef:
                    Error("typedef " + typedef.Name + " is not converted", typedef.Position);
                    break;
            }
        }

        indent--;
        Line("}");
    }

    private static bool IsDoc(string comment) => comment.StartsWith("///", StringComparison.Ordinal) && !comment.StartsWith("////", StringComparison.Ordinal);

    private void EmitClassAttributes()
    {
        var shaderArguments = new List<string>();
        if (options.DeclarationsOnly)
            shaderArguments.Add("External = true");
        if (shader.Modifiers.Contains("internal"))
            shaderArguments.Add("Internal = true");
        if (shader.Bases.Count > 0 && shader.Bases[0].GenericArguments.Count > 0)
            shaderArguments.Add("BaseGenerics = " + Quote(string.Join(", ", shader.Bases[0].GenericArguments)));
        Line(shaderArguments.Count == 0 ? "[Shader]" : "[Shader(" + string.Join(", ", shaderArguments) + ")]");

        var plain = new List<string>();
        void FlushPlain()
        {
            if (plain.Count > 0)
                Line("[Mixin(" + string.Join(", ", plain.Select(p => "typeof(" + p + ")")) + ")]");
            plain.Clear();
        }
        for (int i = 1; i < shader.Bases.Count; i++)
        {
            var reference = shader.Bases[i];
            if (reference.GenericArguments.Count == 0)
            {
                plain.Add(TypeName(reference.Name));
                continue;
            }
            FlushPlain();
            Line("[Mixin(typeof(" + TypeName(reference.Name) + "), Generics = " + Quote(string.Join(", ", reference.GenericArguments)) + ")]");
        }
        FlushPlain();

        foreach (var error in shader.Errors)
            Line("[PreprocessorError(" + Quote(error.Name) + (error.Condition != null ? ", If = " + Quote(error.Condition) : string.Empty) + ")]");

        foreach (var define in shader.Defines)
        {
            var arguments = new List<string> { Quote(define.Name) };
            if (define.Value != null)
                arguments.Add(Quote(define.Value));
            if (define.Condition != null)
                arguments.Add("If = " + Quote(define.Condition));
            Line("[Define(" + string.Join(", ", arguments) + ")]");
        }
    }

    // -- members -------------------------------------------------------------------------------------

    private void EmitComments(SdslNode node)
    {
        foreach (var comment in node.Comments)
            CommentLine(comment);
    }

    private void EmitVariable(SdslVariable variable)
    {
        EmitComments(variable);
        var attributes = new List<string>();
        if (variable.Conditions.Count > 0)
            attributes.Add("If(" + Quote(JoinConditions(variable.Conditions)) + ")");
        if (variable.Group != null)
            attributes.Add(GroupAttributeOf(variable));

        bool isStatic = variable.Has("static");
        bool isConst = variable.Has("const");
        bool isCompose = variable.Has("compose");
        foreach (var modifier in variable.Modifiers)
        {
            switch (modifier)
            {
                case "stage": attributes.Add("Stage"); break;
                case "stream": attributes.Add(variable.Semantic == null ? "Stream" : "Stream(" + Quote(variable.Semantic) + ")"); break;
                case "patchstream": attributes.Add(variable.Semantic == null ? "PatchStream" : "PatchStream(" + Quote(variable.Semantic) + ")"); break;
                case "compose": attributes.Add("Compose"); break;
                case "groupshared": attributes.Add("GroupShared"); break;
                case "clone": attributes.Add("Clone"); break;
                case "static":
                case "const":
                    break;
                default: attributes.Add("Modifiers(" + Quote(modifier) + ")"); break;
            }
        }
        if (variable.Semantic != null && !variable.Has("stream") && !variable.Has("patchstream"))
            attributes.Add("Semantic(" + Quote(variable.Semantic) + ")");
        if (isConst && !isStatic)
            attributes.Add("Modifiers(\"const\")");
        foreach (var attribute in variable.Attributes)
            attributes.Add(MemberAttribute(attribute));
        if (variable.ArraySizes.Count > 0 && variable.ArraySizes.Any(s => s.Length > 0))
            attributes.Add("Size(" + string.Join(", ", variable.ArraySizes.Select(Quote)) + ")");
        if (variable.SamplerState != null)
            attributes.Add(SamplerAttribute(variable.SamplerState));
        if (variants.TryGetValue(variable, out var others))
            foreach (var other in others)
                attributes.Add("Variant(" + Quote(SdslPrinter.Variable(other)) + (other.Conditions.Count > 0 ? ", If = " + Quote(JoinConditions(other.Conditions)) : string.Empty) + ")");

        var type = MemberType(variable.Type, variable.Position, out var sdslType);
        if (sdslType != null)
            attributes.Add("Type(" + Quote(sdslType) + ")");
        for (int i = 0; i < variable.ArraySizes.Count; i++)
            type += "[]";

        var line = new StringBuilder();
        if (attributes.Count > 0)
            line.Append('[').Append(string.Join(", ", attributes)).Append("] ");
        line.Append("public ");
        bool csharpConst = false;
        if (isStatic && isConst)
        {
            csharpConst = variable.ArraySizes.Count == 0 && IsConstType(type) && variable.Initializer != null && IsConstantExpression(variable.Initializer);
            line.Append(csharpConst ? "const " : "static readonly ");
        }
        else if (isStatic)
            line.Append("static ");
        line.Append(type).Append(' ').Append(EscapeIdentifier(variable.Name));
        if (variable.Initializer != null && !options.DeclarationsOnly || csharpConst)
            line.Append(" = ").Append(Initializer(variable.Initializer!, variable.Type, variable.ArraySizes));
        else if (variable.Initializer != null && isStatic)
            line.Append(" = default!");
        line.Append(';');
        Line(line.ToString());
    }

    private readonly Dictionary<SdslVariable, List<SdslVariable>> variants = new Dictionary<SdslVariable, List<SdslVariable>>();

    /// <summary>The group of the member before the one being written: two blocks of one name stay two.</summary>
    private SdslGroup? previousGroup;

    private string GroupAttributeOf(SdslMember member)
    {
        var group = member.Group!;
        var text = GroupAttribute(group);
        if (previousGroup != null && previousGroup != group && previousGroup.Kind == group.Kind && previousGroup.Name == group.Name)
            text = text.Substring(0, text.Length - 1) + ", NewBlock = true)";
        return text;
    }

    private static string JoinConditions(List<string> conditions) =>
        conditions.Count == 1 ? conditions[0] : string.Join(" && ", conditions.Select(c => "(" + c + ")"));

    private static string GroupAttribute(SdslGroup group)
    {
        var name = group.Kind switch { "cbuffer" => "CBuffer", "rgroup" => "RGroup", _ => "TBuffer" };
        return group.Name.Length == 0 ? name : name + "(" + Quote(group.Name) + ")";
    }

    private static string MemberAttribute(SdslAttribute attribute)
    {
        switch (attribute.Name)
        {
            case "Link" when attribute.StringArgument != null:
                return "Link(" + Quote(attribute.StringArgument) + ")";
            case "Color" when attribute.Arguments == null:
                return "Color";
            default:
                return "Hlsl(" + Quote(attribute.Text) + ")";
        }
    }

    private static readonly HashSet<string> SamplerFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "Filter", "AddressU", "AddressV", "AddressW", "MipLODBias", "MaxAnisotropy", "ComparisonFunc", "BorderColor", "MinLOD", "MaxLOD",
    };

    private string SamplerAttribute(List<KeyValuePair<string, string>> state)
    {
        var arguments = new List<string>();
        foreach (var pair in state)
        {
            if (SamplerFields.Contains(pair.Key))
                arguments.Add(pair.Key + " = " + Quote(pair.Value));
            else
                Error("Sampler state field " + pair.Key + " is not converted", shader.Position);
        }
        return "Sampler(" + string.Join(", ", arguments) + ")";
    }

    private static bool IsConstType(string type) => type is "bool" or "int" or "uint" or "float" or "double" or "long" or "ulong";

    private static bool IsConstantExpression(SdslExpression expression) => expression switch
    {
        SdslLiteral literal => literal.Kind != SdslLiteralKind.String,
        SdslUnary unary => !unary.Postfix && (unary.Operator == "-" || unary.Operator == "+" || unary.Operator == "~" || unary.Operator == "!") && IsConstantExpression(unary.Operand),
        SdslBinary binary => IsConstantExpression(binary.Left) && IsConstantExpression(binary.Right),
        _ => false,
    };

    private void EmitStruct(SdslStruct declaration)
    {
        EmitComments(declaration);
        var attributes = new List<string>();
        if (declaration.Conditions.Count > 0)
            attributes.Add("If(" + Quote(JoinConditions(declaration.Conditions)) + ")");
        foreach (var attribute in declaration.Attributes)
            attributes.Add(MemberAttribute(attribute));
        if (attributes.Count > 0)
            Line("[" + string.Join(", ", attributes) + "]");
        Line("public struct " + EscapeIdentifier(declaration.Name));
        Line("{");
        indent++;
        foreach (var field in declaration.Fields)
        {
            EmitComments(field);
            var fieldAttributes = new List<string>();
            if (field.Semantic != null)
                fieldAttributes.Add("Semantic(" + Quote(field.Semantic) + ")");
            foreach (var modifier in field.Modifiers)
                fieldAttributes.Add("Modifiers(" + Quote(modifier) + ")");
            foreach (var attribute in field.Attributes)
                fieldAttributes.Add(MemberAttribute(attribute));
            if (field.ArraySizes.Count > 0)
                fieldAttributes.Add("Size(" + string.Join(", ", field.ArraySizes.Select(Quote)) + ")");
            var type = MemberType(field.Type, field.Position, out var sdslType);
            if (sdslType != null)
                fieldAttributes.Add("Type(" + Quote(sdslType) + ")");
            for (int i = 0; i < field.ArraySizes.Count; i++)
                type += "[]";
            Line((fieldAttributes.Count > 0 ? "[" + string.Join(", ", fieldAttributes) + "] " : string.Empty) + "public " + type + " " + EscapeIdentifier(field.Name) + ";");
        }
        indent--;
        Line("}");
    }

    private void EmitMethod(SdslMethod method)
    {
        EmitComments(method);
        var attributes = new List<string>();
        if (method.Conditions.Count > 0)
            attributes.Add("If(" + Quote(JoinConditions(method.Conditions)) + ")");
        if (method.Group != null)
            attributes.Add(GroupAttributeOf(method));

        bool isOverride = method.Has("override");
        bool isAbstract = method.Has("abstract");
        bool isStatic = method.Has("static");
        bool csharpOverride = false;
        var inBase = index.FindInCSharpBases(shader, method.Name, method.Parameters.Count);
        if (isOverride)
        {
            csharpOverride = inBase != null;
            if (!csharpOverride)
                attributes.Add("Override");
        }
        else if (!isAbstract && !isStatic && inBase != null && inBase.Has("abstract"))
        {
            // SDSL implements an abstract method without saying override; C# has to.
            csharpOverride = true;
            attributes.Add("Redeclare");
        }
        bool stageAfterOverride = method.Modifiers.IndexOf("override") >= 0 && method.Modifiers.IndexOf("stage") > method.Modifiers.IndexOf("override");
        foreach (var modifier in method.Modifiers)
        {
            switch (modifier)
            {
                case "stage": attributes.Add(stageAfterOverride ? "Stage(AfterOverride = true)" : "Stage"); break;
                case "clone": attributes.Add("Clone"); break;
                case "override":
                case "abstract":
                case "static":
                case "virtual":
                    break;
                default: attributes.Add("Modifiers(" + Quote(modifier) + ")"); break;
            }
        }
        foreach (var attribute in method.Attributes)
            attributes.Add(MemberAttribute(attribute));

        var returnType = MemberType(method.ReturnType, method.Position, out var returnSdslType);
        if (returnSdslType != null)
            Error("Return type " + returnSdslType + " is not converted", method.Position);

        var header = new StringBuilder("public ");
        if (isStatic) header.Append("static ");
        else if (isAbstract && index.FindInCSharpBases(shader, method.Name, method.Parameters.Count) != null)
        {
            // Declared abstract again: C# has to say it overrides; the attribute says SDSL did not.
            header.Append("abstract override ");
            if (!isOverride)
                attributes.Add("Redeclare");
        }
        else if (isAbstract) header.Append("abstract ");
        else if (csharpOverride) header.Append("override ");
        else header.Append("virtual ");
        header.Append(returnType).Append(' ').Append(EscapeIdentifier(method.Name)).Append('(');

        var scope = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < method.Parameters.Count; i++)
        {
            var parameter = method.Parameters[i];
            if (i > 0) header.Append(", ");
            header.Append(Parameter(parameter));
            scope.Add(parameter.Name);
        }
        header.Append(')');

        if (attributes.Count > 0)
            Line("[" + string.Join(", ", attributes) + "]");
        if (method.ReturnSemantic != null)
            Line("[return: Semantic(" + Quote(method.ReturnSemantic) + ")]");

        if (isAbstract || method.Body == null)
        {
            if (!isAbstract)
                Error("Method " + method.Name + " has no body and is not abstract", method.Position);
            Line(header + ";");
            return;
        }
        if (options.DeclarationsOnly)
        {
            Line(header + " => throw Gpu.Only;");
            return;
        }
        Line(header.ToString());
        scopes.Add(scope);
        EmitBlock(method.Body);
        scopes.RemoveAt(scopes.Count - 1);
    }

    private string Parameter(SdslParameter parameter)
    {
        var attributes = new List<string>();
        string prefix = string.Empty;
        foreach (var modifier in parameter.Modifiers)
        {
            switch (modifier)
            {
                case "out": prefix = "out "; break;
                case "inout": prefix = "ref "; break;
                case "in": prefix = "in "; break;
                default: attributes.Add("Modifiers(" + Quote(modifier) + ")"); break;
            }
        }
        if (parameter.Semantic != null)
            attributes.Add("Semantic(" + Quote(parameter.Semantic) + ")");
        if (parameter.ArraySizes.Count > 0)
            attributes.Add("Size(" + string.Join(", ", parameter.ArraySizes.Select(Quote)) + ")");
        var type = MemberType(parameter.Type, parameter.Position, out var sdslType);
        if (sdslType != null)
            attributes.Add("Type(" + Quote(sdslType) + ")");
        for (int i = 0; i < parameter.ArraySizes.Count; i++)
            type += "[]";
        var text = new StringBuilder();
        if (attributes.Count > 0)
            text.Append('[').Append(string.Join(", ", attributes)).Append("] ");
        text.Append(prefix).Append(type).Append(' ').Append(EscapeIdentifier(parameter.Name));
        if (parameter.Default != null)
            text.Append(" = ").Append(Expression(parameter.Default));
        return text.ToString();
    }

    // -- types ---------------------------------------------------------------------------------------

    private static string GenericParameterType(string type) => type switch
    {
        "LinkType" or "Semantic" or "MemberName" => type,
        _ => MapTypeName(type) ?? type,
    };

    /// <summary>
    /// The C# type of a declaration. Types C# cannot name (the stream structures of geometry and
    /// tessellation stages) come out as dynamic, the SDSL type given back in <paramref name="sdslType"/>
    /// for a [Type] attribute.
    /// </summary>
    private string MemberType(SdslType type, SdslPosition at, out string? sdslType)
    {
        sdslType = null;
        if (DynamicTypes.Contains(type.Name)
            || ((type.Name == "Texture2DMS" || type.Name == "Texture2DMSArray") && type.Arguments.Count == 2 && !int.TryParse(type.Arguments[1].Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            // Texture2DMS<float4, MACRO> included: C# cannot write a sample count it does not know.
            sdslType = type.ToString();
            return "dynamic";
        }
        return TypeText(type, at);
    }

    private string TypeText(SdslType type, SdslPosition at)
    {
        if (type.Arguments.Count == 0)
        {
            // HLSL's matrix and vector without arguments are float4x4 and float4.
            if (type.Name == "matrix") return "float4x4";
            if (type.Name == "vector") return "float4";
            return TypeName(type.Name);
        }
        if (type.Name == "Texture2DMS" || type.Name == "Texture2DMSArray")
        {
            // The sample count is a value: a marker type stands for it (Samples4 for 4).
            if (type.Arguments.Count == 2 && int.TryParse(type.Arguments[1].Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return TypeName(type.Name) + "<" + TypeText(type.Arguments[0], at) + ", Samples" + type.Arguments[1].Name + ">";
        }
        if (type.Name == "vector" || type.Name == "matrix")
        {
            Error(type + " is not converted: write the type as float3 or float4x4", at);
            return type.Name;
        }
        return TypeName(type.Name) + "<" + string.Join(", ", type.Arguments.Select(a => TypeText(a, at))) + ">";
    }

    private static string? MapTypeName(string name)
    {
        switch (name)
        {
            case "void": case "bool": case "int": case "uint": case "float": case "double": case "half": return name;
            case "dword": return "uint";
            case "min16float": case "min10float": return "float";
            case "min16int": case "min12int": return "int";
            case "min16uint": return "uint";
            case "int64_t": return "long";
            case "uint64_t": return "ulong";
        }
        var match = VectorOrMatrix.Match(name);
        if (match.Success)
            return name;
        return null;
    }

    /// <summary>A type or shader name as C# spells it.</summary>
    private string TypeName(string name)
    {
        var mapped = MapTypeName(name);
        if (mapped != null)
            return mapped;
        // A struct declared in another shader, not reachable through the C# base chain: qualify it.
        if (!structs.Contains(name))
        {
            var owner = index.StructOwner(name);
            if (owner != null && owner != shader && !index.CSharpBaseChain(shader).Contains(owner))
                return EscapeIdentifier(owner.Name) + "." + EscapeIdentifier(name);
        }
        return EscapeIdentifier(name.Replace(".", "_"));
    }

    private bool IsStructName(string name) => structs.Contains(name) || index.StructOwner(name) != null;

    private static bool IsBuiltinValueType(string name) => SdslSyntaxParser.IsBuiltinType(name);

    // -- statements ----------------------------------------------------------------------------------

    private void EmitBlock(SdslBlock block)
    {
        Line("{");
        indent++;
        scopes.Add(new HashSet<string>(StringComparer.Ordinal));
        EmitStatements(block.Statements);
        foreach (var comment in block.TrailingComments)
            CommentLine(comment);
        scopes.RemoveAt(scopes.Count - 1);
        indent--;
        Line("}");
    }

    private void EmitStatements(List<SdslStatement> statements)
    {
        for (int i = 0; i < statements.Count; i++)
        {
            if (i > 0 && statements[i].BlankLineBefore)
                Line(string.Empty);
            EmitStatement(statements[i]);
        }
    }

    /// <summary>The body of an if, a loop: a block as is, anything else indented.</summary>
    private void EmitBody(SdslStatement statement)
    {
        if (statement is SdslBlock block && block.Attributes.Count == 0)
        {
            foreach (var comment in block.Comments)
                CommentLine(comment);
            EmitBlock(block);
            return;
        }
        indent++;
        scopes.Add(new HashSet<string>(StringComparer.Ordinal));
        EmitStatement(statement);
        scopes.RemoveAt(scopes.Count - 1);
        indent--;
    }

    private void EmitStatement(SdslStatement statement)
    {
        EmitComments(statement);
        foreach (var attribute in statement.Attributes)
            Line(StatementAttribute(attribute) + ";");

        switch (statement)
        {
            case SdslBlock block:
                EmitBlock(block);
                break;

            case SdslDeclarationStatement declaration:
                Line(Declaration(declaration) + ";");
                break;

            case SdslExpressionStatement expression:
                Line(Expression(expression.Expression) + ";");
                break;

            case SdslIfStatement ifStatement:
                Line("if (" + Expression(ifStatement.Condition) + ")");
                EmitBody(ifStatement.Then);
                var elseBranch = ifStatement.Else;
                while (elseBranch != null)
                {
                    if (elseBranch is SdslIfStatement chained && chained.Attributes.Count == 0 && chained.Comments.Count == 0)
                    {
                        Line("else if (" + Expression(chained.Condition) + ")");
                        EmitBody(chained.Then);
                        elseBranch = chained.Else;
                    }
                    else
                    {
                        Line("else");
                        EmitBody(elseBranch);
                        elseBranch = null;
                    }
                }
                break;

            case SdslForStatement forStatement:
            {
                scopes.Add(new HashSet<string>(StringComparer.Ordinal));
                var head = new StringBuilder("for (");
                if (forStatement.Initializer is SdslDeclarationStatement forDeclaration)
                    head.Append(Declaration(forDeclaration));
                else if (forStatement.Initializer is SdslExpressionStatement forExpression)
                    head.Append(ExpressionList(forExpression.Expression));
                head.Append("; ");
                if (forStatement.Condition != null)
                    head.Append(Expression(forStatement.Condition));
                head.Append("; ");
                head.Append(string.Join(", ", forStatement.Incrementors.Select(ExpressionList)));
                head.Append(')');
                Line(head.ToString());
                EmitBody(forStatement.Body);
                scopes.RemoveAt(scopes.Count - 1);
                break;
            }

            case SdslForeachStatement foreachStatement:
            {
                var collection = Expression(foreachStatement.Collection);
                scopes.Add(new HashSet<string>(StringComparer.Ordinal) { foreachStatement.Name });
                var type = foreachStatement.Type == null ? "var" : TypeText(foreachStatement.Type, statement.Position);
                Line("foreach (" + type + " " + EscapeIdentifier(foreachStatement.Name) + " in " + collection + ")");
                EmitBody(foreachStatement.Body);
                scopes.RemoveAt(scopes.Count - 1);
                break;
            }

            case SdslWhileStatement whileStatement:
                Line("while (" + Expression(whileStatement.Condition) + ")");
                EmitBody(whileStatement.Body);
                break;

            case SdslDoStatement doStatement:
                Line("do");
                EmitBody(doStatement.Body);
                Line("while (" + Expression(doStatement.Condition) + ");");
                break;

            case SdslSwitchStatement switchStatement:
                Line("switch (" + Expression(switchStatement.Expression) + ")");
                Line("{");
                indent++;
                foreach (var section in switchStatement.Sections)
                {
                    foreach (var label in section.Labels)
                        Line(label == null ? "default:" : "case " + Expression(label) + ":");
                    indent++;
                    scopes.Add(new HashSet<string>(StringComparer.Ordinal));
                    EmitStatements(section.Statements);
                    scopes.RemoveAt(scopes.Count - 1);
                    indent--;
                }
                indent--;
                Line("}");
                break;

            case SdslReturnStatement returnStatement:
                Line(returnStatement.Value == null ? "return;" : "return " + Expression(returnStatement.Value) + ";");
                break;

            case SdslKeywordStatement keyword:
                switch (keyword.Keyword)
                {
                    case "discard": Line("discard();"); break;
                    case ";": Line(";"); break;
                    default: Line(keyword.Keyword + ";"); break;
                }
                break;

            case SdslConditionalStatement conditional:
                EmitConditional(conditional);
                break;

            case SdslMacroStatement macro:
                Line("Sdsl.MacroStatement(" + Quote(macro.Name) + ");");
                break;

            default:
                Error("Statement " + statement.GetType().Name + " is not converted", statement.Position);
                break;
        }
    }

    /// <summary>#if A / #elif B / #else / #endif as if (Sdsl.If("A")) … else if (Sdsl.If("B")) … else ….</summary>
    private void EmitConditional(SdslConditionalStatement conditional)
    {
        for (int i = 0; i < conditional.Branches.Count; i++)
        {
            var branch = conditional.Branches[i];
            if (branch.Directive == "else")
                Line("else");
            else
            {
                var condition = branch.Directive switch
                {
                    "ifdef" => "defined(" + branch.Condition + ")",
                    "ifndef" => "!defined(" + branch.Condition + ")",
                    _ => branch.Condition ?? string.Empty,
                };
                Line((i == 0 ? "if" : "else if") + " (Sdsl.If(" + Quote(condition) + "))");
            }
            Line("{");
            indent++;
            scopes.Add(new HashSet<string>(StringComparer.Ordinal));
            EmitStatements(branch.Statements);
            scopes.RemoveAt(scopes.Count - 1);
            indent--;
            Line("}");
        }
    }

    private string StatementAttribute(SdslAttribute attribute)
    {
        if (LoopMarkers.TryGetValue(attribute.Name, out var marker))
        {
            if (attribute.Arguments == null)
                return marker + "()";
            if (attribute.Name == "unroll" && int.TryParse(attribute.Arguments.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                return marker + "(" + attribute.Arguments.Trim() + ")";
        }
        return "Attribute(" + Quote(attribute.Text) + ")";
    }

    private string Declaration(SdslDeclarationStatement declaration)
    {
        bool isConst = declaration.Modifiers.Contains("const");
        bool isStatic = declaration.Modifiers.Contains("static");
        foreach (var modifier in declaration.Modifiers)
        {
            if (modifier != "const" && modifier != "static")
                Error("Local modifier " + modifier + " is not converted", declaration.Position);
        }

        var baseType = declaration.Type.Name == "var" ? "var" : TypeText(declaration.Type, declaration.Position);
        bool anyArray = declaration.Declarators.Any(d => d.ArraySizes.Count > 0);
        bool allArrays = declaration.Declarators.All(d => d.ArraySizes.Count > 0);
        if (anyArray && !allArrays)
        {
            Error("A declaration mixing arrays and non-arrays is not converted", declaration.Position);
        }
        var type = baseType;
        if (allArrays)
            type += string.Concat(Enumerable.Repeat("[]", declaration.Declarators[0].ArraySizes.Count));

        // C# const only takes literals of the built-in types; anything else keeps the SDSL modifier in a marker.
        bool csharpConst = isConst && !isStatic && !allArrays && IsConstType(type)
            && declaration.Declarators.All(d => d.Initializer != null && IsConstantExpression(d.Initializer));

        var text = new StringBuilder();
        if (csharpConst) text.Append("const ");
        text.Append(type).Append(' ');
        for (int i = 0; i < declaration.Declarators.Count; i++)
        {
            var declarator = declaration.Declarators[i];
            if (i > 0) text.Append(", ");
            text.Append(EscapeIdentifier(declarator.Name));
            string? value = null;
            if (declarator.Initializer != null)
                value = Initializer(declarator.Initializer, declaration.Type, declarator.ArraySizes);
            else if (declarator.ArraySizes.Count > 0)
                value = ArrayCreation(declaration.Type, declarator.ArraySizes, null);
            if (value != null && !csharpConst && (isConst || isStatic))
                value = (isStatic ? "Sdsl.StaticConst(" : "Sdsl.Const(") + value + ")";
            else if (value == null && (isConst || isStatic))
                Error("A const local without a value is not converted", declaration.Position);
            if (value != null)
                text.Append(" = ").Append(value);
            CurrentScope?.Add(declarator.Name);
        }
        return text.ToString();
    }

    private HashSet<string>? CurrentScope => scopes.Count > 0 ? scopes[scopes.Count - 1] : null;

    /// <summary>The value of a declaration: an expression, or an initializer list for an array or a vector.</summary>
    private string Initializer(SdslExpression initializer, SdslType type, List<string> arraySizes)
    {
        if (initializer is SdslInitializerList list)
        {
            if (arraySizes.Count > 0)
                return ArrayCreation(type, arraySizes, list);
            // { a, b } for a vector or a struct.
            return "new " + TypeText(type, initializer.Position) + "(" + string.Join(", ", list.Items.Select(Expression)) + ")";
        }
        return Expression(initializer);
    }

    private string ArrayCreation(SdslType type, List<string> sizes, SdslInitializerList? values)
    {
        var element = TypeText(type, new SdslPosition());
        if (sizes.Count > 1)
        {
            Error("Arrays of more than one dimension are not converted", new SdslPosition());
            return "null!";
        }
        var size = sizes[0];
        var items = values == null ? null : "{ " + string.Join(", ", values.Items.Select(i => i is SdslInitializerList inner ? "new " + element + "(" + string.Join(", ", inner.Items.Select(Expression)) + ")" : Expression(i))) + " }";
        if (size.Length == 0)
            return items == null ? "new " + element + "[0]" : "new " + element + "[] " + items;
        var sizeText = int.TryParse(size, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? size : SizeExpression(size);
        return items == null ? "new " + element + "[" + sizeText + "]" : "new " + element + "[" + sizeText + "] " + items;
    }

    /// <summary>An array size that is a name (a generic parameter, a constant, a macro).</summary>
    private string SizeExpression(string size)
    {
        var unit = SdslSyntaxParser.Parse("size", "shader S { void M() { int x = " + size + "; } };");
        if (unit.Diagnostics.Count == 0 && unit.Shaders().First().Members[0] is SdslMethod method
            && method.Body!.Statements[0] is SdslDeclarationStatement declaration && declaration.Declarators[0].Initializer is { } expression)
            return Expression(expression);
        return "Sdsl.Macro(" + Quote(size) + ")";
    }

    // -- expressions ---------------------------------------------------------------------------------

    private string ExpressionList(SdslExpression expression) =>
        expression is SdslSequence sequence ? string.Join(", ", sequence.Items.Select(Expression)) : Expression(expression);

    private string Expression(SdslExpression expression)
    {
        var text = ExpressionCore(expression);
        return expression.Parenthesized ? "(" + text + ")" : text;
    }

    private string ExpressionCore(SdslExpression expression)
    {
        switch (expression)
        {
            case SdslLiteral literal:
                return Literal(literal);

            case SdslIdentifier identifier:
                return Identifier(identifier);

            case SdslMemberAccess access:
                return MemberAccess(access);

            case SdslIndexer indexer:
                return Expression(indexer.Target) + "[" + Expression(indexer.Index) + "]";

            case SdslCall call:
                return Call(call);

            case SdslCast cast:
            {
                if (cast.ArraySizes.Count > 0)
                    Error("A cast to an array type is not converted", cast.Position);
                var type = TypeText(cast.Type, cast.Position);
                if (IsStructName(cast.Type.Name) && cast.Operand is SdslLiteral { Text: "0" })
                    return "default(" + type + ")";
                return "(" + type + ")" + Expression(cast.Operand);
            }

            case SdslUnary unary:
                return unary.Postfix ? Expression(unary.Operand) + unary.Operator : unary.Operator + Expression(unary.Operand);

            case SdslBinary binary:
                return Expression(binary.Left) + " " + binary.Operator + " " + Expression(binary.Right);

            case SdslAssignment assignment:
                return Expression(assignment.Target) + " " + assignment.Operator + " " + Expression(assignment.Value);

            case SdslConditional conditional:
                return Expression(conditional.Condition) + " ? " + Expression(conditional.WhenTrue) + " : " + Expression(conditional.WhenFalse);

            case SdslInitializerList list:
                Error("An initializer list here is not converted", list.Position);
                return "default";

            case SdslSequence sequence:
                Error("The comma operator is not converted", sequence.Position);
                return string.Join(", ", sequence.Items.Select(Expression));

            case SdslTypeExpression typeExpression:
                Error(typeExpression.Type + " in an expression is not converted", typeExpression.Position);
                return typeExpression.Type.Name;
        }
        Error("Expression " + expression.GetType().Name + " is not converted", expression.Position);
        return "default";
    }

    private string Literal(SdslLiteral literal)
    {
        switch (literal.Kind)
        {
            case SdslLiteralKind.Boolean:
                return literal.Text;
            case SdslLiteralKind.String:
                return Quote(literal.Text);
            case SdslLiteralKind.Integer:
            {
                var text = literal.Text;
                // HLSL's l suffix is a 32-bit int.
                if (text.EndsWith("l", StringComparison.OrdinalIgnoreCase) && !text.EndsWith("ul", StringComparison.OrdinalIgnoreCase))
                    text = text.Substring(0, text.Length - 1);
                return text;
            }
            default:
            {
                var text = literal.Text;
                if (text.Contains("#"))
                {
                    Error("Literal " + text + " is not converted", literal.Position);
                    return "float.PositiveInfinity";
                }
                bool isDouble = text.EndsWith("lf", StringComparison.OrdinalIgnoreCase) || text.EndsWith("l", StringComparison.OrdinalIgnoreCase);
                text = text.TrimEnd('f', 'F', 'h', 'H', 'l', 'L');
                if (text.EndsWith(".", StringComparison.Ordinal))
                    text += "0";
                if (text.StartsWith(".", StringComparison.Ordinal))
                    text = "0" + text;
                return text + (isDouble ? "d" : "f");
            }
        }
    }

    private bool IsLocal(string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--)
            if (scopes[i].Contains(name))
                return true;
        return false;
    }

    private string Identifier(SdslIdentifier identifier, bool callTarget = false)
    {
        var name = identifier.Name;
        if (name.IndexOf('<') >= 0)
        {
            Error("Generic shader " + name + " in an expression is not converted", identifier.Position);
            return name.Substring(0, name.IndexOf('<'));
        }
        if (!callTarget && IsMacro(name))
            return "Sdsl.Macro(" + Quote(name) + ")";
        if (!callTarget && IsMemberName(name) && !IsLocal(name))
            return "Sdsl.Member(this, " + EscapeIdentifier(name) + ")";
        return EscapeIdentifier(name);
    }

    /// <summary>
    /// A name no scope declares: not a local, a member of the shader or of any base, a generic
    /// parameter, a shader or a struct. SDSL leaves it to the preprocessor: a macro.
    /// </summary>
    private bool IsMacro(string name)
    {
        if (name == "streams" || name == "this" || name == "base" || IsLocal(name) || memberNames.Contains(name) || genericKinds.ContainsKey(name))
            return false;
        if (index.Contains(name) || IsStructName(name) || IsBuiltinValueType(name))
            return false;
        if (shader.Defines.Any(d => d.Name == name))
            return true;
        if (index.FindVariable(shader, name) != null)
            return false;
        foreach (var ancestor in index.AllBases(shader))
        {
            if (ancestor.GenericParameters.Any(p => p.Name == name))
                return false;
            if (ancestor.Members.Any(m => m is SdslMethod method && method.Name == name))
                return false;
        }
        // Everything the index knows is above; a base it does not know may declare the name.
        return AllBasesKnown();
    }

    private bool? allBasesKnown;

    private bool AllBasesKnown()
    {
        if (allBasesKnown == null)
        {
            allBasesKnown = true;
            foreach (var candidate in new[] { shader }.Concat(index.AllBases(shader)))
                foreach (var reference in candidate.Bases)
                    if (!index.Contains(reference.Name))
                        allBasesKnown = false;
        }
        return allBasesKnown.Value;
    }

    private static bool NeedsParentheses(SdslExpression expression) =>
        !expression.Parenthesized && expression is SdslBinary or SdslConditional or SdslAssignment or SdslUnary { Postfix: false } or SdslCast;

    private bool IsMemberName(string name) => genericKinds.TryGetValue(name, out var kind) && kind == "MemberName";

    private string MemberAccess(SdslMemberAccess access)
    {
        var name = access.Name;
        if (access.Target is SdslIdentifier { Name: "streams" } && !IsLocal("streams"))
        {
            if (IsMemberName(name))
                return "streams[" + EscapeIdentifier(name) + "]";
            // A stream no base declares: SDSL finds it in whatever the effect mixes in.
            if (AllBasesKnown() && index.FindVariable(shader, name) == null)
                return "streams[" + Quote(name) + "]";
            return "streams." + EscapeIdentifier(name);
        }
        // base.M() where M is not in the C# base class: the mixin's, through the class itself.
        if (access.Target is SdslIdentifier { Name: "base" } && !InCSharpBaseChain(name))
            return "Sdsl.Base(this)." + EscapeIdentifier(name);
        // x.Name where Name is a macro (a method the preprocessor picks): no C# member to name.
        if (shader.Defines.Any(d => d.Name == name))
            return "Sdsl.Member(" + Expression(access.Target) + ", " + Quote(name) + ")";
        // m._m00_m11: several matrix elements at once.
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^((_m[0-3][0-3])|(_[1-4][1-4])){2,}$"))
            return "Sdsl.Member(" + Expression(access.Target) + ", " + Quote(name) + ")";
        if (IsMemberName(name))
            return "Sdsl.Member(" + Expression(access.Target) + ", " + EscapeIdentifier(name) + ")";

        // Shader.Member: a shader named as a qualifier, its member reached without an instance.
        if (access.Target is SdslIdentifier target && !access.Target.Parenthesized && !IsLocal(target.Name) && !memberNames.Contains(target.Name)
            && index.Contains(target.Name))
        {
            var owner = index.Find(target.Name)!;
            if (!IsStaticMember(owner, name))
                return "Sdsl.Static<" + TypeName(target.Name) + ">()." + EscapeIdentifier(name);
            return TypeName(target.Name) + "." + EscapeIdentifier(name);
        }
        if (access.Target is SdslIdentifier generic && generic.Name.IndexOf('<') > 0)
        {
            var shaderName = generic.Name.Substring(0, generic.Name.IndexOf('<'));
            var arguments = generic.Name.Substring(shaderName.Length + 1, generic.Name.Length - shaderName.Length - 2);
            return "Sdsl.Static<" + TypeName(shaderName) + ">(" + Quote(arguments) + ")." + EscapeIdentifier(name);
        }
        return Expression(access.Target) + "." + EscapeIdentifier(name);
    }

    private bool InCSharpBaseChain(string name)
    {
        foreach (var ancestor in index.CSharpBaseChain(shader))
            foreach (var member in ancestor.Members)
                if ((member is SdslMethod method && method.Name == name) || (member is SdslVariable variable && variable.Name == name))
                    return true;
        return !AllBasesKnown();
    }

    private bool IsStaticMember(SdslShaderDeclaration owner, string name)
    {
        foreach (var candidate in new[] { owner }.Concat(index.AllBases(owner)))
        {
            foreach (var member in candidate.Members)
            {
                if (member is SdslMethod method && method.Name == name)
                    return method.Has("static");
                if (member is SdslVariable variable && variable.Name == name)
                    return variable.Has("static");
            }
        }
        return false;
    }

    private string Call(SdslCall call)
    {
        var arguments = string.Join(", ", call.Arguments.Select(Expression));
        if (call.Target is SdslIdentifier identifier && !identifier.Parenthesized && !IsLocal(identifier.Name))
        {
            var name = identifier.Name;
            if (IsBuiltinValueType(name) && call.Arguments.Count == 1 && !VectorOrMatrix.IsMatch(TypeName(name)))
            {
                // float(x): C# has no constructor for its scalars; the cast means the same.
                var operand = Expression(call.Arguments[0]);
                return "((" + TypeName(name) + ")" + (NeedsParentheses(call.Arguments[0]) ? "(" + operand + ")" : operand) + ")";
            }
            if (IsBuiltinValueType(name) || (IsStructName(name) && !memberNames.Contains(name)))
                return "new " + TypeName(name) + "(" + arguments + ")";
        }
        if (call.Target is SdslIdentifier function && !function.Parenthesized)
            return Identifier(function, callTarget: true) + "(" + arguments + ")";
        return Expression(call.Target) + "(" + arguments + ")";
    }

    // -- output --------------------------------------------------------------------------------------

    public static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': result.Append("\\\\"); break;
                case '"': result.Append("\\\""); break;
                case '\n': result.Append("\\n"); break;
                case '\r': break;
                case '\t': result.Append("\\t"); break;
                default: result.Append(c); break;
            }
        }
        return result.Append('"').ToString();
    }

    private void Line(string text)
    {
        if (text.Length > 0)
            for (int i = 0; i < indent; i++)
                sb.Append("    ");
        sb.Append(text).Append('\n');
    }

    /// <summary>A comment as written: a /* */ block over several lines keeps its lines; an empty string is an empty line.</summary>
    private void CommentLine(string comment)
    {
        if (comment.Length == 0)
        {
            Line(string.Empty);
            return;
        }
        foreach (var line in comment.Split('\n'))
            Line(line.Trim());
    }
}
