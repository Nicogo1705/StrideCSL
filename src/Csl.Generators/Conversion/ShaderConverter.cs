using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Csl.Generators.CSharp;
using Csl.Generators.Sdsl.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Csl.Generators.Conversion;

/// <summary>One shader through the conversion: its C#, what went wrong, and the SDSL the C# translates back to.</summary>
public sealed class ConvertedShader
{
    public ConvertedShader(string name, string sourcePath)
    {
        Name = name;
        SourcePath = sourcePath;
    }

    public string Name { get; }

    /// <summary>The .sdsl the shader came from.</summary>
    public string SourcePath { get; }

    /// <summary>The C# file, after the fixes; null when the SDSL did not parse.</summary>
    public string? CSharp { get; set; }

    /// <summary>What the SDSL to C# conversion could not express, with SDSL positions.</summary>
    public List<string> ConversionErrors { get; } = new List<string>();

    /// <summary>C# compiler errors left after the fixes, with C# positions.</summary>
    public List<string> CompileErrors { get; } = new List<string>();

    /// <summary>The generator's own diagnostics on the class (CSL1xx): what does not translate back.</summary>
    public List<string> TranslationErrors { get; } = new List<string>();

    /// <summary>The SDSL the C# translates back to, or null when it does not.</summary>
    public string? Sdsl { get; set; }

    /// <summary>How many fixes were applied to the C# the conversion wrote.</summary>
    public int Fixes { get; set; }

    public bool Succeeded => CSharp != null && Sdsl != null && ConversionErrors.Count == 0 && CompileErrors.Count == 0 && TranslationErrors.Count == 0;
}

public sealed class ShaderConverterOptions
{
    /// <summary>What the C# compiles against: the runtime, Csl.Types, and Csl.Engine or any other shader library.</summary>
    public List<MetadataReference> References { get; } = new List<MetadataReference>();

    /// <summary>Shaders known by name but not converted: their C# comes from <see cref="References"/> (the engine's, say).</summary>
    public List<SdslCompilationUnit> KnownShaders { get; } = new List<SdslCompilationUnit>();

    public SdslToCSharpOptions CSharp { get; } = new SdslToCSharpOptions();

    /// <summary>Rounds of compiler-guided fixes at most.</summary>
    public int MaxFixRounds { get; set; } = 12;
}

