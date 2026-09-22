using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Csl.Generators.CSharp;

/// <summary>
/// Turns a [Shader] partial class into SDSL. The subset of C# it accepts is what SDSL has: fields,
/// methods, nested structs, locals, the HLSL types and intrinsics, if/for/foreach/while/do/switch,
/// the operators; what SDSL declares and C# has no keyword for comes as attributes ([Stage],
/// [CBuffer], [If]…) and as calls to markers (Sdsl.If, Sdsl.Macro, Unroll()…). Anything else is a
/// diagnostic on the offending node, and the shader is not emitted.
/// </summary>
public sealed partial class ShaderTranslator
{
    private const string TypesNamespace = "Csl.Types";
    private const string IntrinsicsType = "Csl.Types.Intrinsics";
    private const string SdslType = "Csl.Sdsl";

    private static readonly Dictionary<string, string> LoopMarkers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Unroll"] = "[unroll]",
        ["Loop"] = "[loop]",
        ["Branch"] = "[branch]",
        ["Flatten"] = "[flatten]",
    };

    private readonly INamedTypeSymbol shader;
    private readonly Compilation compilation;
    private readonly CancellationToken cancellation;
    private readonly TranslatedShader result;
    private readonly StringBuilder sb = new StringBuilder();
    private readonly Dictionary<SyntaxTree, SemanticModel> models = new Dictionary<SyntaxTree, SemanticModel>();
    private SemanticModel model = null!;
    private int indent;

    /// <summary>The [Mixin] shaders, and those of the C# bases: their members may not be in the input compilation (the stubs are generated), so names resolve here.</summary>
    private readonly List<INamedTypeSymbol> mixins = new List<INamedTypeSymbol>();

    private ShaderTranslator(INamedTypeSymbol shader, Compilation compilation, CancellationToken cancellation)
    {
        this.shader = shader;
        this.compilation = compilation;
        this.cancellation = cancellation;
        var attribute = shader.GetAttributes().First(a => a.AttributeClass?.ToDisplayString() == "Csl.ShaderAttribute");
        string? name = null;
        bool external = false;
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "Name" && named.Value.Value is string s) name = s;
            if (named.Key == "External" && named.Value.Value is bool b) external = b;
        }
        var ns = shader.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : null;
        var path = SourceDeclarations(shader).FirstOrDefault()?.SyntaxTree.FilePath ?? shader.Name + ".cs";
        result = new TranslatedShader(shader.Name, name ?? shader.Name, ns, path) { IsExternal = external };
    }

    public static TranslatedShader Translate(INamedTypeSymbol shader, Compilation compilation, CancellationToken cancellation)
    {
        var translator = new ShaderTranslator(shader, compilation, cancellation);
        translator.Run();
        return translator.result;
    }

    /// <summary>The SDSL name of a shader class: its [Shader(Name)] or its own name.</summary>
    public static string ShaderNameOf(INamedTypeSymbol type)
    {
        foreach (var attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Csl.ShaderAttribute")
                continue;
            foreach (var named in attribute.NamedArguments)
                if (named.Key == "Name" && named.Value.Value is string s)
                    return s;
        }
        return type.Name;
    }

    public static bool IsShaderClass(ITypeSymbol? type) =>
        type != null && type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Csl.ShaderAttribute");

    /// <summary>The declarations written by hand: the generated partials (streams, stubs, SDSL) are not the shader.</summary>
    private static IEnumerable<ClassDeclarationSyntax> SourceDeclarations(INamedTypeSymbol type) => type.DeclaringSyntaxReferences
        .Select(r => r.GetSyntax())
        .OfType<ClassDeclarationSyntax>()
        .Where(d => !d.SyntaxTree.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
        .OrderBy(d => d.SyntaxTree.FilePath, StringComparer.Ordinal).ThenBy(d => d.SpanStart);

    // -- structure -----------------------------------------------------------------------------------

    private void Run()
    {
        var declarations = SourceDeclarations(shader).ToList();
        if (declarations.Count == 0)
            return;

        foreach (var declaration in declarations)
        {
            if (!declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
                Report(Diagnostics.ShaderNotPartial, declaration.Identifier.GetLocation(), shader.Name);
        }
        if (shader.IsGenericType)
            Report(Diagnostics.ShaderGeneric, declarations[0].Identifier.GetLocation(), shader.Name);
        if (shader.ContainingType != null)
            Report(Diagnostics.ShaderNested, declarations[0].Identifier.GetLocation(), shader.Name);

        foreach (var attribute in shader.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == "Csl.NumThreadsAttribute" && attribute.ConstructorArguments.Length == 3)
                result.NumThreads = ((int)attribute.ConstructorArguments[0].Value!, (int)attribute.ConstructorArguments[1].Value!, (int)attribute.ConstructorArguments[2].Value!);
        }

        for (var type = shader; type != null && type.SpecialType != SpecialType.System_Object; type = type.BaseType)
            foreach (var mixin in ShaderPartialEmitter.MixinsOf(type))
                if (IsShaderClass(mixin) && !mixins.Contains(mixin, SymbolEqualityComparer.Default))
                    mixins.Add(mixin);
        result.MixinStubs = ShaderPartialEmitter.Emit(shader, result);

        if (result.IsExternal)
            return;

        // Header: namespace, name, generic parameters, bases.
        string? baseGenerics = null;
        bool isInternal = false;
        foreach (var attribute in shader.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Csl.ShaderAttribute")
                continue;
            foreach (var named in attribute.NamedArguments)
            {
                if (named.Key == "BaseGenerics" && named.Value.Value is string g) baseGenerics = g;
                if (named.Key == "Internal" && named.Value.Value is bool i) isInternal = i;
            }
        }
        var bases = new List<string>();
        if (shader.BaseType != null && shader.BaseType.SpecialType != SpecialType.System_Object)
        {
            if (IsShaderClass(shader.BaseType))
                bases.Add(ShaderNameOf(shader.BaseType) + (baseGenerics != null ? "<" + baseGenerics + ">" : string.Empty));
            else
                Report(Diagnostics.BaseNotShader, declarations[0].BaseList?.GetLocation() ?? declarations[0].Identifier.GetLocation(), shader.BaseType.ToDisplayString());
        }
        foreach (var attribute in shader.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Csl.MixinAttribute")
                continue;
            var location = attribute.ApplicationSyntaxReference?.GetSyntax(cancellation).GetLocation() ?? declarations[0].Identifier.GetLocation();
            string? generics = null;
            foreach (var named in attribute.NamedArguments)
                if (named.Key == "Generics" && named.Value.Value is string g)
                    generics = g;
            foreach (var argument in attribute.ConstructorArguments.SelectMany(a => a.Kind == TypedConstantKind.Array ? a.Values : System.Collections.Immutable.ImmutableArray.Create(a)))
            {
                if (argument.Value is INamedTypeSymbol mixin && IsShaderClass(mixin))
                    bases.Add(ShaderNameOf(mixin) + (generics != null ? "<" + generics + ">" : string.Empty));
                else
                    Report(Diagnostics.BaseNotShader, location, argument.Value?.ToString() ?? "?");
            }
        }

        var generic = new List<string>();
        foreach (var declaration in declarations)
        {
            model = ModelFor(declaration.SyntaxTree);
            foreach (var field in declaration.Members.OfType<FieldDeclarationSyntax>())
            {
                if (!HasAttribute(field.AttributeLists, "Csl.GenericAttribute"))
                    continue;
                var type = model.GetTypeInfo(field.Declaration.Type, cancellation).Type;
                var typeName = type?.ToDisplayString() switch
                {
                    "Csl.LinkType" => "LinkType",
                    "Csl.Semantic" => "Semantic",
                    "Csl.MemberName" => "MemberName",
                    _ => SdslTypeName(type, field.Declaration.Type),
                };
                foreach (var variable in field.Declaration.Variables)
                    generic.Add(typeName + " " + variable.Identifier.ValueText);
            }
        }

        if (result.Namespace != null)
        {
            Line("namespace " + result.Namespace);
            Line("{");
            indent++;
        }
        EmitDefines();
        EmitDoc(declarations[0]);
        Line((isInternal ? "internal " : string.Empty) + "shader " + result.ShaderName
            + (generic.Count > 0 ? "<" + string.Join(", ", generic) + ">" : string.Empty)
            + (bases.Count > 0 ? " : " + string.Join(", ", bases) : string.Empty));
        Line("{");
        indent++;

        var members = new List<(MemberDeclarationSyntax Member, SemanticModel Model)>();
        foreach (var declaration in declarations)
        {
            var declarationModel = ModelFor(declaration.SyntaxTree);
            foreach (var member in declaration.Members)
                members.Add((member, declarationModel));
        }
        EmitMembers(members);
        RunChecks(declarations);

        indent--;
        Line("};");
        if (result.Namespace != null)
        {
            indent--;
            Line("}");
        }
        if (!result.HasErrors)
            result.Sdsl = sb.ToString();
    }

    /// <summary>
    /// The [Define] attributes, in order. Consecutive defines under one condition share its #if: the
    /// condition is read once, before the first of them defines anything.
    /// </summary>
    private void EmitDefines()
    {
        foreach (var attribute in shader.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Csl.PreprocessorErrorAttribute" || attribute.ConstructorArguments.Length < 1)
                continue;
            string? condition = null;
            foreach (var named in attribute.NamedArguments)
                if (named.Key == "If" && named.Value.Value is string c)
                    condition = c;
            if (condition != null)
                RawLine("#if " + condition);
            RawLine("#error \"" + attribute.ConstructorArguments[0].Value + "\"");
            if (condition != null)
                RawLine("#endif");
        }
        string? open = null;
        foreach (var attribute in shader.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() != "Csl.DefineAttribute" || attribute.ConstructorArguments.Length < 1)
                continue;
            var name = attribute.ConstructorArguments[0].Value as string ?? string.Empty;
            var value = attribute.ConstructorArguments.Length > 1 ? attribute.ConstructorArguments[1].Value as string : null;
            string? condition = null;
            foreach (var named in attribute.NamedArguments)
                if (named.Key == "If" && named.Value.Value is string c)
                    condition = c;
            if (condition != open)
            {
                if (open != null)
                    RawLine("#endif");
                if (condition != null)
                    RawLine(condition == "!defined(" + name + ")" ? "#ifndef " + name : "#if " + condition);
                open = condition;
            }
            RawLine("#define " + name + (value != null ? " " + value : string.Empty));
        }
        if (open != null)
            RawLine("#endif");
    }

    private SemanticModel ModelFor(SyntaxTree tree)
    {
        if (!models.TryGetValue(tree, out var m))
        {
            m = compilation.GetSemanticModel(tree);
            models[tree] = m;
        }
        return m;
    }

    private bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).Any(a => AttributeName(a) == name);

    private string? AttributeName(AttributeSyntax attribute) =>
        model.GetTypeInfo(attribute, cancellation).Type?.ToDisplayString() ?? model.GetSymbolInfo(attribute, cancellation).Symbol?.ContainingType?.ToDisplayString();

    /// <summary>The attribute's first argument as a string, and its named string arguments.</summary>
    private (string? First, Dictionary<string, string> Named, List<string> All) AttributeArguments(AttributeSyntax attribute)
    {
        string? first = null;
        var named = new Dictionary<string, string>(StringComparer.Ordinal);
        var all = new List<string>();
        if (attribute.ArgumentList == null)
            return (null, named, all);
        foreach (var argument in attribute.ArgumentList.Arguments)
        {
            var value = model.GetConstantValue(argument.Expression, cancellation);
            var text = value.HasValue ? System.Convert.ToString(value.Value, CultureInfo.InvariantCulture) : null;
            if (argument.NameEquals != null)
            {
                if (text != null) named[argument.NameEquals.Name.Identifier.ValueText] = text;
                continue;
            }
            if (text != null)
            {
                first ??= text;
                all.Add(text);
            }
        }
        return (first, named, all);
    }

    // -- members -------------------------------------------------------------------------------------

    /// <summary>A member's SDSL lines, and the #if and cbuffer it sits in.</summary>
    private sealed class MemberText
    {
        public string? Condition;
        public string? Group;
        public bool NewBlock;
        public readonly List<string> Lines = new List<string>();
        public bool BlankBefore;
    }

    private void EmitMembers(List<(MemberDeclarationSyntax Member, SemanticModel Model)> members)
    {
        var texts = new List<MemberText>();
        foreach (var (member, memberModel) in members)
        {
            cancellation.ThrowIfCancellationRequested();
            model = memberModel;
            var saved = sb.Length;
            var savedIndent = indent;
            indent = 0;
            var text = new MemberText { BlankBefore = texts.Count > 0 && (member is MethodDeclarationSyntax || member is StructDeclarationSyntax || HasBlankLineBefore(member)) };
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    if (HasAttribute(field.AttributeLists, "Csl.GenericAttribute"))
                        break;
                    // The member's other #if versions come first, each under its own condition.
                    foreach (var (variantText, variantCondition) in ReadAttributes(field.AttributeLists).Variants)
                    {
                        var variant = new MemberText { Condition = variantCondition, BlankBefore = text.BlankBefore };
                        variant.Lines.Add(variantText);
                        texts.Add(variant);
                        text.BlankBefore = false;
                    }
                    EmitField(field, text);
                    break;
                case MethodDeclarationSyntax method:
                    EmitMethod(method, text);
                    break;
                case StructDeclarationSyntax nested:
                    EmitStruct(nested, text);
                    break;
                default:
                    Report(Diagnostics.UnsupportedMember, member.GetLocation(), member.Kind().ToString().Replace("Declaration", string.Empty));
                    break;
            }
            indent = savedIndent;
            var body = sb.ToString(saved, sb.Length - saved);
            sb.Length = saved;
            if (body.Length == 0)
                continue;
            text.Lines.AddRange(body.TrimEnd('\n').Split('\n'));
            texts.Add(text);
        }

        // Consecutive members of one cbuffer share its block; those under one #if share it.
        string? openGroup = null;
        string? openCondition = null;
        foreach (var text in texts)
        {
            if (text.Group != openGroup || (text.NewBlock && text.Group != null))
            {
                if (openCondition != null)
                {
                    RawLine("#endif");
                    openCondition = null;
                }
                if (openGroup != null)
                {
                    indent--;
                    Line("}");
                    openGroup = null;
                }
                if (text.Group != null)
                {
                    if (text.BlankBefore)
                        Line(string.Empty);
                    Line(text.Group);
                    Line("{");
                    indent++;
                    openGroup = text.Group;
                    text.BlankBefore = false;
                }
            }
            if (text.Condition != openCondition)
            {
                if (openCondition != null)
                    RawLine("#endif");
                openCondition = text.Condition;
                if (openCondition != null)
                {
                    if (text.BlankBefore)
                        Line(string.Empty);
                    RawLine("#if " + openCondition);
                    text.BlankBefore = false;
                }
            }
            if (text.BlankBefore)
                Line(string.Empty);
            foreach (var line in text.Lines)
            {
                if (line.StartsWith("#", StringComparison.Ordinal))
                    RawLine(line);
                else
                    Line(line);
            }
        }
        if (openCondition != null)
            RawLine("#endif");
        if (openGroup != null)
        {
            indent--;
            Line("}");
        }
    }

    private static bool HasBlankLineBefore(SyntaxNode node)
    {
        int newlines = 0;
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            {
                if (++newlines >= 1)
                    return true;
            }
            else if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                return false;
        }
        return false;
    }

    /// <summary>What the attributes of a member say, sorted into SDSL pieces.</summary>
    private sealed class MemberAttributes
    {
        public readonly List<string> Keywords = new List<string>();
        public readonly List<string> Lines = new List<string>();
        public string? Semantic;
        public string? Condition;
        public string? Group;
        public bool NewBlock;
        public List<string>? Sizes;
        public string? TypeOverride;
        public List<string>? Sampler;
        public readonly List<(string Sdsl, string? Condition)> Variants = new List<(string, string?)>();
        public bool IsGeneric;
        public bool Override;
        public bool Redeclare;
        public bool IsStream;
        public bool StageAfterOverride;
    }

    private MemberAttributes ReadAttributes(SyntaxList<AttributeListSyntax> lists)
    {
        var result = new MemberAttributes();
        foreach (var list in lists)
        {
            foreach (var attribute in list.Attributes)
            {
                var name = AttributeName(attribute);
                var (first, named, all) = AttributeArguments(attribute);
                switch (name)
                {
                    case "Csl.StageAttribute":
                        if (named.TryGetValue("AfterOverride", out var after) && after == "True")
                            result.StageAfterOverride = true;
                        else
                            result.Keywords.Add("stage");
                        break;
                    case "Csl.StreamAttribute": result.Keywords.Add("stream"); result.IsStream = true; if (first != null) result.Semantic = first; break;
                    case "Csl.PatchStreamAttribute": result.Keywords.Add("patchstream"); result.IsStream = true; if (first != null) result.Semantic = first; break;
                    case "Csl.ComposeAttribute": result.Keywords.Add("compose"); break;
                    case "Csl.GroupSharedAttribute": result.Keywords.Add("groupshared"); break;
                    case "Csl.CloneAttribute": result.Keywords.Add("clone"); break;
                    case "Csl.ModifiersAttribute": if (first != null) result.Keywords.Add(first); break;
                    case "Csl.SemanticAttribute": result.Semantic = first; break;
                    case "Csl.ColorAttribute": result.Lines.Add("[Color]"); break;
                    case "Csl.LinkAttribute": if (first != null) result.Lines.Add("[Link(\"" + first + "\")]"); break;
                    case "Csl.HlslAttribute": if (first != null) result.Lines.Add("[" + first + "]"); break;
                    case "Csl.IfAttribute": result.Condition = first; break;
                    case "Csl.CBufferAttribute": result.Group = "cbuffer" + (first != null ? " " + first : string.Empty); result.NewBlock = named.ContainsKey("NewBlock") && named["NewBlock"] == "True"; break;
                    case "Csl.RGroupAttribute": result.Group = "rgroup" + (first != null ? " " + first : string.Empty); result.NewBlock = named.ContainsKey("NewBlock") && named["NewBlock"] == "True"; break;
                    case "Csl.TBufferAttribute": result.Group = "tbuffer" + (first != null ? " " + first : string.Empty); result.NewBlock = named.ContainsKey("NewBlock") && named["NewBlock"] == "True"; break;
                    case "Csl.SizeAttribute": result.Sizes = all; break;
                    case "Csl.TypeAttribute": result.TypeOverride = first; break;
                    case "Csl.GenericAttribute": result.IsGeneric = true; break;
                    case "Csl.OverrideAttribute": result.Override = true; break;
                    case "Csl.RedeclareAttribute": result.Redeclare = true; break;
                    case "Csl.NumThreadsAttribute": break;
                    case "Csl.VariantAttribute":
                        if (first != null)
                            result.Variants.Add((first, named.TryGetValue("If", out var variantCondition) ? variantCondition : null));
                        break;
                    case "Csl.SamplerAttribute":
                        result.Sampler = new List<string>();
                        foreach (var argument in attribute.ArgumentList?.Arguments ?? default)
                            if (argument.NameEquals != null && named.TryGetValue(argument.NameEquals.Name.Identifier.ValueText, out var value))
                                result.Sampler.Add(argument.NameEquals.Name.Identifier.ValueText + " = " + value + ";");
                        break;
                    default:
                        Report(Diagnostics.UnsupportedSyntax, attribute.GetLocation(), "attribute " + attribute.Name);
                        break;
                }
            }
        }
        return result;
    }

    private void EmitStruct(StructDeclarationSyntax nested, MemberText text)
    {
        var attributes = ReadAttributes(nested.AttributeLists);
        text.Condition = attributes.Condition;
        EmitDoc(nested);
        foreach (var line in attributes.Lines)
            Line(line);
        Line("struct " + nested.Identifier.ValueText);
        Line("{");
        indent++;
        foreach (var member in nested.Members)
        {
            if (member is not FieldDeclarationSyntax field)
            {
                Report(Diagnostics.UnsupportedMember, member.GetLocation(), "struct " + member.Kind());
                continue;
            }
            var fieldAttributes = ReadAttributes(field.AttributeLists);
            var type = fieldAttributes.TypeOverride ?? SdslTypeName(model.GetTypeInfo(field.Declaration.Type, cancellation).Type, field.Declaration.Type, stripArray: true);
            EmitDoc(field);
            foreach (var line in fieldAttributes.Lines)
                Line(line);
            foreach (var variable in field.Declaration.Variables)
            {
                var declaration = new StringBuilder();
                foreach (var keyword in fieldAttributes.Keywords)
                    declaration.Append(keyword).Append(' ');
                declaration.Append(type).Append(' ').Append(variable.Identifier.ValueText);
                declaration.Append(ArraySuffix(fieldAttributes.Sizes, field.Declaration.Type));
                if (fieldAttributes.Semantic != null)
                    declaration.Append(" : ").Append(fieldAttributes.Semantic);
                Line(declaration + ";");
            }
        }
        indent--;
        Line("};");
    }

    /// <summary>[n] per dimension of an array type: the sizes of [Size], or [] when there is none.</summary>
    private string ArraySuffix(List<string>? sizes, TypeSyntax type)
    {
        int rank = 0;
        for (var t = type; t is ArrayTypeSyntax array; t = array.ElementType)
            rank += array.RankSpecifiers.Count;
        if (rank == 0)
            return sizes == null ? string.Empty : string.Concat(sizes.Select(s => "[" + s + "]"));
        var result = new StringBuilder();
        for (int i = 0; i < rank; i++)
            result.Append('[').Append(sizes != null && i < sizes.Count ? sizes[i] : string.Empty).Append(']');
        return result.ToString();
    }

    private void EmitField(FieldDeclarationSyntax field, MemberText text)
    {
        var attributes = ReadAttributes(field.AttributeLists);
        text.Condition = attributes.Condition;
        text.Group = attributes.Group;
        text.NewBlock = attributes.NewBlock;

        bool isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
        bool isStatic = field.Modifiers.Any(SyntaxKind.StaticKeyword);
        bool isReadonly = field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword);
        var keywords = new List<string>();
        if (isConst || (isStatic && isReadonly))
            keywords.Add("static const");
        else if (isStatic)
            keywords.Add("static");
        keywords.AddRange(attributes.Keywords);

        var typeSymbol = model.GetTypeInfo(field.Declaration.Type, cancellation).Type;
        string type;
        if (attributes.TypeOverride != null)
            type = attributes.TypeOverride;
        else if (attributes.Keywords.Contains("compose"))
        {
            var element = typeSymbol is IArrayTypeSymbol array ? array.ElementType : typeSymbol;
            type = IsShaderClass(element) ? ShaderNameOf((INamedTypeSymbol)element!) : Unsupported(field.Declaration.Type, element);
        }
        else
            type = SdslTypeName(typeSymbol, field.Declaration.Type, stripArray: true);

        foreach (var variable in field.Declaration.Variables)
        {
            EmitDoc(field);
            foreach (var line in attributes.Lines)
                Line(line);
            var declaration = new StringBuilder();
            foreach (var keyword in keywords)
                declaration.Append(keyword).Append(' ');
            declaration.Append(type).Append(' ').Append(variable.Identifier.ValueText);
            declaration.Append(ArraySuffix(attributes.Sizes, field.Declaration.Type));
            if (attributes.Semantic != null)
                declaration.Append(" : ").Append(attributes.Semantic);
            if (attributes.Sampler != null)
            {
                Line(declaration.ToString());
                Line("{");
                indent++;
                foreach (var entry in attributes.Sampler)
                    Line(entry);
                indent--;
                Line("};");
                continue;
            }
            if (variable.Initializer != null && !IsDefaultLiteral(variable.Initializer.Value))
                declaration.Append(" = ").Append(Initializer(variable.Initializer.Value));
            declaration.Append(';');
            Line(declaration.ToString());
        }
    }

    private static bool IsDefaultLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.DefaultLiteralExpression)
        || expression is PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang && IsDefaultLiteral(bang.Operand);

    /// <summary>An initializer: an expression, or an array's values as <c>{ a, b }</c>.</summary>
    private string Initializer(ExpressionSyntax value)
    {
        switch (value)
        {
            case InitializerExpressionSyntax list:
                return "{ " + string.Join(", ", list.Expressions.Select(Initializer)) + " }";
            case ArrayCreationExpressionSyntax creation:
                if (creation.Initializer != null)
                    return Initializer(creation.Initializer);
                Report(Diagnostics.UnsupportedSyntax, value.GetLocation(), "array creation outside a declaration");
                return "0";
            case ImplicitArrayCreationExpressionSyntax implicitCreation:
                return Initializer(implicitCreation.Initializer);
            case CollectionExpressionSyntax collection:
                return "{ " + string.Join(", ", collection.Elements.OfType<ExpressionElementSyntax>().Select(e => Initializer(e.Expression))) + " }";
            default:
                return Expression(value);
        }
    }

    private void EmitMethod(MethodDeclarationSyntax method, MemberText text)
    {
        var symbol = model.GetDeclaredSymbol(method, cancellation);
        if (symbol == null)
            return;
        if (symbol.IsGenericMethod)
            Report(Diagnostics.UnsupportedSyntax, method.Identifier.GetLocation(), "generic method");

        var attributes = ReadAttributes(method.AttributeLists);
        text.Condition = attributes.Condition;
        text.Group = attributes.Group;
        string? returnSemantic = null;
        foreach (var list in method.AttributeLists)
        {
            if (list.Target?.Identifier.IsKind(SyntaxKind.ReturnKeyword) != true)
                continue;
            foreach (var attribute in list.Attributes)
                if (AttributeName(attribute) == "Csl.SemanticAttribute")
                    returnSemantic = AttributeArguments(attribute).First;
        }

        var header = new StringBuilder();
        foreach (var keyword in attributes.Keywords)
            header.Append(keyword).Append(' ');
        bool isAbstract = method.Modifiers.Any(SyntaxKind.AbstractKeyword);
        bool isOverride = method.Modifiers.Any(SyntaxKind.OverrideKeyword) && !attributes.Redeclare;
        if (isOverride || attributes.Override) header.Append("override ");
        if (attributes.StageAfterOverride) header.Append("stage ");
        if (isAbstract) header.Append("abstract ");
        if (method.Modifiers.Any(SyntaxKind.StaticKeyword)) header.Append("static ");
        header.Append(SdslTypeName(symbol.ReturnType, method.ReturnType)).Append(' ').Append(method.Identifier.ValueText).Append('(');
        for (int i = 0; i < method.ParameterList.Parameters.Count; i++)
        {
            var parameter = method.ParameterList.Parameters[i];
            var parameterSymbol = symbol.Parameters[i];
            var parameterAttributes = ReadAttributes(parameter.AttributeLists);
            if (i > 0) header.Append(", ");
            bool outModifier = parameterAttributes.Keywords.Remove("out");
            switch (parameterSymbol.RefKind)
            {
                case RefKind.Ref: header.Append(outModifier ? "out " : "inout "); break;
                case RefKind.Out: header.Append("out "); break;
                case RefKind.In: header.Append("in "); break;
            }
            foreach (var keyword in parameterAttributes.Keywords)
                header.Append(keyword).Append(' ');
            header.Append(parameterAttributes.TypeOverride ?? SdslTypeName(parameterSymbol.Type, parameter.Type ?? (SyntaxNode)parameter, stripArray: true));
            header.Append(' ').Append(parameter.Identifier.ValueText);
            if (parameter.Type != null)
                header.Append(ArraySuffix(parameterAttributes.Sizes, parameter.Type));
            if (parameterAttributes.Semantic != null)
                header.Append(" : ").Append(parameterAttributes.Semantic);
            if (parameter.Default != null)
                header.Append(" = ").Append(Expression(parameter.Default.Value));
        }
        header.Append(')');
        if (returnSemantic != null)
            header.Append(" : ").Append(returnSemantic);

        EmitDoc(method);
        foreach (var line in attributes.Lines)
            Line(line);
        if (method.Body == null && method.ExpressionBody == null)
        {
            Line(header + ";");
            return;
        }
        Line(header.ToString());
        Line("{");
        indent++;
        if (method.Body != null)
        {
            EmitStatements(method.Body.Statements);
            EmitTrailingComments(method.Body.CloseBraceToken);
        }
        else if (method.ExpressionBody != null)
        {
            var expression = Expression(method.ExpressionBody.Expression);
            Line(symbol.ReturnsVoid ? expression + ";" : "return " + expression + ";");
        }
        indent--;
        Line("}");
    }

    // -- statements ----------------------------------------------------------------------------------

    private void EmitStatements(SyntaxList<StatementSyntax> statements)
    {
        for (int i = 0; i < statements.Count; i++)
        {
            if (i > 0 && HasBlankLineBefore(statements[i]))
                Line(string.Empty);
            EmitStatement(statements[i]);
        }
    }

    private void EmitStatement(StatementSyntax statement)
    {
        cancellation.ThrowIfCancellationRequested();
        EmitComments(statement);
        switch (statement)
        {
            case BlockSyntax block:
                Line("{");
                indent++;
                EmitStatements(block.Statements);
                EmitTrailingComments(block.CloseBraceToken);
                indent--;
                Line("}");
                break;

            case LocalDeclarationStatementSyntax local:
                if (local.UsingKeyword != default)
                    Report(Diagnostics.UnsupportedSyntax, local.GetLocation(), "using declaration");
                Line(Declaration(local.Declaration, local.IsConst) + ";");
                break;

            case ExpressionStatementSyntax expressionStatement:
                if (expressionStatement.Expression is InvocationExpressionSyntax invocation)
                {
                    if (TryLoopMarker(invocation, out var marker))
                    {
                        Line(marker);
                        break;
                    }
                    if (IsMarker(invocation, "MacroStatement", out var macroArguments))
                    {
                        Line(StringArgument(macroArguments, 0) ?? string.Empty);
                        break;
                    }
                    if (model.GetSymbolInfo(invocation, cancellation).Symbol is IMethodSymbol { Name: "discard" } discardMethod && discardMethod.ContainingType?.ToDisplayString() == IntrinsicsType)
                    {
                        Line("discard;");
                        break;
                    }
                    if (IsMarker(invocation, "Undefined", out _))
                        break;
                }
                Line(Expression(expressionStatement.Expression) + ";");
                break;

            case IfStatementSyntax ifStatement when IsPreprocessorIf(ifStatement.Condition, out _):
                EmitPreprocessorIf(ifStatement);
                break;

            case IfStatementSyntax ifStatement:
                Line("if (" + Expression(ifStatement.Condition) + ")");
                EmitBody(ifStatement.Statement);
                var rest = ifStatement.Else;
                while (rest != null)
                {
                    if (rest.Statement is IfStatementSyntax chained && !IsPreprocessorIf(chained.Condition, out _) && !chained.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)))
                    {
                        Line("else if (" + Expression(chained.Condition) + ")");
                        EmitBody(chained.Statement);
                        rest = chained.Else;
                    }
                    else
                    {
                        Line("else");
                        EmitBody(rest.Statement);
                        rest = null;
                    }
                }
                break;

            case ForStatementSyntax forStatement:
            {
                var head = new StringBuilder("for (");
                if (forStatement.Declaration != null)
                    head.Append(Declaration(forStatement.Declaration, isConst: false));
                else
                    head.Append(string.Join(", ", forStatement.Initializers.Select(Expression)));
                head.Append("; ");
                if (forStatement.Condition != null)
                    head.Append(Expression(forStatement.Condition));
                head.Append("; ");
                head.Append(string.Join(", ", forStatement.Incrementors.Select(Expression)));
                head.Append(')');
                Line(head.ToString());
                EmitBody(forStatement.Statement);
                break;
            }

            case ForEachStatementSyntax foreachStatement:
            {
                var type = foreachStatement.Type.IsVar ? "var" : SdslTypeName(model.GetTypeInfo(foreachStatement.Type, cancellation).Type, foreachStatement.Type);
                Line("foreach (" + type + " " + foreachStatement.Identifier.ValueText + " in " + Expression(foreachStatement.Expression) + ")");
                EmitBody(foreachStatement.Statement);
                break;
            }

            case WhileStatementSyntax whileStatement:
                Line("while (" + Expression(whileStatement.Condition) + ")");
                EmitBody(whileStatement.Statement);
                break;

            case DoStatementSyntax doStatement:
                Line("do");
                EmitBody(doStatement.Statement);
                Line("while (" + Expression(doStatement.Condition) + ");");
                break;

            case SwitchStatementSyntax switchStatement:
                Line("switch (" + Expression(switchStatement.Expression) + ")");
                Line("{");
                indent++;
                foreach (var section in switchStatement.Sections)
                {
                    foreach (var label in section.Labels)
                    {
                        switch (label)
                        {
                            case CaseSwitchLabelSyntax caseLabel:
                                Line("case " + Expression(caseLabel.Value) + ":");
                                break;
                            case DefaultSwitchLabelSyntax:
                                Line("default:");
                                break;
                            default:
                                Report(Diagnostics.UnsupportedSyntax, label.GetLocation(), "pattern case");
                                break;
                        }
                    }
                    indent++;
                    EmitStatements(section.Statements);
                    indent--;
                }
                indent--;
                Line("}");
                break;

            case BreakStatementSyntax:
                Line("break;");
                break;
            case ContinueStatementSyntax:
                Line("continue;");
                break;
            case ReturnStatementSyntax returnStatement:
                Line(returnStatement.Expression == null ? "return;" : "return " + Expression(returnStatement.Expression) + ";");
                break;
            case EmptyStatementSyntax:
                Line(";");
                break;
            default:
                Report(Diagnostics.UnsupportedSyntax, statement.GetLocation(), Describe(statement.Kind()));
                break;
        }
    }

    private void EmitBody(StatementSyntax statement)
    {
        if (statement is BlockSyntax)
        {
            EmitStatement(statement);
            return;
        }
        indent++;
        EmitStatement(statement);
        indent--;
    }

    /// <summary>if (Sdsl.If("A")) { } else if (Sdsl.If("B")) { } else { } as an #if chain, the braces dropped.</summary>
    private void EmitPreprocessorIf(IfStatementSyntax ifStatement)
    {
        IsPreprocessorIf(ifStatement.Condition, out var condition);
        RawLine("#if " + condition);
        EmitUnwrapped(ifStatement.Statement);
        var rest = ifStatement.Else;
        while (rest != null)
        {
            if (rest.Statement is IfStatementSyntax chained && IsPreprocessorIf(chained.Condition, out var chainedCondition))
            {
                RawLine("#elif " + chainedCondition);
                EmitUnwrapped(chained.Statement);
                rest = chained.Else;
            }
            else
            {
                RawLine("#else");
                EmitUnwrapped(rest.Statement);
                rest = null;
            }
        }
        RawLine("#endif");
    }

    private void EmitUnwrapped(StatementSyntax statement)
    {
        if (statement is BlockSyntax block)
        {
            EmitStatements(block.Statements);
            EmitTrailingComments(block.CloseBraceToken);
        }
        else
            EmitStatement(statement);
    }

    private bool IsPreprocessorIf(ExpressionSyntax condition, out string text)
    {
        text = string.Empty;
        if (condition is InvocationExpressionSyntax invocation && IsMarker(invocation, "If", out var arguments))
        {
            text = StringArgument(arguments, 0) ?? string.Empty;
            return true;
        }
        return false;
    }

    /// <summary>A call to a method of Csl.Sdsl.</summary>
    private bool IsMarker(InvocationExpressionSyntax invocation, string name, out SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        arguments = invocation.ArgumentList.Arguments;
        if (model.GetSymbolInfo(invocation, cancellation).Symbol is IMethodSymbol method)
            return method.Name == name && method.ContainingType?.ToDisplayString() == SdslType;
        // Unresolved (an argument of an unknown type): recognise Sdsl.X by syntax.
        return invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Sdsl" } } access
            && access.Name.Identifier.ValueText == name;
    }

    private string? StringArgument(SeparatedSyntaxList<ArgumentSyntax> arguments, int index)
    {
        if (index >= arguments.Count)
            return null;
        var value = model.GetConstantValue(arguments[index].Expression, cancellation);
        return value.HasValue ? value.Value as string : null;
    }

    private bool TryLoopMarker(InvocationExpressionSyntax invocation, out string marker)
    {
        marker = string.Empty;
        if (model.GetSymbolInfo(invocation, cancellation).Symbol is not IMethodSymbol method || method.ContainingType?.ToDisplayString() != IntrinsicsType)
            return false;
        if (method.Name == "Attribute")
        {
            marker = "[" + StringArgument(invocation.ArgumentList.Arguments, 0) + "]";
            return true;
        }
        if (!LoopMarkers.TryGetValue(method.Name, out var attribute))
            return false;
        if (method.Name == "Unroll" && invocation.ArgumentList.Arguments.Count == 1)
            attribute = "[unroll(" + Expression(invocation.ArgumentList.Arguments[0].Expression) + ")]";
        marker = attribute;
        return true;
    }

    private string Declaration(VariableDeclarationSyntax declaration, bool isConst)
    {
        var declared = model.GetTypeInfo(declaration.Type, cancellation).Type;
        var text = new StringBuilder();
        var parts = new List<string>();
        string? prefix = isConst ? "const " : null;
        string? typeText = null;
        foreach (var variable in declaration.Variables)
        {
            var type = declared;
            var initializer = variable.Initializer?.Value;
            // Sdsl.Const(x) and Sdsl.StaticConst(x): the SDSL modifiers of a local C# cannot declare const.
            if (initializer is InvocationExpressionSyntax marker)
            {
                if (IsMarker(marker, "Const", out var constArguments) && constArguments.Count == 1)
                {
                    prefix = "const ";
                    initializer = constArguments[0].Expression;
                }
                else if (IsMarker(marker, "StaticConst", out var staticArguments) && staticArguments.Count == 1)
                {
                    prefix = "static const ";
                    initializer = staticArguments[0].Expression;
                }
            }
            if (declaration.Type.IsVar && initializer != null)
                type = model.GetTypeInfo(initializer, cancellation).ConvertedType ?? type;

            var element = type is IArrayTypeSymbol arrayType ? arrayType.ElementType : type;
            // var copy = streams: the streams structure, which only var names in SDSL.
            if (declaration.Type.IsVar && (IsShaderClass(element) || element?.TypeKind == TypeKind.Dynamic))
                typeText ??= "var";
            typeText ??= SdslTypeName(element, declaration.Type);
            var part = new StringBuilder(variable.Identifier.ValueText);
            if (type is IArrayTypeSymbol)
            {
                // T[] a = new T[n] { … }: T a[n] = { … }.
                string size = string.Empty;
                if (initializer is ArrayCreationExpressionSyntax creation)
                {
                    var rank = creation.Type.RankSpecifiers.FirstOrDefault();
                    if (rank != null && rank.Sizes.Count == 1 && rank.Sizes[0] is not OmittedArraySizeExpressionSyntax)
                        size = Expression(rank.Sizes[0]);
                    initializer = creation.Initializer;
                }
                part.Append('[').Append(size).Append(']');
            }
            if (initializer != null && !IsDefaultLiteral(initializer))
                part.Append(" = ").Append(Initializer(initializer));
            parts.Add(part.ToString());
        }
        text.Append(prefix).Append(typeText).Append(' ').Append(string.Join(", ", parts));
        return text.ToString();
    }

    // -- expressions ---------------------------------------------------------------------------------

    private string Expression(ExpressionSyntax expression)
    {
        cancellation.ThrowIfCancellationRequested();
        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return Literal(literal);

            case ParenthesizedExpressionSyntax parenthesized:
                return "(" + Expression(parenthesized.Expression) + ")";

            case IdentifierNameSyntax identifier:
                return Identifier(identifier);

            case ThisExpressionSyntax:
                return "this";

            case BaseExpressionSyntax:
                return "base";

            case MemberAccessExpressionSyntax memberAccess:
                return MemberAccess(memberAccess);

            case InvocationExpressionSyntax invocation:
                return Invocation(invocation);

            case ElementAccessExpressionSyntax elementAccess:
                return ElementAccess(elementAccess);

            case ObjectCreationExpressionSyntax creation:
                return Creation(model.GetTypeInfo(creation, cancellation).Type, creation.ArgumentList, creation.Initializer, creation);

            case ImplicitObjectCreationExpressionSyntax implicitCreation:
                return Creation(model.GetTypeInfo(implicitCreation, cancellation).Type, implicitCreation.ArgumentList, implicitCreation.Initializer, implicitCreation);

            case CastExpressionSyntax cast:
            {
                var type = SdslTypeName(model.GetTypeInfo(cast.Type, cancellation).Type, cast.Type);
                return "(" + type + ")" + Operand(cast.Expression, 14);
            }

            case PrefixUnaryExpressionSyntax prefix:
                return prefix.OperatorToken.Text + Operand(prefix.Operand, 14);

            case PostfixUnaryExpressionSyntax postfix:
                if (postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
                    return Expression(postfix.Operand);
                return Operand(postfix.Operand) + postfix.OperatorToken.Text;

            case BinaryExpressionSyntax binary:
                if (binary.IsKind(SyntaxKind.IsExpression) || binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.CoalesceExpression))
                {
                    Report(Diagnostics.UnsupportedSyntax, binary.GetLocation(), Describe(binary.Kind()));
                    return "0";
                }
            {
                var precedence = Precedence(binary);
                return Operand(binary.Left, precedence) + " " + binary.OperatorToken.Text + " " + Operand(binary.Right, precedence, rightSide: true);
            }

            case AssignmentExpressionSyntax assignment:
                if (assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression))
                {
                    Report(Diagnostics.UnsupportedSyntax, assignment.GetLocation(), "??=");
                    return "0";
                }
                return Expression(assignment.Left) + " " + assignment.OperatorToken.Text + " " + Expression(assignment.Right);

            case ConditionalExpressionSyntax conditional:
                return Operand(conditional.Condition, 2) + " ? " + Operand(conditional.WhenTrue, 1) + " : " + Operand(conditional.WhenFalse, 1);

            case DefaultExpressionSyntax defaultExpression:
            {
                var type = SdslTypeName(model.GetTypeInfo(defaultExpression, cancellation).Type, defaultExpression);
                return "(" + type + ")0";
            }

            case CheckedExpressionSyntax checkedExpression:
                return Expression(checkedExpression.Expression);

            default:
                Report(Diagnostics.UnsupportedSyntax, expression.GetLocation(), Describe(expression.Kind()));
                return "0";
        }
    }

    /// <summary>
    /// An operand of an operator, parenthesised when reading it back would need it: HLSL has C's
    /// precedence, which is C#'s, so only a lower-precedence child needs them.
    /// </summary>
    private string Operand(ExpressionSyntax expression, int parentPrecedence, bool rightSide = false)
    {
        var text = Expression(expression);
        var precedence = Precedence(expression);
        if (precedence < parentPrecedence || (rightSide && precedence == parentPrecedence && parentPrecedence < 14))
            return "(" + text + ")";
        return text;
    }

    private string Operand(ExpressionSyntax expression) => Operand(expression, 15);

    /// <summary>C precedence, higher binds tighter. Primaries are 16, unary 15.</summary>
    private int Precedence(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case ConditionalExpressionSyntax: return 1;
            case AssignmentExpressionSyntax: return 0;
            case CastExpressionSyntax:
            case PrefixUnaryExpressionSyntax: return 14;
            case InvocationExpressionSyntax invocation when IsMarker(invocation, "Cast", out _): return 14;
            case InvocationExpressionSyntax invocation when IsMarker(invocation, "Implicit", out var implicitArguments) && implicitArguments.Count == 1:
                return Precedence(implicitArguments[0].Expression);
            case BinaryExpressionSyntax binary:
                switch (binary.Kind())
                {
                    case SyntaxKind.LogicalOrExpression: return 2;
                    case SyntaxKind.LogicalAndExpression: return 3;
                    case SyntaxKind.BitwiseOrExpression: return 4;
                    case SyntaxKind.ExclusiveOrExpression: return 5;
                    case SyntaxKind.BitwiseAndExpression: return 6;
                    case SyntaxKind.EqualsExpression:
                    case SyntaxKind.NotEqualsExpression: return 7;
                    case SyntaxKind.LessThanExpression:
                    case SyntaxKind.GreaterThanExpression:
                    case SyntaxKind.LessThanOrEqualExpression:
                    case SyntaxKind.GreaterThanOrEqualExpression: return 8;
                    case SyntaxKind.LeftShiftExpression:
                    case SyntaxKind.RightShiftExpression: return 9;
                    case SyntaxKind.AddExpression:
                    case SyntaxKind.SubtractExpression: return 10;
                    default: return 11;
                }
            default: return 16;
        }
    }

    private string Literal(LiteralExpressionSyntax literal)
    {
        switch (literal.Kind())
        {
            case SyntaxKind.TrueLiteralExpression: return "true";
            case SyntaxKind.FalseLiteralExpression: return "false";
            case SyntaxKind.NumericLiteralExpression:
            {
                var text = literal.Token.Text.Replace("_", string.Empty);
                var value = literal.Token.Value;
                switch (value)
                {
                    case float:
                    {
                        // Keep the digits as written, drop the C# suffix, make sure it reads as a real.
                        var digits = text.TrimEnd('f', 'F', 'd', 'D', 'm', 'M');
                        if (digits.IndexOfAny(new[] { '.', 'e', 'E' }) < 0)
                            digits += ".0";
                        return digits;
                    }
                    case double:
                    {
                        var digits = text.TrimEnd('d', 'D');
                        if (digits.IndexOfAny(new[] { '.', 'e', 'E' }) < 0)
                            digits += ".0";
                        return digits + "L";
                    }
                    case uint when text.EndsWith("u", StringComparison.OrdinalIgnoreCase) && text.StartsWith("0", StringComparison.Ordinal):
                        // The engine's SDSL parser (4.4 beta8) rejects a suffix after a leading 0 (0u, 0x10u): a cast keeps the type.
                        return "(uint)" + text.Substring(0, text.Length - 1);
                    case uint:
                    case int:
                    case long:
                    case ulong:
                        // As written: C# and HLSL read the size of a bare literal alike.
                        return text;
                    default:
                        Report(Diagnostics.UnsupportedSyntax, literal.GetLocation(), "literal of type " + value?.GetType().Name);
                        return text;
                }
            }
            case SyntaxKind.DefaultLiteralExpression:
            {
                var type = model.GetTypeInfo(literal, cancellation).ConvertedType;
                return "(" + SdslTypeName(type, literal) + ")0";
            }
            default:
                Report(Diagnostics.UnsupportedSyntax, literal.GetLocation(), Describe(literal.Kind()));
                return "0";
        }
    }

    private string Identifier(IdentifierNameSyntax identifier)
    {
        var name = identifier.Identifier.ValueText;
        var symbol = model.GetSymbolInfo(identifier, cancellation).Symbol;
        switch (symbol)
        {
            case IFieldSymbol field:
                return FieldReference(field, identifier);
            case ILocalSymbol:
            case IParameterSymbol:
                return name;
            case IMethodSymbol:
                return name;
            case IPropertySymbol { Name: "streams" }:
                return "streams";
            case IPropertySymbol property when property.ContainingType?.ContainingNamespace?.ToDisplayString() == TypesNamespace:
                return name;
            case INamedTypeSymbol type when IsShaderClass(type):
                return ShaderNameOf(type);
            case null:
                if (name == "streams")
                    return "streams";
                return MixinMember(name) is IFieldSymbol mixinField ? FieldReference(mixinField, identifier) : name;
            default:
                Report(Diagnostics.UnsupportedSyntax, identifier.GetLocation(), symbol.Kind.ToString().ToLowerInvariant() + " " + name);
                return name;
        }
    }

    /// <summary>A member of one of the [Mixin] shaders (of this class or of its C# bases), by name.</summary>
    private ISymbol? MixinMember(string name)
    {
        var covered = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var mixin in mixins)
        {
            foreach (var member in ShaderPartialEmitter.AllMembers(mixin, covered))
            {
                if (member.Name == name && member.DeclaredAccessibility != Accessibility.Private)
                    return member;
            }
        }
        return null;
    }

    private string FieldReference(IFieldSymbol field, SyntaxNode at)
    {
        if (field.ContainingType == null || !IsShaderClass(field.ContainingType))
        {
            if (field.ContainingType?.TypeKind == TypeKind.Struct)
                return field.Name;
            Report(Diagnostics.UnsupportedSyntax, at.GetLocation(), "field " + field.ToDisplayString());
            return field.Name;
        }
        return IsStream(field) ? "streams." + field.Name : field.Name;
    }

    private static bool IsStream(IFieldSymbol field) => field.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() is "Csl.StreamAttribute" or "Csl.PatchStreamAttribute");

    private bool IsStreamsExpression(ExpressionSyntax expression) =>
        expression is IdentifierNameSyntax { Identifier.ValueText: "streams" } identifier
        && model.GetSymbolInfo(identifier, cancellation).Symbol is null or IPropertySymbol { Name: "streams" };

    private string MemberAccess(MemberAccessExpressionSyntax memberAccess)
    {
        var symbol = model.GetSymbolInfo(memberAccess, cancellation).Symbol;
        var name = memberAccess.Name.Identifier.ValueText;

        if (IsStreamsExpression(memberAccess.Expression))
            return "streams." + name;
        if (memberAccess.Expression is ThisExpressionSyntax)
            return "this." + name;
        if (memberAccess.Expression is BaseExpressionSyntax)
            return "base." + name;
        // Sdsl.Base(this).M: base.M.
        if (memberAccess.Expression is InvocationExpressionSyntax baseInvocation && IsMarker(baseInvocation, "Base", out _))
            return "base." + name;
        // Sdsl.Static<T>().Member: T.Member.
        if (memberAccess.Expression is InvocationExpressionSyntax staticInvocation && IsMarker(staticInvocation, "Static", out var staticArguments))
        {
            var typeArgument = (staticInvocation.Expression as MemberAccessExpressionSyntax)?.Name as GenericNameSyntax;
            var target = typeArgument?.TypeArgumentList.Arguments.FirstOrDefault();
            var targetType = target == null ? null : model.GetTypeInfo(target, cancellation).Type;
            var shaderName = targetType is INamedTypeSymbol namedTarget && IsShaderClass(namedTarget) ? ShaderNameOf(namedTarget) : target?.ToString() ?? "?";
            var generics = StringArgument(staticArguments, 0);
            return shaderName + (generics != null ? "<" + generics + ">" : string.Empty) + "." + name;
        }

        // A static member of a type: the intrinsics, and shaders' statics.
        if (model.GetSymbolInfo(memberAccess.Expression, cancellation).Symbol is INamedTypeSymbol type)
        {
            if (type.ToDisplayString() == IntrinsicsType)
                return name;
            if (IsShaderClass(type))
                return ShaderNameOf(type) + "." + name;
            if (type.ContainingNamespace?.ToDisplayString() == TypesNamespace && type.IsValueType)
                return SdslTypeName(type, memberAccess.Expression) + "." + name;
            Report(Diagnostics.UnsupportedCall, memberAccess.GetLocation(), type.ToDisplayString() + "." + name);
            return name;
        }

        switch (symbol)
        {
            case IPropertySymbol property when property.ContainingType?.ContainingNamespace?.ToDisplayString() == TypesNamespace:
                // A swizzle, a matrix element, a resource member.
                return Operand(memberAccess.Expression) + "." + name;
            case IFieldSymbol field when field.ContainingType?.TypeKind == TypeKind.Struct:
                return Operand(memberAccess.Expression) + "." + name;
            case IMethodSymbol:
                return Operand(memberAccess.Expression) + "." + name;
            case IFieldSymbol field when IsShaderClass(field.ContainingType):
                // compose.Member, or a shader's static.
                return Operand(memberAccess.Expression) + "." + name;
            case null:
                // dynamic, or a member the generated stubs will declare.
                return Operand(memberAccess.Expression) + "." + name;
            default:
                Report(Diagnostics.UnsupportedSyntax, memberAccess.GetLocation(), "member " + name + " of " + symbol.ContainingType?.ToDisplayString());
                return Operand(memberAccess.Expression) + "." + name;
        }
    }

    private string ElementAccess(ElementAccessExpressionSyntax elementAccess)
    {
        var arguments = elementAccess.ArgumentList.Arguments;
        // streams[TName]: a member named by a MemberName generic parameter; streams["X"]: a stream the effect brings.
        if (arguments.Count == 1 && model.GetTypeInfo(arguments[0].Expression, cancellation).Type?.ToDisplayString() == "Csl.MemberName")
            return Operand(elementAccess.Expression) + "." + arguments[0].Expression;
        if (arguments.Count == 1 && IsStreamsExpression(elementAccess.Expression) && StringArgument(arguments, 0) is { } streamName)
            return "streams." + streamName;
        return Operand(elementAccess.Expression) + "[" + string.Join(", ", arguments.Select(a => Expression(a.Expression))) + "]";
    }

    private string Invocation(InvocationExpressionSyntax invocation)
    {
        var symbol = model.GetSymbolInfo(invocation, cancellation).Symbol as IMethodSymbol;
        var arguments = invocation.ArgumentList.Arguments;

        // The markers of Csl.Sdsl.
        if (symbol?.ContainingType?.ToDisplayString() == SdslType || (symbol == null && invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Sdsl" } }))
        {
            var markerName = symbol?.Name ?? ((MemberAccessExpressionSyntax)invocation.Expression).Name.Identifier.ValueText;
            switch (markerName)
            {
                case "Macro":
                    return StringArgument(arguments, 0) ?? "0";
                case "Member":
                    if (arguments.Count == 2)
                    {
                        var memberName = StringArgument(arguments, 1) ?? arguments[1].Expression.ToString();
                        if (arguments[0].Expression is ThisExpressionSyntax)
                            return memberName;
                        return Operand(arguments[0].Expression) + "." + memberName;
                    }
                    break;
                case "Ref":
                    if (arguments.Count == 1)
                        return Expression(arguments[0].Expression);
                    break;
                case "Implicit":
                    if (arguments.Count == 1)
                        return Expression(arguments[0].Expression);
                    break;
                case "Cast":
                    if (arguments.Count == 1 && invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax castName })
                    {
                        var castType = model.GetTypeInfo(castName.TypeArgumentList.Arguments[0], cancellation).Type;
                        return "(" + SdslTypeName(castType, castName) + ")" + Operand(arguments[0].Expression, 14);
                    }
                    break;
            }
            Report(Diagnostics.UnsupportedSyntax, invocation.GetLocation(), "Sdsl." + markerName + " here");
            return "0";
        }

        var argumentText = string.Join(", ", arguments.Select(a => Expression(a.Expression)));
        if (symbol == null)
        {
            // A method of a [Mixin] shader (its stub is generated), or a call on a dynamic value.
            switch (invocation.Expression)
            {
                case IdentifierNameSyntax id:
                    return id.Identifier.ValueText + "(" + argumentText + ")";
                case MemberAccessExpressionSyntax access:
                    return MemberAccess(access) + "(" + argumentText + ")";
                case InvocationExpressionSyntax member when IsMarker(member, "Member", out _):
                    // Sdsl.Member(x, "name")(args): a method named by a macro.
                    return Expression(member) + "(" + argumentText + ")";
            }
            Report(Diagnostics.UnsupportedCall, invocation.GetLocation(), invocation.Expression.ToString());
            return invocation.Expression + "(" + argumentText + ")";
        }

        var owner = symbol.ContainingType;
        if (owner.ToDisplayString() == IntrinsicsType)
        {
            if (LoopMarkers.ContainsKey(symbol.Name) || symbol.Name == "Attribute" || symbol.Name == "discard")
            {
                Report(Diagnostics.UnsupportedSyntax, invocation.GetLocation(), symbol.Name + "() anywhere but as a statement");
                return string.Empty;
            }
            return symbol.Name + "(" + argumentText + ")";
        }
        if (owner.ContainingNamespace?.ToDisplayString() == TypesNamespace)
        {
            // Texture.Load(...), Buffer.GetDimensions(...): a member of a resource.
            var target = invocation.Expression is MemberAccessExpressionSyntax access ? Operand(access.Expression) + "." : string.Empty;
            return target + symbol.Name + "(" + argumentText + ")";
        }
        if (IsShaderClass(owner))
        {
            if (invocation.Expression is MemberAccessExpressionSyntax access)
                return MemberAccess(access) + "(" + argumentText + ")";
            return symbol.Name + "(" + argumentText + ")";
        }
        Report(Diagnostics.UnsupportedCall, invocation.GetLocation(), symbol.ToDisplayString());
        return symbol.Name + "(" + argumentText + ")";
    }

    private string Creation(ITypeSymbol? type, ArgumentListSyntax? arguments, InitializerExpressionSyntax? initializer, SyntaxNode at)
    {
        if (initializer != null)
            Report(Diagnostics.UnsupportedSyntax, initializer.GetLocation(), "object initializer");
        if (type == null || type.TypeKind != TypeKind.Struct)
        {
            Report(Diagnostics.ObjectCreation, at.GetLocation(), type?.ToDisplayString() ?? "?");
            return "0";
        }
        var sdslType = SdslTypeName(type, at);
        if (arguments == null || arguments.Arguments.Count == 0)
            return "(" + sdslType + ")0";
        return sdslType + "(" + string.Join(", ", arguments.Arguments.Select(a => Expression(a.Expression))) + ")";
    }

    // -- types ---------------------------------------------------------------------------------------

    private string SdslTypeName(ITypeSymbol? type, SyntaxNode at, bool stripArray = false)
    {
        if (stripArray)
            while (type is IArrayTypeSymbol array)
                type = array.ElementType;
        if (type == null)
        {
            Report(Diagnostics.UnsupportedType, at.GetLocation(), "?");
            return "?";
        }
        switch (type.SpecialType)
        {
            case SpecialType.System_Void: return "void";
            case SpecialType.System_Boolean: return "bool";
            case SpecialType.System_Int32: return "int";
            case SpecialType.System_UInt32: return "uint";
            case SpecialType.System_Single: return "float";
            case SpecialType.System_Double: return "double";
            case SpecialType.System_Int64: return "int64_t";
            case SpecialType.System_UInt64: return "uint64_t";
        }
        if (type is INamedTypeSymbol named)
        {
            if (named.ContainingNamespace?.ToDisplayString() == TypesNamespace)
            {
                if (named.IsGenericType)
                {
                    // Texture2DMS<float4, Samples4> is Texture2DMS<float4, 4>.
                    var arguments = named.TypeArguments.Select(t => t.Name.StartsWith("Samples", StringComparison.Ordinal) && t.ContainingNamespace?.ToDisplayString() == TypesNamespace
                        ? t.Name.Substring("Samples".Length)
                        : SdslTypeName(t, at));
                    return named.Name + "<" + string.Join(", ", arguments) + ">";
                }
                return named.Name;
            }
            if (named.TypeKind == TypeKind.Struct && named.ContainingType != null && IsShaderClass(named.ContainingType))
                return named.Name;
            if (IsShaderClass(named))
                return ShaderNameOf(named);
        }
        return Unsupported(at, type);
    }

    private string Unsupported(SyntaxNode at, ITypeSymbol? type)
    {
        Report(Diagnostics.UnsupportedType, at.GetLocation(), type?.ToDisplayString() ?? "?");
        return type?.Name ?? "?";
    }

    // -- output --------------------------------------------------------------------------------------

    private void Line(string text)
    {
        if (text.Length > 0)
            for (int i = 0; i < indent; i++)
                sb.Append("    ");
        sb.Append(text).Append('\n');
    }

    /// <summary>A preprocessor line: at the start of the line.</summary>
    private void RawLine(string text) => sb.Append(text).Append('\n');

    /// <summary>The comments before a node (not the doc comment, which EmitDoc writes), as written.</summary>
    private void EmitComments(SyntaxNode node)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                foreach (var line in trivia.ToFullString().Replace("\r", string.Empty).Split('\n'))
                    Line(line.Trim());
        }
    }

    private void EmitTrailingComments(SyntaxToken closeBrace)
    {
        foreach (var trivia in closeBrace.LeadingTrivia)
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                foreach (var line in trivia.ToFullString().Replace("\r", string.Empty).Split('\n'))
                    Line(line.Trim());
        }
    }

    /// <summary>The /// comment of a member as /// lines, as written, and the other comments before it.</summary>
    private void EmitDoc(SyntaxNode node)
    {
        foreach (var piece in node.GetLeadingTrivia())
        {
            if (piece.IsKind(SyntaxKind.SingleLineCommentTrivia) || piece.IsKind(SyntaxKind.MultiLineCommentTrivia))
            {
                foreach (var line in piece.ToFullString().Replace("\r", string.Empty).Split('\n'))
                    Line(line.Trim());
                continue;
            }
            if (!piece.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia))
                continue;
            foreach (var rawLine in piece.ToFullString().Replace("\r", string.Empty).Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("///", StringComparison.Ordinal))
                    Line(line);
            }
        }
    }

    private void Report(DiagnosticDescriptor descriptor, Location location, params object[] args)
        => result.Diagnostics.Add(Diagnostic.Create(descriptor, location, args));

    private static string Describe(SyntaxKind kind)
    {
        var text = kind.ToString();
        if (text.EndsWith("Expression", StringComparison.Ordinal)) text = text.Substring(0, text.Length - "Expression".Length) + " expression";
        else if (text.EndsWith("Statement", StringComparison.Ordinal)) text = text.Substring(0, text.Length - "Statement".Length) + " statement";
        return char.ToLowerInvariant(text[0]) + text.Substring(1);
    }
}
