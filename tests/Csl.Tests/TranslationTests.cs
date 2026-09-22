using System.Collections.Immutable;
using Csl.Generators;
using Csl.Tests.Samples;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Csl.Tests;

public class TranslationTests
{
    [Fact]
    public void ASimpleComputeShaderReadsLikeTheSdslItCameFrom()
    {
        var sdsl = SampleSpread.SdslSource;
        Assert.StartsWith("namespace Csl.Tests.Samples\n{\n    /// One thread per brick", sdsl);
        Assert.Contains("    shader SampleSpread : ComputeShaderBase\n    {", sdsl);
        Assert.Contains("stage Texture3D<uint> ChangedBricks;", sdsl);
        Assert.Contains("stage RWTexture3D<uint> DrawnOut;", sdsl);
        Assert.Contains("/// Every brick that was active on any sub-step of this step", sdsl);
        Assert.Contains("uint Changed(int3 b)\n        {\n            if (any(b < 0) || any(b >= BrickCount))\n                return 0;\n            return ChangedBricks.Load(int4(b, 0));\n        }", sdsl);
        Assert.Contains("override void Compute()", sdsl);
        Assert.Contains("int3 b = (int3)streams.DispatchThreadId;", sdsl);
        Assert.Contains("uint active = Changed(b) | Changed(b + int3(1, 0, 0)) | Changed(b - int3(1, 0, 0))", sdsl);
        Assert.Contains("uint on = active != 0 ? 1 : 0;", sdsl);
        Assert.Contains("DrawnOut[b] = Reset != 0 ? on : (DrawnOut[b] | on);", sdsl);
        Assert.EndsWith("    };\n}\n", sdsl);
    }

    [Fact]
    public void LoopsMarkersSwizzlesAndMixinsTranslate()
    {
        var sdsl = SampleMip.SdslSource;
        Assert.Contains("shader SampleMip : ComputeShaderBase, SampleBricks", sdsl);
        Assert.Contains("[loop]\n            for (int i = 0; i < Level; i++)", sdsl);
        Assert.Contains("lo = lo * 2 - 1;", sdsl);
        Assert.Contains("if (!BoxDirty(lo, hi))", sdsl);
        Assert.Contains("[unroll]\n            for (int dz = -1; dz <= 1; dz++)", sdsl);
        Assert.Contains("float w = (dx == 0 ? 2.0 : 1.0) * (dy == 0 ? 2.0 : 1.0) * (dz == 0 ? 2.0 : 1.0);", sdsl);
        Assert.Contains("sum += Source.Load(int4(s, 0)).r * w;", sdsl);
        Assert.Contains("Target[c] = float2(sum / weight, Source.Load(int4(centre, 0)).g);", sdsl);
        Assert.Contains("float sum = 0.0;", sdsl);
    }

    [Fact]
    public void AMixinWithoutComputeBaseTranslatesToAPlainShader()
    {
        var sdsl = SampleBricks.SdslSource;
        Assert.Contains("shader SampleBricks\n    {", sdsl);
        Assert.Contains("int3 b0 = max(lo, int3(0, 0, 0)) >> 3;", sdsl);
        Assert.Contains("bool BoxDirty(int3 lo, int3 hi)", sdsl);
        Assert.Contains("return ActiveBricks.Load(int4(brick, 0)) != 0;", sdsl);
    }

