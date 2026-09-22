using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Csl.Generators.CSharp;
using Csl.Generators.Sdsl;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Csl.Generators;

/// <summary>
/// One <c>&lt;Shader&gt;Effect</c> class per compute shader of the project, from the .sdsl files the
/// Stride SDK already passes as AdditionalFiles and from the [Shader] classes written in C#. A shader
/// that other compute shaders inherit gets an abstract wrapper carrying its parameters, so they are
/// set the same way from every pass. A C# shader also gets its SDSL (registered with the effect
/// compiler at start-up, and kept as <c>SdslSource</c> on the class) and its *Keys class.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ShaderEffectGenerator : IIncrementalGenerator
{
    private const string ComputeShaderBase = "ComputeShaderBase";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var files = context.AdditionalTextsProvider
            .Where(static text => text.Path.EndsWith(".sdsl", StringComparison.OrdinalIgnoreCase))
            .Select(static (text, cancellation) => (text.Path, Text: text.GetText(cancellation)?.ToString() ?? string.Empty))
            .Collect();
        var shaders = context.SyntaxProvider
            .ForAttributeWithMetadataName("Csl.ShaderAttribute",
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, cancellation) => ShaderTranslator.Translate((INamedTypeSymbol)syntaxContext.TargetSymbol, syntaxContext.SemanticModel.Compilation, cancellation))
            .Collect();
        context.RegisterSourceOutput(files.Combine(shaders), static (production, inputs) => Generate(production, inputs.Left, inputs.Right));
    }

    private static void Generate(SourceProductionContext production, ImmutableArray<(string Path, string Text)> inputs, ImmutableArray<TranslatedShader> translated)
    {
        var files = new List<SdslFile>();
        foreach (var (path, text) in inputs.OrderBy(i => i.Path, StringComparer.Ordinal))
        {
            production.CancellationToken.ThrowIfCancellationRequested();
            var file = SdslParser.Parse(path, text);
            files.Add(file);
            foreach (var error in file.Errors)
                production.ReportDiagnostic(Diagnostic.Create(Diagnostics.SdslParseProblem, Diagnostics.FileLocation(path, error.Line, error.Column), error.Message));
        }

        // Shaders written in C#: their SDSL is parsed like a file, so keys and wrappers come out the same way.
        var fromCSharp = new Dictionary<string, TranslatedShader>(StringComparer.Ordinal);
        foreach (var shader in translated.OrderBy(t => t.Path, StringComparer.Ordinal).ThenBy(t => t.ClassName, StringComparer.Ordinal))
        {
            production.CancellationToken.ThrowIfCancellationRequested();
            foreach (var diagnostic in shader.Diagnostics)
                production.ReportDiagnostic(diagnostic);
            if (shader.MixinStubs != null)
                production.AddSource(shader.ClassName + ".Mixins.g.cs", shader.MixinStubs);
            if (shader.IsExternal || shader.Sdsl == null)
                continue;
            var existing = files.SelectMany(f => f.Shaders, (f, s) => (File: f, Shader: s)).FirstOrDefault(e => e.Shader.Name == shader.ShaderName);
            if (existing.File != null)
            {
                production.ReportDiagnostic(Diagnostic.Create(Diagnostics.ShaderNameClash, Diagnostics.FileLocation(shader.Path, 1, 1), shader.ShaderName, existing.File.Path));
                continue;
            }
            var file = SdslParser.Parse(shader.Path, shader.Sdsl);
            foreach (var error in file.Errors)
                production.ReportDiagnostic(Diagnostic.Create(Diagnostics.SdslParseProblem, Diagnostics.FileLocation(shader.Path, error.Line, error.Column), "generated SDSL: " + error.Message));
            foreach (var parsed in file.Shaders)
                parsed.DefaultThreads = shader.NumThreads;
            files.Add(file);
            fromCSharp[shader.ShaderName] = shader;
            var keysNamespace = shader.Namespace ?? "Stride.Rendering";
            foreach (var parsed in file.Shaders)
                production.AddSource(parsed.Name + "Keys.g.cs", KeysEmitter.Emit(parsed, keysNamespace));
            production.AddSource(shader.ClassName + ".Sdsl.g.cs", SdslSourceEmitter.EmitPartial(shader));
        }
        if (fromCSharp.Count > 0)
            production.AddSource("CslShaderSources.g.cs", SdslSourceEmitter.EmitRegistration(fromCSharp.Values.OrderBy(s => s.ShaderName, StringComparer.Ordinal)));

        var shaders = new Dictionary<string, (SdslShader Shader, SdslFile File)>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var shader in file.Shaders)
            {
                if (shaders.TryGetValue(shader.Name, out var first))
                {
                    production.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicateShader, Diagnostics.FileLocation(file.Path, shader.Line, 1), shader.Name, first.File.Path));
                    continue;
                }
                shaders[shader.Name] = (shader, file);
            }
        }

        // A shader is a compute shader when its inheritance reaches ComputeShaderBase; a mixin gets a
        // wrapper when a compute shader inherits it, directly or not.
        var isCompute = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in shaders.Keys)
            isCompute[name] = ReachesComputeBase(name, shaders, new HashSet<string>(StringComparer.Ordinal));
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in shaders.Keys)
        {
            if (!isCompute[name] || shaders[name].Shader.GenericParameters.Count > 0)
                continue;
            emitted.Add(name);
            foreach (var mixin in ProjectAncestors(name, shaders))
                if (shaders[mixin].Shader.GenericParameters.Count == 0)
                    emitted.Add(mixin);
        }

        foreach (var name in emitted.OrderBy(n => n, StringComparer.Ordinal))
        {
            production.CancellationToken.ThrowIfCancellationRequested();
            var model = BuildModel(name, shaders, isCompute, emitted, production);
            production.AddSource(model.ClassName + ".g.cs", WrapperEmitter.Emit(model));
        }
    }

    private static bool ReachesComputeBase(string name, Dictionary<string, (SdslShader Shader, SdslFile File)> shaders, HashSet<string> visiting)
    {
        if (!visiting.Add(name) || !shaders.TryGetValue(name, out var entry))
            return false;
        foreach (var b in entry.Shader.Bases)
        {
            if (b == ComputeShaderBase || ReachesComputeBase(b, shaders, visiting))
                return true;
        }
        return false;
    }

    /// <summary>Every project shader above this one, nearest first, each once.</summary>
    private static List<string> ProjectAncestors(string name, Dictionary<string, (SdslShader Shader, SdslFile File)> shaders)
    {
        var result = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(name);
        var seen = new HashSet<string>(StringComparer.Ordinal) { name };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!shaders.TryGetValue(current, out var entry))
                continue;
            foreach (var b in entry.Shader.Bases)
            {
                if (shaders.ContainsKey(b) && seen.Add(b))
                {
                    result.Add(b);
                    queue.Enqueue(b);
                }
            }
        }
        return result;
    }

    private static WrapperModel BuildModel(string name, Dictionary<string, (SdslShader Shader, SdslFile File)> shaders,
        Dictionary<string, bool> isCompute, HashSet<string> emitted, SourceProductionContext production)
    {
        var (shader, file) = shaders[name];
        var model = new WrapperModel(name, file.Namespace, file.Namespace ?? "Stride.Rendering") { IsCompute = isCompute[name], DefaultThreads = shader.DefaultThreads };
        model.Doc.AddRange(shader.Doc);

        // The C# base: the first base with a wrapper. Its ancestors come with it; every other project
        // ancestor is flattened into this class so nothing is lost to single inheritance.
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in shader.Bases)
        {
            if (emitted.Contains(b))
            {
                model.BaseClassName = b + "Effect";
                model.BaseClassNamespace = shaders[b].File.Namespace;
                model.BaseIsCompute = isCompute[b];
                covered.Add(b);
                foreach (var ancestor in ProjectAncestors(b, shaders))
                    covered.Add(ancestor);
                model.BaseHasSlots = covered.Any(c => shaders[c].Shader.Members.Any(IsWrappedResource));
                break;
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        AddProperties(model, shader, file, names, production);
        foreach (var ancestor in ProjectAncestors(name, shaders))
        {
            if (covered.Contains(ancestor))
                continue;
            var (mixin, mixinFile) = shaders[ancestor];
            AddProperties(model, mixin, mixinFile, names, production);
        }
        return model;
    }

    private static bool IsWrappedResource(SdslMember member) =>
        !member.IsStream && !member.IsCompose && !member.IsConst && !member.IsStatic && !member.IsGroupShared && !member.IsArray
        && SdslTypes.IsResource(member.Type);

    private static void AddProperties(WrapperModel model, SdslShader shader, SdslFile file, HashSet<string> names, SourceProductionContext production)
    {
        var keysType = (file.Namespace ?? "Stride.Rendering") + "." + shader.Name + "Keys";
        foreach (var member in shader.Members)
        {
            if (member.IsStream || member.IsCompose || member.IsConst || member.IsStatic || member.IsGroupShared)
                continue;
            if (!names.Add(member.Name))
                continue;
            if (member.IsArray)
            {
                production.ReportDiagnostic(Diagnostic.Create(Diagnostics.ArrayParameterNotWrapped, Diagnostics.FileLocation(file.Path, member.Line, member.Column), member.Name, shader.Name));
                continue;
            }
            var csharpType = SdslTypes.ToCSharp(member);
            if (csharpType == null)
            {
                var typeText = member.GenericArgument == null ? member.Type : member.Type + "<" + member.GenericArgument + ">";
                production.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnmappedMemberType, Diagnostics.FileLocation(file.Path, member.Line, member.Column), member.Name, shader.Name, typeText));
                continue;
            }
            var property = new PropertyModel(member.Name, csharpType, keysType);
            property.Doc.AddRange(member.Doc);
            var kind = SdslTypes.ResourceKind(member.Type);
            if (kind != SdslResourceKind.None)
            {
                shader.Usage.TryGetValue(member.Name, out var usage);
                // Not writable through this declaration: whatever the body does, the view is a read.
                if (!SdslTypes.IsReadWriteType(member.Type))
                    usage = SdslAccess.Read;
                else if (usage == SdslAccess.None)
                    usage = SdslAccess.Write;
                var hlsl = member.GenericArgument == null ? member.Type : member.Type + "<" + member.GenericArgument + ">";
                property.Slot = new SlotModel(shader.Name, hlsl, member.GenericArgument, kind, usage);
            }
            model.Properties.Add(property);
        }
    }
}
