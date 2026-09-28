using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;

namespace Csl.Debugging;

/// <summary>
/// The scene's colour as the draws left it, before the post effects (tone mapping, bloom) turn it into
/// the picture: the GPU's value of a pixel to set beside the CPU's. A forwarding implementation of the
/// renderer's <see cref="IPostProcessingEffects"/>, emitted (its Draw takes a span, which a
/// DispatchProxy cannot carry), that hands the inputs to <see cref="Hook"/> first.
/// </summary>
[DebuggerNonUserCode]
public static class PostEffectsTap
{
    public delegate void InputsHook(RenderDrawContext context, Span<Texture> inputs);

    /// <summary>Called with the post effects' inputs (the colour first) before they draw.</summary>
    public static InputsHook? Hook { get; set; }

    /// <summary>Called by the emitted wrapper.</summary>
    public static void BeforeDraw(RenderDrawContext context, Span<Texture> inputs) => Hook?.Invoke(context, inputs);

    private static Type? wrapper;

    public static bool IsWrapped(IPostProcessingEffects effects) => effects.GetType() == wrapper;

    public static IPostProcessingEffects Wrap(IPostProcessingEffects inner)
    {
        wrapper ??= Build();
        return (IPostProcessingEffects)Activator.CreateInstance(wrapper, inner)!;
    }

    private static Type Build()
    {
        var face = typeof(IPostProcessingEffects);
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Csl.PostEffectsTap"), AssemblyBuilderAccess.Run).DefineDynamicModule("Csl.PostEffectsTap");
        var type = module.DefineType("Csl.PostEffectsTapped", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        var interfaces = new[] { face }.Concat(face.GetInterfaces()).ToArray();
        foreach (var i in interfaces)
            type.AddInterfaceImplementation(i);
        var inner = type.DefineField("inner", face, FieldAttributes.Private | FieldAttributes.InitOnly);

        var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [face]);
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, inner);
        il.Emit(OpCodes.Ret);

        var draw = face.GetMethod(nameof(IPostProcessingEffects.Draw))!;
        var before = typeof(PostEffectsTap).GetMethod(nameof(BeforeDraw))!;
        foreach (var i in interfaces)
        foreach (var method in i.GetMethods())
        {
            if (method.IsStatic || !method.IsAbstract)
                continue;
            var parameters = method.GetParameters();
            var forward = type.DefineMethod(i.FullName + "." + method.Name,
                MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                method.ReturnType, parameters.Select(p => p.ParameterType).ToArray());
            il = forward.GetILGenerator();
            if (method == draw)
            {
                // Draw(drawContext, outputValidator, inputs, inputDepthStencil, outputTarget)
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Call, before);
            }
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, inner);
            for (int p = 1; p <= parameters.Length; p++)
                il.Emit(OpCodes.Ldarg, p);
            il.Emit(OpCodes.Callvirt, method);
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(forward, method);
        }
        return type.CreateType();
    }
}
