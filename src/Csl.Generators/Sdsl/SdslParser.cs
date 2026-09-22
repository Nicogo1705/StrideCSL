using System;
using System.Collections.Generic;
using System.Text;

namespace Csl.Generators.Sdsl;

/// <summary>
/// Reads the declarations of an SDSL file: namespaces, shaders, their bases and members. Method
/// bodies are not parsed, only kept as tokens and scanned for what they do to each resource. This
/// covers what a wrapper needs and nothing more; the engine's own parser stays the authority on
/// whether the shader is valid.
/// </summary>
public sealed class SdslParser
{
    private static readonly HashSet<string> Modifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "stage", "stream", "patchstream", "compose", "const", "static", "groupshared", "override", "abstract", "clone",
        "internal", "inline", "extern", "precise", "uniform", "nointerpolation", "linear", "centroid", "noperspective",
        "sample", "unsigned", "row_major", "column_major", "in", "out", "inout", "volatile", "shared",
    };

    private static readonly HashSet<string> ReadingMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "Load", "Sample", "SampleLevel", "SampleGrad", "SampleBias", "SampleCmp", "SampleCmpLevelZero",
        "Gather", "GatherRed", "GatherGreen", "GatherBlue", "GatherAlpha", "GetDimensions", "CalculateLevelOfDetail",
        "Load2", "Load3", "Load4",
    };

    private static readonly HashSet<string> WritingMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "Store", "Store2", "Store3", "Store4", "Append",
    };

    private readonly List<SdslToken> tokens;
    private readonly SdslFile file;
    private int position;

    private SdslParser(string path, string text)
    {
        tokens = SdslTokenizer.Tokenize(text);
        file = new SdslFile(path);
    }

    public static SdslFile Parse(string path, string text)
    {
        var parser = new SdslParser(path, text);
        parser.ParseFile();
        return parser.file;
    }

    private SdslToken Current => tokens[position];
    private SdslToken Peek(int offset = 1) => tokens[Math.Min(position + offset, tokens.Count - 1)];
    private bool AtEnd => Current.Kind == SdslTokenKind.End;

    private SdslToken Advance()
    {
        var token = Current;
        if (!AtEnd) position++;
        return token;
    }

    private void Error(string message, SdslToken at) => file.Errors.Add(new SdslError(message, at.Line, at.Column));

    private void ParseFile()
    {
        while (!AtEnd)
        {
            if (Current.IsIdentifier("namespace"))
            {
                Advance();
                var name = new StringBuilder();
                while (Current.Kind == SdslTokenKind.Identifier)
                {
                    name.Append(Advance().Text);
                    if (Current.Is("."))
                    {
                        Advance();
                        name.Append('.');
                    }
                }
                file.Namespace = name.ToString();
                if (Current.Is("{"))
                    Advance();
                continue;
            }
            if (Current.IsIdentifier("shader") || Current.IsIdentifier("class"))
            {
                ParseShader();
                continue;
            }
            if (Current.IsIdentifier("effect") || Current.IsIdentifier("struct"))
            {
                SkipDeclaration();
                continue;
            }
            Advance();
        }
    }

    private void ParseShader()
    {
        var keyword = Advance();
        if (Current.Kind != SdslTokenKind.Identifier)
        {
            Error("Expected a shader name", Current);
            return;
        }
        var nameToken = Advance();
        var shader = new SdslShader(nameToken.Text, nameToken.Line);
        if (keyword.Doc != null)
            shader.Doc.AddRange(keyword.Doc);

        if (Current.Is("<"))
        {
            foreach (var generic in ReadGenericArguments())
                shader.GenericParameters.Add(generic);
        }

        if (Current.Is(":"))
        {
            Advance();
            while (Current.Kind == SdslTokenKind.Identifier)
            {
                shader.Bases.Add(ReadQualifiedName());
                if (Current.Is("<"))
                    ReadGenericArguments();
                if (Current.Is(","))
                    Advance();
                else
                    break;
            }
        }

        if (!Current.Is("{"))
        {
            Error("Expected '{' after the shader header", Current);
            return;
        }
        Advance();
        ParseMembers(shader, keysOnly: false);
        if (Current.Is(";"))
            Advance();

        ScanUsage(shader);
        file.Shaders.Add(shader);
    }

    /// <summary>Members up to the closing brace of the block, which is consumed.</summary>
    private void ParseMembers(SdslShader shader, bool keysOnly)
    {
        var attributes = new List<(string, string?)>();
        List<string>? doc = null;

        while (!AtEnd && !Current.Is("}"))
        {
            var start = Current;
            doc ??= start.Doc;

            if (Current.Is("["))
            {
                attributes.Add(ReadAttribute());
                continue;
            }

            if (Current.IsIdentifier("cbuffer") || Current.IsIdentifier("rgroup") || Current.IsIdentifier("tbuffer"))
            {
                Advance();
                if (Current.Kind == SdslTokenKind.Identifier) Advance();
                if (Current.Is("{"))
                {
                    Advance();
                    ParseMembers(shader, keysOnly: true);
                }
                if (Current.Is(";")) Advance();
                attributes.Clear();
                doc = null;
                continue;
            }

            if (Current.IsIdentifier("struct") || Current.IsIdentifier("typedef"))
            {
                SkipDeclaration();
                attributes.Clear();
                doc = null;
                continue;
            }

            bool isStage = false, isStream = false, isCompose = false, isConst = false, isStatic = false, isGroupShared = false;
            bool isAbstract = false, isOverride = false;
            while (Current.Kind == SdslTokenKind.Identifier && Modifiers.Contains(Current.Text))
            {
                switch (Advance().Text)
                {
                    case "stage": isStage = true; break;
                    case "stream": case "patchstream": isStream = true; break;
                    case "compose": isCompose = true; break;
                    case "const": isConst = true; break;
                    case "static": isStatic = true; break;
                    case "groupshared": isGroupShared = true; break;
                    case "abstract": isAbstract = true; break;
                    case "override": isOverride = true; break;
                }
            }

            if (Current.Kind != SdslTokenKind.Identifier)
            {
                Error($"Unexpected '{Current.Text}' in shader {shader.Name}", Current);
                SkipToSemicolonOrBlock();
                attributes.Clear();
                doc = null;
                continue;
            }

            var typeName = ReadQualifiedName();
            string? genericArgument = null;
            if (Current.Is("<"))
                genericArgument = string.Join(", ", ReadGenericArguments());

            if (Current.Kind != SdslTokenKind.Identifier)
            {
                Error($"Expected a name after type {typeName}", Current);
                SkipToSemicolonOrBlock();
                attributes.Clear();
                doc = null;
                continue;
            }

            // A method: name, parameters, body or ';'.
            if (Peek().Is("("))
            {
                var methodName = Advance();
                var method = new SdslMethod(methodName.Text, methodName.Line) { IsAbstract = isAbstract, IsOverride = isOverride };
                if (doc != null)
                    method.Doc.AddRange(doc);
                method.Signature = ReadParameters(new SdslSignature(typeName, genericArgument));
                if (Current.Is("{"))
                {
                    int depth = 0;
                    while (!AtEnd)
                    {
                        var token = Advance();
                        if (token.Is("{")) depth++;
                        else if (token.Is("}") && --depth == 0) break;
                        else method.Body.Add(token);
                    }
                }
                else if (Current.Is(";"))
                {
                    Advance();
                }
                shader.Methods.Add(method);
                attributes.Clear();
                doc = null;
                continue;
            }

            // Variables, possibly several declarators sharing the type.
            while (true)
            {
                var nameToken = Advance();
                var member = new SdslMember(typeName, nameToken.Text, nameToken.Line, nameToken.Column)
                {
                    GenericArgument = genericArgument,
                    IsStage = isStage,
                    IsStream = isStream,
                    IsCompose = isCompose,
                    IsConst = isConst,
                    IsStatic = isStatic,
                    IsGroupShared = isGroupShared,
                };
                member.Attributes.AddRange(attributes);
                if (doc != null)
                    member.Doc.AddRange(doc);

                while (Current.Is("["))
                {
                    member.IsArray = true;
                    SkipBalanced("[", "]");
                }
                if (Current.Is(":"))
                {
                    Advance();
                    if (Current.Kind == SdslTokenKind.Identifier) member.Semantic = Advance().Text;
                }
                if (Current.Is("="))
                {
                    Advance();
                    member.Initializer = ReadInitializer();
                }
                shader.Members.Add(member);

                if (Current.Is(","))
                {
                    Advance();
                    if (Current.Kind == SdslTokenKind.Identifier)
                        continue;
                }
                break;
            }
            if (Current.Is(";"))
                Advance();
            else
            {
                Error($"Expected ';' after {typeName}", Current);
                SkipToSemicolonOrBlock();
            }
            attributes.Clear();
            doc = null;
        }
        if (Current.Is("}"))
            Advance();
    }

    /// <summary>The parameter list, from '(' to ')' inclusive.</summary>
    private SdslSignature ReadParameters(SdslSignature signature)
    {
        Advance(); // (
        while (!AtEnd && !Current.Is(")"))
        {
            string? modifier = null;
            while (Current.Kind == SdslTokenKind.Identifier && (Current.Text == "in" || Current.Text == "out" || Current.Text == "inout" || Modifiers.Contains(Current.Text)))
            {
                var m = Advance().Text;
                if (m == "in" || m == "out" || m == "inout") modifier = m;
            }
            if (Current.Kind != SdslTokenKind.Identifier)
            {
                SkipBalanced("(", ")");
                return signature;
            }
            var type = ReadQualifiedName();
            string? generic = null;
            if (Current.Is("<"))
                generic = string.Join(", ", ReadGenericArguments());
            var name = Current.Kind == SdslTokenKind.Identifier ? Advance().Text : "_";
            while (Current.Is("["))
                SkipBalanced("[", "]");
            if (Current.Is(":"))
            {
                Advance();
                if (Current.Kind == SdslTokenKind.Identifier) Advance();
            }
            if (Current.Is("="))
            {
                Advance();
                ReadInitializer();
            }
            signature.Parameters.Add(new SdslParameter(type, generic, name, modifier));
            if (Current.Is(","))
                Advance();
        }
        if (Current.Is(")"))
            Advance();
        return signature;
    }

    private (string Name, string? Argument) ReadAttribute()
    {
        Advance(); // [
        string name = Current.Kind == SdslTokenKind.Identifier ? Advance().Text : string.Empty;
        string? argument = null;
        if (Current.Is("("))
        {
            Advance();
            if (Current.Kind == SdslTokenKind.String || Current.Kind == SdslTokenKind.Identifier || Current.Kind == SdslTokenKind.Number)
                argument = Current.Text;
            int depth = 1;
            while (!AtEnd && depth > 0)
            {
                var token = Advance();
                if (token.Is("(")) depth++;
                else if (token.Is(")")) depth--;
            }
        }
        while (!AtEnd && !Current.Is("]"))
            Advance();
        if (Current.Is("]"))
            Advance();
        return (name, argument);
    }

    private string ReadQualifiedName()
    {
        var name = new StringBuilder(Advance().Text);
        while (Current.Is(".") && Peek().Kind == SdslTokenKind.Identifier)
        {
            Advance();
            name.Append('.').Append(Advance().Text);
        }
        return name.ToString();
    }

    /// <summary>Reads &lt;a, b&lt;c&gt;&gt; into its top-level arguments as text.</summary>
    private List<string> ReadGenericArguments()
    {
        var result = new List<string>();
        Advance(); // <
        int depth = 1;
        var current = new StringBuilder();
        while (!AtEnd && depth > 0)
        {
            var token = Advance();
            if (token.Is("<")) depth++;
            else if (token.Is(">")) depth--;
            else if (token.Is(">>")) depth -= 2;
            if (depth <= 0)
                break;
            if (token.Is(",") && depth == 1)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }
            if (current.Length > 0 && token.Kind != SdslTokenKind.Punctuation && !current[current.Length - 1].Equals('<'))
                current.Append(' ');
            current.Append(token.Text);
        }
        if (current.Length > 0)
            result.Add(current.ToString().Trim());
        return result;
    }

    private string ReadInitializer()
    {
        var text = new StringBuilder();
        int depth = 0;
        while (!AtEnd)
        {
            if (depth == 0 && (Current.Is(";") || Current.Is(",")))
                break;
            var token = Advance();
            if (token.Is("(") || token.Is("[") || token.Is("{")) depth++;
            else if (token.Is(")") || token.Is("]") || token.Is("}")) depth--;
            if (text.Length > 0 && token.Kind != SdslTokenKind.Punctuation && text[text.Length - 1] != '(' && text[text.Length - 1] != '-')
                text.Append(' ');
            text.Append(token.Text);
        }
        return text.ToString();
    }

    private void SkipBalanced(string open, string close)
    {
        if (!Current.Is(open))
            return;
        int depth = 0;
        while (!AtEnd)
        {
            var token = Advance();
            if (token.Is(open)) depth++;
            else if (token.Is(close) && --depth == 0) break;
        }
    }

    /// <summary>Skips a declaration up to its ';', including any block it has.</summary>
    private void SkipDeclaration()
    {
        while (!AtEnd)
        {
            if (Current.Is("{"))
            {
                SkipBalanced("{", "}");
                if (Current.Is(";")) Advance();
                return;
            }
            if (Advance().Is(";"))
                return;
        }
    }

    private void SkipToSemicolonOrBlock()
    {
        while (!AtEnd)
        {
            if (Current.Is("{"))
            {
                SkipBalanced("{", "}");
                return;
            }
            if (Current.Is("}"))
                return;
            if (Advance().Is(";"))
                return;
        }
    }

    // -- usage --------------------------------------------------------------------------------------

    /// <summary>What each resource member is used for, from every method body of the shader.</summary>
    private static void ScanUsage(SdslShader shader)
    {
        var resources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in shader.Members)
        {
            if (SdslTypes.IsResource(member.Type))
            {
                resources.Add(member.Name);
                shader.Usage[member.Name] = SdslAccess.None;
            }
        }
        if (resources.Count == 0)
            return;

        foreach (var method in shader.Methods)
        {
            var body = method.Body;
            for (int i = 0; i < body.Count; i++)
            {
                var token = body[i];
                if (token.Kind != SdslTokenKind.Identifier || !resources.Contains(token.Text))
                    continue;
                // A member access on something else: streams.Foo, or this.Foo counts as ours.
                if (i > 0 && body[i - 1].Is(".") && !(i > 1 && body[i - 2].IsIdentifier("this")))
                    continue;

                shader.Usage[token.Text] |= ClassifyUse(body, i);
            }
        }
    }

    private static SdslAccess ClassifyUse(List<SdslToken> body, int index)
    {
        // Interlocked*(Resource[...], ...) both reads and writes.
        if (index >= 2 && body[index - 1].Is("(") && body[index - 2].Kind == SdslTokenKind.Identifier
            && body[index - 2].Text.StartsWith("Interlocked", StringComparison.Ordinal))
            return SdslAccess.ReadWrite;

        int next = index + 1;
        if (next < body.Count && body[next].Is("["))
        {
            int depth = 0;
            int j = next;
            for (; j < body.Count; j++)
            {
                if (body[j].Is("[")) depth++;
                else if (body[j].Is("]") && --depth == 0) break;
            }
            j++;
            // Nested indexing (Resource[a][b]) keeps going.
            while (j < body.Count && body[j].Is("["))
            {
                depth = 0;
                for (; j < body.Count; j++)
                {
                    if (body[j].Is("[")) depth++;
                    else if (body[j].Is("]") && --depth == 0) break;
                }
                j++;
            }
            if (j < body.Count && body[j].Kind == SdslTokenKind.Punctuation)
            {
                var op = body[j].Text;
                if (op == "=")
                    return SdslAccess.Write;
                if (op.Length >= 2 && op.EndsWith("=", StringComparison.Ordinal) && op != "==" && op != "!=" && op != "<=" && op != ">=")
                    return SdslAccess.ReadWrite;
                if (op == "++" || op == "--")
                    return SdslAccess.ReadWrite;
            }
            return SdslAccess.Read;
        }
        if (next < body.Count && body[next].Is(".") && next + 1 < body.Count && body[next + 1].Kind == SdslTokenKind.Identifier)
        {
            var method = body[next + 1].Text;
            if (WritingMethods.Contains(method))
                return SdslAccess.Write;
            if (ReadingMethods.Contains(method))
                return SdslAccess.Read;
            if (method.StartsWith("Interlocked", StringComparison.Ordinal))
                return SdslAccess.ReadWrite;
            return SdslAccess.Read;
        }
        // Passed along or compared: assume a read.
        return SdslAccess.Read;
    }
}
