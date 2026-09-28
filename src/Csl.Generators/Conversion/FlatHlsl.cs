using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Csl.Generators.Conversion;

/// <summary>
/// One stage of a mixed effect as SPIRV-Cross writes it in HLSL (from the SPIR-V the engine's mixer
/// produced, not legalized: the methods keep their class-qualified names and their locals), made a
/// shader class the converter takes: the register and packoffset annotations dropped, the static
/// globals made members (each lane has its own streams), the entry point wrapper and its structs
/// removed, their semantics kept to bind the stage's inputs and outputs by.
/// </summary>
public sealed class FlatHlsl
{
    private FlatHlsl(string className, string sdsl, string entryPoint, IReadOnlyList<(string Field, string Semantic)> inputs, IReadOnlyList<(string Field, string Semantic)> outputs, IReadOnlyList<(string Buffer, string Field, int Offset)> constants)
    {
        Constants = constants;
        ClassName = className;
        Sdsl = sdsl;
        EntryPoint = entryPoint;
        Inputs = inputs;
        Outputs = outputs;
    }

    public string ClassName { get; }

    /// <summary>The stage as an SDSL shader, for <see cref="ShaderConverter"/>.</summary>
    public string Sdsl { get; }

    /// <summary>The method that runs the stage: frag_main, vert_main.</summary>
    public string EntryPoint { get; }

    /// <summary>The members the stage reads its inputs from, with their semantics (TEXCOORD0, SV_Position…).</summary>
    public IReadOnlyList<(string Field, string Semantic)> Inputs { get; }

    /// <summary>The constant buffer members: their buffer, their member, their byte offset (from packoffset): how the mixer's reflection finds them.</summary>
    public IReadOnlyList<(string Buffer, string Field, int Offset)> Constants { get; }

    /// <summary>The members the stage writes its outputs to, with their semantics.</summary>
    public IReadOnlyList<(string Field, string Semantic)> Outputs { get; }

    private static readonly Regex Annotation = new Regex(@"\s*:\s*(register|packoffset)\s*\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Layout = new Regex(@"\b(column_major|row_major)\s+", RegexOptions.Compiled);
    private static readonly Regex StaticGlobal = new Regex(@"^static (?!const\b)", RegexOptions.Compiled | RegexOptions.Multiline);
    /// <summary>A literal splat, <c>1.0f.xxx</c>: SPIRV-Cross's way to write <c>float3(1.0f)</c>.</summary>
    private static readonly Regex Splat = new Regex(@"(?<![\w.])(\d+\.\d*(?:[eE][+-]?\d+)?)f\.(x{1,4})\b", RegexOptions.Compiled);
    private static readonly Regex Member = new Regex(@"^\s*[\w<>, ]+?\s+(\w+)\s*:\s*(\w+)\s*;", RegexOptions.Compiled);

    public static FlatHlsl From(string hlsl, string className)
    {
        var text = hlsl.Replace("\r\n", "\n");
        var inputs = StructSemantics(text, "SPIRV_Cross_Input");
        var outputs = StructSemantics(text, "SPIRV_Cross_Output");
        var constants = ConstantOffsets(text);
        text = RemoveBlock(text, "struct SPIRV_Cross_Input");
        text = RemoveBlock(text, "struct SPIRV_Cross_Output");
        text = RemoveBlock(text, "SPIRV_Cross_Output main(");
        text = RemoveBlock(text, "void main(");
        text = Annotation.Replace(text, string.Empty);
        text = Layout.Replace(text, string.Empty);
        text = StaticGlobal.Replace(text, string.Empty);
        // Minimum-precision types: 32 bits on the hardware the engine targets.
        text = Regex.Replace(text, @"\bmin(16|10)float", "float");
        text = Regex.Replace(text, @"\bmin(16|12)int", "int");
        text = Regex.Replace(text, @"\bmin16uint", "uint");
        text = Splat.Replace(text, m => m.Groups[2].Length == 1 ? m.Groups[1].Value + "f" : "float" + m.Groups[2].Length + "(" + m.Groups[1].Value + "f)");
        var entry = Regex.IsMatch(text, @"\bvoid frag_main\(") ? "frag_main" : Regex.IsMatch(text, @"\bvoid vert_main\(") ? "vert_main" : "comp_main";
        var sdsl = new StringBuilder();
        sdsl.Append("shader ").Append(className).AppendLine();
        sdsl.AppendLine("{");
        foreach (var line in text.Split('\n'))
            sdsl.Append("    ").AppendLine(line);
        sdsl.AppendLine("};");
        return new FlatHlsl(className, sdsl.ToString(), entry, inputs, outputs, constants);
    }

    private static readonly Regex ConstantBuffer = new Regex(@"cbuffer\s+(\w+)[^{]*\{([^}]*)\}", RegexOptions.Compiled);
    private static readonly Regex PackOffset = new Regex(@"(\w+)(\s*\[\s*\d+\s*\])?\s*:\s*packoffset\(\s*c(\d+)(?:\.([xyzw]))?\s*\)", RegexOptions.Compiled);

    private static List<(string, string, int)> ConstantOffsets(string text)
    {
        var result = new List<(string, string, int)>();
        foreach (Match buffer in ConstantBuffer.Matches(text))
            foreach (Match member in PackOffset.Matches(buffer.Groups[2].Value))
            {
                int component = member.Groups[4].Success ? "xyzw".IndexOf(member.Groups[4].Value[0]) : 0;
                result.Add((buffer.Groups[1].Value, member.Groups[1].Value, int.Parse(member.Groups[3].Value) * 16 + component * 4));
            }
        return result;
    }

    /// <summary>The fields of a struct with their semantics, its members prefixed as main copies them (stage_input.X → X).</summary>
    private static List<(string, string)> StructSemantics(string text, string name)
    {
        var result = new List<(string, string)>();
        int start = text.IndexOf("struct " + name, StringComparison.Ordinal);
        if (start < 0)
            return result;
        int open = text.IndexOf('{', start), close = text.IndexOf('}', open);
        foreach (var line in text.Substring(open + 1, close - open - 1).Split('\n'))
        {
            var match = Member.Match(line);
            if (match.Success)
                result.Add((match.Groups[1].Value, match.Groups[2].Value));
        }
        return result;
    }

    /// <summary>Removes the declaration starting with <paramref name="head"/> through its matching closing brace (and a following ';').</summary>
    private static string RemoveBlock(string text, string head)
    {
        int start = text.IndexOf(head, StringComparison.Ordinal);
        if (start < 0)
            return text;
        int open = text.IndexOf('{', start);
        int depth = 0, i = open;
        for (; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) break;
        }
        int end = i + 1;
        if (end < text.Length && text[end] == ';')
            end++;
        return text.Substring(0, start) + text.Substring(end);
    }
}
