using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.SPIRV;
using Silk.NET.SPIRV.Cross;
using Stride.Shaders.Compilers;
using Stride.Shaders.Spirv.Tools;

namespace Csl.TestApp;

/// <summary>
/// What the effect compiler does after the SPIR-V for Direct3D 11, on the CPU: legalize the module,
/// translate each entry point to HLSL with SPIRV-Cross, compile it with d3dcompiler_47 (fxc).
/// </summary>
internal static unsafe class D3D11Compiler
{
    private static readonly D3DCompiler Api = D3DCompiler.GetApi();

    /// <summary>The fxc errors for each entry point, empty when all compile.</summary>
    public static string Compile(byte[] spirv)
    {
        var errors = new StringBuilder();
        var legalized = SpirvTools.LegalizeForHlsl(MemoryMarshal.Cast<byte, uint>(spirv.AsSpan()));
        var translator = new SpirvTranslator(legalized.AsMemory());
        foreach (var entryPoint in translator.GetEntryPoints())
        {
            var hlsl = translator.Translate(Backend.Hlsl, entryPoint);
            var profile = entryPoint.ExecutionModel switch
            {
                ExecutionModel.Vertex => "vs_5_0",
                ExecutionModel.Fragment => "ps_5_0",
                ExecutionModel.GLCompute => "cs_5_0",
                ExecutionModel.Geometry => "gs_5_0",
                ExecutionModel.TessellationControl => "hs_5_0",
                ExecutionModel.TessellationEvaluation => "ds_5_0",
                _ => throw new NotSupportedException(entryPoint.ExecutionModel.ToString()),
            };
            var source = Encoding.UTF8.GetBytes(hlsl);
            ID3D10Blob* code = null;
            ID3D10Blob* messages = null;
            int result;
            fixed (byte* text = source)
                result = Api.Compile(text, (nuint)source.Length, (byte*)null, null, null, entryPoint.TranslatedName, profile, 0, 0, &code, &messages);
            if (result < 0)
            {
                errors.Append(profile).Append(": ");
                errors.AppendLine(messages != null ? Marshal.PtrToStringAnsi((IntPtr)messages->GetBufferPointer(), (int)messages->GetBufferSize()) : $"HRESULT 0x{result:X8}");
            }
            if (code != null) code->Release();
            if (messages != null) messages->Release();
        }
        return errors.ToString();
    }
}
