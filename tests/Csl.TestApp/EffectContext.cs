using System.Text;
using System.Text.RegularExpressions;
using Csl.Generators.Conversion;
using Csl.Generators.Sdsl.Syntax;
using Stride.Shaders;

namespace Csl.TestApp;

/// <summary>
/// What an effect gives a shader for it to compile, made up for a shader tested on its own: generic
/// arguments, compositions filled with stubs, stubs for the abstract methods nothing implements,
/// streams written by a vertex shader when the shader reads streams nothing writes. The stubs are
/// SDSL added to the sources; the same context compiles the original and the round trip.
/// </summary>
internal sealed class EffectContext
{
    private readonly SdslShaderIndex index;
    private readonly SdslShaderDeclaration shader;

    public EffectContext(SdslShaderIndex index, SdslShaderDeclaration shader)
    {
        this.index = index;
        this.shader = shader;
    }

    /// <summary>Stub shaders by name, to add to the sources.</summary>
    public Dictionary<string, string> Stubs { get; } = new(StringComparer.Ordinal);

    public List<string> Hosts { get; } = new();
    public List<string> After { get; } = new();
    public string[] GenericArguments { get; private set; } = Array.Empty<string>();
    private readonly Dictionary<string, ShaderSource> compositions = new(StringComparer.Ordinal);
    private readonly List<(string Name, object Value)> macros = new();

    /// <summary>What was made up, for the report.</summary>
    public List<string> Notes { get; } = new();

    /// <summary>For each MemberName parameter, the values to try, from how the shader uses it.</summary>
    private List<string>[] memberNameCandidates = Array.Empty<List<string>>();
    private int[] memberNameChoice = Array.Empty<int>();

    public ShaderMixinSource Mixin()
    {
        var mixin = new ShaderMixinSource { Name = "CslTest" };
        foreach (var host in Hosts)
            mixin.Mixins.Add(new ShaderClassSource(host));
        mixin.Mixins.Add(GenericArguments.Length == 0 ? new ShaderClassSource(shader.Name) : new ShaderClassSource(shader.Name, GenericArguments));
        foreach (var after in After)
            mixin.Mixins.Add(new ShaderClassSource(after));
        foreach (var pair in compositions)
            mixin.Compositions[pair.Key] = pair.Value;
        foreach (var (name, value) in EngineCompiler.DefaultMacros.Concat(macros))
            mixin.AddMacro(name, value);
        return mixin;
    }

    /// <summary>
    /// Compiles the original with more and more context until it compiles or nothing more can be
    /// added. The sources get the stubs.
    /// </summary>
    public EngineCompiler.Result Settle(Dictionary<string, string> sources)
    {
        memberNameCandidates = shader.GenericParameters.Select(p => p.Type == "MemberName" ? MemberNameValues(p.Name) : new List<string>()).ToArray();
        memberNameChoice = new int[shader.GenericParameters.Count];
        GenericArguments = shader.GenericParameters.Select((p, i) => SampleArgument(p.Type, i)).ToArray();
        if (index.AllBases(shader).Any(b => b.Name == "TessellationBase"))
        {
            // As MaterialTessellationBaseFeature mixes them: after a flat tessellation, 12 control points.
            Hosts.Add("ShaderBase");
            if (shader.Name != "TessellationFlat")
                Hosts.Add("TessellationFlat");
            macros.Add(("InputControlPointCount", 12));
            Notes.Add("tessellation context");
        }
        FillCompositions();
        StubAbstractMethods();
        EngineCompiler.Result result = null!;
        for (int attempt = 0; attempt < 80; attempt++)
        {
            result = SettleOnce(sources);
            if (result.Success || !NextMemberNames())
                break;
        }
        if (memberNameCandidates.Any(c => c.Count > 0))
            Notes.Add("MemberName " + string.Join(", ", GenericArguments.Where((_, i) => memberNameCandidates[i].Count > 0)));
        return result;
    }

    private EngineCompiler.Result SettleOnce(Dictionary<string, string> sources)
    {
        EngineCompiler.Result result = null!;
        var tried = new HashSet<string>(StringComparer.Ordinal);
        for (int round = 0; round < 12; round++)
        {
            foreach (var stub in Stubs)
                sources[stub.Key] = stub.Value;
            result = new EngineCompiler(sources).Compile(Mixin());
            if (result.Success || !tried.Add(result.Messages))
                break;
            if (!Adjust(result.Messages))
                break;
        }
        return result;
    }

    /// <summary>The next combination of MemberName values, odometer-wise; false when all were tried.</summary>
    private bool NextMemberNames()
    {
        for (int i = 0; i < memberNameChoice.Length; i++)
        {
            if (memberNameCandidates[i].Count == 0)
                continue;
            if (++memberNameChoice[i] < memberNameCandidates[i].Count)
            {
                GenericArguments = shader.GenericParameters.Select((p, k) => SampleArgument(p.Type, k)).ToArray();
                return true;
            }
            memberNameChoice[i] = 0;
        }
        return false;
    }

