using Csl.Generators.Sdsl;

// Csl.Stubs --out DIR [--namespace NS] file.sdsl...
// One C# file per shader, [Shader(External = true)], for C# shaders to inherit.
string? outDir = null;
string ns = "Csl.Engine";
var inputs = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outDir = args[++i]; break;
        case "--namespace": ns = args[++i]; break;
        default: inputs.Add(args[i]); break;
    }
}
if (outDir == null || inputs.Count == 0)
{
    Console.Error.WriteLine("usage: Csl.Stubs --out DIR [--namespace NS] file.sdsl...");
    return 2;
}
Directory.CreateDirectory(outDir);
int failures = 0;
foreach (var input in inputs)
{
    var file = SdslParser.Parse(input, File.ReadAllText(input));
    foreach (var error in file.Errors)
    {
        Console.Error.WriteLine($"{input}({error.Line},{error.Column}): {error.Message}");
        failures++;
    }
    foreach (var shader in file.Shaders)
    {
        var path = Path.Combine(outDir, shader.Name + ".cs");
        File.WriteAllText(path, SdslStubEmitter.Emit(shader, ns, "the engine's " + Path.GetFileName(input) + " (Stride.Rendering package, stride/Assets)"));
        Console.WriteLine($"{shader.Name} -> {path}");
    }
}
return failures == 0 ? 0 : 1;
