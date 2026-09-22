using System;
using System.Collections.Generic;
using Csl.Generators.Sdsl.Syntax;

namespace Csl.Generators.Conversion;

/// <summary>
/// Every shader the conversion knows by name: those being converted and those they build on (the
/// engine's, typically). The C# of a shader depends on its bases: whether a method overrides one of
/// the C# base class or one of a mixin, which name is a shader, which is a struct.
/// </summary>
public sealed class SdslShaderIndex
{
    private readonly Dictionary<string, SdslShaderDeclaration> shaders = new Dictionary<string, SdslShaderDeclaration>(StringComparer.Ordinal);
    private readonly Dictionary<string, SdslShaderDeclaration> structOwners = new Dictionary<string, SdslShaderDeclaration>(StringComparer.Ordinal);

    public IEnumerable<SdslShaderDeclaration> Shaders => shaders.Values;

    /// <summary>Adds the shaders of a file; the first declaration of a name wins.</summary>
    public void Add(SdslCompilationUnit unit)
    {
        foreach (var shader in unit.Shaders())
        {
            if (shaders.ContainsKey(shader.Name))
                continue;
            shaders[shader.Name] = shader;
            foreach (var member in shader.Members)
            {
                if (member is SdslStruct declaration && !structOwners.ContainsKey(declaration.Name))
                    structOwners[declaration.Name] = shader;
            }
        }
    }

    public bool Contains(string name) => shaders.ContainsKey(name);

    public SdslShaderDeclaration? Find(string name) => shaders.TryGetValue(name, out var shader) ? shader : null;

    /// <summary>The shader declaring a struct of this name, or null.</summary>
    public SdslShaderDeclaration? StructOwner(string name) => structOwners.TryGetValue(name, out var shader) ? shader : null;

    /// <summary>The C# base chain: the first base, its first base, and so on.</summary>
    public IEnumerable<SdslShaderDeclaration> CSharpBaseChain(SdslShaderDeclaration shader)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { shader.Name };
        var current = shader;
        while (current.Bases.Count > 0)
        {
            var next = Find(current.Bases[0].Name);
            if (next == null || !seen.Add(next.Name))
                yield break;
            yield return next;
            current = next;
        }
    }

    /// <summary>Every shader above this one, through all bases, each once, nearest first.</summary>
    public List<SdslShaderDeclaration> AllBases(SdslShaderDeclaration shader)
    {
        var result = new List<SdslShaderDeclaration>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { shader.Name };
        var queue = new Queue<SdslShaderDeclaration>();
        queue.Enqueue(shader);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var reference in current.Bases)
            {
                var found = Find(reference.Name);
                if (found != null && seen.Add(found.Name))
                {
                    result.Add(found);
                    queue.Enqueue(found);
                }
            }
        }
        return result;
    }

    /// <summary>A method of the C# base chain with this name and parameter count: C# can override it.</summary>
    public SdslMethod? FindInCSharpBases(SdslShaderDeclaration shader, string name, int parameterCount)
    {
        foreach (var ancestor in CSharpBaseChain(shader))
        {
            foreach (var member in ancestor.Members)
            {
                if (member is SdslMethod method && method.Name == name && method.Parameters.Count == parameterCount && !method.Has("static"))
                    return method;
            }
        }
        return null;
    }

    /// <summary>A variable of the shader or any base, by name.</summary>
    public SdslVariable? FindVariable(SdslShaderDeclaration shader, string name)
    {
        foreach (var member in shader.Members)
            if (member is SdslVariable variable && variable.Name == name)
                return variable;
        foreach (var ancestor in AllBases(shader))
            foreach (var member in ancestor.Members)
                if (member is SdslVariable variable && variable.Name == name)
                    return variable;
        return null;
    }
}
