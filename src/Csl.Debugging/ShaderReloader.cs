using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Csl.Generators;
using Csl.Generators.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Stride.Rendering;

namespace Csl.Debugging;

/// <summary>
/// The C# shaders of a folder, reloaded on save: compiled again as the build does (Roslyn, the Csl
/// generator, the translator), each shader whose SDSL changed registered again under its own name, and
/// the effect system told, as when an .sdsl file changes: the effects that use it are compiled again.
/// The CPU runs the new code too, its flat classes being made from the SDSL.
/// </summary>
[DebuggerNonUserCode]
internal sealed class ShaderReloader
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
    private static readonly FieldInfo? RecentlyModified = typeof(EffectSystem).GetField("recentlyModifiedShaders", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly FileSystemWatcher watcher;
    private readonly Dictionary<string, string> known = new(StringComparer.Ordinal);
    private readonly object gate = new();
    private DateTime? changedAt;
    private Task<List<(string Name, string Sdsl, string Path)>>? compiling;
    private ImmutableArray<MetadataReference> references;

    public ShaderReloader(string directory)
    {
        Directory = directory;
        foreach (var pair in ShaderSourceRegistry.Sources)
            known[pair.Key] = pair.Value.Source;
        watcher = new FileSystemWatcher(directory, "*.cs") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        watcher.Changed += (_, _) => Touch();
        watcher.Created += (_, _) => Touch();
        watcher.Renamed += (_, _) => Touch();
        watcher.EnableRaisingEvents = true;
    }

    public string Directory { get; }

    private void Touch()
    {
        lock (gate)
            changedAt = DateTime.UtcNow;
    }

    /// <summary>On the game's thread, each frame: starts a compilation after a save, applies it when done.</summary>
    public void Apply(EffectSystem effectSystem)
    {
        if (compiling == null)
        {
            lock (gate)
            {
                // Editors write a file in several steps: wait for them to be done.
                if (changedAt is { } at && DateTime.UtcNow - at > TimeSpan.FromMilliseconds(250))
                {
                    changedAt = null;
                    compiling = Task.Run(Compile);
                }
            }
            return;
        }
        if (!compiling.IsCompleted)
            return;
        var task = compiling;
        compiling = null;
        if (task.IsFaulted)
        {
            CslDebug.Log("reloading the C# shaders failed: " + task.Exception!.GetBaseException().Message);
            return;
        }
        var changed = task.Result.Where(s => !known.TryGetValue(s.Name, out var old) || old != s.Sdsl).ToList();
        if (changed.Count == 0)
            return;
        foreach (var (name, sdsl, path) in changed)
        {
            known[name] = sdsl;
            ShaderSourceRegistry.Add(name, sdsl, path);
        }
        // As the effect system's own file watcher does for an .sdsl: the effects using these are disposed
        // and compiled again on their next draw.
        if (RecentlyModified?.GetValue(effectSystem) is HashSet<string> modified)
        {
            lock (modified)
                foreach (var (name, _, _) in changed)
                    modified.Add(name);
        }
        FlatEffects.Reset();
        CslDebug.Log("reloaded: " + string.Join(", ", changed.Select(c => c.Name)));
    }

    private List<(string, string, string)> Compile()
    {
        if (references.IsDefault)
        {
            var paths = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var entry = Assembly.GetEntryAssembly()?.Location;
            // Everything the game runs with, but the game's own assemblies: their copy of the shaders would clash with the files.
            references = paths.Where(p => !string.Equals(p, entry, StringComparison.OrdinalIgnoreCase) && !IsGameAssembly(p))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray();
        }
        var trees = System.IO.Directory.GetFiles(Directory, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(path => CSharpSyntaxTree.ParseText(ReadShared(path), ParseOptions, path, Encoding.UTF8))
            .ToList();
        var compilation = CSharpCompilation.Create("CslReload", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create(new[] { new ShaderEffectGenerator().AsSourceGenerator() }, parseOptions: ParseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out _);
        var errors = generated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("\n", errors.Take(5)));
        var result = new List<(string, string, string)>();
        foreach (var tree in trees)
        {
            var model = generated.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type || !type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Csl.ShaderAttribute"))
                    continue;
                var translated = ShaderTranslator.Translate(type, generated, default);
                if (translated.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                    throw new InvalidOperationException(string.Join("\n", translated.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5)));
                if (!translated.IsExternal && translated.Sdsl != null)
                    result.Add((translated.ShaderName, translated.Sdsl, tree.FilePath));
            }
        }
        return result;
    }

    /// <summary>An assembly that has shader classes of its own (the game's): left out of the references.</summary>
    private static bool IsGameAssembly(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return AppDomain.CurrentDomain.GetAssemblies().Any(a => !a.IsDynamic && a.GetName().Name == name
            && a.GetReferencedAssemblies().Any(r => r.Name == "Csl.Types") && name is not ("Csl.Engine" or "Csl.Runtime" or "Csl.Debugging" or "Csl.Types"));
    }

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
}
