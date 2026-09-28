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
        // The documentation comes as written, XML included: SDSL uses the same comments.
        Assert.StartsWith("namespace Csl.Tests.Samples\n{\n    /// <summary>\n    /// One thread per brick", sdsl);
        Assert.Contains("    shader SampleSpread : ComputeShaderBase\n    {", sdsl);
        Assert.Contains("stage Texture3D<uint> ChangedBricks;", sdsl);
        Assert.Contains("stage RWTexture3D<uint> DrawnOut;", sdsl);
        Assert.Contains("/// Every brick that was active on any sub-step of this step", sdsl);
        Assert.Contains("uint Changed(int3 b)\n        {\n            if (any(b < 0) || any(b >= BrickCount))\n                return 0;\n            return ChangedBricks.Load(int4(b, 0));\n        }", sdsl);
        Assert.Contains("override void Compute()", sdsl);
        Assert.Contains("int3 b = (int3)streams.DispatchThreadId;", sdsl);
        Assert.Contains("uint active = Changed(b) | Changed(b + int3(1, 0, 0)) | Changed(b - int3(1, 0, 0))", sdsl);
        // 0u: the engine's 4.4 parser rejects a suffix after a leading 0, a cast keeps the type.
        Assert.Contains("uint on = active != 0 ? 1u : (uint)0;", sdsl);
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
using Csl.Types;
using static Csl.Types.Intrinsics;
namespace T
{
    [Shader, NumThreads(64)]
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

    private const string CheckedShader = @"
using Csl;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;
namespace T
{
    [Shader, NumThreads(8, 8, 1)]
    public partial class Checked : ComputeShaderBase
    {
        [Stage] public RWTexture2D<float4> Output;
        [Stage] public float Scale;
        [Stage] public float2 Offset;
        [Stage] public Texture2D<float4> Input;
        [Stage] public SamplerState Sampler;
        [GroupShared] public static float[] Shared;
        public static float Counter;
        MEMBERS
        public override void Compute()
        {
            uint2 p = streams.DispatchThreadId.xy;
            BODY
        }
    }
}";

    private static async Task<string[]> CheckIds(string body, string members = "")
    {
        var result = await RunOnSource(CheckedShader.Replace("BODY", body).Replace("MEMBERS", members));
        return result.Diagnostics.Where(d => d.Id.StartsWith("CSL11")).Select(d => d.Id).ToArray();
    }

    [Theory]
    [InlineData("float sample = 1.0f;", "CSL110")]
    [InlineData("float point = 1.0f;", "CSL110")]
    [InlineData("float half = 1.0f;", "CSL110")]
    [InlineData("float float2 = 1.0f;", "CSL110")]
    [InlineData("float Texture2D = 1.0f;", "CSL110")]
    [InlineData("float @while = 1.0f;", "CSL110")]
    [InlineData("float rgroup = 1.0f;", "CSL110")]
    [InlineData("float @base = 1.0f;", "CSL110")]
    [InlineData("Scale = 2.0f;", "CSL112")]
    [InlineData("Scale += 2.0f;", "CSL112")]
    [InlineData("Scale++;", "CSL112")]
    [InlineData("Offset.x = 2.0f;", "CSL112")]
    [InlineData("this.Scale = 2.0f;", "CSL112")]
    [InlineData("float d = ddx((float)p.x);", "CSL113")]
    [InlineData("discard();", "CSL113")]
    [InlineData("float4 c = Input.Sample(Sampler, new float2(0.5f, 0.5f));", "CSL113")]
    [InlineData("for (int k = 0; k < 4; k++) { if (k == 2) return; }", "CSL114")]
    [InlineData("float3 v = new float3(p.x / 4, 0.0f, 0.0f);", "CSL118")]
    public async Task EachCheckFiresOnItsCase(string body, string expectedId)
    {
        Assert.Contains(expectedId, await CheckIds(body));
    }

    [Theory]
    [InlineData("float distance = 1.0f; float length = 2.0f; float stage = 3.0f; float mixin = 4.0f;")]
    [InlineData("float @params = 1.0f; float @object = 2.0f; float @checked = 3.0f;")]
    [InlineData("float local = Scale; local = 2.0f; local += 1.0f;")]
    [InlineData("Output[p] = new float4(Scale, 0.0f, 0.0f, 1.0f);")]
    [InlineData("Counter = 1.0f; Shared[0] = 1.0f;")]
    [InlineData("float4 c = Input.SampleLevel(Sampler, new float2(0.5f, 0.5f), 0.0f);")]
    [InlineData("int found = 0; for (int k = 0; k < 4; k++) { if (k == 2) { found = k; break; } }")]
    [InlineData("uint q = p.x / 4; float3 v = new float3(q, 0.0f, 0.0f); float3 w = new float3(p.x / 4.0f, 0.0f, 0.0f);")]
    public async Task LegitimateCodeRaisesNoCheck(string body)
    {
        Assert.Empty(await CheckIds(body));
    }

