using Csl.Generators.Sdsl.Syntax;
using Csl.TestApp;

var command = args.Length > 0 ? args[0] : "help";
var rest = args.Skip(1).ToArray();
switch (command)
{
    case "parse":
        return ParseCommand(rest);
    case "convert":
        return ConvertCommand.Run(rest);
    case "roundtrip":
        return RoundTripCommand.Run(rest);
    case "compile":
        return CompileCommand(rest);
    case "flat-all":
        return RoundTripCommand.FlatAll(rest);
    case "flat":
        return FlatCommand(rest);
    case "hlsl":
        return HlslCommand(rest);
    case "names":
        return NamesProbe.Run(rest);
    case "gpu":
        return GpuTests.Run(rest);
    default:
        Console.WriteLine("Csl.TestApp parse [files...]              parse the engine's shaders (or these files) with the full SDSL parser");
        Console.WriteLine("Csl.TestApp convert [--out DIR] [--only NAME,...]");
        Console.WriteLine("                                          convert the engine's shaders SDSL to C# to SDSL, report what fails");
        Console.WriteLine("Csl.TestApp roundtrip [--out DIR] [--only NAME,...]");
        Console.WriteLine("                                          compile each shader from its SDSL and from its round trip, compare the SPIR-V");
        Console.WriteLine("Csl.TestApp gpu                           run the C# shaders of Shaders/ on the GPU (a hidden game window) and check what they compute");
        Console.WriteLine("Csl.TestApp names [NAME...]                which identifiers fail as a local, parameter, method, variable or field (SPIR-V, then D3D11 on the CPU)");
        Console.WriteLine("Csl.TestApp compile NAME...               compile engine shaders, mixed in this order, with the engine's SDSL compiler");
        return command == "help" ? 0 : 2;
}

// Compiles the engine's shaders, mixed in the order given, and writes the SPIR-V next to the size.
static int CompileCommand(string[] names)
{
    var sources = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var path in EngineShaders.Files())
    {
        var text = File.ReadAllText(path);
        foreach (var shader in SdslSyntaxParser.Parse(path, text).Shaders())
            sources.TryAdd(shader.Name, text);
    }
    // The C# shaders of this app (Shaders/, Modified/) as registered at start-up, over the engine's.
    foreach (var pair in Csl.ShaderSourceRegistry.Sources)
        sources[pair.Key] = pair.Value.Source;
    var result = new EngineCompiler(sources).Compile(names);
    Console.WriteLine(result.Success ? $"{result.Bytecode.Length} bytes, {SpirvCompare.Strip(result.Bytecode).Length} words without debug" : "failed");
    if (result.Messages.Length > 0)
        Console.WriteLine(result.Messages);
    if (result.Success)
        File.WriteAllBytes(Path.Combine(Path.GetTempPath(), "csl-compile.spv"), result.Bytecode);
    return result.Success ? 0 : 1;
}

static int ParseCommand(string[] files)
{
    var paths = files.Length > 0 ? files.ToList() : EngineShaders.Files();
    int failed = 0, shaders = 0;
    foreach (var path in paths)
    {
        var unit = SdslSyntaxParser.Parse(path, File.ReadAllText(path));
        shaders += unit.Shaders().Count();
        if (unit.Diagnostics.Count == 0)
            continue;
        failed++;
        foreach (var diagnostic in unit.Diagnostics)
            Console.WriteLine($"{Path.GetFileName(path)}({diagnostic.Position}): {diagnostic.Message}");
    }
    Console.WriteLine($"{paths.Count} files, {shaders} shaders, {failed} files with errors");
    return failed == 0 ? 0 : 1;
}

// The HLSL the Direct3D 11 path compiles for shaders mixed in this order: what fxc sees, per stage.
static int HlslCommand(string[] args)
{
    bool raw = args.Contains("--raw");
    var names = args.Where(a => a != "--raw").ToArray();
    var sources = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var path in EngineShaders.Files())
    {
        var text = File.ReadAllText(path);
        foreach (var shader in SdslSyntaxParser.Parse(path, text).Shaders())
            sources.TryAdd(shader.Name, text);
    }
    foreach (var pair in Csl.ShaderSourceRegistry.Sources)
        sources[pair.Key] = pair.Value.Source;
    var result = new EngineCompiler(sources) { ForD3D11 = true }.Compile(names);
    if (!result.Success)
    {
        Console.WriteLine(result.Messages);
        return 1;
    }
    foreach (var (stage, hlsl) in D3D11Compiler.Translate(result.Bytecode, legalize: !raw))
    {
        Console.WriteLine("// ===== " + stage);
        Console.WriteLine(hlsl);
    }
    return 0;
}

// The shaders mixed as the engine mixes them, each stage made a flat C# class (FlatHlsl, then the
// converter): what the CPU runs of a whole effect. Writes the C# to %TEMP%/csl-flat and reports errors.
static int FlatCommand(string[] names)
{
    var sources = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var path in EngineShaders.Files())
    {
        var text = File.ReadAllText(path);
        foreach (var shader in SdslSyntaxParser.Parse(path, text).Shaders())
            sources.TryAdd(shader.Name, text);
    }
    foreach (var pair in Csl.ShaderSourceRegistry.Sources)
        sources[pair.Key] = pair.Value.Source;
    var result = new EngineCompiler(sources) { ForD3D11 = true }.Compile(names);
    if (!result.Success)
    {
        Console.WriteLine(result.Messages);
        return 1;
    }
    var outDir = Path.Combine(Path.GetTempPath(), "csl-flat");
    Directory.CreateDirectory(outDir);
    var files = new List<(string Path, string Text)>();
    foreach (var (stage, hlsl) in D3D11Compiler.Translate(result.Bytecode, legalize: false))
    {
        var flat = Csl.Generators.Conversion.FlatHlsl.From(hlsl, "Flat" + stage);
        File.WriteAllText(Path.Combine(outDir, flat.ClassName + ".sdsl"), flat.Sdsl);
        files.Add((Path.Combine(outDir, flat.ClassName + ".sdsl"), flat.Sdsl));
        Console.WriteLine($"{flat.ClassName}: entry {flat.EntryPoint}, in [{string.Join(", ", flat.Inputs.Select(i => i.Field + ":" + i.Semantic))}], out [{string.Join(", ", flat.Outputs.Select(o => o.Field + ":" + o.Semantic))}]");
    }
    int failures = 0;
    foreach (var converted in ConvertCommand.Convert(files))
    {
        if (converted.CSharp != null)
            File.WriteAllText(Path.Combine(outDir, converted.Name + ".cs"), converted.CSharp);
        foreach (var error in converted.ConversionErrors.Concat(converted.CompileErrors).Take(15))
        {
            Console.WriteLine($"  {converted.Name}: {error}");
            failures++;
        }
    }
    Console.WriteLine(failures == 0 ? "flat: every stage converts and compiles as C#" : $"flat: {failures} errors; files in {outDir}");
    return failures == 0 ? 0 : 1;
}