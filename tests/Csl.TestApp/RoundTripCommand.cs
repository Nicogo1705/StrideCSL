using System.Collections.Concurrent;
using System.Diagnostics;
using Csl.Generators.Conversion;
using Csl.Generators.Sdsl.Syntax;

namespace Csl.TestApp;

/// <summary>
/// The proof the conversion keeps meaning: every engine shader that compiles on its own is compiled
/// by the engine's SDSL compiler twice, from its original source and from the SDSL its C# translates
/// back to (its bases round-tripped too), and the two SPIR-V modules are compared byte for byte.
/// </summary>
internal static class RoundTripCommand
{
    public static int Run(string[] args)
    {
        string outDir = Path.Combine(Path.GetTempPath(), "csl-roundtrip");
        HashSet<string>? only = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = args[++i]; break;
                case "--only": only = new HashSet<string>(args[++i].Split(','), StringComparer.Ordinal); break;
            }
        }
        Directory.CreateDirectory(outDir);

        var watch = Stopwatch.StartNew();
        var files = EngineShaders.Files().Select(p => (Path: p, Text: File.ReadAllText(p))).ToList();
        var original = new Dictionary<string, string>(StringComparer.Ordinal);
        var generic = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
        {
            var unit = SdslSyntaxParser.Parse(path, text);
            var names = unit.Shaders().ToList();
            if (names.Count == 0)
                original.TryAdd(Path.GetFileNameWithoutExtension(path), text);
            foreach (var shader in names)
            {
                original.TryAdd(shader.Name, text);
                if (shader.GenericParameters.Count > 0)
                    generic.Add(shader.Name);
            }
        }

        var results = ConvertCommand.Convert(files);
        var converted = results.Where(r => r.Succeeded).ToDictionary(r => r.Name, StringComparer.Ordinal);
        var roundTrip = new Dictionary<string, string>(original, StringComparer.Ordinal);
        foreach (var result in converted.Values)
            roundTrip[result.Name] = result.Sdsl!;
        Console.WriteLine($"{converted.Count} of {results.Count} shaders translate back ({watch.Elapsed.TotalSeconds:F1}s)");

        var index = new SdslShaderIndex();
        foreach (var (path, text) in files)
            index.Add(SdslSyntaxParser.Parse(path, text));
        var candidates = converted.Keys.Where(n => only == null || only.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var outcomes = new ConcurrentDictionary<string, (string Outcome, string Detail)>(StringComparer.Ordinal);
        var contexts = new ConcurrentDictionary<string, EffectContext>(StringComparer.Ordinal);
        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, name =>
        {
            // The context an effect would give (generic arguments, compositions, stubs), made up until the original compiles.
            var context = new EffectContext(index, index.Find(name)!);
            contexts[name] = context;
            var originalSources = new Dictionary<string, string>(original, StringComparer.Ordinal);
            var before = context.Settle(originalSources);
            var notes = context.Notes.Count == 0 ? string.Empty : "[" + string.Join("; ", context.Notes) + "] ";
            if (!before.Success)
            {
                outcomes[name] = ("untestable", notes + FirstLine(before.Messages));
                return;
            }
            var roundTripSources = new Dictionary<string, string>(roundTrip, StringComparer.Ordinal);
            foreach (var stub in context.Stubs)
                roundTripSources[stub.Key] = stub.Value;
            var after = new EngineCompiler(roundTripSources).Compile(context.Mixin());
            if (!after.Success)
            {
                outcomes[name] = ("FAILS", notes + after.Messages);
                return;
            }
            outcomes[name] = SpirvCompare.SameCode(before.Bytecode, after.Bytecode) ? ("identical", notes) : ("DIFFERENT", notes + $"{before.Bytecode.Length} vs {after.Bytecode.Length} bytes");
            if (outcomes[name].Outcome == "DIFFERENT")
            {
                File.WriteAllBytes(Path.Combine(outDir, name + ".original.spv"), before.Bytecode);
                File.WriteAllBytes(Path.Combine(outDir, name + ".roundtrip.spv"), after.Bytecode);
            }
        });

        // For each shader that changes: which of its bases, round-tripped alone, changes it.
        foreach (var pair in outcomes.Where(p => p.Value.Outcome is "FAILS" or "DIFFERENT").ToList())
        {
            var shader = index.Find(pair.Key);
            if (shader == null)
                continue;
            var context = contexts[pair.Key];
            var withStubs = new Dictionary<string, string>(original, StringComparer.Ordinal);
            foreach (var stub in context.Stubs)
                withStubs[stub.Key] = stub.Value;
            var reference = new EngineCompiler(withStubs).Compile(context.Mixin());
            var culprits = new List<string>();
            foreach (var candidate in new[] { shader }.Concat(index.AllBases(shader)))
            {
                if (!converted.ContainsKey(candidate.Name))
                    continue;
                var single = new Dictionary<string, string>(withStubs, StringComparer.Ordinal) { [candidate.Name] = converted[candidate.Name].Sdsl! };
                var compiled = new EngineCompiler(single).Compile(context.Mixin());
                if (!compiled.Success || !SpirvCompare.SameCode(reference.Bytecode, compiled.Bytecode))
                    culprits.Add(candidate.Name);
            }
            outcomes[pair.Key] = (pair.Value.Outcome, "changed by: " + (culprits.Count == 0 ? "(no single shader)" : string.Join(", ", culprits)) + Environment.NewLine + pair.Value.Detail);
        }

        using (var report = new StreamWriter(Path.Combine(outDir, "roundtrip.txt")))
        {
            foreach (var pair in outcomes.OrderBy(p => p.Value.Outcome, StringComparer.Ordinal).ThenBy(p => p.Key, StringComparer.Ordinal))
                report.WriteLine($"{pair.Value.Outcome,-10} {pair.Key}  {pair.Value.Detail.Replace("\n", "\n           ")}");
        }
        foreach (var group in outcomes.GroupBy(p => p.Value.Outcome).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {group.Count(),4} {group.Key}");
        Console.WriteLine($"  ({outcomes.Count(o => generic.Contains(o.Key) && o.Value.Outcome == "identical")} of the identical are generic shaders, instantiated with sample arguments; {outcomes.Count(o => o.Value.Outcome == "identical" && o.Value.Detail.Length > 0)} needed a made-up context)");
        foreach (var pair in outcomes.Where(p => p.Value.Outcome is "FAILS" or "DIFFERENT").OrderBy(p => p.Key, StringComparer.Ordinal).Take(40))
            Console.WriteLine($"  {pair.Value.Outcome} {pair.Key}: {FirstLine(pair.Value.Detail)}");
        Console.WriteLine($"done in {watch.Elapsed.TotalSeconds:F1}s; report in {outDir}");
        return outcomes.Values.Any(o => o.Outcome is "FAILS" or "DIFFERENT") ? 1 : 0;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;
        return line.Length > 200 ? line.Substring(0, 200) : line;
    }

    /// <summary>
    /// Every engine shader the round trip compiles, in the context it made up, mixed by the engine and
    /// each stage flattened to C# (FlatHlsl, the converter): how many SPIRV-Cross translates and how many
    /// convert and compile, the whole effect as the CPU would run it.
    /// </summary>
    public static int FlatAll(string[] args)
    {
        var files = EngineShaders.Files().Select(p => (Path: p, Text: File.ReadAllText(p))).ToList();
        var original = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = new SdslShaderIndex();
        foreach (var (path, text) in files)
        {
            var unit = SdslSyntaxParser.Parse(path, text);
            index.Add(unit);
            foreach (var shader in unit.Shaders())
                original.TryAdd(shader.Name, text);
        }
        var flats = new ConcurrentBag<(string Shader, string Path, string Text)>();
        var failures = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        int compiled = 0;
        Parallel.ForEach(index.Shaders.Select(s => s.Name).Where(n => original.ContainsKey(n)).ToList(), name =>
        {
            var context = new EffectContext(index, index.Find(name)!);
            var result = context.Settle(new Dictionary<string, string>(original, StringComparer.Ordinal));
            if (!result.Success)
                return;
            Interlocked.Increment(ref compiled);
            try
            {
                var d3d = new EngineCompiler(new Dictionary<string, string>(original.Concat(context.Stubs).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value), StringComparer.Ordinal)) { ForD3D11 = true }.Compile(context.Mixin());
                if (!d3d.Success)
                    return;
                foreach (var (stage, hlsl) in D3D11Compiler.Translate(d3d.Bytecode, legalize: false))
                {
                    var flat = Csl.Generators.Conversion.FlatHlsl.From(hlsl, "Flat_" + name + "_" + stage);
                    flats.Add((name, flat.ClassName + ".sdsl", flat.Sdsl));
                }
            }
            catch (Exception e)
            {
                failures[name] = "SPIRV-Cross: " + e.GetBaseException().Message.Split('\n')[0];
            }
        });
        var converted = ConvertCommand.Convert(flats.Select(f => (f.Path, f.Text)).ToList());
        var byShader = flats.GroupBy(f => f.Shader).ToDictionary(g => g.Key, g => g.Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Path)).ToList());
        var outcome = converted.ToDictionary(r => r.Name, StringComparer.Ordinal);
        int ok = 0;
        var errors = new List<string>();
        foreach (var (shader, classes) in byShader)
        {
            var bad = classes.Select(c => outcome.TryGetValue(c, out var r) ? r : null).Where(r => r == null || r.ConversionErrors.Count + r.CompileErrors.Count > 0).ToList();
            if (bad.Count == 0)
                ok++;
            else
                errors.AddRange(bad.Where(r => r != null).SelectMany(r => r!.ConversionErrors.Concat(r.CompileErrors).Select(e => shader + ": " + e)).Take(3));
        }
        Console.WriteLine($"{compiled} effects compile; {failures.Count} fail in SPIRV-Cross; {ok} of {byShader.Count} flatten to C# that compiles");
        foreach (var group in errors.Select(e => System.Text.RegularExpressions.Regex.Replace(e.Substring(e.IndexOf(": ") + 2), @"\(\d+,\d+\)|'[^']*'", "…")).GroupBy(e => e).OrderByDescending(g => g.Count()).Take(15))
            Console.WriteLine($"  {group.Count(),4} {group.Key.Substring(0, Math.Min(150, group.Key.Length))}");
        foreach (var failure in failures.Take(5))
            Console.WriteLine($"  {failure.Key}: {failure.Value}");
        File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "csl-flat-all.txt"), errors.Concat(failures.Select(f => f.Key + ": " + f.Value)));
        return ok == byShader.Count && failures.IsEmpty ? 0 : 1;
    }
}