    /// <summary>
    /// What a MemberName parameter can be, from its uses in the bodies: a stream (streams.T, input[i].T),
    /// a swizzle (value.T), or a member named on its own (T).
    /// </summary>
    private List<string> MemberNameValues(string parameter)
    {
        bool stream = false, swizzle = false, named = false;
        foreach (var method in shader.Members.OfType<SdslMethod>())
        {
            if (method.Body == null)
                continue;
            foreach (var expression in SdslWalker.Expressions(method.Body))
            {
                if (expression is SdslMemberAccess access && access.Name == parameter)
                {
                    if (access.Target is SdslIdentifier { Name: "streams" } || access.Target is SdslIndexer)
                        stream = true;
                    else
                        swizzle = true;
                }
                else if (expression is SdslIdentifier identifier && identifier.Name == parameter)
                    named = true;
            }
        }
        var values = new List<string>();
        if (swizzle)
            values.AddRange(new[] { "rgba", "r" });
        if (stream)
        {
            // The streams the shader sees, float4 first.
            var streams = new[] { shader }.Concat(index.AllBases(shader))
                .SelectMany(s => s.Members.OfType<SdslVariable>()).Where(v => v.Has("stream") && v.ArraySizes.Count == 0)
                .OrderBy(v => v.Type.Name == "float4" ? 0 : 1).Select(v => v.Name).Distinct().Take(40);
            values.AddRange(streams);
        }
        if (named)
            values.AddRange(new[] { "Transformation.WorldViewProjection", "Transformation.World" });
        if (values.Count == 0)
            values.Add("rgba");
        return values;
    }

    /// <summary>One change for one error; false when the error is not one context can fix.</summary>
    private bool Adjust(string messages)
    {
        if (messages.Contains("At least a pixel or compute shader is expected") && Hosts.Count == 0)
        {
            Hosts.Add(ReachesCompute() ? "ComputeShaderBase" : "ShaderBase");
            Notes.Add("hosted after " + Hosts[0]);
            return true;
        }
        var stream = Regex.Match(messages, @"Stream '(\w+)' \(read by '\w+'\) is expected as a vertex shader input");
        if (stream.Success && WriteStream(stream.Groups[1].Value))
            return true;
        return false;
    }

    private bool ReachesCompute() => index.AllBases(shader).Any(b => b.Name == "ComputeShaderBase");

    private string SampleArgument(string kind, int parameter) => kind switch
    {
        "LinkType" => "CslTest.Key",
        "Semantic" => "TEXCOORD0",
        "MemberName" => memberNameCandidates[parameter][memberNameChoice[parameter]],
        "int" or "uint" => "2",
        "float" or "half" or "double" => "1.0",
        "bool" => "true",
        "Texture2D" or "Texture3D" or "TextureCube" => "Texturing.Texture0",
        "SamplerState" => "Texturing.Sampler",
        _ => kind.StartsWith("float", StringComparison.Ordinal) ? kind + "(1.0)" : "0",
    };

    // -- compositions ------------------------------------------------------------------------------

    private void FillCompositions()
    {
        foreach (var owner in new[] { shader }.Concat(index.AllBases(shader)))
        {
            foreach (var member in owner.Members.OfType<SdslVariable>().Where(v => v.Has("compose")))
            {
                if (compositions.ContainsKey(member.Name))
                    continue;
                var stub = Implementation(member.Type.Name);
                if (stub == null)
                    continue;
                compositions[member.Name] = member.ArraySizes.Count > 0
                    ? new ShaderArraySource { new ShaderClassSource(stub) }
                    : new ShaderClassSource(stub);
                Notes.Add("composition " + member.Name + " = " + stub);
            }
        }
    }

    /// <summary>A stub implementing an interface shader: every abstract method returns zero.</summary>
    private string? Implementation(string interfaceName)
    {
        var declaration = index.Find(interfaceName);
        if (declaration == null || declaration.GenericParameters.Count > 0)
            return null;
        var name = "CslStub_" + interfaceName;
        if (!Stubs.ContainsKey(name))
            Stubs[name] = StubShader(name, interfaceName, Unimplemented(declaration, new[] { declaration }.Concat(index.AllBases(declaration))));
        return name;
    }

    // -- abstract methods --------------------------------------------------------------------------

