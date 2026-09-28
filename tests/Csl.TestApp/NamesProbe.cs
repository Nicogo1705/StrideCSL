using System.Collections.Concurrent;
using System.Reflection;
using Csl.Generators.Sdsl.Syntax;
using Microsoft.CodeAnalysis.CSharp;

namespace Csl.TestApp;

/// <summary>
/// Which identifiers a C# shader cannot use: each candidate name (C#, HLSL and SDSL keywords, the
/// intrinsics, names the backends generate) declared as a local (declared, and at the start of a statement), a parameter, a method, a shader
/// variable and a struct field of a small image effect, and a local of a compute shader, compiled by the engine to SPIR-V and on to
/// Direct3D 11 (SPIRV-Cross, fxc) on the CPU. The names that fail somewhere are what CSL110 reports.
/// </summary>
internal static class NamesProbe
{
    private static readonly string[] Positions = { "local", "statement", "parameter", "method", "variable", "field", "compute" };

    public static int Run(string[] args)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in EngineShaders.Files())
        {
            var text = File.ReadAllText(path);
            foreach (var shader in SdslSyntaxParser.Parse(path, text).Shaders())
                sources.TryAdd(shader.Name, text);
        }

        var names = args.Length > 0 ? args.ToList() : Candidates();
        // The probe itself must compile, or every failure means nothing.
        var control = Probe(sources, "cslProbeName", "local");
        if (control != null)
        {
            Console.WriteLine("the control name fails: " + control);
            return 2;
        }

        var failures = new ConcurrentDictionary<(string Name, string Position), string>();
        Parallel.ForEach(names.SelectMany(n => Positions.Select(p => (Name: n, Position: p))), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, pair =>
        {
            var error = Probe(sources, pair.Name, pair.Position);
            if (error != null)
                failures[pair] = error;
        });

        var outPath = Path.Combine(Path.GetTempPath(), "csl-names.txt");
        using (var report = new StreamWriter(outPath))
        {
            foreach (var name in names.Where(n => failures.Keys.Any(k => k.Name == n)).OrderBy(n => n, StringComparer.Ordinal))
            {
                report.WriteLine(name);
                foreach (var position in Positions)
                    report.WriteLine($"    {position,-10} {(failures.TryGetValue((name, position), out var e) ? e : "ok")}");
            }
        }
        var failing = names.Where(n => failures.Keys.Any(k => k.Name == n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Console.WriteLine($"{names.Count} names, {failing.Count} fail somewhere; report in {outPath}");
        Console.WriteLine(string.Join(" ", failing));
        return 0;
    }

    /// <summary>The first error compiling the probe with this name at this position, or null.</summary>
    private static string? Probe(Dictionary<string, string> sources, string name, string position)
    {
        var shaderName = "CslProbe_" + Math.Abs(StringComparer.Ordinal.GetHashCode(name + "/" + position)).ToString("x8");
        var body = position switch
        {
            "local" => $"stage override float4 Shading() {{ float {name} = 0.5; return float4({name}, 0, 0, 1); }}",
            "statement" => $"stage override float4 Shading() {{ float {name}; {name} = 0.5; {name} += 0.25; return float4({name}, 0, 0, 1); }}",
            "parameter" => $"float Twice(float {name}) {{ return {name} * 2.0; }}\n stage override float4 Shading() {{ return float4(Twice(0.25), 0, 0, 1); }}",
            "method" => $"float {name}(float a) {{ return a * 2.0; }}\n stage override float4 Shading() {{ return float4({name}(0.25), 0, 0, 1); }}",
            "variable" => $"stage float {name};\n stage override float4 Shading() {{ return float4({name}, 0, 0, 1); }}",
            "field" => $"struct CslProbeData {{ float {name}; }};\n stage override float4 Shading() {{ CslProbeData d; d.{name} = 0.5; return float4(d.{name}, 0, 0, 1); }}",
            "compute" => $"RWStructuredBuffer<float> CslProbeOut;\n override void Compute() {{ float {name} = 0.5; {name} += 0.25; CslProbeOut[0] = {name}; }}",
            _ => throw new ArgumentOutOfRangeException(nameof(position)),
        };
        var own = new Dictionary<string, string>(sources, StringComparer.Ordinal)
        {
            [shaderName] = $"shader {shaderName} : {(position == "compute" ? "ComputeShaderBase" : "ImageEffectShader")}\n{{\n {body}\n}};\n",
        };
        EngineCompiler.Result compiled;
        try
        {
            compiled = new EngineCompiler(own) { ForD3D11 = true }.Compile(new[] { shaderName });
        }
        catch (Exception e)
        {
            return "SPIR-V: " + FirstLine(e.Message);
        }
        if (!compiled.Success)
            return "SPIR-V: " + FirstLine(compiled.Messages);
        try
        {
            var errors = D3D11Compiler.Compile(compiled.Bytecode);
            return errors.Length == 0 ? null : "fxc: " + FirstLine(errors);
        }
        catch (Exception e)
        {
            return "HLSL: " + FirstLine(e.Message);
        }
    }

    private static List<string> Candidates()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        // C#: every keyword, reserved or contextual; a C# shader writes the reserved ones @name.
        foreach (var kind in SyntaxFacts.GetKeywordKinds().Concat(SyntaxFacts.GetContextualKeywordKinds()))
        {
            var text = SyntaxFacts.GetText(kind);
            if (text.Length > 0 && char.IsLetter(text[0]) && !text.StartsWith("__", StringComparison.Ordinal))
                names.Add(text);
        }
        // HLSL keywords and reserved words, object types, shader model names.
        names.UnionWith(new[]
        {
            "asm", "asm_fragment", "auto", "BlendState", "break", "Buffer", "ByteAddressBuffer", "case", "catch", "cbuffer", "centroid", "char",
            "class", "column_major", "compile", "compile_fragment", "CompileShader", "const", "const_cast", "continue", "ComputeShader",
            "ConsumeStructuredBuffer", "AppendStructuredBuffer", "default", "delete", "DepthStencilState", "DepthStencilView", "discard", "do",
            "DomainShader", "dword", "dynamic_cast", "else", "enum", "explicit", "export", "extern", "false", "for", "friend", "fxgroup",
            "GeometryShader", "globallycoherent", "goto", "groupshared", "half", "HullShader", "if", "in", "inline", "inout", "InputPatch",
            "interface", "line", "lineadj", "linear", "LineStream", "long", "matrix", "mutable", "namespace", "new", "nointerpolation",
            "noperspective", "NULL", "operator", "out", "OutputPatch", "packoffset", "pass", "pixelfragment", "PixelShader", "point",
            "PointStream", "precise", "private", "protected", "public", "RasterizerState", "register", "reinterpret_cast", "RenderTargetView",
            "return", "row_major", "RWBuffer", "RWByteAddressBuffer", "RWStructuredBuffer", "RWTexture1D", "RWTexture1DArray", "RWTexture2D",
            "RWTexture2DArray", "RWTexture3D", "sample", "sampler", "sampler1D", "sampler2D", "sampler3D", "samplerCUBE", "SamplerComparisonState",
            "SamplerState", "sampler_state", "shared", "short", "signed", "sizeof", "snorm", "stateblock", "stateblock_state", "static",
            "static_cast", "string", "struct", "StructuredBuffer", "switch", "tbuffer", "technique", "technique10", "technique11", "template",
            "texture", "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray", "Texture2DMS", "Texture2DMSArray", "Texture3D",
            "TextureCube", "TextureCubeArray", "this", "throw", "triangle", "triangleadj", "TriangleStream", "true", "try", "typedef",
            "typename", "uniform", "union", "unorm", "unsigned", "using", "vector", "vertexfragment", "VertexShader", "virtual", "void",
            "volatile", "while", "min16float", "min10float", "min16int", "min12int", "min16uint", "float2", "int3", "uint4", "bool2",
            "half3", "double4", "float4x4", "float3x3", "float1", "float1x1",
        });
        // SDSL.
        names.UnionWith(new[]
        {
            "abstract", "base", "clone", "compose", "effect", "foreach", "internal", "mixin", "override", "params", "partial", "rgroup",
            "shader", "stage", "stream", "streams", "var", "Link", "Color", "Semantic", "LinkType", "MemberName",
        });
        // Intrinsics, and names the translation to SPIR-V or HLSL may generate.
        foreach (var method in typeof(Csl.Types.Intrinsics).GetMethods(BindingFlags.Public | BindingFlags.Static))
            names.Add(method.Name);
        names.UnionWith(new[]
        {
            "main", "input", "output", "result", "stage_input", "stage_output", "gl_Position", "gl_FragCoord", "gl_x", "__x", "_x",
            "x__y", "SPIRV_Cross_Input", "SPIRV_Cross_Output", "globals", "Globals", "PS", "VS", "PSMain", "VSMain", "Shading", "Twice",
        });
        return names.ToList();
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length > 180 ? line.Substring(0, 180) : line;
    }
}
