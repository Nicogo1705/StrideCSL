using System.Collections.Immutable;
using Csl.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Csl.Tests;

public class AnalyzerTests
{
    private static async Task<ImmutableArray<Diagnostic>> Analyze(string body)
    {
        var source = @"
using Stride.Core.Mathematics;
using Stride.Graphics;
using Csl;
class Usage
{
    void Allocate(GraphicsDevice device)
    {
        " + body + @"
    }
}";
        var references = new List<MetadataReference>();
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
            references.Add(MetadataReference.CreateFromFile(path));
        foreach (var assembly in new[] { typeof(ComputeEffect).Assembly, typeof(AnalyzerTests).Assembly, typeof(Stride.Graphics.Texture).Assembly,
                     typeof(Stride.Core.Mathematics.Int3).Assembly, typeof(Stride.Core.IServiceRegistry).Assembly, typeof(Stride.Rendering.ParameterCollection).Assembly })
            references.Add(MetadataReference.CreateFromFile(assembly.Location));

        var compilation = CSharpCompilation.Create("Usage", new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.True(errors.Length == 0, string.Join("\n", errors.Select(e => e.ToString())));

        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new TypedUavFormatAnalyzer()));
        return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
    }

    [Fact]
    public async Task RejectsAHalfTextureOnASlotReadThroughItsRwView()
    {
        var diagnostics = await Analyze("Textures.New3D<Half>(device, new Int3(8), Demo.VoxelWaterSpreadEffect.Slots.DrawnOut);");
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("CSL010", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("DrawnOut", diagnostic.GetMessage());
        Assert.Contains("Half", diagnostic.GetMessage());
    }

    [Fact]
    public async Task AcceptsAUintTextureThere()
    {
        var diagnostics = await Analyze("Textures.New3D<uint>(device, new Int3(8), Demo.VoxelWaterSpreadEffect.Slots.DrawnOut, Demo.VoxelWaterSpreadEffect.Slots.ActiveOut);");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task AcceptsAHalfTextureOnSlotsThatOnlyReadOrOnlyWrite()
    {
        var diagnostics = await Analyze("Textures.New3D<Half>(device, new Int3(8), Demo.VoxelWaterStepEffect.Slots.Amounts, Demo.VoxelWaterStepEffect.Slots.AmountsOut);");
        Assert.Empty(diagnostics);
    }
}