/// <summary>
/// SDSL files to C# [Shader] classes that compile, and back. The C# is first written from the SDSL
/// tree, then compiled with the generator (which adds the mixin stubs and translates the classes back
/// to SDSL); what HLSL leaves implicit and C# does not accept is made explicit from the compiler's
/// errors, round after round (<see cref="CSharpFixer"/>).
/// </summary>
public static class ShaderConverter
{
    public static List<ConvertedShader> Convert(IEnumerable<(string Path, string Text)> files, ShaderConverterOptions options, CancellationToken cancellation = default)
    {
        var index = new SdslShaderIndex();
        var units = new List<SdslCompilationUnit>();
        foreach (var (path, text) in files)
        {
            var unit = SdslSyntaxParser.Parse(path, text);
            units.Add(unit);
            index.Add(unit);
        }
        foreach (var known in options.KnownShaders)
            index.Add(known);

        var results = new List<ConvertedShader>();
        var trees = new Dictionary<ConvertedShader, SyntaxTree>();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        foreach (var unit in units)
        {
            var shaders = unit.Shaders().ToList();
            if (shaders.Count == 0 && unit.Diagnostics.Count > 0)
            {
                var failed = new ConvertedShader(System.IO.Path.GetFileNameWithoutExtension(unit.Path), unit.Path);
                failed.ConversionErrors.AddRange(unit.Diagnostics.Select(d => d.ToString()));
                results.Add(failed);
                continue;
            }
            IEnumerable<string>? fileComments = unit.Items.Count > 0 && unit.Items[0] is SdslNamespaceDeclaration ns ? ns.Comments : null;
            foreach (var shader in shaders)
            {
                var result = new ConvertedShader(shader.Name, unit.Path);
                if (results.Any(r => r.Name == shader.Name))
                    continue; // the same shader in two packages: the first one is the one the index knows
                results.Add(result);
                if (unit.Diagnostics.Count > 0)
                {
                    result.ConversionErrors.AddRange(unit.Diagnostics.Select(d => d.ToString()));
                    continue;
                }
                var diagnostics = new List<SdslDiagnostic>();
                result.CSharp = SdslToCSharp.Convert(shader, index, options.CSharp, diagnostics, fileComments);
                result.ConversionErrors.AddRange(diagnostics.Select(d => d.ToString()));
                trees[result] = CSharpSyntaxTree.ParseText(result.CSharp, parseOptions, path: shader.Name + ".cs", cancellationToken: cancellation);
            }
        }

        var generator = new ShaderEffectGenerator().AsSourceGenerator();
        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable);
        Compilation? final = null;
        for (int round = 0; ; round++)
        {
            cancellation.ThrowIfCancellationRequested();
            var compilation = CSharpCompilation.Create("CslConversion", trees.Values, options.References, compilationOptions);
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { generator }, parseOptions: parseOptions);
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics, cancellation);
            final = output;
            var exception = driver.GetRunResult().Results.FirstOrDefault().Exception;
            if (exception != null)
                throw new InvalidOperationException("The generator failed: " + exception, exception);
            if (round >= options.MaxFixRounds)
                break;

            var byTree = output.GetDiagnostics(cancellation)
                .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.IsInSource)
                .GroupBy(d => d.Location.SourceTree!)
                .ToDictionary(g => g.Key, g => g.ToList());
            bool changed = false;
            foreach (var pair in trees.ToList())
            {
                var tree = output.SyntaxTrees.FirstOrDefault(t => t.FilePath == pair.Value.FilePath);
                if (tree == null || !byTree.TryGetValue(tree, out var errors))
                    continue;
                var model = output.GetSemanticModel(tree);
                var fixedTree = CSharpFixer.Fix(tree, model, errors, out int applied, cancellation);
                if (applied > 0)
                {
                    pair.Key.Fixes += applied;
                    trees[pair.Key] = fixedTree;
                    changed = true;
                }
            }
            if (!changed)
                break;
        }

        // What is left: compile errors per shader, and the SDSL each class translates back to.
        var finalDiagnostics = final!.GetDiagnostics(cancellation).Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.IsInSource).ToList();
        foreach (var pair in trees)
        {
            var result = pair.Key;
            var tree = final.SyntaxTrees.First(t => t.FilePath == pair.Value.FilePath);
            result.CSharp = tree.GetText(cancellation).ToString();
            foreach (var diagnostic in finalDiagnostics.Where(d => d.Location.SourceTree == tree))
            {
                var line = diagnostic.Location.GetLineSpan().StartLinePosition;
                var message = diagnostic.Id + " (" + (line.Line + 1) + "," + (line.Character + 1) + "): " + diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
                if (diagnostic.Id.StartsWith("CSL", StringComparison.Ordinal))
                    result.TranslationErrors.Add(message);
                else
                    result.CompileErrors.Add(message);
            }
            var classDeclaration = tree.GetRoot(cancellation).DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDeclaration == null)
                continue;
            var symbol = final.GetSemanticModel(tree).GetDeclaredSymbol(classDeclaration, cancellation);
            if (symbol == null)
                continue;
            var translated = ShaderTranslator.Translate(symbol, final, cancellation);
            foreach (var diagnostic in translated.Diagnostics)
            {
                var line = diagnostic.Location.GetLineSpan().StartLinePosition;
                var message = diagnostic.Id + " (" + (line.Line + 1) + "," + (line.Character + 1) + "): " + diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
                if (!result.TranslationErrors.Contains(message))
                    result.TranslationErrors.Add(message);
            }
            result.Sdsl = translated.Sdsl;
        }
        return results;
    }
}
