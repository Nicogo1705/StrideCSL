using System.Collections.Generic;

namespace Csl.Generators.Sdsl.Syntax;

// The whole of an SDSL file as a tree: declarations, method bodies down to the expressions, the
// preprocessor conditions around members and statements, and the comments. This is what the SDSL
// to C# conversion reads; the wrappers only need the lighter SdslParser.

/// <summary>Where a node starts in its file, 1-based.</summary>
public readonly struct SdslPosition
{
    public SdslPosition(int line, int column)
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }
    public int Column { get; }

    public override string ToString() => Line + ":" + Column;
}

public abstract class SdslNode
{
    public SdslPosition Position { get; set; }

    /// <summary>The comments on the lines before the node, as written (// or /* */ included). Doc comments (///) too.</summary>
    public List<string> Comments { get; } = new List<string>();

    /// <summary>An empty line separates this node from the one before.</summary>
    public bool BlankLineBefore { get; set; }

    /// <summary>
    /// The preprocessor conditions the node sits under, outermost first, each the text after #if
    /// (an #ifdef X is "defined(X)", an #else is the negation of every branch before it).
    /// </summary>
    public List<string> Conditions { get; } = new List<string>();
}

public sealed class SdslCompilationUnit
{
    public SdslCompilationUnit(string path)
    {
        Path = path;
    }

    public string Path { get; }
    public List<SdslTopLevel> Items { get; } = new List<SdslTopLevel>();
    public List<SdslDiagnostic> Diagnostics { get; } = new List<SdslDiagnostic>();

    /// <summary>The #define lines at file level, with their conditions.</summary>
    public List<SdslDefine> Defines { get; } = new List<SdslDefine>();

    public IEnumerable<SdslShaderDeclaration> Shaders()
    {
        foreach (var item in Items)
        {
            if (item is SdslShaderDeclaration shader)
                yield return shader;
            else if (item is SdslNamespaceDeclaration ns)
                foreach (var inner in ns.Shaders())
                    yield return inner;
        }
    }
}

public sealed class SdslDiagnostic
{
    public SdslDiagnostic(string message, SdslPosition position)
    {
        Message = message;
        Position = position;
    }

    public string Message { get; }
    public SdslPosition Position { get; }

    public override string ToString() => Position + ": " + Message;
}

/// <summary>A macro given a value: <c>#define Name Value</c>, possibly under a condition.</summary>
public sealed class SdslDefine
{
    public SdslDefine(string name, string? value, string? condition)
    {
        Name = name;
        Value = value;
        Condition = condition;
    }

    public string Name { get; }
    public string? Value { get; }

    /// <summary>
    /// The #if condition the define sits under, or null. <c>!defined(Name)</c> is the engine's way of
    /// giving a macro a default: <c>#ifndef X / #define X V / #endif</c>.
    /// </summary>
    public string? Condition { get; }
}

public abstract class SdslTopLevel : SdslNode
{
}

