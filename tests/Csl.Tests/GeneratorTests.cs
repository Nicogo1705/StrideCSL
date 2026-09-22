using Microsoft.CodeAnalysis;

namespace Csl.Tests;

public class GeneratorTests
{
    [Fact]
    public void WrapsEveryComputeShaderOfTheDemoAndTheirMixin()
    {
        var result = TestData.RunGenerator(TestData.DemoShaderPaths().ToArray());

        Assert.Empty(result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning));
        var names = result.Results.Single().GeneratedSources.Select(s => s.HintName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[]
        {
            "VoxelWaterBricksEffect.g.cs",
            "VoxelWaterComposeEffect.g.cs",
            "VoxelWaterMipEffect.g.cs",
            "VoxelWaterOccupancyBaseEffect.g.cs",
            "VoxelWaterOccupancyUpEffect.g.cs",
            "VoxelWaterSpreadEffect.g.cs",
            "VoxelWaterStepEffect.g.cs",
        }, names);
    }

    [Fact]
    public void AComputeShaderDerivesFromItsMixinWrapper()
    {
        var result = TestData.RunGenerator(TestData.DemoShaderPaths().ToArray());
        var step = TestData.GeneratedSource(result, "VoxelWaterStepEffect.g.cs");

        Assert.Contains("namespace Demo", step);
        Assert.Contains("public partial class VoxelWaterStepEffect : global::Demo.VoxelWaterBricksEffect", step);
        Assert.Contains("public const string ShaderName = \"VoxelWaterStep\";", step);
        Assert.Contains("public VoxelWaterStepEffect(IServiceRegistry services, Int3 threadNumbers) : base(services, ShaderName, threadNumbers)", step);
        Assert.Contains("public Texture? Terrain { get => _terrain; set => SetTexture(ref _terrain, global::Demo.VoxelWaterStepKeys.Terrain, value, Slots.Terrain); }", step);
        Assert.Contains("public float IsoLevel { get => _isoLevel; set { _isoLevel = value; Parameters.Set(global::Demo.VoxelWaterStepKeys.IsoLevel, value); } }", step);
        Assert.Contains("public Vector3 PourCentre", step);
        // The mixin's parameters live on the base, not here; group-shared memory is not a parameter.
        Assert.DoesNotContain("ActiveBricks", step);
        Assert.DoesNotContain("anyChanged", step);
        Assert.Contains("[global::Csl.Slot(global::Csl.ResourceAccess.Write, global::Csl.ResourceKind.Texture)]", step);
        Assert.Contains("new global::Csl.ResourceSlot(\"VoxelWaterStep\", \"AmountsOut\", global::Csl.ResourceKind.Texture, global::Csl.ResourceAccess.Write, \"RWTexture3D<float>\", \"float\")", step);
        Assert.Contains("/// Amounts and hand-overs are multiples of this", step);
    }

    [Fact]
    public void TheMixinWrapperIsAbstractAndCarriesItsParametersOnce()
    {
        var result = TestData.RunGenerator(TestData.DemoShaderPaths().ToArray());
        var bricks = TestData.GeneratedSource(result, "VoxelWaterBricksEffect.g.cs");

        Assert.Contains("public abstract partial class VoxelWaterBricksEffect : global::Csl.ComputeEffect", bricks);
        Assert.DoesNotContain("ShaderName", bricks);
        Assert.Contains("protected VoxelWaterBricksEffect(IServiceRegistry services, string shaderName, Int3 threadNumbers)", bricks);
        Assert.Contains("public Int3 SampleCount", bricks);
        Assert.Contains("global::Csl.ResourceAccess.Read, \"Texture3D<uint>\", \"uint\")", bricks);
    }

    [Fact]
    public void AReadBackThroughTheRwViewIsAReadWriteSlot()
    {
        var result = TestData.RunGenerator(TestData.DemoShaderPaths().ToArray());
        var spread = TestData.GeneratedSource(result, "VoxelWaterSpreadEffect.g.cs");

        Assert.Contains("public partial class VoxelWaterSpreadEffect : global::Csl.ComputeEffect", spread);
        Assert.Contains("[global::Csl.Slot(global::Csl.ResourceAccess.ReadWrite, global::Csl.ResourceKind.Texture)]", spread);
        Assert.Contains("\"DrawnOut\", global::Csl.ResourceKind.Texture, global::Csl.ResourceAccess.ReadWrite, \"RWTexture3D<uint>\"", spread);
        Assert.Contains("\"ActiveOut\", global::Csl.ResourceKind.Texture, global::Csl.ResourceAccess.Write", spread);
    }

    [Fact]
    public void ReportsWhatItCannotWrap()
    {
        var path = Path.Combine(Path.GetTempPath(), "CslTest_" + Guid.NewGuid().ToString("N") + ".sdsl");
        File.WriteAllText(path, @"
shader Odd : ComputeShaderBase
{
    stage float Weights[4];
    stage float3x3 Rotation;
    stage MyStruct Custom;
    stage int Count;
    override void Compute() { }
};");
        try
        {
            var result = TestData.RunGenerator(path);
            var ids = result.Diagnostics.Select(d => d.Id).OrderBy(id => id).ToArray();
            Assert.Equal(new[] { "CSL002", "CSL002", "CSL003" }, ids);
            var array = result.Diagnostics.Single(d => d.Id == "CSL003");
            Assert.Equal(4, array.Location.GetLineSpan().StartLinePosition.Line + 1);

            // No namespace: the engine files the keys under Stride.Rendering; the wrapper stays global.
            var source = TestData.GeneratedSource(result, "OddEffect.g.cs");
            Assert.Contains("global::Stride.Rendering.OddKeys.Count", source);
            Assert.DoesNotContain("namespace", source.Replace("// Generated by", string.Empty));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ADuplicateShaderNameIsReportedAndSkipped()
    {
        var a = Path.Combine(Path.GetTempPath(), "CslTestA_" + Guid.NewGuid().ToString("N") + ".sdsl");
        var b = Path.Combine(Path.GetTempPath(), "CslTestB_" + Guid.NewGuid().ToString("N") + ".sdsl");
        File.WriteAllText(a, "shader Twice : ComputeShaderBase { stage int A; override void Compute() { } };");
        File.WriteAllText(b, "shader Twice : ComputeShaderBase { stage int B; override void Compute() { } };");
        try
        {
            var result = TestData.RunGenerator(a, b);
            Assert.Single(result.Diagnostics, d => d.Id == "CSL004");
            Assert.Single(result.Results.Single().GeneratedSources);
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public void GeneratedCodeCompilesAgainstTheEngine()
    {
        // The wrappers of the demo's shaders are part of this very assembly, generated at its build.
        var step = typeof(Demo.VoxelWaterStepEffect);
        Assert.Equal(typeof(Demo.VoxelWaterBricksEffect), step.BaseType);
        Assert.Equal(typeof(ComputeEffect), typeof(Demo.VoxelWaterBricksEffect).BaseType);
        Assert.True(typeof(Demo.VoxelWaterBricksEffect).IsAbstract);
        Assert.Equal(typeof(float), step.GetProperty("IsoLevel")!.PropertyType);
        Assert.Equal(typeof(Stride.Core.Mathematics.Int3), step.GetProperty("SampleCount")!.PropertyType);
        Assert.Equal(typeof(Stride.Graphics.Texture), step.GetProperty("Terrain")!.PropertyType);

        Assert.True(Demo.VoxelWaterSpreadEffect.Slots.DrawnOut.NeedsTypedUavLoad);
        Assert.False(Demo.VoxelWaterSpreadEffect.Slots.ActiveOut.NeedsTypedUavLoad);
        Assert.Equal(Stride.Graphics.TextureFlags.ShaderResource | Stride.Graphics.TextureFlags.UnorderedAccess, Demo.VoxelWaterSpreadEffect.Slots.DrawnOut.TextureFlags);
        Assert.Equal(Stride.Graphics.TextureFlags.ShaderResource, Demo.VoxelWaterStepEffect.Slots.Terrain.TextureFlags);
        Assert.Equal(Stride.Graphics.TextureFlags.UnorderedAccess, Demo.VoxelWaterStepEffect.Slots.AmountsOut.TextureFlags);
    }
}