    private void StubAbstractMethods()
    {
        var chain = new[] { shader }.Concat(index.AllBases(shader)).ToList();
        var missing = Unimplemented(shader, chain);
        if (missing.Count == 0)
            return;
        // One stub per declaring base, the base named as the shader names it (generic arguments included).
        foreach (var group in missing.GroupBy(m => m.Owner))
        {
            var reference = ReferenceTo(group.Key.Name);
            if (reference == null)
                continue;
            var name = "CslStub_" + shader.Name + "_" + group.Key.Name;
            Stubs[name] = StubShader(name, reference, group.ToList());
            After.Add(name);
            Notes.Add("abstract " + string.Join(", ", group.Select(m => m.Method.Name)) + " stubbed");
        }
    }

    /// <summary>The abstract methods of the chain no shader of it implements.</summary>
    private List<(SdslShaderDeclaration Owner, SdslMethod Method)> Unimplemented(SdslShaderDeclaration root, IEnumerable<SdslShaderDeclaration> chain)
    {
        var all = chain.SelectMany(s => s.Members.OfType<SdslMethod>().Select(m => (Owner: s, Method: m))).ToList();
        return all.Where(a => a.Method.Has("abstract")
                && !all.Any(o => !o.Method.Has("abstract") && o.Method.Name == a.Method.Name && o.Method.Parameters.Count == a.Method.Parameters.Count))
            .GroupBy(a => a.Method.Name + "/" + a.Method.Parameters.Count).Select(g => g.First()).ToList();
    }

    /// <summary>How the tested shader's bases name this shader, its generic parameters replaced by the sample arguments.</summary>
    private string? ReferenceTo(string name)
    {
        foreach (var owner in new[] { shader }.Concat(index.AllBases(shader)))
        {
            var reference = owner.Bases.FirstOrDefault(b => b.Name == name);
            if (reference == null)
                continue;
            var text = reference.Text;
            if (owner == shader)
                for (int i = 0; i < shader.GenericParameters.Count; i++)
                    text = Regex.Replace(text, @"\b" + Regex.Escape(shader.GenericParameters[i].Name) + @"\b", GenericArguments[i]);
            // Deeper, usable when the arguments are fixed (none is a generic parameter of that base).
            bool fixedArguments = reference.GenericArguments.All(a => !owner.GenericParameters.Any(p => Regex.IsMatch(a, @"\b" + Regex.Escape(p.Name) + @"\b")));
            return owner == shader || fixedArguments ? text : null;
        }
        return null;
    }

    private static string StubShader(string name, string baseText, List<(SdslShaderDeclaration Owner, SdslMethod Method)> methods)
    {
        var sb = new StringBuilder();
        sb.Append("shader ").Append(name).Append(" : ").Append(baseText).AppendLine();
        sb.AppendLine("{");
        foreach (var (_, method) in methods)
        {
            sb.Append("    ");
            if (method.Has("stage")) sb.Append("stage ");
            sb.Append("override ").Append(method.ReturnType).Append(' ').Append(method.Name).Append('(');
            sb.Append(string.Join(", ", method.Parameters.Select(p =>
                string.Concat(p.Modifiers.Select(m => m + " ")) + p.Type + " " + p.Name + string.Concat(p.ArraySizes.Select(s => "[" + s + "]"))
                + (p.Semantic != null ? " : " + p.Semantic : string.Empty))));
            sb.Append(") { ");
            foreach (var parameter in method.Parameters.Where(p => p.Modifiers.Contains("out") && p.ArraySizes.Count == 0))
                sb.Append(parameter.Name).Append(" = (").Append(parameter.Type).Append(")0; ");
            if (method.ReturnType.Name != "void")
                sb.Append("return (").Append(method.ReturnType).Append(")0; ");
            sb.AppendLine("}");
        }
        sb.AppendLine("};");
        return sb.ToString();
    }

    // -- streams -----------------------------------------------------------------------------------

    /// <summary>A vertex shader writing a stream the shader reads and nothing writes.</summary>
    private bool WriteStream(string stream)
    {
        var name = "CslStub_Write_" + stream;
        if (Stubs.ContainsKey(name))
            return false;
        var declarer = new[] { shader }.Concat(index.AllBases(shader)).FirstOrDefault(s => s.Members.OfType<SdslVariable>().Any(v => v.Name == stream && v.Has("stream")));
        if (declarer == null)
            return false;
        var variable = declarer.Members.OfType<SdslVariable>().First(v => v.Name == stream);
        var declarerText = ReferenceTo(declarer.Name) ?? (declarer.GenericParameters.Count == 0 ? declarer.Name : null);
        if (declarerText == null)
            return false;
        Stubs[name] = "shader " + name + " : ShaderBase, " + declarerText + "\n{\n    stage override void VSMain() { base.VSMain(); streams." + stream + " = (" + variable.Type + ")0; }\n};\n";
        if (!Hosts.Contains("ShaderBase"))
            Hosts.Insert(0, "ShaderBase");
        After.Add(name);
        Notes.Add("stream " + stream + " written");
        return true;
    }
}