public sealed class SdslNamespaceDeclaration : SdslTopLevel
{
    public SdslNamespaceDeclaration(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public List<SdslTopLevel> Items { get; } = new List<SdslTopLevel>();

    public IEnumerable<SdslShaderDeclaration> Shaders()
    {
        foreach (var item in Items)
        {
            if (item is SdslShaderDeclaration shader)
                yield return shader;
            else if (item is SdslNamespaceDeclaration ns)
                foreach (var inner in ns.Shaders())
                    yield return inner;
        }
    }
}

/// <summary>Something at file level the conversion does not handle (an effect, say), kept as text.</summary>
public sealed class SdslUnsupportedTopLevel : SdslTopLevel
{
    public SdslUnsupportedTopLevel(string kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public string Kind { get; }
    public string Text { get; }
}

public sealed class SdslShaderDeclaration : SdslTopLevel
{
    public SdslShaderDeclaration(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>Keywords before <c>shader</c>: internal.</summary>
    public List<string> Modifiers { get; } = new List<string>();

    /// <summary>The namespace the shader is declared in, dotted, or null.</summary>
    public string? Namespace { get; set; }

    public List<SdslGenericParameter> GenericParameters { get; } = new List<SdslGenericParameter>();
    public List<SdslBaseReference> Bases { get; } = new List<SdslBaseReference>();
    public List<SdslMember> Members { get; } = new List<SdslMember>();

    /// <summary>The #define lines of the file before the shader and in the shader, in order.</summary>
    public List<SdslDefine> Defines { get; } = new List<SdslDefine>();

    /// <summary>The #error lines, with their conditions: Name is the message.</summary>
    public List<SdslDefine> Errors { get; } = new List<SdslDefine>();
}

/// <summary><c>LinkType TTexture</c>, <c>int TCount</c>: a generic parameter of a shader.</summary>
public sealed class SdslGenericParameter : SdslNode
{
    public SdslGenericParameter(string type, string name)
    {
        Type = type;
        Name = name;
    }

    public string Type { get; }
    public string Name { get; }
}

/// <summary>A base shader: <c>DynamicTexture&lt;TTexture, PerMaterial&gt;</c>.</summary>
public sealed class SdslBaseReference : SdslNode
{
    public SdslBaseReference(string name)
    {
        Name = name;
    }

    public string Name { get; }

    /// <summary>The generic arguments as written, each trimmed; empty when the base is not generic.</summary>
    public List<string> GenericArguments { get; } = new List<string>();

    public string Text => GenericArguments.Count == 0 ? Name : Name + "<" + string.Join(", ", GenericArguments) + ">";
}

/// <summary>An attribute: <c>[Link("X")]</c>, <c>[numthreads(8, 8, 1)]</c>, <c>[unroll]</c>.</summary>
public sealed class SdslAttribute : SdslNode
{
    public SdslAttribute(string name, string? arguments)
    {
        Name = name;
        Arguments = arguments;
    }

    public string Name { get; }

    /// <summary>The text between the parentheses as written, or null when there are none.</summary>
    public string? Arguments { get; }

    /// <summary>The first argument when it is a string literal, unquoted.</summary>
    public string? StringArgument { get; set; }

    public string Text => Arguments == null ? Name : Name + "(" + Arguments + ")";
}

/// <summary>A type as written: <c>float3</c>, <c>Texture2D&lt;float4&gt;</c>, <c>OutputPatch&lt;Input, 3&gt;</c>.</summary>
public sealed class SdslType
{
    public SdslType(string name)
    {
        Name = name;
    }

    /// <summary>The name, dotted when qualified.</summary>
    public string Name { get; }

    /// <summary>Generic arguments: types, or numbers and names for <c>OutputPatch&lt;Input, 3&gt;</c>.</summary>
    public List<SdslType> Arguments { get; } = new List<SdslType>();

    public override string ToString() => Arguments.Count == 0 ? Name : Name + "<" + string.Join(", ", Arguments) + ">";
}

public abstract class SdslMember : SdslNode
{
    public List<SdslAttribute> Attributes { get; } = new List<SdslAttribute>();

    /// <summary>The storage and stage keywords, in the order written: stage, stream, static, const…</summary>
    public List<string> Modifiers { get; } = new List<string>();

    /// <summary>The constant buffer or resource group the member is declared in, or null.</summary>
    public SdslGroup? Group { get; set; }

    public bool Has(string modifier) => Modifiers.Contains(modifier);
}

/// <summary>A <c>cbuffer</c>, <c>rgroup</c> or <c>tbuffer</c>; the members point to it.</summary>
public sealed class SdslGroup
{
    public SdslGroup(string kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    /// <summary>cbuffer, rgroup, tbuffer.</summary>
    public string Kind { get; }

    public string Name { get; }
    public List<SdslAttribute> Attributes { get; } = new List<SdslAttribute>();
    public List<string> Modifiers { get; } = new List<string>();
}

/// <summary>One variable of a declaration; <c>float a, b;</c> gives two.</summary>
public sealed class SdslVariable : SdslMember
{
    public SdslVariable(SdslType type, string name)
    {
        Type = type;
        Name = name;
    }

