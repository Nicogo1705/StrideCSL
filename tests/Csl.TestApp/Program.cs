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
    default:
        Console.WriteLine("Csl.TestApp parse [files...]              parse the engine's shaders (or these files) with the full SDSL parser");
        Console.WriteLine("Csl.TestApp convert [--out DIR] [--only NAME,...]");
        Console.WriteLine("                                          convert the engine's shaders SDSL to C# to SDSL, report what fails");
        return command == "help" ? 0 : 2;
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