    [Fact]
    public void KeysWrappersAndRegistrationComeWithTheShader()
    {
        // Keys, in the engine's shape.
        Assert.IsType<Stride.Rendering.ObjectParameterKey<Stride.Graphics.Texture>>(SampleMipKeys.Source);
        Assert.IsType<Stride.Rendering.ValueParameterKey<Stride.Core.Mathematics.Int3>>(SampleMipKeys.SourceSize);
        Assert.IsType<Stride.Rendering.ValueParameterKey<int>>(SampleMipKeys.Level);
        Assert.IsType<Stride.Rendering.ObjectParameterKey<Stride.Graphics.Texture>>(SampleBricksKeys.ActiveBricks);

        // The wrapper, on the mixin's wrapper, with the declared thread numbers.
        Assert.Equal(typeof(SampleBricksEffect), typeof(SampleMipEffect).BaseType);
        Assert.Equal(new Stride.Core.Mathematics.Int3(8, 8, 8), SampleMipEffect.DefaultThreadNumbers);
        Assert.Equal(new Stride.Core.Mathematics.Int3(4, 4, 4), SampleSpreadEffect.DefaultThreadNumbers);
        Assert.NotNull(typeof(SampleMipEffect).GetConstructor(new[] { typeof(Stride.Core.IServiceRegistry) }));
        Assert.True(SampleSpreadEffect.Slots.DrawnOut.NeedsTypedUavLoad);

        // The sources, registered when the assembly loaded.
        Assert.True(ShaderSourceRegistry.Contains("SampleMip"));
        Assert.True(ShaderSourceRegistry.Contains("SampleBricks"));
        Assert.Equal(SampleSpread.SdslSource, ShaderSourceRegistry.Sources["SampleSpread"].Source);
        Assert.Equal("SampleSpread", SampleSpread.ShaderName);
    }

    [Theory]
    [InlineData("string s = \"x\";", "CSL102")]
    [InlineData("var list = new System.Collections.Generic.List<int>();", "CSL103")]
    [InlineData("float f = System.MathF.Floor(1.5f);", "CSL107")]
    [InlineData("foreach (var i in new int[3]) { }", "CSL101")]
    [InlineData("try { } catch { }", "CSL101")]
    [InlineData("throw new System.Exception();", "CSL101")]
    [InlineData("System.Func<int, int> f = x => x;", "CSL102")]
    [InlineData("object o = null;", "CSL102")]
    public async Task ForbiddenConstructsAreReportedOnTheirLine(string statement, string expectedId)
    {
        var source = @"
using Csl;
using Csl.Engine;
using Csl.Hlsl;
using static Csl.Hlsl.Intrinsics;
namespace T
{
    [Shader]
    public partial class Bad : ComputeShaderBase
    {
        [Stage] public float A;
        public override void Compute()
        {
            " + statement + @"
        }
    }
}";
        var result = await RunOnSource(source);
        var diagnostics = result.Diagnostics.Where(d => d.Id.StartsWith("CSL")).ToArray();
        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.Id == expectedId);
        Assert.All(diagnostics, d => Assert.Equal(13, d.Location.GetLineSpan().StartLinePosition.Line));
        // No SDSL for a shader with errors.
        Assert.DoesNotContain(result.GeneratedTrees, t => t.FilePath.EndsWith("Bad.Sdsl.g.cs"));
    }

    [Fact]
    public async Task AShaderMustBePartialAndInheritShaders()
    {
        var result = await RunOnSource(@"
using Csl;
namespace T
{
    [Shader] public class NotPartial { }
    [Shader] public partial class WrongBase : System.Collections.ArrayList { }
}");
        var ids = result.Diagnostics.Select(d => d.Id).OrderBy(id => id).ToArray();
        Assert.Equal(new[] { "CSL100", "CSL108" }, ids);
    }

    [Fact]
    public async Task ACSharpShaderCannotShareItsNameWithASdslFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "CslClash_" + Guid.NewGuid().ToString("N") + ".sdsl");
        File.WriteAllText(path, "shader Twin : ComputeShaderBase { stage int A; override void Compute() { } };");
        try
        {
            var result = await RunOnSource(@"
using Csl; using Csl.Engine;
[Shader] public partial class Twin : ComputeShaderBase { [Stage] public int A; public override void Compute() { } }", path);
            Assert.Single(result.Diagnostics, d => d.Id == "CSL109");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Task<GeneratorDriverRunResult> RunOnSource(string source, params string[] sdslPaths)
    {
        var references = new List<MetadataReference>();
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator))
            references.Add(MetadataReference.CreateFromFile(path));
        references.Add(MetadataReference.CreateFromFile(typeof(ShaderAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create("Shaders", new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var texts = sdslPaths.Select(p => (AdditionalText)new TestData.FileAdditionalText(p)).ToImmutableArray();
        var driver = CSharpGeneratorDriver.Create(new[] { new ShaderEffectGenerator().AsSourceGenerator() }, additionalTexts: texts);
        return Task.FromResult(driver.RunGenerators(compilation).GetRunResult());
    }
}
