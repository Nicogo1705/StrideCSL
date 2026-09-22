using Csl.Tests.Samples;
using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Storage;
using Stride.Shaders;
using Stride.Shaders.Compilers;
using Stride.Shaders.Compilers.SDSL;

namespace Csl.Tests;

/// <summary>
/// The generated SDSL through the engine's own shader compiler (parse, mix, SPIR-V), on the CPU:
/// what the effect compiler does before handing the code to the graphics API.
/// </summary>
public class SdslCompileTests
{
    /// <summary>Serves shaders from memory: the samples, and the engine's ComputeShaderBase from the test data.</summary>
    private sealed class MemoryShaderLoader : ShaderLoaderBase
    {
        private readonly Dictionary<string, string> sources;

        public MemoryShaderLoader(Dictionary<string, string> sources, string cacheDirectory)
            : base(new FileShaderCache(new FileSystemProvider("/csl-test-cache-" + Path.GetFileName(cacheDirectory), cacheDirectory), "shaders"))
        {
            this.sources = sources;
        }

        protected override bool ExternalFileExists(string name) => sources.ContainsKey(name);

        public override bool LoadExternalFileContent(string name, out string filename, out string code, out ObjectId hash)
        {
            code = sources[name];
            filename = name + ".sdsl";
            hash = ObjectId.FromBytes(System.Text.Encoding.UTF8.GetBytes(code));
            return true;
        }
    }

    public static IEnumerable<object[]> Shaders() => new[]
    {
        new object[] { "SampleSpread", SampleSpread.SdslSource, 4 },
        new object[] { "SampleMip", SampleMip.SdslSource, 8 },
        new object[] { "VoxelWaterSpread", Demo.VoxelWaterSpread.SdslSource, 4 },
        new object[] { "VoxelWaterStep", Demo.VoxelWaterStep.SdslSource, 8 },
        new object[] { "VoxelWaterCompose", Demo.VoxelWaterCompose.SdslSource, 8 },
        new object[] { "VoxelWaterMip", Demo.VoxelWaterMip.SdslSource, 8 },
        new object[] { "VoxelWaterOccupancyBase", Demo.VoxelWaterOccupancyBase.SdslSource, 4 },
        new object[] { "VoxelWaterOccupancyUp", Demo.VoxelWaterOccupancyUp.SdslSource, 4 },
    };

    [Theory]
    [MemberData(nameof(Shaders))]
    public void GeneratedSdslCompilesWithTheEngine(string name, string sdsl, int threads)
    {
        // Every C# shader of this assembly, as registered when it loaded, plus the engine's base.
        var sources = ShaderSourceRegistry.Sources.ToDictionary(p => p.Key, p => p.Value.Source, StringComparer.Ordinal);
        sources["ComputeShaderBase"] = File.ReadAllText(TestData.DataPath("ComputeShaderBase"));
        sources[name] = sdsl;
        var cache = Path.Combine(Path.GetTempPath(), "csl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        var loader = new MemoryShaderLoader(sources, cache);

        // What ComputeEffectShader's effect mixes: the base, then the shader, with the thread numbers as macros.
        var mixin = new ShaderMixinSource { Name = name };
        mixin.Mixins.Add(new ShaderClassSource("ComputeShaderBase"));
        mixin.Mixins.Add(new ShaderClassSource(name));
        mixin.AddMacro("ThreadNumberX", threads);
        mixin.AddMacro("ThreadNumberY", threads);
        mixin.AddMacro("ThreadNumberZ", threads);
        mixin.AddMacro("STRIDE_GRAPHICS_API_DIRECT3D", 1);
        mixin.AddMacro("STRIDE_GRAPHICS_API_DIRECT3D11", 1);
        mixin.AddMacro("class", "shader");

        var log = new LoggerResult();
        var ok = new ShaderMixer(loader).MergeSDSL(mixin, new ShaderMixer.Options(false), log, out var bytecode, out var reflection, out _, out var entryPoints);
        var messages = string.Join("\n", log.Messages.Select(m => m.ToString()));
        Assert.True(ok, messages);
        Assert.True(bytecode.Length > 0, "no SPIR-V");
        Assert.NotEmpty(entryPoints!);
        // Every parameter is in the reflection, under the key names the engine's generator would give.
        Assert.Contains(reflection!.ResourceBindings, b => b.KeyInfo.KeyName.StartsWith(name + ".") || b.KeyInfo.KeyName.StartsWith("VoxelWaterBricks.") || b.KeyInfo.KeyName.StartsWith("SampleBricks."));
    }
}
