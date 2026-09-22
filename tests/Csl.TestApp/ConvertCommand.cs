using System.Diagnostics;
using System.Text.RegularExpressions;
using Csl.Generators.Conversion;
using Microsoft.CodeAnalysis;

namespace Csl.TestApp;

/// <summary>
/// The engine's shaders through SDSL to C# to SDSL: how many convert, compile and translate back,
/// and why the others do not. The C# and the SDSL are written to --out for reading.
/// </summary>
internal static class ConvertCommand
{
    public static int Run(string[] args)
    {
        string outDir = Path.Combine(Path.GetTempPath(), "csl-convert");
        HashSet<string>? only = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outDir = args[++i]; break;
                case "--only": only = new HashSet<string>(args[++i].Split(','), StringComparer.Ordinal); break;
            }
        }

        var watch = Stopwatch.StartNew();
        var files = EngineShaders.Files().Select(p => (Path: p, Text: File.ReadAllText(p))).ToList();
        var results = Convert(files);
        Console.WriteLine($"converted {results.Count} shaders in {watch.Elapsed.TotalSeconds:F1}s");

        Directory.CreateDirectory(Path.Combine(outDir, "cs"));
        Directory.CreateDirectory(Path.Combine(outDir, "sdsl"));
        foreach (var result in results)
        {
            if (result.CSharp != null)
                File.WriteAllText(Path.Combine(outDir, "cs", result.Name + ".cs"), result.CSharp);
            if (result.Sdsl != null)
                File.WriteAllText(Path.Combine(outDir, "sdsl", result.Name + ".sdsl"), result.Sdsl);
        }

        var shown = only == null ? results : results.Where(r => only.Contains(r.Name)).ToList();
        using (var report = new StreamWriter(Path.Combine(outDir, "report.txt")))
        {
            foreach (var result in shown.Where(r => !r.Succeeded))
            {
                report.WriteLine($"== {result.Name} ({result.SourcePath})");
                foreach (var error in result.ConversionErrors) report.WriteLine("  conversion: " + error);
                foreach (var error in result.CompileErrors) report.WriteLine("  compile:    " + error);
                foreach (var error in result.TranslationErrors) report.WriteLine("  translate:  " + error);
            }
        }

        int converted = results.Count(r => r.ConversionErrors.Count == 0);
        int compiled = results.Count(r => r.ConversionErrors.Count == 0 && r.CompileErrors.Count == 0);
        int translated = results.Count(r => r.Succeeded);
        Console.WriteLine($"{results.Count} shaders: {converted} converted, {compiled} compile as C#, {translated} translate back to SDSL; {results.Sum(r => r.Fixes)} fixes applied");
        Console.WriteLine("errors by kind (shaders affected):");
        foreach (var group in results.SelectMany(r => r.ConversionErrors.Select(e => "conversion " + Normalize(e)).Concat(r.CompileErrors.Select(e => "compile " + Normalize(e))).Concat(r.TranslationErrors.Select(e => "translate " + Normalize(e))).Distinct())
                     .GroupBy(e => e).OrderByDescending(g => g.Count()).Take(60))
            Console.WriteLine($"  {group.Count(),4} {group.Key}");
        Console.WriteLine("output in " + outDir);
        return translated == results.Count ? 0 : 1;
    }

    public static List<ConvertedShader> Convert(List<(string Path, string Text)> files)
    {
        var options = new ShaderConverterOptions();
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith("System", StringComparison.Ordinal) || name is "netstandard" or "mscorlib" or "Microsoft.CSharp")
                options.References.Add(MetadataReference.CreateFromFile(path));
        }
        options.References.Add(MetadataReference.CreateFromFile(typeof(Csl.ShaderAttribute).Assembly.Location));
        return ShaderConverter.Convert(files, options);
    }

    /// <summary>An error without its position and names, to count kinds.</summary>
    private static string Normalize(string error)
    {
        error = Regex.Replace(error, @"\(\d+,\d+\)", string.Empty);
        error = Regex.Replace(error, @"^\d+:\d+: ", string.Empty);
        error = Regex.Replace(error, "'[^']*'", "'…'");
        return error.Length > 150 ? error.Substring(0, 150) : error;
    }
}