    [Fact]
    public async Task RecursionIsReportedWithItsCycle()
    {
        var result = await RunOnSource(CheckedShader
            .Replace("BODY", "float f = A(1.0f);")
            .Replace("MEMBERS", "public float A(float x) { return x <= 0.0f ? 0.0f : B(x - 1.0f); } public float B(float x) { return A(x); } public float C(float x) { return x; }"));
        var recursion = result.Diagnostics.Where(d => d.Id == "CSL111").Select(d => d.GetMessage()).OrderBy(m => m).ToArray();
        Assert.Equal(2, recursion.Length);
        Assert.Contains("A → B → A", recursion[0]);
        Assert.Contains("B → A → B", recursion[1]);
    }

    [Fact]
    public async Task CallingTheOverriddenMethodIsNotRecursion()
    {
        var result = await RunOnSource(@"
using Csl; using Csl.Engine; using Csl.Types; using static Csl.Types.Intrinsics;
[Shader]
public partial class Brighter : ImageEffectShader
{
    [Stage]
    public override float4 Shading()
    {
        return Sdsl.Base(this).Shading() * 2.0f + base.Shading();
    }
}");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CSL111");
    }

    [Fact]
    public async Task DerivativesAreFineOutsideComputeShaders()
    {
        var result = await RunOnSource(@"
using Csl; using Csl.Engine; using Csl.Types; using static Csl.Types.Intrinsics;
[Shader]
public partial class Pixel : ImageEffectShader
{
    [Stage]
    public override float4 Shading()
    {
        float2 uv = streams.TexCoord;
        if (uv.x > 2.0f) discard();
        return Texture0.Sample(PointSampler, uv) + ddx(uv.x);
    }
}");
        Assert.DoesNotContain(result.Diagnostics, d => d.Id.StartsWith("CSL11"));
    }

    [Theory]
    [InlineData("NumThreads(64, 64, 1)", "CSL115")]
    [InlineData("NumThreads(1, 1, 128)", "CSL115")]
    [InlineData("NumThreads(0, 1, 1)", "CSL115")]
    [InlineData("NumThreads(32, 32, 1)", null)]
    public async Task ThreadGroupsStayWithinDirect3DLimits(string threads, string? expectedId)
    {
        var ids = (await RunOnSource(CheckedShader.Replace("NumThreads(8, 8, 1)", threads).Replace("BODY", "").Replace("MEMBERS", "")))
            .Diagnostics.Where(d => d.Id.StartsWith("CSL11")).Select(d => d.Id).ToArray();
        if (expectedId == null)
            Assert.Empty(ids);
        else
            Assert.Equal(new[] { expectedId }, ids);
    }

    [Fact]
    public async Task AComputeShaderWithoutNumThreadsIsNoted()
    {
        var result = await RunOnSource(CheckedShader.Replace("Shader, NumThreads(8, 8, 1)", "Shader").Replace("BODY", "").Replace("MEMBERS", ""));
        var note = Assert.Single(result.Diagnostics, d => d.Id == "CSL116");
        Assert.Equal(DiagnosticSeverity.Info, note.Severity);
    }

    [Theory]
    [InlineData("[Shader]", "CSL117")]
    [InlineData("[Shader(Replaces = true)]", null)]
    public async Task ReplacingAnEngineShaderIsSaidOutLoud(string attribute, string? expectedId)
    {
        var result = await RunOnSource(@"
using Csl; using Csl.Types;
" + attribute + @"
public abstract partial class LuminanceUtils
{
    public static float Luma(float3 color) { return color.x; }
}");
        var ids = result.Diagnostics.Where(d => d.Id.StartsWith("CSL11")).Select(d => d.Id).ToArray();
        Assert.Equal(expectedId == null ? Array.Empty<string>() : new[] { expectedId }, ids);
    }

    [Fact]
    public async Task ACheckErrorKeepsTheShaderFromBeingEmitted()
    {
        var result = await RunOnSource(CheckedShader.Replace("BODY", "Scale = 2.0f;").Replace("MEMBERS", ""));
        Assert.DoesNotContain(result.GeneratedTrees, t => t.FilePath.EndsWith("Checked.Sdsl.g.cs"));
        var warned = await RunOnSource(CheckedShader.Replace("BODY", "float3 v = new float3(p.x / 4, 0.0f, 0.0f);").Replace("MEMBERS", ""));
        Assert.Contains(warned.GeneratedTrees, t => t.FilePath.EndsWith("Checked.Sdsl.g.cs"));
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