    public SdslType Type { get; }
    public string Name { get; }

    /// <summary>One entry per [], the size as written or an empty string for <c>[]</c>.</summary>
    public List<string> ArraySizes { get; } = new List<string>();

    public string? Semantic { get; set; }
    public SdslExpression? Initializer { get; set; }

    /// <summary>A sampler state description block: <c>{ Filter = MIN_MAG_MIP_POINT; AddressU = Wrap; }</c>.</summary>
    public List<KeyValuePair<string, string>>? SamplerState { get; set; }
}

public sealed class SdslMethod : SdslMember
{
    public SdslMethod(SdslType returnType, string name)
    {
        ReturnType = returnType;
        Name = name;
    }

    public SdslType ReturnType { get; }
    public string Name { get; }
    public List<SdslParameter> Parameters { get; } = new List<SdslParameter>();
    public string? ReturnSemantic { get; set; }

    /// <summary>The body, or null for a declaration ending in ';'.</summary>
    public SdslBlock? Body { get; set; }
}

public sealed class SdslParameter : SdslNode
{
    public SdslParameter(SdslType type, string name)
    {
        Type = type;
        Name = name;
    }

    /// <summary>in, out, inout, const, uniform, and the primitive kinds of a geometry shader input.</summary>
    public List<string> Modifiers { get; } = new List<string>();

    public SdslType Type { get; }
    public string Name { get; }
    public List<string> ArraySizes { get; } = new List<string>();
    public string? Semantic { get; set; }
    public SdslExpression? Default { get; set; }
}

/// <summary>A struct, at file level or in a shader.</summary>
public sealed class SdslStruct : SdslMember
{
    public SdslStruct(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public List<SdslVariable> Fields { get; } = new List<SdslVariable>();
}

/// <summary><c>typedef float3 Color;</c></summary>
public sealed class SdslTypedef : SdslMember
{
    public SdslTypedef(SdslType type, string name)
    {
        Type = type;
        Name = name;
    }

    public SdslType Type { get; }
    public string Name { get; }
}

/// <summary>File-level struct.</summary>
public sealed class SdslStructTopLevel : SdslTopLevel
{
    public SdslStructTopLevel(SdslStruct declaration)
    {
        Declaration = declaration;
    }

    public SdslStruct Declaration { get; }
}

// -- statements ----------------------------------------------------------------------------------

public abstract class SdslStatement : SdslNode
{
    /// <summary>Statement attributes: [unroll], [loop], [branch], [flatten]…</summary>
    public List<SdslAttribute> Attributes { get; } = new List<SdslAttribute>();
}

public sealed class SdslBlock : SdslStatement
{
    public List<SdslStatement> Statements { get; } = new List<SdslStatement>();

    /// <summary>Comments after the last statement, before the closing brace.</summary>
    public List<string> TrailingComments { get; } = new List<string>();
}

public sealed class SdslDeclarationStatement : SdslStatement
{
    public SdslDeclarationStatement(SdslType type)
    {
        Type = type;
    }

    /// <summary>const, static…</summary>
    public List<string> Modifiers { get; } = new List<string>();

    public SdslType Type { get; }
    public List<SdslDeclarator> Declarators { get; } = new List<SdslDeclarator>();
}

public sealed class SdslDeclarator
{
    public SdslDeclarator(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public List<string> ArraySizes { get; } = new List<string>();
    public SdslExpression? Initializer { get; set; }
}

public sealed class SdslExpressionStatement : SdslStatement
{
    public SdslExpressionStatement(SdslExpression expression)
    {
        Expression = expression;
    }

