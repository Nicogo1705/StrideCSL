using System.Collections.Immutable;
using System.Text;
using Csl.Generators;
using Csl.Generators.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Csl.Demo;

/// <summary>
/// The C# shaders of a folder translated to SDSL while the app runs, as the build does: Roslyn over
/// the files, the Csl generator for the stubs the classes compile against, the translator for the
/// SDSL. The C# errors come back as the build would print them.
/// </summary>
internal sealed class LiveCompiler
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(LoadReferences);

    public LiveCompiler(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>What the gallery does with a shader: draws it as a tile, runs it over the tiles, or only lets others use it.</summary>
    public enum ShaderKind
    {
        Image,
        Compute,
        Library,
    }

    public sealed record Result(IReadOnlyList<(string ShaderName, string Sdsl, string Path, ShaderKind Kind)> Shaders, IReadOnlyList<string> Errors);

    public Result Compile(CancellationToken cancellation = default)
    {
        var trees = System.IO.Directory.GetFiles(Directory, "*.cs").Order(StringComparer.Ordinal)
            .Select(path => CSharpSyntaxTree.ParseText(ReadShared(path), ParseOptions, path, Encoding.UTF8, cancellation))
            .ToList();
        var compilation = CSharpCompilation.Create("CslDemos", trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create(new[] { new ShaderEffectGenerator().AsSourceGenerator() }, parseOptions: ParseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _, cancellation);

        var errors = generated.GetDiagnostics(cancellation).Where(d => d.Severity == DiagnosticSeverity.Error).Select(Format).ToList();
        var shaders = new List<(string, string, string, ShaderKind)>();
        foreach (var tree in trees)
        {
            var model = generated.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot(cancellation).DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration, cancellation) is not INamedTypeSymbol type
                    || !type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Csl.ShaderAttribute"))
                    continue;
                var translated = ShaderTranslator.Translate(type, generated, cancellation);
                errors.AddRange(translated.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(Format));
                if (!translated.IsExternal && translated.Sdsl != null)
                    shaders.Add((translated.ShaderName, translated.Sdsl, tree.FilePath, KindOf(type)));
            }
        }
        return new Result(shaders, errors.Distinct().ToList());
    }

    private static ShaderKind KindOf(INamedTypeSymbol type)
    {
        if (type.IsAbstract)
            return ShaderKind.Library;
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            switch (b.ToDisplayString())
            {
                case "Csl.Engine.ComputeShaderBase": return ShaderKind.Compute;
                case "Csl.Engine.ImageEffectShader": return ShaderKind.Image;
            }
        }
        return ShaderKind.Library;
    }

    /// <summary>The same, for a shader class compiled into the app.</summary>
    public static ShaderKind KindOf(Type? type)
    {
        if (type == null || type.IsAbstract)
            return ShaderKind.Library;
        if (type.IsSubclassOf(typeof(Csl.Engine.ComputeShaderBase)))
            return ShaderKind.Compute;
        return type.IsSubclassOf(typeof(Csl.Engine.ImageEffectShader)) ? ShaderKind.Image : ShaderKind.Library;
    }

    private static string Format(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var where = span.Path.Length > 0 ? $"{Path.GetFileName(span.Path)}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): " : string.Empty;
        return where + diagnostic.Id + " " + diagnostic.GetMessage();
    }

    /// <summary>An editor may still hold the file it is saving: a few tries, sharing it.</summary>
    private static string ReadShared(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Everything the app runs with, except the app: its own copy of the demos would clash with the files.</summary>
    private static ImmutableArray<MetadataReference> LoadReferences()
    {
        var self = typeof(LiveCompiler).Assembly.Location;
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return paths.Where(p => !string.Equals(p, self, StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();
    }
}
