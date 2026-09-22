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
    case "gpu":
        return GpuTests.Run(rest);
    default:
        Console.WriteLine("Csl.TestApp parse [files...]              parse the engine's shaders (or these files) with the full SDSL parser");
        Console.WriteLine("Csl.TestApp convert [--out DIR] [--only NAME,...]");
        Console.WriteLine("                                          convert the engine's shaders SDSL to C# to SDSL, report what fails");
        Console.WriteLine("Csl.TestApp roundtrip [--out DIR] [--only NAME,...]");
        Console.WriteLine("                                          compile each shader from its SDSL and from its round trip, compare the SPIR-V");
        Console.WriteLine("Csl.TestApp gpu                           run the C# shaders of Shaders/ on the GPU (a hidden game window) and check what they compute");
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
