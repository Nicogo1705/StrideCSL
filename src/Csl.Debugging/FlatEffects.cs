using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using Csl.Cpu;
using Csl.Generators.Conversion;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using Stride.Core.Diagnostics;
using Stride.Graphics;
using Stride.Shaders;
using Stride.Shaders.Compilers;
using Stride.Shaders.Compilers.SDSL;

namespace Csl.Debugging;

/// <summary>An effect as the CPU runs it: its vertex and pixel stages as C# classes, and what the engine's mixer reflected of it.</summary>
public sealed class FlatEffect
{
    internal FlatEffect(StageProgram vertex, StageProgram pixel, EffectReflection reflection, string directory, IReadOnlyList<(string Buffer, string Field, int Offset)> constants)
    {
        Constants = constants;
        Vertex = vertex;
        Pixel = pixel;
        Reflection = reflection;
        Directory = directory;
    }

    public StageProgram Vertex { get; }
    public StageProgram Pixel { get; }

    /// <summary>The constant buffers and resources the mixer laid out: how the draw's data reaches the classes' members.</summary>
    public EffectReflection Reflection { get; }

    /// <summary>Both stages' constant buffer members, by buffer and byte offset: where the reflection's members land.</summary>
    public IReadOnlyList<(string Buffer, string Field, int Offset)> Constants { get; }

    /// <summary>Where the stages' C# files are, the ones the debugger opens.</summary>
    public string Directory { get; }
}

/// <summary>
/// A mixed effect made C# the CPU runs: the engine's mixer composes it (mixins, base calls,
/// compositions, generics, stage members: nothing reimplemented), SPIRV-Cross writes each stage of the
/// SPIR-V as HLSL, <see cref="FlatHlsl"/> and the converter make it C#, Roslyn compiles it with a PDB
/// on files the debugger opens. Cached by effect.
/// </summary>
[DebuggerNonUserCode]
public static class FlatEffects
{
    private static readonly ConcurrentDictionary<string, FlatEffect> Cache = new(StringComparer.Ordinal);
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var result = paths.Where(p => Path.GetFileName(p).StartsWith("System", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll" or "Microsoft.CSharp.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        result.Add(MetadataReference.CreateFromFile(typeof(ShaderAttribute).Assembly.Location));
        return result.ToImmutableArray();
    });
    private static int generation;

    /// <summary>The effect for a mixin as the game compiled it, Direct3D 11's macros added as the effect compiler adds them.</summary>
    public static FlatEffect Compile(ShaderMixinSource mixin, ShaderLoaderBase loader)
    {
        var source = new ShaderMixinSource();
        source.DeepCloneFrom(mixin);
        AddPlatformMacros(source);
        var key = source.ToString() + "|" + string.Join(";", source.Macros.Select(m => m.Name + "=" + m.Definition));
        return Cache.GetOrAdd(key, _ => Build(source, loader));
    }

    /// <summary>Forgets what was compiled: after a shader changed.</summary>
    public static void Reset() => Cache.Clear();

    private static void AddPlatformMacros(ShaderMixinSource mixin)
    {
        void Add(string name, object value)
        {
            if (!mixin.Macros.Any(m => m.Name == name))
                mixin.AddMacro(name, value);
        }
        Add("STRIDE_GRAPHICS_API_DIRECT3D", 1);
        Add("STRIDE_GRAPHICS_API_DIRECT3D11", 1);
        Add("STRIDE_GRAPHICS_PROFILE", (int)GraphicsProfile.Level_11_0);
        Add("GRAPHICS_PROFILE_LEVEL_9_1", (int)GraphicsProfile.Level_9_1);
        Add("GRAPHICS_PROFILE_LEVEL_9_2", (int)GraphicsProfile.Level_9_2);
        Add("GRAPHICS_PROFILE_LEVEL_9_3", (int)GraphicsProfile.Level_9_3);
        Add("GRAPHICS_PROFILE_LEVEL_10_0", (int)GraphicsProfile.Level_10_0);
        Add("GRAPHICS_PROFILE_LEVEL_10_1", (int)GraphicsProfile.Level_10_1);
        Add("GRAPHICS_PROFILE_LEVEL_11_0", (int)GraphicsProfile.Level_11_0);
        Add("GRAPHICS_PROFILE_LEVEL_11_1", (int)GraphicsProfile.Level_11_1);
        Add("GRAPHICS_PROFILE_LEVEL_11_2", (int)GraphicsProfile.Level_11_2);
        Add("class", "shader");
    }

