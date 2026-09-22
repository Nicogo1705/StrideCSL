using System.Collections.Immutable;
using Csl.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Csl.Tests;

internal static class TestData
{
    public static string ShaderPath(string name) => Path.Combine(AppContext.BaseDirectory, "Shaders", name + ".sdsl");
    public static string DataPath(string name) => Path.Combine(AppContext.BaseDirectory, "Data", name + ".sdsl");

    public static string Shader(string name) => File.ReadAllText(ShaderPath(name));

    public static IEnumerable<string> DemoShaderPaths() => Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Shaders"), "*.sdsl").OrderBy(p => p, StringComparer.Ordinal);

    /// <summary>Runs the wrapper generator over these .sdsl files with no C# at all.</summary>
    public static GeneratorDriverRunResult RunGenerator(params string[] paths)
    {
        var compilation = CSharpCompilation.Create("Shaders", references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        var texts = paths.Select(p => (AdditionalText)new FileAdditionalText(p)).ToImmutableArray();
        var driver = CSharpGeneratorDriver.Create(new[] { new ShaderEffectGenerator().AsSourceGenerator() }, additionalTexts: texts);
        return driver.RunGenerators(compilation).GetRunResult();
    }

    public static string GeneratedSource(GeneratorDriverRunResult result, string hintName)
    {
        var source = result.Results.Single().GeneratedSources.SingleOrDefault(s => s.HintName == hintName);
        Assert.True(source.HintName != null, $"No generated source named {hintName}; got: {string.Join(", ", result.Results.Single().GeneratedSources.Select(s => s.HintName))}");
        return source.SourceText.ToString();
    }

    internal sealed class FileAdditionalText : AdditionalText
    {
        public FileAdditionalText(string path) => Path = path;
        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(File.ReadAllText(Path));
    }
}
