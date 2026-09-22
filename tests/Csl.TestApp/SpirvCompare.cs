using Op = Stride.Shaders.Spirv.Specification.Op;

namespace Csl.TestApp;

/// <summary>
/// SPIR-V modules compared for what they compute: the debug instructions (the source text, file
/// names, line numbers, the source hashes of the shader cache) are left out, the rest must match word
/// for word.
/// </summary>
internal static class SpirvCompare
{
    private static readonly HashSet<int> DebugOps = new()
    {
        (int)Op.OpSourceContinued, (int)Op.OpSource, (int)Op.OpSourceExtension, (int)Op.OpString,
        (int)Op.OpLine, (int)Op.OpNoLine, (int)Op.OpModuleProcessed, (int)Op.OpSourceHashSDSL,
    };

    public static bool SameCode(byte[] a, byte[] b) => Strip(a).AsSpan().SequenceEqual(Strip(b));

    /// <summary>The words of the module without its header and debug instructions.</summary>
    public static uint[] Strip(byte[] bytecode)
    {
        var words = new uint[bytecode.Length / 4];
        Buffer.BlockCopy(bytecode, 0, words, 0, words.Length * 4);
        var result = new List<uint>(words.Length);
        int i = 5;
        while (i < words.Length)
        {
            var count = (int)(words[i] >> 16);
            var op = (int)(words[i] & 0xFFFF);
            if (count == 0)
                break;
            if (!DebugOps.Contains(op))
                for (int k = 0; k < count && i + k < words.Length; k++)
                    result.Add(words[i + k]);
            i += count;
        }
        return result.ToArray();
    }
}