    private static FlatEffect Build(ShaderMixinSource mixin, ShaderLoaderBase loader)
    {
        var log = new LoggerResult();
        if (!new ShaderMixer(loader).MergeSDSL(mixin, new ShaderMixer.Options(true), log, out var bytecode, out var reflection, out _, out _))
            throw new InvalidOperationException("The engine could not mix the effect: " + string.Join("\n", log.Messages.Where(m => m.Type >= LogMessageType.Error)));
        var words = MemoryMarshal.Cast<byte, uint>(bytecode).ToArray();
        var translator = new SpirvTranslator(words.AsMemory());
        int id = Interlocked.Increment(ref generation);
        var directory = Path.Combine(Path.GetTempPath(), "csl-debug", $"{Environment.ProcessId}-{id}");
        System.IO.Directory.CreateDirectory(directory);

        var flats = new Dictionary<ExecutionModel, FlatHlsl>();
        foreach (var entry in translator.GetEntryPoints())
        {
            if (entry.ExecutionModel is not (ExecutionModel.Vertex or ExecutionModel.Fragment))
                continue;
            var hlsl = translator.Translate(Backend.Hlsl, entry);
            var name = (entry.ExecutionModel == ExecutionModel.Vertex ? "Vertex" : "Pixel") + "Stage" + id;
            File.WriteAllText(Path.Combine(directory, name + ".hlsl"), hlsl);
            flats[entry.ExecutionModel] = FlatHlsl.From(hlsl, name);
        }
        if (!flats.ContainsKey(ExecutionModel.Vertex) || !flats.ContainsKey(ExecutionModel.Fragment))
            throw new NotSupportedException("Only effects with a vertex and a pixel stage run on the CPU");

        var options = new ShaderConverterOptions();
        options.References.AddRange(References.Value);
        options.CSharp.Namespace = "Csl.Debugging.Flat" + id;
        var converted = ShaderConverter.Convert(flats.Values.Select(f => (Path.Combine(directory, f.ClassName + ".sdsl"), f.Sdsl)).ToList(), options);
        var errors = converted.SelectMany(c => c.ConversionErrors.Concat(c.CompileErrors).Select(e => c.Name + ": " + e)).ToList();
        if (errors.Count > 0)
            throw new NotSupportedException("The effect does not convert to C# yet:\n" + string.Join("\n", errors.Take(10)));

        // The C# on disk, compiled with a PDB that points at it: the debugger opens these files.
        var trees = new List<SyntaxTree>();
        foreach (var result in converted)
        {
            var path = Path.Combine(directory, result.Name + ".cs");
            // Concrete: nothing in a flat stage is abstract, and a run instantiates it as it is.
            File.WriteAllText(path, result.CSharp!.Replace("public abstract partial class ", "public partial class "), Encoding.UTF8);
            using var stream = File.OpenRead(path);
            trees.Add(CSharpSyntaxTree.ParseText(Microsoft.CodeAnalysis.Text.SourceText.From(stream, Encoding.UTF8, Microsoft.CodeAnalysis.Text.SourceHashAlgorithm.Sha256),
                new CSharpParseOptions(LanguageVersion.Preview), path));
        }
        var compilation = CSharpCompilation.Create("CslFlat" + id, trees, References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug, nullableContextOptions: NullableContextOptions.Disable));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var emitted = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!emitted.Success)
            throw new InvalidOperationException("The flat C# does not compile:\n" + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(10)));
        pe.Position = 0;
        pdb.Position = 0;
        var assembly = new AssemblyLoadContext("CslFlat" + id, isCollectible: true).LoadFromStream(pe, pdb);

        StageProgram Program(FlatHlsl flat) => new(assembly.GetType(options.CSharp.Namespace + "." + flat.ClassName)!, flat.EntryPoint, flat.Inputs, flat.Outputs);
        var constants = flats.Values.SelectMany(f => f.Constants).Distinct().ToList();
        return new FlatEffect(Program(flats[ExecutionModel.Vertex]), Program(flats[ExecutionModel.Fragment]), reflection, directory, constants);
    }
}
