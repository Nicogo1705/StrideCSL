using System.Diagnostics;
using System.Reflection;
using Csl.Cpu;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Shaders;

namespace Csl.Debugging;

/// <summary>
/// A flat effect's classes given what a draw had: each constant buffer member and resource the mixer
/// reflected, by its key, onto the member of its raw name (the name SPIRV-Cross writes in the HLSL).
/// </summary>
[DebuggerNonUserCode]
public static class FlatBinding
{
    /// <summary>From a parameter collection (an EffectInstance's, an image effect's): values by key name. Returns the names nothing was found for.</summary>
    public static List<string> Apply(FlatEffect effect, CpuMeshDraw draw, ParameterCollection parameters, CommandList commandList)
    {
        var infos = parameters.ParameterKeyInfos.ToDictionary(i => i.Key.Name, StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var buffer in effect.Reflection.ConstantBuffers)
        {
            foreach (var member in buffer.Members)
            {
                if (!infos.TryGetValue(member.KeyInfo.KeyName, out var info) || !info.IsValueParameter)
                {
                    missing.Add(member.KeyInfo.KeyName);
                    continue;
                }
                foreach (var field in FieldsAt(effect, buffer.Name, member.Offset))
                    Set(draw, field, type => CpuCapture.ValueOf(parameters.DataValues, info.Offset, info.Count, type));
            }
        }
        foreach (var binding in effect.Reflection.ResourceBindings)
        {
            if (!infos.TryGetValue(binding.KeyInfo.KeyName, out var info) || !info.IsResourceParameter)
            {
                // A sampler the shader describes itself (sampler_state): the reflection has its description.
                if (effect.Reflection.SamplerStates.FirstOrDefault(s => s.KeyName == binding.KeyInfo.KeyName) is { } sampler)
                {
                    var description = CpuCapture.Describe(sampler.Description);
                    Set(draw, binding.RawName, type => type == typeof(Csl.Types.SamplerComparisonState) ? new Csl.Types.SamplerComparisonState(description) : new Csl.Types.SamplerState(description));
                    continue;
                }
                missing.Add(binding.KeyInfo.KeyName);
                continue;
            }
            var value = parameters.ObjectValues[info.BindingSlot];
            Set(draw, binding.RawName, type => CpuCapture.ResourceOf(value, type, commandList));
        }
        return missing;
    }

    /// <summary>The flat members at that offset of that constant buffer (SPIRV-Cross names them after the buffer's instance, not the key).</summary>
    public static IEnumerable<string> FieldsAt(FlatEffect effect, string buffer, int offset)
        => effect.Constants.Where(c => c.Buffer == buffer && c.Offset == offset).Select(c => c.Field).Distinct();

    /// <summary>The member of that name on both stages' shaders, the value converted to its type.</summary>
    internal static void Set(CpuMeshDraw draw, string name, Func<Type, object?> value)
    {
        foreach (var shader in new[] { draw.VertexShader, draw.Shader }.Distinct())
        {
            if (!Members.InstanceFields(shader.GetType()).TryGetValue(name, out var field))
                continue;
            if (value(field.FieldType) is { } converted)
                field.SetValue(shader, converted);
        }
    }

    /// <summary>What the flat classes have that the reflection names, and what they have that it does not: to see why a value is missing.</summary>
    public static string Describe(FlatEffect effect, CpuMeshDraw draw)
    {
        var fields = Members.InstanceFields(draw.Shader.GetType()).Keys.Concat(Members.InstanceFields(draw.VertexShader.GetType()).Keys).ToHashSet(StringComparer.Ordinal);
        var named = effect.Reflection.ConstantBuffers.SelectMany(b => b.Members.Select(m => (Buffer: b.Name, RawName: FieldsAt(effect, b.Name, m.Offset).FirstOrDefault() ?? m.RawName, KeyName: m.KeyInfo.KeyName)))
            .Concat(effect.Reflection.ResourceBindings.Select(r => (Buffer: "resource", RawName: r.RawName, KeyName: r.KeyInfo.KeyName))).ToList();
        return string.Join("\n", named.Select(n => $"  {(fields.Contains(n.RawName) ? "ok " : "-- ")}{n.Buffer}: {n.RawName} <- {n.KeyName}"));
    }
}