    public SdslExpression Expression { get; }
}

public sealed class SdslIfStatement : SdslStatement
{
    public SdslIfStatement(SdslExpression condition, SdslStatement then)
    {
        Condition = condition;
        Then = then;
    }

    public SdslExpression Condition { get; }
    public SdslStatement Then { get; }
    public SdslStatement? Else { get; set; }
}

public sealed class SdslForStatement : SdslStatement
{
    /// <summary>A declaration or expressions, or null.</summary>
    public SdslStatement? Initializer { get; set; }

    public SdslExpression? Condition { get; set; }
    public List<SdslExpression> Incrementors { get; } = new List<SdslExpression>();
    public SdslStatement Body { get; set; } = null!;
}

public sealed class SdslForeachStatement : SdslStatement
{
    public SdslForeachStatement(SdslType? type, string name, SdslExpression collection, SdslStatement body)
    {
        Type = type;
        Name = name;
        Collection = collection;
        Body = body;
    }

    /// <summary>The element type, or null for <c>var</c>.</summary>
    public SdslType? Type { get; }

    public string Name { get; }
    public SdslExpression Collection { get; }
    public SdslStatement Body { get; }
}

public sealed class SdslWhileStatement : SdslStatement
{
    public SdslWhileStatement(SdslExpression condition, SdslStatement body)
    {
        Condition = condition;
        Body = body;
    }

    public SdslExpression Condition { get; }
    public SdslStatement Body { get; }
}

public sealed class SdslDoStatement : SdslStatement
{
    public SdslDoStatement(SdslStatement body, SdslExpression condition)
    {
        Body = body;
        Condition = condition;
    }

    public SdslStatement Body { get; }
    public SdslExpression Condition { get; }
}

public sealed class SdslSwitchStatement : SdslStatement
{
    public SdslSwitchStatement(SdslExpression expression)
    {
        Expression = expression;
    }

    public SdslExpression Expression { get; }
    public List<SdslSwitchSection> Sections { get; } = new List<SdslSwitchSection>();
}

public sealed class SdslSwitchSection
{
    /// <summary>The case values; null for <c>default</c>.</summary>
    public List<SdslExpression?> Labels { get; } = new List<SdslExpression?>();

    public List<SdslStatement> Statements { get; } = new List<SdslStatement>();
}

public sealed class SdslReturnStatement : SdslStatement
{
    public SdslReturnStatement(SdslExpression? value)
    {
        Value = value;
    }

    public SdslExpression? Value { get; }
}

/// <summary>break, continue, discard, or an empty statement.</summary>
public sealed class SdslKeywordStatement : SdslStatement
{
    public SdslKeywordStatement(string keyword)
    {
        Keyword = keyword;
    }

