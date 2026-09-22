using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Stride.Rendering;
using Stride.Shaders.Compiler;
using Stride.Shaders.Compilers;

namespace Csl;

/// <summary>
/// The SDSL of the shaders written in C#, handed to the engine's effect compiler in memory: no
/// .sdsl file, no asset. Each assembly registers its shaders from a module initializer the
/// generator writes; the first <see cref="ShaderContext"/> installs them into the game's compiler,
/// and anything registered after that goes straight in.
/// </summary>
public static class ShaderSourceRegistry
{
    private static readonly object locker = new();
    private static readonly Dictionary<string, (string Source, string Path)> sources = new(StringComparer.Ordinal);
    private static ShaderSourceManager? installedOn;

    /// <summary>Registers, or replaces, the source of a shader class.</summary>
    public static void Add(string shaderName, string source, string path)
    {
        if (string.IsNullOrEmpty(shaderName)) throw new ArgumentException("A shader name is required", nameof(shaderName));
        if (source == null) throw new ArgumentNullException(nameof(source));
        lock (locker)
        {
            sources[shaderName] = (source, path);
            installedOn?.AddShaderSource(shaderName, source, path);
        }
    }

    /// <summary>The registered shaders, by SDSL name.</summary>
    public static IReadOnlyDictionary<string, (string Source, string Path)> Sources
    {
        get
        {
            lock (locker)
                return new Dictionary<string, (string Source, string Path)>(sources, StringComparer.Ordinal);
        }
    }

    public static bool Contains(string shaderName)
    {
        lock (locker)
            return sources.ContainsKey(shaderName);
    }

    /// <summary>
    /// Hands every registered source to the game's effect compiler. Called by <see cref="ShaderContext"/>;
    /// call it yourself only to use a C# shader before any ComputeEffect exists (in a material, say).
    /// </summary>
    public static void InstallInto(EffectSystem effectSystem)
    {
        if (effectSystem == null) throw new ArgumentNullException(nameof(effectSystem));
        var manager = FindSourceManager(effectSystem.Compiler);
        lock (locker)
        {
            if (ReferenceEquals(installedOn, manager))
                return;
            installedOn = manager;
            foreach (var pair in sources)
                manager.AddShaderSource(pair.Key, pair.Value.Source, pair.Value.Path);
        }
    }

    /// <summary>Writes every registered source as a .sdsl file, to read or to compile by hand.</summary>
    public static void DumpTo(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var pair in Sources)
            File.WriteAllText(Path.Combine(directory, pair.Key + ".sdsl"), pair.Value.Source);
    }

    /// <summary>
    /// The compiler's source manager. The engine wraps its EffectCompiler in a cache whose inner
    /// compiler is protected, so this walks the chain by reflection; a remote or null compiler
    /// cannot take sources, and says so.
    /// </summary>
    private static ShaderSourceManager FindSourceManager(IEffectCompiler compiler)
    {
        object? current = compiler;
        var inner = typeof(EffectCompilerChain).GetProperty("Compiler", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EffectCompilerChain.Compiler is not where this engine version keeps it; C# shaders cannot be registered");
        while (current is EffectCompilerChain chain)
            current = inner.GetValue(chain);
        if (current is EffectCompiler local)
            return local.GetFileShaderLoader().SourceManager;
        throw new InvalidOperationException($"C# shaders need the local effect compiler (Stride.Shaders.Compiler.EffectCompiler); the game uses {current?.GetType().FullName ?? "none"}");
    }
}
