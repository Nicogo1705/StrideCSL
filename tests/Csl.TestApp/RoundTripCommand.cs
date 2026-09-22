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
}