    /// <summary>break, continue, discard, or ";" for an empty statement.</summary>
    public string Keyword { get; }
}

/// <summary>A macro used as a statement, on a line of its own without ';': <c>IndirectStoreMacro</c>.</summary>
public sealed class SdslMacroStatement : SdslStatement
{
    public SdslMacroStatement(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

/// <summary>
/// Statements under an #if chain, kept as a unit so the chain survives: each branch has its
/// condition (null for #else) and its statements.
/// </summary>
public sealed class SdslConditionalStatement : SdslStatement
{
    public List<SdslConditionalBranch> Branches { get; } = new List<SdslConditionalBranch>();
}

public sealed class SdslConditionalBranch
{
    public SdslConditionalBranch(string directive, string? condition)
    {
        Directive = directive;
        Condition = condition;
    }

    /// <summary>if, ifdef, ifndef, elif, else.</summary>
    public string Directive { get; }

    /// <summary>The text after the directive; null for else.</summary>
    public string? Condition { get; }

    public List<SdslStatement> Statements { get; } = new List<SdslStatement>();
}

// -- expressions ---------------------------------------------------------------------------------

public abstract class SdslExpression
{
    public SdslPosition Position { get; set; }

    /// <summary>The expression was written between parentheses.</summary>
    public bool Parenthesized { get; set; }
}

public enum SdslLiteralKind
{
    Integer,
    Real,
    Boolean,
    String,
}

public sealed class SdslLiteral : SdslExpression
{
    public SdslLiteral(SdslLiteralKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public SdslLiteralKind Kind { get; }

    /// <summary>As written, suffix included.</summary>
    public string Text { get; }
}

public sealed class SdslIdentifier : SdslExpression
{
    public SdslIdentifier(string name)
    {
        Name = name;
    }

    public string Name { get; }
}

public sealed class SdslMemberAccess : SdslExpression
{
    public SdslMemberAccess(SdslExpression target, string name)
    {
        Target = target;
        Name = name;
    }

    public SdslExpression Target { get; }
    public string Name { get; }
}

public sealed class SdslIndexer : SdslExpression
{
    public SdslIndexer(SdslExpression target, SdslExpression index)
    {
        Target = target;
        Index = index;
    }

    public SdslExpression Target { get; }
    public SdslExpression Index { get; }
}

/// <summary>A call: <c>f(a)</c>, <c>x.Sample(s, uv)</c>, and constructors such as <c>float3(a, b, c)</c>.</summary>
public sealed class SdslCall : SdslExpression
{
    public SdslCall(SdslExpression target)
    {
        Target = target;
    }

    public SdslExpression Target { get; }

    public List<SdslExpression> Arguments { get; } = new List<SdslExpression>();
}

/// <summary>A type used as the target of a constructor call with generic arguments: <c>vector&lt;float, 3&gt;(…)</c>.</summary>
public sealed class SdslTypeExpression : SdslExpression
{
    public SdslTypeExpression(SdslType type)
    {
        Type = type;
    }

    public SdslType Type { get; }
}

public sealed class SdslCast : SdslExpression
{
    public SdslCast(SdslType type, List<string> arraySizes, SdslExpression operand)
    {
        Type = type;
        ArraySizes = arraySizes;
        Operand = operand;
    }

    public SdslType Type { get; }
    public List<string> ArraySizes { get; }
    public SdslExpression Operand { get; }
}

public sealed class SdslUnary : SdslExpression
{
    public SdslUnary(string op, SdslExpression operand, bool postfix)
    {
        Operator = op;
        Operand = operand;
        Postfix = postfix;
    }

    public string Operator { get; }
    public SdslExpression Operand { get; }
    public bool Postfix { get; }
}

public sealed class SdslBinary : SdslExpression
{
    public SdslBinary(string op, SdslExpression left, SdslExpression right)
    {
        Operator = op;
        Left = left;
        Right = right;
    }

    public string Operator { get; }
    public SdslExpression Left { get; }
    public SdslExpression Right { get; }
}

public sealed class SdslAssignment : SdslExpression
{
    public SdslAssignment(string op, SdslExpression target, SdslExpression value)
    {
        Operator = op;
        Target = target;
        Value = value;
    }

    public string Operator { get; }
    public SdslExpression Target { get; }
    public SdslExpression Value { get; }
}

public sealed class SdslConditional : SdslExpression
{
    public SdslConditional(SdslExpression condition, SdslExpression whenTrue, SdslExpression whenFalse)
    {
        Condition = condition;
        WhenTrue = whenTrue;
        WhenFalse = whenFalse;
    }

    public SdslExpression Condition { get; }
    public SdslExpression WhenTrue { get; }
    public SdslExpression WhenFalse { get; }
}

/// <summary><c>{ a, b, c }</c>, in an initializer.</summary>
public sealed class SdslInitializerList : SdslExpression
{
    public List<SdslExpression> Items { get; } = new List<SdslExpression>();
}

/// <summary><c>a, b</c> as one expression.</summary>
public sealed class SdslSequence : SdslExpression
{
    public List<SdslExpression> Items { get; } = new List<SdslExpression>();
}
