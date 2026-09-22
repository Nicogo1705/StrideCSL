using Stride.Core.Diagnostics;
using Stride.Core.IO;
using Stride.Core.Storage;
using Stride.Shaders;
using Stride.Shaders.Compilers;
using Stride.Shaders.Compilers.SDSL;

namespace Csl.TestApp;

/// <summary>
/// The engine's SDSL compiler (parse, mix, SPIR-V) over shaders served from memory, on the CPU: what
/// the effect compiler does before handing the code to the graphics API.
/// </summary>
internal sealed class EngineCompiler
{
    private static readonly object ProviderLock = new();
    private readonly Dictionary<string, string> sources;
    private readonly string cacheDirectory;

    public EngineCompiler(Dictionary<string, string> sources)
    {
        this.sources = sources;
        cacheDirectory = Path.Combine(Path.GetTempPath(), "csl-testapp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);
    }

    /// <summary>Macros every compilation gets: the graphics API, and thread numbers for compute shaders.</summary>
    public static readonly (string Name, object Value)[] DefaultMacros =
    {
        ("STRIDE_GRAPHICS_API_DIRECT3D", 1),
        ("STRIDE_GRAPHICS_API_DIRECT3D11", 1),
        ("STRIDE_GRAPHICS_PROFILE", 0xb000),
        ("GRAPHICS_PROFILE_LEVEL_9_1", 0x9100),
        ("GRAPHICS_PROFILE_LEVEL_9_2", 0x9200),
        ("GRAPHICS_PROFILE_LEVEL_9_3", 0x9300),
        ("GRAPHICS_PROFILE_LEVEL_10_0", 0xa000),
        ("GRAPHICS_PROFILE_LEVEL_10_1", 0xa100),
        ("GRAPHICS_PROFILE_LEVEL_11_0", 0xb000),
        ("GRAPHICS_PROFILE_LEVEL_11_1", 0xb100),
        ("GRAPHICS_PROFILE_LEVEL_11_2", 0xb200),
        ("ThreadNumberX", 8),
        ("ThreadNumberY", 8),
        ("ThreadNumberZ", 1),
        ("class", "shader"),
    };

    public sealed class Result
    {
        public bool Success;
        public byte[] Bytecode = Array.Empty<byte>();
        public string Messages = string.Empty;
    }

    /// <summary>Mixes the shaders in order (the first one alone, usually) and compiles them.</summary>
    public Result Compile(IEnumerable<string> mixins, IEnumerable<(string Name, object Value)>? macros = null)
    {
        var mixin = new ShaderMixinSource { Name = "CslTest" };
        foreach (var name in mixins)
            mixin.Mixins.Add(ClassSource(name));
        foreach (var (name, value) in macros ?? DefaultMacros)
            mixin.AddMacro(name, value);
        return Compile(mixin);
    }

    /// <summary>Compiles a whole mixin: its classes, compositions and macros.</summary>
    public Result Compile(ShaderMixinSource mixin)
    {
        MemoryShaderLoader loader;
        // Each loader registers a file provider with the engine's virtual file system, which is not thread-safe.
        lock (ProviderLock)
            loader = new MemoryShaderLoader(sources, cacheDirectory);
        var log = new LoggerResult();
        var result = new Result();
        try
        {
            result.Success = new ShaderMixer(loader).MergeSDSL(mixin, new ShaderMixer.Options(false), log, out var bytecode, out _, out _, out _);
            result.Bytecode = bytecode.ToArray();
        }
        catch (Exception e)
        {
            result.Success = false;
            log.Error(e.GetType().Name + ": " + e.Message);
        }
        result.Messages = string.Join("\n", log.Messages.Where(m => m.Type >= LogMessageType.Warning).Select(m => m.ToString()));
        if (result.Success && result.Bytecode.Length == 0)
            result.Success = false;
        return result;
    }

    /// <summary>"Name" or "Name&lt;a, b&gt;" as a class source with its generic arguments.</summary>
    private static ShaderClassSource ClassSource(string text)
    {
        int open = text.IndexOf('<');
        if (open < 0)
            return new ShaderClassSource(text);
        var arguments = new List<string>();
        int depth = 0, start = open + 1;
        for (int i = open + 1; i < text.Length - 1; i++)
        {
            if (text[i] == '(' || text[i] == '<') depth++;
            else if (text[i] == ')' || text[i] == '>') depth--;
            else if (text[i] == ',' && depth == 0)
            {
                arguments.Add(text.Substring(start, i - start).Trim());
                start = i + 1;
            }
        }
        arguments.Add(text.Substring(start, text.Length - 1 - start).Trim());
        return new ShaderClassSource(text.Substring(0, open), arguments.ToArray());
    }

    private sealed class MemoryShaderLoader : ShaderLoaderBase
    {
        private readonly Dictionary<string, string> sources;

        public MemoryShaderLoader(Dictionary<string, string> sources, string cacheDirectory)
            : base(new FileShaderCache(new FileSystemProvider("/csl-cache-" + Path.GetFileName(cacheDirectory), cacheDirectory), "shaders"))
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
}
