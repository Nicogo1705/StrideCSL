using Csl.Generators.Sdsl.Syntax;

namespace Csl.TestApp;

/// <summary>
/// What the engine's SDSL compiler does with constructs the conversion had to work around, on the
/// CPU. Each probe says what it observed; none is a test of the C# shaders. The GPU ones are in
/// <see cref="GpuTests"/>.
/// </summary>
internal static class EngineProbes
{
    public static int Run()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in EngineShaders.Files())
        {
            var text = File.ReadAllText(path);
            foreach (var shader in SdslSyntaxParser.Parse(path, text).Shaders())
                sources.TryAdd(shader.Name, text);
        }

        // Suffixed literals: the translator writes (uint)0 for 0u and (uint)0x10 for 0x10u.
        foreach (var literal in new[] { "1u", "10u", "0u", "0x10u", "0xFFFFFFFFu" })
            Report("the literal " + literal, () => Compiles(sources, "Output[0] = " + literal + ";") ?? "parsed and compiled");

        Report("override stage and stage override", () =>
        {
            // ShadowMapCasterNoPixelShader writes override stage; the same shader with stage override.
            var name = "ShadowMapCasterNoPixelShader";
            var swapped = new Dictionary<string, string>(sources) { [name] = sources[name].Replace("override stage void PSMain", "stage override void PSMain") };
            if (swapped[name] == sources[name])
                return "the engine's shader no longer writes override stage: nothing to compare";
            var asWritten = new EngineCompiler(sources).Compile(new[] { name });
            var reordered = new EngineCompiler(swapped).Compile(new[] { name });
            if (!asWritten.Success || !reordered.Success)
                return "does not compile: " + FirstLine(asWritten.Messages + reordered.Messages);
            return SpirvCompare.SameCode(asWritten.Bytecode, reordered.Bytecode)
                ? "same SPIR-V"
                : $"different SPIR-V ({SpirvCompare.Strip(asWritten.Bytecode).Length} words as written, {SpirvCompare.Strip(reordered.Bytecode).Length} reordered)";
        });
        return 0;
    }

    /// <summary>Null when a compute shader with this statement compiles, else why not.</summary>
    private static string? Compiles(Dictionary<string, string> sources, string statement)
    {
        var probe = new Dictionary<string, string>(sources)
        {
            ["CslProbe"] = "shader CslProbe : ComputeShaderBase { stage RWStructuredBuffer<uint> Output; override void Compute() { " + statement + " } };",
        };
        var result = new EngineCompiler(probe).Compile(new[] { "CslProbe" });
        return result.Success ? null : "rejected: " + FirstLine(result.Messages);
    }

    private static void Report(string name, Func<string> probe)
    {
        string observed;
        try
        {
            observed = probe();
        }
        catch (Exception e)
        {
            observed = e.GetType().Name + ": " + e.Message;
        }
        Console.WriteLine($"ENGINE {name}: {observed}");
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;
        return line.Length > 200 ? line.Substring(0, 200) : line;
    }
}
