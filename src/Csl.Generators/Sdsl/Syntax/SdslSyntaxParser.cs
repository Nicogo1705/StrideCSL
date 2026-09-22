using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Csl.Generators.Sdsl.Syntax;

/// <summary>
/// Parses a whole SDSL file into <see cref="SdslCompilationUnit"/>: shaders, members, bodies and
/// expressions, with the #if conditions around members and statements kept as structure. Errors are
/// collected, not thrown; a shader with errors should not be converted.
/// </summary>
public sealed class SdslSyntaxParser
{
    private static readonly HashSet<string> MemberModifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "stage", "stream", "patchstream", "compose", "const", "static", "groupshared", "override", "abstract", "clone",
        "internal", "inline", "extern", "precise", "uniform", "nointerpolation", "linear", "centroid", "noperspective",
        "sample", "volatile", "shared", "row_major", "column_major", "snorm", "unorm", "virtual",
    };

    private static readonly HashSet<string> ParameterModifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "in", "out", "inout", "const", "uniform", "point", "line", "triangle", "lineadj", "triangleadj",
        "nointerpolation", "linear", "centroid", "noperspective", "sample", "precise", "row_major", "column_major",
    };

    private static readonly HashSet<string> LocalModifiers = new HashSet<string>(StringComparer.Ordinal)
    {
        "const", "static", "uniform", "precise", "row_major", "column_major", "snorm", "unorm", "volatile",
    };

    private static readonly HashSet<string> StatementKeywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "switch", "case", "default", "return", "break", "continue", "discard",
    };

    /// <summary>Types that take generic arguments, so a statement starting with one followed by '&lt;' is a declaration.</summary>
    private static readonly HashSet<string> GenericTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "vector", "matrix", "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray", "Texture2DMS", "Texture2DMSArray",
        "Texture3D", "TextureCube", "TextureCubeArray", "RWTexture1D", "RWTexture1DArray", "RWTexture2D", "RWTexture2DArray",
        "RWTexture3D", "Buffer", "RWBuffer", "StructuredBuffer", "RWStructuredBuffer", "AppendStructuredBuffer",
        "ConsumeStructuredBuffer", "InputPatch", "OutputPatch", "TriangleStream", "LineStream", "PointStream",
        "RasterizerOrderedTexture2D", "RasterizerOrderedBuffer", "RasterizerOrderedStructuredBuffer",
    };

    private static readonly Regex BuiltinTypePattern = new Regex(
        @"^(bool|int|uint|dword|half|float|double|min16float|min10float|min16int|min12int|min16uint|int64_t|uint64_t)([1-4](x[1-4])?)?$",
        RegexOptions.Compiled);

    private readonly List<SdslLexToken> tokens;
    private readonly SdslCompilationUnit unit;
    private readonly HashSet<string> knownTypes = new HashSet<string>(StringComparer.Ordinal);
    private int position;

    /// <summary>The #if chains the parser is inside, outermost first.</summary>
    private readonly List<ConditionFrame> conditions = new List<ConditionFrame>();

    private SdslSyntaxParser(string path, string text)
    {
        tokens = SdslLexer.Tokenize(text);
        unit = new SdslCompilationUnit(path);
        // Struct and typedef names are types for the cast rule: (Name)x.
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if ((tokens[i].IsWord("struct") || tokens[i].IsWord("typedef")) && tokens[i + 1].Kind == SdslLexKind.Identifier)
            {
                if (tokens[i].IsWord("struct"))
                    knownTypes.Add(tokens[i + 1].Text);
                else
                {
                    int j = i + 1;
                    while (j + 1 < tokens.Count && !tokens[j + 1].Is(";")) j++;
                    if (tokens[j].Kind == SdslLexKind.Identifier)
                        knownTypes.Add(tokens[j].Text);
                }
            }
        }
        foreach (var name in new[] { "Streams", "Input", "Output", "Input2", "Output2", "Constants", "SamplerState", "SamplerComparisonState" })
            knownTypes.Add(name);
    }

    public static SdslCompilationUnit Parse(string path, string text)
    {
        var parser = new SdslSyntaxParser(path, text);
        try
        {
            parser.ParseItems(parser.unit.Items, null, topLevel: true);
        }
        catch (ParseAbort)
        {
        }
        return parser.unit;
    }

    public static bool IsBuiltinType(string name) => BuiltinTypePattern.IsMatch(name);

    private sealed class ParseAbort : Exception
    {
    }

    private sealed class ConditionFrame
    {
        public ConditionFrame(string directive, string condition)
        {
            Branches.Add((directive, condition));
        }

        /// <summary>Every branch of the chain so far: its directive and its text.</summary>
        public List<(string Directive, string? Condition)> Branches { get; } = new List<(string, string?)>();

        /// <summary>The condition of the current branch, alone: what a member under it needs.</summary>
        public string Current
        {
            get
            {
                var parts = new List<string>();
                for (int i = 0; i < Branches.Count - 1; i++)
                    parts.Add("!" + Wrap(Normalize(Branches[i].Directive, Branches[i].Condition)));
                var last = Branches[Branches.Count - 1];
                if (last.Directive != "else")
                    parts.Add(Branches.Count == 1 ? Normalize(last.Directive, last.Condition) : Wrap(Normalize(last.Directive, last.Condition)));
                return string.Join(" && ", parts);
            }
        }

        public static string Normalize(string directive, string? condition) => directive switch
        {
            "ifdef" => "defined(" + condition + ")",
            "ifndef" => "!defined(" + condition + ")",
            _ => condition ?? string.Empty,
        };

        private static string Wrap(string condition) => IsSimple(condition) ? condition : "(" + condition + ")";

        private static bool IsSimple(string condition) => Regex.IsMatch(condition, @"^!?(defined\s*\(\s*\w+\s*\)|\w+)$");
    }

    // -- tokens --------------------------------------------------------------------------------------

    private SdslLexToken Current => tokens[position];
    private SdslLexToken Peek(int offset = 1) => tokens[Math.Min(position + offset, tokens.Count - 1)];
    private bool AtEnd => Current.Kind == SdslLexKind.End;

    private SdslLexToken Advance()
    {
        var token = Current;
        if (!AtEnd) position++;
        return token;
    }

    private void Error(string message, SdslLexToken at) => unit.Diagnostics.Add(new SdslDiagnostic(message, at.Position));

    private SdslLexToken Expect(string punctuation)
    {
        if (!Current.Is(punctuation))
        {
            Error("Expected '" + punctuation + "' but found '" + Current.Text + "'", Current);
            throw new ParseAbort();
        }
        return Advance();
    }

    private string ExpectIdentifier(string what)
    {
        if (Current.Kind != SdslLexKind.Identifier)
        {
            Error("Expected " + what + " but found '" + Current.Text + "'", Current);
            throw new ParseAbort();
        }
        return Advance().Text;
    }

    /// <summary>Copies the comments and position of the token to the node.</summary>
    private T Mark<T>(T node, SdslLexToken at) where T : SdslNode
    {
        node.Position = at.Position;
        if (at.Comments != null)
            node.Comments.AddRange(at.Comments);
        node.BlankLineBefore = at.BlankLineBefore;
        foreach (var frame in conditions)
            node.Conditions.Add(frame.Current);
        return node;
    }

    // -- file ----------------------------------------------------------------------------------------

    private void ParseItems(List<SdslTopLevel> items, string? ns, bool topLevel)
    {
        while (!AtEnd)
        {
            if (Current.Is("}") && !topLevel)
                return;
            if (Current.Kind == SdslLexKind.Directive)
            {
                FileDirective(Advance());
                continue;
            }
            var start = Current;
            if (Current.IsWord("namespace"))
            {
                Advance();
                var name = QualifiedName();
                var declaration = Mark(new SdslNamespaceDeclaration(name), start);
                if (Current.Is(";"))
                {
                    // namespace X; for the rest of the file.
                    Advance();
                    ParseItems(declaration.Items, Combine(ns, name), topLevel: true);
                    items.Add(declaration);
                    return;
                }
                Expect("{");
                ParseItems(declaration.Items, Combine(ns, name), topLevel: false);
                Expect("}");
                if (Current.Is(";")) Advance();
                items.Add(declaration);
                continue;
            }
            if (Current.IsWord("shader") || Current.IsWord("class")
                || (Current.IsWord("internal") && (Peek().IsWord("shader") || Peek().IsWord("class"))))
            {
                items.Add(ParseShader(ns));
                continue;
            }
            if (Current.IsWord("struct"))
            {
                var declaration = ParseStruct(new List<SdslAttribute>());
                items.Add(Mark(new SdslStructTopLevel(declaration), start));
                continue;
            }
            if (Current.IsWord("using"))
            {
                while (!AtEnd && !Current.Is(";")) Advance();
                if (Current.Is(";")) Advance();
                continue;
            }
            if (Current.IsWord("effect") || Current.IsWord("partial") || Current.IsWord("params"))
            {
                var kind = Current.Text;
                var text = new StringBuilder();
                while (!AtEnd && !Current.Is("{") && !Current.Is(";"))
                    text.Append(Advance().Text).Append(' ');
                if (Current.Is("{")) SkipBalanced("{", "}");
                if (Current.Is(";")) Advance();
                // An effect in a .sdsl file: not a shader, nothing to convert.
                items.Add(Mark(new SdslUnsupportedTopLevel(kind, text.ToString().Trim()), start));
                continue;
            }
            Error("Unexpected '" + Current.Text + "' at file level", Current);
            throw new ParseAbort();
        }
    }

    private static string Combine(string? outer, string inner) => outer == null ? inner : outer + "." + inner;

    /// <summary>A preprocessor line outside shaders: #define kept with its condition, #if chains tracked.</summary>
    private void FileDirective(SdslLexToken directive)
    {
        var (keyword, rest) = SplitDirective(directive.Text);
        if (keyword == "define")
        {
            unit.Defines.Add(Define(rest, directive));
            return;
        }
        if (keyword == "pragma" || keyword == "line" || keyword == "undef" || keyword == "include" || keyword == "error")
        {
            Error("#" + keyword + " is not converted", directive);
            return;
        }
        ApplyConditionDirective(keyword, rest, directive);
    }

    private SdslDefine Define(string text, SdslLexToken at)
    {
        var (name, value) = SplitDefine(text);
        if (name.Contains("("))
        {
            Error("Function-like macro " + name + " is not converted", at);
            throw new ParseAbort();
        }
        var condition = conditions.Count == 0 ? null : string.Join(" && ", conditions.Select(c => conditions.Count > 1 ? "(" + c.Current + ")" : c.Current));
        return new SdslDefine(name, value, condition);
    }

    private bool ApplyConditionDirective(string keyword, string rest, SdslLexToken at)
    {
        switch (keyword)
        {
            case "if":
            case "ifdef":
            case "ifndef":
                conditions.Add(new ConditionFrame(keyword, rest));
                return true;
            case "elif":
            case "else":
                if (conditions.Count == 0)
                {
                    Error("#" + keyword + " without #if", at);
                    return true;
                }
                conditions[conditions.Count - 1].Branches.Add((keyword, keyword == "else" ? null : rest));
                return true;
            case "endif":
                if (conditions.Count == 0)
                {
                    Error("#endif without #if", at);
                    return true;
                }
                conditions.RemoveAt(conditions.Count - 1);
                return true;
        }
        Error("Unknown directive #" + keyword, at);
        return false;
    }

    private static (string Keyword, string Text) SplitDirective(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        int start = i;
        while (i < text.Length && char.IsLetter(text[i])) i++;
        return (text.Substring(start, i - start), text.Substring(i).Trim());
    }

    private static (string Name, string? Value) SplitDefine(string text)
    {
        int i = 0;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
        if (i < text.Length && text[i] == '(')
        {
            int close = text.IndexOf(')', i);
            i = close < 0 ? text.Length : close + 1;
        }
        var name = text.Substring(0, i);
        var value = text.Substring(i).Trim();
        return (name, value.Length == 0 ? null : value);
    }

    // -- shader --------------------------------------------------------------------------------------

    private SdslShaderDeclaration ParseShader(string? ns)
    {
        var start = Current;
        var modifiers = new List<string>();
        while (Current.IsWord("internal"))
            modifiers.Add(Advance().Text);
        Advance(); // shader / class
        var name = ExpectIdentifier("a shader name");
        var shader = Mark(new SdslShaderDeclaration(name), start);
        shader.Modifiers.AddRange(modifiers);
        shader.Namespace = ns;
        shader.Defines.AddRange(unit.Defines);

        if (Current.Is("<"))
        {
            Advance();
            while (!AtEnd && !Current.Is(">"))
            {
                var typeToken = Current;
                var type = ParseType();
                var parameterName = ExpectIdentifier("a generic parameter name");
                shader.GenericParameters.Add(Mark(new SdslGenericParameter(type.ToString(), parameterName), typeToken));
                if (Current.Is(",")) Advance();
                else if (!Current.Is(">"))
                {
                    Error("Expected ',' or '>' in the generic parameters", Current);
                    throw new ParseAbort();
                }
            }
            Expect(">");
        }

        if (Current.Is(":"))
        {
            Advance();
            while (true)
            {
                var baseToken = Current;
                var baseName = QualifiedName();
                var reference = Mark(new SdslBaseReference(baseName), baseToken);
                if (Current.Is("<"))
                    reference.GenericArguments.AddRange(RawGenericArguments());
                shader.Bases.Add(reference);
                if (Current.Is(","))
                {
                    Advance();
                    continue;
                }
                break;
            }
        }

        Expect("{");
        ParseMembers(shader.Members, null, shader);
        Expect("}");
        if (Current.Is(";")) Advance();
        return shader;
    }

    /// <summary>&lt;a, b&lt;c&gt;, 3&gt; as the text of each top-level argument.</summary>
    private List<string> RawGenericArguments()
    {
        var result = new List<string>();
        Advance(); // <
        int depth = 1;
        var current = new StringBuilder();
        while (!AtEnd)
        {
            var token = Current;
            if (token.Is("<")) depth++;
            else if (token.Is(">")) depth--;
            else if (token.Is(">>")) depth -= 2;
            if (depth <= 0)
            {
                Advance();
                break;
            }
            Advance();
            if (token.Is(",") && depth == 1)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            if (current.Length > 0 && token.SpaceBefore)
                current.Append(' ');
            current.Append(token.Kind == SdslLexKind.String ? "\"" + token.Text + "\"" : token.Text);
        }
        if (current.Length > 0)
            result.Add(current.ToString().Trim());
        return result;
    }

    private string QualifiedName()
    {
        var name = new StringBuilder(ExpectIdentifier("a name"));
        while (Current.Is(".") && Peek().Kind == SdslLexKind.Identifier)
        {
            Advance();
            name.Append('.').Append(Advance().Text);
        }
        return name.ToString();
    }

    private void ParseMembers(List<SdslMember> members, SdslGroup? group, SdslShaderDeclaration shader)
    {
        var attributes = new List<SdslAttribute>();
        SdslLexToken? attributesStart = null;
        int conditionDepth = conditions.Count;

        while (!AtEnd && !Current.Is("}"))
        {
            if (Current.Kind == SdslLexKind.Directive)
            {
                var directive = Advance();
                var (keyword, rest) = SplitDirective(directive.Text);
                if (keyword == "define")
                {
                    shader.Defines.Add(Define(rest, directive));
                    continue;
                }
                if (keyword == "pragma" || keyword == "undef" || keyword == "include" || keyword == "line" || keyword == "error")
                {
                    Error("#" + keyword + " is not converted", directive);
                    throw new ParseAbort();
                }
                if (keyword == "endif" && conditions.Count <= conditionDepth)
                {
                    Error("#endif closes an #if opened outside this block", directive);
                    throw new ParseAbort();
                }
                ApplyConditionDirective(keyword, rest, directive);
                continue;
            }

            var start = Current;
            if (Current.Is("["))
            {
                attributesStart ??= Current;
                attributes.AddRange(ParseAttributeList());
                continue;
            }
            if (Current.Is(";"))
            {
                Advance();
                continue;
            }

            var modifiers = new List<string>();
            while (Current.Kind == SdslLexKind.Identifier && MemberModifiers.Contains(Current.Text)
                   && !(Peek().Is("(") || Peek().Is(";") || Peek().Is("=")))
                modifiers.Add(Advance().Text);

            if (Current.IsWord("cbuffer") || Current.IsWord("rgroup") || Current.IsWord("tbuffer"))
            {
                var kind = Advance().Text;
                var name = Current.Kind == SdslLexKind.Identifier ? QualifiedName() : string.Empty;
                var inner = new SdslGroup(kind, name);
                inner.Attributes.AddRange(attributes);
                inner.Modifiers.AddRange(modifiers);
                attributes.Clear();
                attributesStart = null;
                Expect("{");
                int before = members.Count;
                ParseMembers(members, inner, shader);
                Expect("}");
                if (Current.Is(";")) Advance();
                // The group's leading comments go on its first member.
                if (members.Count > before && start.Comments != null)
                    members[before].Comments.InsertRange(0, start.Comments);
                continue;
            }

            if (Current.IsWord("struct"))
            {
                var declaration = ParseStruct(attributes);
                Mark(declaration, attributesStart ?? start);
                declaration.Modifiers.AddRange(modifiers);
                declaration.Group = group;
                members.Add(declaration);
                attributes = new List<SdslAttribute>();
                attributesStart = null;
                continue;
            }

            if (Current.IsWord("typedef"))
            {
                Advance();
                var aliased = ParseType();
                var name = ExpectIdentifier("a typedef name");
                Expect(";");
                var typedef = Mark(new SdslTypedef(aliased, name), attributesStart ?? start);
                typedef.Group = group;
                members.Add(typedef);
                attributes = new List<SdslAttribute>();
                attributesStart = null;
                continue;
            }

            var type = ParseType();
            if (Current.Kind != SdslLexKind.Identifier)
            {
                Error("Expected a member name after " + type + " but found '" + Current.Text + "'", Current);
                throw new ParseAbort();
            }

            if (Peek().Is("("))
            {
                var method = Mark(new SdslMethod(type, Advance().Text), attributesStart ?? start);
                method.Attributes.AddRange(attributes);
                method.Modifiers.AddRange(modifiers);
                method.Group = group;
                ParseParameters(method.Parameters);
                if (Current.Is(":"))
                {
                    Advance();
                    method.ReturnSemantic = ExpectIdentifier("a semantic");
                }
                if (Current.Is("{"))
                    method.Body = ParseBlock();
                else
                    Expect(";");
                members.Add(method);
                attributes = new List<SdslAttribute>();
                attributesStart = null;
                continue;
            }

            bool first = true;
            while (true)
            {
                var nameToken = Current;
                var variable = new SdslVariable(type, ExpectIdentifier("a member name"));
                Mark(variable, first ? attributesStart ?? start : nameToken);
                if (!first)
                {
                    variable.Comments.Clear();
                    variable.BlankLineBefore = false;
                }
                variable.Attributes.AddRange(attributes);
                variable.Modifiers.AddRange(modifiers);
                variable.Group = group;
                variable.ArraySizes.AddRange(ParseArraySizes());
                ParseSemantic(variable);
                if (Current.Is("="))
                {
                    Advance();
                    variable.Initializer = ParseInitializer();
                }
                else if (Current.Is("{"))
                {
                    variable.SamplerState = ParseSamplerState();
                }
                members.Add(variable);
                first = false;
                if (Current.Is(","))
                {
                    Advance();
                    continue;
                }
                break;
            }
            if (Current.Is(";"))
                Advance();
            else if (!Current.Is("}"))
            {
                Error("Expected ';' after member " + ((SdslVariable)members[members.Count - 1]).Name, Current);
                throw new ParseAbort();
            }
            attributes = new List<SdslAttribute>();
            attributesStart = null;
        }

        if (conditions.Count != conditionDepth)
        {
            Error("An #if in this block is not closed", Current);
            throw new ParseAbort();
        }
    }

    /// <summary>': SEMANTIC' or ': register(...)' or ': packoffset(...)' after a name.</summary>
    private void ParseSemantic(SdslVariable variable)
    {
        while (Current.Is(":"))
        {
            Advance();
            var name = ExpectIdentifier("a semantic");
            if (Current.Is("("))
            {
                Error(name + " is not converted", Current);
                throw new ParseAbort();
            }
            variable.Semantic = name;
        }
    }

    private List<KeyValuePair<string, string>> ParseSamplerState()
    {
        var result = new List<KeyValuePair<string, string>>();
        Expect("{");
        while (!AtEnd && !Current.Is("}"))
        {
            var key = ExpectIdentifier("a sampler state field");
            Expect("=");
            var value = new StringBuilder();
            while (!AtEnd && !Current.Is(";") && !Current.Is("}"))
            {
                if (value.Length > 0 && Current.SpaceBefore) value.Append(' ');
                value.Append(Current.Kind == SdslLexKind.String ? "\"" + Current.Text + "\"" : Current.Text);
                Advance();
            }
            if (Current.Is(";")) Advance();
            result.Add(new KeyValuePair<string, string>(key, value.ToString()));
        }
        Expect("}");
        return result;
    }

    private SdslStruct ParseStruct(List<SdslAttribute> attributes)
    {
        var start = Advance(); // struct
        var name = ExpectIdentifier("a struct name");
        var declaration = Mark(new SdslStruct(name), start);
        declaration.Attributes.AddRange(attributes);
        Expect("{");
        while (!AtEnd && !Current.Is("}"))
        {
            var fieldStart = Current;
            var fieldAttributes = new List<SdslAttribute>();
            while (Current.Is("["))
                fieldAttributes.AddRange(ParseAttributeList());
            var modifiers = new List<string>();
            while (Current.Kind == SdslLexKind.Identifier && MemberModifiers.Contains(Current.Text))
                modifiers.Add(Advance().Text);
            var type = ParseType();
            while (true)
            {
                var field = Mark(new SdslVariable(type, ExpectIdentifier("a field name")), fieldStart);
                field.Attributes.AddRange(fieldAttributes);
                field.Modifiers.AddRange(modifiers);
                field.ArraySizes.AddRange(ParseArraySizes());
                ParseSemantic(field);
                declaration.Fields.Add(field);
                if (Current.Is(","))
                {
                    Advance();
                    continue;
                }
                break;
            }
            Expect(";");
        }
        Expect("}");
        if (Current.Is(";")) Advance();
        return declaration;
    }

    private List<SdslAttribute> ParseAttributeList()
    {
        var result = new List<SdslAttribute>();
        var start = Advance(); // [
        while (!AtEnd && !Current.Is("]"))
        {
            var nameToken = Current;
            var name = ExpectIdentifier("an attribute name");
            string? arguments = null;
            string? stringArgument = null;
            if (Current.Is("("))
            {
                Advance();
                if (Current.Kind == SdslLexKind.String)
                    stringArgument = Current.Text;
                var text = new StringBuilder();
                int depth = 1;
                while (!AtEnd)
                {
                    if (Current.Is("(")) depth++;
                    else if (Current.Is(")") && --depth == 0) break;
                    if (text.Length > 0 && Current.SpaceBefore) text.Append(' ');
                    text.Append(Current.Kind == SdslLexKind.String ? "\"" + Current.Text + "\"" : Current.Text);
                    Advance();
                }
                Expect(")");
                arguments = text.ToString();
            }
            var attribute = new SdslAttribute(name, arguments) { StringArgument = stringArgument, Position = nameToken.Position };
            result.Add(attribute);
            if (Current.Is(",")) Advance();
        }
        Expect("]");
        return result;
    }

    private void ParseParameters(List<SdslParameter> parameters)
    {
        Expect("(");
        while (!AtEnd && !Current.Is(")"))
        {
            var start = Current;
            var modifiers = new List<string>();
            while (Current.Kind == SdslLexKind.Identifier && ParameterModifiers.Contains(Current.Text)
                   && Peek().Kind == SdslLexKind.Identifier)
                modifiers.Add(Advance().Text);
            if (Current.IsWord("void") && Peek().Is(")"))
            {
                Advance();
                break;
            }
            var type = ParseType();
            var name = ExpectIdentifier("a parameter name");
            var parameter = Mark(new SdslParameter(type, name), start);
            parameter.Modifiers.AddRange(modifiers);
            parameter.ArraySizes.AddRange(ParseArraySizes());
            if (Current.Is(":"))
            {
                Advance();
                parameter.Semantic = ExpectIdentifier("a semantic");
            }
            if (Current.Is("="))
            {
                Advance();
                parameter.Default = ParseAssignment();
            }
            parameters.Add(parameter);
            if (Current.Is(","))
                Advance();
            else if (!Current.Is(")"))
            {
                Error("Expected ',' or ')' in the parameters", Current);
                throw new ParseAbort();
            }
        }
        Expect(")");
    }

    private List<string> ParseArraySizes()
    {
        var sizes = new List<string>();
        while (Current.Is("["))
        {
            Advance();
            var text = new StringBuilder();
            int depth = 1;
            while (!AtEnd)
            {
                if (Current.Is("[")) depth++;
                else if (Current.Is("]") && --depth == 0) break;
                if (text.Length > 0 && Current.SpaceBefore) text.Append(' ');
                text.Append(Current.Text);
                Advance();
            }
            Expect("]");
            sizes.Add(text.ToString());
        }
        return sizes;
    }

    // -- types ---------------------------------------------------------------------------------------

    private SdslType ParseType()
    {
        var name = QualifiedName();
        var type = new SdslType(name);
        if (Current.Is("<") && (GenericTypes.Contains(name) || LooksLikeGenericArguments()))
        {
            Advance();
            ParseTypeArguments(type);
        }
        return type;
    }

    /// <summary>Whether '&lt;' at the current position opens type arguments: a type or number, then ',' or '&gt;'.</summary>
    private bool LooksLikeGenericArguments()
    {
        int i = position + 1;
        int depth = 1;
        while (i < tokens.Count)
        {
            var token = tokens[i];
            if (token.Is("<")) depth++;
            else if (token.Is(">")) { if (--depth == 0) return true; }
            else if (token.Is(">>")) { depth -= 2; if (depth <= 0) return true; }
            else if (!(token.Kind == SdslLexKind.Identifier || token.Kind == SdslLexKind.Number || token.Is(",") || token.Is(".")))
                return false;
            i++;
        }
        return false;
    }

    /// <summary>After '&lt;': arguments up to and including the closing '&gt;' (a '&gt;&gt;' closes two).</summary>
    private bool pendingClose;

    private void ParseTypeArguments(SdslType type)
    {
        while (!AtEnd)
        {
            if (Current.Kind == SdslLexKind.Number)
            {
                type.Arguments.Add(new SdslType(Advance().Text));
            }
            else
            {
                var argumentName = QualifiedName();
                var argument = new SdslType(argumentName);
                if (Current.Is("<"))
                {
                    Advance();
                    ParseTypeArguments(argument);
                    if (pendingClose)
                    {
                        pendingClose = false;
                        type.Arguments.Add(argument);
                        return;
                    }
                }
                type.Arguments.Add(argument);
            }
            if (Current.Is(","))
            {
                Advance();
                continue;
            }
            if (Current.Is(">"))
            {
                Advance();
                return;
            }
            if (Current.Is(">>"))
            {
                Advance();
                pendingClose = true;
                return;
            }
            Error("Expected ',' or '>' in the type arguments of " + type.Name, Current);
            throw new ParseAbort();
        }
    }

    private bool IsTypeName(string name) => IsBuiltinType(name) || knownTypes.Contains(name) || GenericTypes.Contains(name)
        || name == "void" || name.StartsWith("Texture", StringComparison.Ordinal) || name.StartsWith("RWTexture", StringComparison.Ordinal);

    // -- statements ----------------------------------------------------------------------------------

    private SdslBlock ParseBlock()
    {
        var start = Expect("{");
        var block = new SdslBlock { Position = start.Position };
        int conditionDepth = conditions.Count;
        ParseStatementsUntil(block.Statements, () => Current.Is("}"));
        if (Current.Comments != null)
            block.TrailingComments.AddRange(Current.Comments);
        Expect("}");
        if (conditions.Count != conditionDepth)
        {
            Error("An #if in this block is not closed", Current);
            throw new ParseAbort();
        }
        return block;
    }

    /// <summary>Statements up to the stop condition, #if chains gathered into conditional statements.</summary>
    private void ParseStatementsUntil(List<SdslStatement> statements, Func<bool> stop)
    {
        while (!AtEnd && !stop())
        {
            if (Current.Kind == SdslLexKind.Directive)
            {
                var (keyword, _) = SplitDirective(Current.Text);
                if (keyword == "if" || keyword == "ifdef" || keyword == "ifndef")
                {
                    statements.Add(ParseConditionalStatement());
                    continue;
                }
                if (keyword == "elif" || keyword == "else" || keyword == "endif")
                    return; // the caller's chain goes on
                Error("#" + keyword + " in a method body is not converted", Current);
                throw new ParseAbort();
            }
            statements.Add(ParseStatement());
        }
    }

    private SdslConditionalStatement ParseConditionalStatement()
    {
        var start = Current;
        var statement = new SdslConditionalStatement { Position = start.Position };
        if (start.Comments != null)
            statement.Comments.AddRange(start.Comments);
        statement.BlankLineBefore = start.BlankLineBefore;
        while (true)
        {
            var directive = Advance();
            var (keyword, rest) = SplitDirective(directive.Text);
            if (keyword == "endif")
                return statement;
            var branch = new SdslConditionalBranch(keyword, keyword == "else" ? null : rest);
            statement.Branches.Add(branch);
            ParseStatementsUntil(branch.Statements, () => Current.Is("}"));
            if (Current.Kind != SdslLexKind.Directive)
            {
                Error("An #if in a method body closes at the end of its block, not inside it", Current);
                throw new ParseAbort();
            }
        }
    }

    private SdslStatement ParseStatement()
    {
        var start = Current;
        var attributes = new List<SdslAttribute>();
        while (Current.Is("[") )
            attributes.AddRange(ParseAttributeList());

        SdslStatement statement = ParseStatementCore();
        statement.Attributes.InsertRange(0, attributes);
        statement.Position = start.Position;
        if (start.Comments != null)
            statement.Comments.InsertRange(0, start.Comments);
        statement.BlankLineBefore = start.BlankLineBefore;
        return statement;
    }

    private SdslStatement ParseStatementCore()
    {
        var token = Current;
        if (token.Is("{"))
            return ParseBlock();
        if (token.Is(";"))
        {
            Advance();
            return new SdslKeywordStatement(";");
        }
        if (token.Kind == SdslLexKind.Identifier)
        {
            switch (token.Text)
            {
                case "if":
                {
                    Advance();
                    Expect("(");
                    var condition = ParseExpression();
                    Expect(")");
                    var then = ParseStatement();
                    var result = new SdslIfStatement(condition, then);
                    if (Current.IsWord("else"))
                    {
                        Advance();
                        result.Else = ParseStatement();
                    }
                    return result;
                }
                case "for":
                {
                    Advance();
                    Expect("(");
                    var result = new SdslForStatement();
                    if (!Current.Is(";"))
                    {
                        if (IsDeclarationStart())
                            result.Initializer = ParseDeclaration();
                        else
                        {
                            var expression = ParseExpression();
                            result.Initializer = new SdslExpressionStatement(expression);
                        }
                    }
                    Expect(";");
                    if (!Current.Is(";"))
                        result.Condition = ParseExpression();
                    Expect(";");
                    while (!Current.Is(")"))
                    {
                        result.Incrementors.Add(ParseAssignment());
                        if (Current.Is(",")) Advance();
                        else break;
                    }
                    Expect(")");
                    result.Body = ParseStatement();
                    return result;
                }
                case "foreach":
                {
                    Advance();
                    Expect("(");
                    SdslType? type = null;
                    if (Current.IsWord("var"))
                        Advance();
                    else
                        type = ParseType();
                    var name = ExpectIdentifier("the foreach variable");
                    if (!Current.IsWord("in"))
                    {
                        Error("Expected 'in' in foreach", Current);
                        throw new ParseAbort();
                    }
                    Advance();
                    var collection = ParseExpression();
                    Expect(")");
                    var body = ParseStatement();
                    return new SdslForeachStatement(type, name, collection, body);
                }
                case "while":
                {
                    Advance();
                    Expect("(");
                    var condition = ParseExpression();
                    Expect(")");
                    return new SdslWhileStatement(condition, ParseStatement());
                }
                case "do":
                {
                    Advance();
                    var body = ParseStatement();
                    if (!Current.IsWord("while"))
                    {
                        Error("Expected 'while' after the body of do", Current);
                        throw new ParseAbort();
                    }
                    Advance();
                    Expect("(");
                    var condition = ParseExpression();
                    Expect(")");
                    Expect(";");
                    return new SdslDoStatement(body, condition);
                }
                case "switch":
                {
                    Advance();
                    Expect("(");
                    var result = new SdslSwitchStatement(ParseExpression());
                    Expect(")");
                    Expect("{");
                    SdslSwitchSection? section = null;
                    while (!AtEnd && !Current.Is("}"))
                    {
                        if (Current.IsWord("case") || Current.IsWord("default"))
                        {
                            if (section == null || section.Statements.Count > 0)
                            {
                                section = new SdslSwitchSection();
                                result.Sections.Add(section);
                            }
                            if (Advance().Text == "case")
                                section.Labels.Add(ParseConditionalExpression());
                            else
                                section.Labels.Add(null);
                            Expect(":");
                            continue;
                        }
                        if (section == null)
                        {
                            Error("A statement before the first case", Current);
                            throw new ParseAbort();
                        }
                        if (Current.Kind == SdslLexKind.Directive)
                        {
                            Error("#if in a switch is not converted", Current);
                            throw new ParseAbort();
                        }
                        section.Statements.Add(ParseStatement());
                    }
                    Expect("}");
                    return result;
                }
                case "return":
                {
                    Advance();
                    SdslExpression? value = null;
                    if (!Current.Is(";"))
                        value = ParseExpression();
                    Expect(";");
                    return new SdslReturnStatement(value);
                }
                case "break":
                case "continue":
                case "discard":
                    Advance();
                    Expect(";");
                    return new SdslKeywordStatement(token.Text);
            }
        }

        // A macro alone on its line: what follows starts another statement, not a declaration's name.
        if (token.Kind == SdslLexKind.Identifier && Peek().Line > token.Line
            && (Peek().Is("}") || StatementKeywords.Contains(Peek().Text)
                || (Peek().Kind == SdslLexKind.Identifier && (Peek(2).Kind == SdslLexKind.Identifier
                    || (Peek(2).Kind == SdslLexKind.Punctuation && !(Peek(2).Is("=") || Peek(2).Is(",") || Peek(2).Is(";") || Peek(2).Is(":") || Peek(2).Is("[")))))))
        {
            Advance();
            return new SdslMacroStatement(token.Text);
        }

        if (IsDeclarationStart())
        {
            var declaration = ParseDeclaration();
            Expect(";");
            return declaration;
        }

        var expressionStatement = new SdslExpressionStatement(ParseExpression());
        Expect(";");
        return expressionStatement;
    }

    /// <summary>A local declaration starts with a modifier, or a type followed by a name.</summary>
    private bool IsDeclarationStart()
    {
        if (Current.Kind != SdslLexKind.Identifier)
            return false;
        if (LocalModifiers.Contains(Current.Text))
            return true;
        if (StatementKeywords.Contains(Current.Text))
            return false;
        int i = position;
        // Qualified type names.
        i++;
        while (tokens[i].Is(".") && tokens[i + 1].Kind == SdslLexKind.Identifier)
            i += 2;
        if (tokens[i].Is("<"))
        {
            if (!GenericTypes.Contains(Current.Text))
                return false;
            int depth = 0;
            for (; i < tokens.Count; i++)
            {
                if (tokens[i].Is("<")) depth++;
                else if (tokens[i].Is(">")) { if (--depth == 0) { i++; break; } }
                else if (tokens[i].Is(">>")) { depth -= 2; if (depth <= 0) { i++; break; } }
                else if (tokens[i].Is(";") || tokens[i].Is("{")) return false;
            }
        }
        return tokens[i].Kind == SdslLexKind.Identifier && !tokens[i].IsWord("in");
    }

    private SdslDeclarationStatement ParseDeclaration()
    {
        var modifiers = new List<string>();
        while (Current.Kind == SdslLexKind.Identifier && LocalModifiers.Contains(Current.Text))
            modifiers.Add(Advance().Text);
        var type = Current.IsWord("var") ? new SdslType(Advance().Text) : ParseType();
        var declaration = new SdslDeclarationStatement(type);
        declaration.Modifiers.AddRange(modifiers);
        while (true)
        {
            var declarator = new SdslDeclarator(ExpectIdentifier("a variable name"));
            declarator.ArraySizes.AddRange(ParseArraySizes());
            if (Current.Is(":"))
            {
                Error("A semantic on a local variable is not converted", Current);
                throw new ParseAbort();
            }
            if (Current.Is("="))
            {
                Advance();
                declarator.Initializer = ParseInitializer();
            }
            declaration.Declarators.Add(declarator);
            if (Current.Is(","))
            {
                Advance();
                continue;
            }
            return declaration;
        }
    }

    private SdslExpression ParseInitializer()
    {
        if (!Current.Is("{"))
            return ParseAssignment();
        var start = Advance();
        var list = new SdslInitializerList { Position = start.Position };
        while (!AtEnd && !Current.Is("}"))
        {
            list.Items.Add(ParseInitializer());
            if (Current.Is(",")) Advance();
            else break;
        }
        Expect("}");
        return list;
    }

    // -- expressions ---------------------------------------------------------------------------------

    /// <summary>A full expression, the comma operator included.</summary>
    private SdslExpression ParseExpression()
    {
        var first = ParseAssignment();
        if (!Current.Is(","))
            return first;
        var sequence = new SdslSequence { Position = first.Position };
        sequence.Items.Add(first);
        while (Current.Is(","))
        {
            Advance();
            sequence.Items.Add(ParseAssignment());
        }
        return sequence;
    }

    private static readonly HashSet<string> AssignmentOperators = new HashSet<string>(StringComparer.Ordinal)
    {
        "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=",
    };

    private SdslExpression ParseAssignment()
    {
        var left = ParseConditionalExpression();
        if (Current.Kind == SdslLexKind.Punctuation && AssignmentOperators.Contains(Current.Text))
        {
            var op = Advance().Text;
            var right = ParseAssignment();
            return new SdslAssignment(op, left, right) { Position = left.Position };
        }
        return left;
    }

    private SdslExpression ParseConditionalExpression()
    {
        var condition = ParseBinary(0);
        if (!Current.Is("?"))
            return condition;
        Advance();
        var whenTrue = ParseAssignment();
        Expect(":");
        var whenFalse = ParseAssignment();
        return new SdslConditional(condition, whenTrue, whenFalse) { Position = condition.Position };
    }

    private static readonly string[][] BinaryLevels =
    {
        new[] { "||" },
        new[] { "&&" },
        new[] { "|" },
        new[] { "^" },
        new[] { "&" },
        new[] { "==", "!=" },
        new[] { "<", ">", "<=", ">=" },
        new[] { "<<", ">>" },
        new[] { "+", "-" },
        new[] { "*", "/", "%" },
    };

    private SdslExpression ParseBinary(int level)
    {
        if (level == BinaryLevels.Length)
            return ParseUnary();
        var left = ParseBinary(level + 1);
        while (Current.Kind == SdslLexKind.Punctuation && Array.IndexOf(BinaryLevels[level], Current.Text) >= 0)
        {
            var op = Advance().Text;
            var right = ParseBinary(level + 1);
            left = new SdslBinary(op, left, right) { Position = left.Position };
        }
        return left;
    }

    private SdslExpression ParseUnary()
    {
        var token = Current;
        if (token.Kind == SdslLexKind.Punctuation)
        {
            switch (token.Text)
            {
                case "-":
                case "+":
                case "!":
                case "~":
                case "++":
                case "--":
                    Advance();
                    return new SdslUnary(token.Text, ParseUnary(), postfix: false) { Position = token.Position };
                case "(":
                    if (TryParseCast(out var cast))
                        return cast;
                    break;
            }
        }
        return ParsePostfix(ParsePrimary());
    }

    /// <summary>(type)operand, when the parentheses hold a type name and something follows that can start an operand.</summary>
    private bool TryParseCast(out SdslExpression cast)
    {
        cast = null!;
        int save = position;
        var start = Advance(); // (
        if (Current.Kind != SdslLexKind.Identifier || !IsTypeName(Current.Text))
        {
            position = save;
            return false;
        }
        var type = ParseType();
        var sizes = ParseArraySizes();
        if (!Current.Is(")"))
        {
            position = save;
            return false;
        }
        Advance();
        var next = Current;
        bool operandFollows = next.Kind == SdslLexKind.Identifier || next.Kind == SdslLexKind.Number
            || next.Is("(") || next.Is("!") || next.Is("~") || next.Is("-") || next.Is("+") || next.Is("++") || next.Is("--");
        if (!operandFollows)
        {
            position = save;
            return false;
        }
        var operand = ParseUnary();
        cast = new SdslCast(type, sizes, operand) { Position = start.Position };
        return true;
    }

    private SdslExpression ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case SdslLexKind.Number:
            {
                Advance();
                var text = token.Text;
                bool hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                bool real = !hex && (text.Contains(".") || text.IndexOfAny(new[] { 'e', 'E' }) > 0 || text.EndsWith("f", StringComparison.OrdinalIgnoreCase) || text.EndsWith("h", StringComparison.OrdinalIgnoreCase));
                return new SdslLiteral(real ? SdslLiteralKind.Real : SdslLiteralKind.Integer, text) { Position = token.Position };
            }
            case SdslLexKind.String:
                Advance();
                return new SdslLiteral(SdslLiteralKind.String, token.Text) { Position = token.Position };
            case SdslLexKind.Identifier:
                Advance();
                if (token.Text == "true" || token.Text == "false")
                    return new SdslLiteral(SdslLiteralKind.Boolean, token.Text) { Position = token.Position };
                if (GenericTypes.Contains(token.Text) && Current.Is("<"))
                {
                    position--;
                    var type = ParseType();
                    return new SdslTypeExpression(type) { Position = token.Position };
                }
                return new SdslIdentifier(token.Text) { Position = token.Position };
            case SdslLexKind.Punctuation:
                if (token.Is("("))
                {
                    Advance();
                    var inner = ParseExpression();
                    Expect(")");
                    inner.Parenthesized = true;
                    return inner;
                }
                if (token.Is("{"))
                    return ParseInitializer();
                break;
        }
        Error("Unexpected '" + token.Text + "' in an expression", token);
        throw new ParseAbort();
    }

    private SdslExpression ParsePostfix(SdslExpression expression)
    {
        while (true)
        {
            var token = Current;
            if (token.Is("."))
            {
                Advance();
                var name = ExpectIdentifier("a member name");
                expression = new SdslMemberAccess(expression, name) { Position = expression.Position };
                continue;
            }
            if (token.Is("["))
            {
                Advance();
                var index = ParseExpression();
                Expect("]");
                expression = new SdslIndexer(expression, index) { Position = expression.Position };
                continue;
            }
            if (token.Is("("))
            {
                Advance();
                var call = new SdslCall(expression) { Position = expression.Position };
                while (!AtEnd && !Current.Is(")"))
                {
                    call.Arguments.Add(ParseAssignment());
                    if (Current.Is(",")) Advance();
                    else break;
                }
                Expect(")");
                expression = call;
                continue;
            }
            if (token.Is("++") || token.Is("--"))
            {
                Advance();
                expression = new SdslUnary(token.Text, expression, postfix: true) { Position = expression.Position };
                continue;
            }
            if (token.Is("<") && expression is SdslIdentifier generic && LooksLikeGenericCall())
            {
                // Shader<8>.Method(): a generic shader named in an expression.
                var arguments = RawGenericArguments();
                expression = new SdslIdentifier(generic.Name + "<" + string.Join(", ", arguments) + ">") { Position = generic.Position };
                continue;
            }
            return expression;
        }
    }

    /// <summary>Name&lt;args&gt;. — a generic shader used as a static qualifier.</summary>
    private bool LooksLikeGenericCall()
    {
        int i = position + 1;
        int depth = 1;
        while (i < tokens.Count)
        {
            var token = tokens[i];
            if (token.Is("<")) depth++;
            else if (token.Is(">")) { if (--depth == 0) return tokens[i + 1].Is("."); }
            else if (!(token.Kind == SdslLexKind.Identifier || token.Kind == SdslLexKind.Number || token.Is(",") || token.Is(".")))
                return false;
            i++;
        }
        return false;
    }

    private void SkipBalanced(string open, string close)
    {
        int depth = 0;
        while (!AtEnd)
        {
            var token = Advance();
            if (token.Is(open)) depth++;
            else if (token.Is(close) && --depth == 0) return;
        }
    }
}
