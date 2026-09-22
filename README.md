# StrideCSL: Stride shaders in C#, both ways

C#SL ("CSL") lets Stride shaders be written, read, extended and modified as C#. A `partial class`
marked `[Shader]` is translated to SDSL at build time by a Roslyn source generator and reaches the
effect compiler in memory; an `.sdsl` shader, the engine's included, is converted to such a class by
`csl convert`. The C# is typed against HLSL types (`float3`, `Texture2D<T>`, `float4x4`…), so the
compiler checks shader code, and the engine's own shaders exist as typed C# classes (`Csl.Engine`)
to inherit, mix in and call.

The two directions are checked against each other on the whole engine: every engine shader is
converted SDSL → C# → SDSL and both versions go through the engine's SDSL compiler; the SPIR-V is
compared instruction for instruction (see [Validation](#validation)).

Built against Stride 4.4 (`StrideVersion` in `Directory.Build.props`, the published 4.4.0-beta8;
`-p:StrideUseDevPackages=true` picks the packages a local Stride checkout packs).

## Projects

| Project | Target | Role |
|---------|--------|------|
| `src/Csl.Generators` | netstandard2.0 | The code generation, both ways. As a Roslyn generator: C# `[Shader]` classes → SDSL, `*Keys` classes and compute wrappers; the stubs every shader class gets. As a library: the SDSL parser (whole files, bodies, preprocessor structure), the SDSL → C# converter and its compiler-guided fixes. |
| `src/Csl.Types` | net10.0 | What shader code is written with: `Csl.Hlsl` (every HLSL scalar, vector and matrix type with HLSL's conversions and swizzles, the resources, the intrinsics), the attributes for what SDSL declares and C# has no keyword for, the `Sdsl` markers. No Stride dependency. |
| `src/Csl.Engine` | net10.0 | The engine's shaders (476 of 479) as `[Shader(External = true)]` classes, declarations only: what C# shaders inherit and call. Written by `csl engine`. |
| `src/Csl.Runtime` | net10.0 | Running C# compute shaders: `ComputeEffect` wrappers, `ShaderContext`, allocation helpers, `ShaderSourceRegistry` (hands the generated SDSL to the effect compiler). |
| `src/Csl.Tool` | net10.0, exe `csl` | `csl convert`: `.sdsl` files, or engine shaders by name, to C#. `csl engine`: regenerates `Csl.Engine`. |
| `tests/Csl.TestApp` | net10.0, exe | The test bench: the whole-engine round trip checked by the engine compiler, and C# shaders run on the GPU. |
| `tests/Csl.Tests` | net10.0, xunit | Unit tests of the generator, the wrappers, the analyzer and the runtime, without a GPU. |

A project that writes shaders in C# references:

```xml
<ProjectReference Include="..\StrideCSL\src\Csl.Types\Csl.Types.csproj" />
<ProjectReference Include="..\StrideCSL\src\Csl.Engine\Csl.Engine.csproj" />
<ProjectReference Include="..\StrideCSL\src\Csl.Runtime\Csl.Runtime.csproj" />
<ProjectReference Include="..\StrideCSL\src\Csl.Generators\Csl.Generators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

## C# to SDSL: writing shaders in C#

```csharp
using Csl; using Csl.Engine; using Csl.Hlsl; using static Csl.Hlsl.Intrinsics;

/// <summary>Output[i] = ToLinear(i / 63): an engine shader mixed in, its method called with types.</summary>
[Shader, NumThreads(64), Mixin(typeof(ColorUtility))]
public partial class CslColorLinear : ComputeShaderBase
{
    [Stage] public RWBuffer<float> Output;

    public override void Compute()
    {
        uint i = streams.DispatchThreadId.x;
        Output[i] = ToLinear(i / 63.0f);
    }
}
```

The generator writes, next to the class, the SDSL (`CslColorLinear.SdslSource`, registered at
start-up in `ShaderSourceRegistry`), `CslColorLinearKeys` in the shape the engine gives a `.sdsl`,
and for a compute shader `CslColorLinearEffect`, a typed wrapper:

```csharp
using var output = Csl.Buffers.NewTyped<float>(GraphicsDevice, 64, CslColorLinearEffect.Slots.Output);
using var effect = new CslColorLinearEffect(Services) { Output = output };
effect.Dispatch(64);
```

A shader of any other kind (a material feature, an image effect) is used by name like any engine
shader: `new ImageEffectShader(nameof(CslInvert))`.

C# has one base class where SDSL has many: the first SDSL base is the C# base, the others are
`[Mixin(...)]`. The generated half of every shader class declares the mixins' members as stubs (so
they are in scope, typed) and `streams`, typed as the class itself, so `streams.Position` reads as in
SDSL and is checked.

### Extending an engine shader

`Csl.Engine` has the engine's shaders as C# classes; inherit one, override its methods:

```csharp
[Shader]
public partial class CslInvert : ImageEffectShader
{
    [Stage]
    public override float4 Shading()
    {
        float4 color = Texture0.Sample(PointSampler, streams.TexCoord);   // Texturing's, through SpriteBase
        return new float4(1.0f - color.rgb, color.a);
    }
}
```

A method of a mixin (not of the C# base class) is overridden with `[Override]`; a mixin's method
reached through `base` is `Sdsl.Base(this).Method()`.

### Modifying an engine shader

`csl convert --engine LuminanceUtils --namespace My.Shaders --out Shaders` writes the engine's
`LuminanceUtils` as a full C# class, bodies included. Edit it; it keeps the engine's SDSL name, so
once registered (at the first `ComputeEffect`, or `ShaderSourceRegistry.InstallInto(effectSystem)`)
it replaces the engine's shader in every effect that uses it. `tests/Csl.TestApp/Modified` does this
and the gpu tests check the result.

## SDSL to C#: converting shaders

```
csl convert Shaders/MyShader.sdsl --out Shaders/Cs              # a project's shaders, bodies included
csl convert --engine ComputeColorTexture --out Shaders/Cs        # an engine shader, to modify
csl convert Shaders/MyShader.sdsl --declarations --out External  # external classes, to extend .sdsl shaders from C#
```

The converter parses whole files (bodies, the `#if` structure, comments and doc comments), writes C#
from the tree, then compiles it with the generator and makes explicit, error after error, what HLSL
does implicitly and C# does not accept. Each fix is a marker the translator leaves out, so the SDSL
the C# translates back to is the original: `Sdsl.Implicit<float3>(v)` is `v` (a narrowing, an int
used as a condition), `= default` on a local is no initializer, `Sdsl.Undefined(out x)` is nothing.

Converting `ShadingBase` gives, for instance:

```csharp
[Shader]
[Define("STRIDE_RENDER_TARGET_COUNT", "1", If = "!defined(STRIDE_RENDER_TARGET_COUNT)")]
public abstract partial class ShadingBase : ShaderBase
{
    [Compose] public ComputeColor ShadingColor0;
    [If("STRIDE_RENDER_TARGET_COUNT > 1"), Compose] public ComputeColor ShadingColor1;
    ...
    [Stage]
    public override void PSMain()
    {
        base.PSMain();
        streams.ColorTarget = this.Shading();

        if (Sdsl.If("STRIDE_RENDER_TARGET_COUNT > 1"))
        {
            streams.ColorTarget1 = ShadingColor1.Compute();
        }
        ...
```

and back exactly the SDSL it came from.

### What C# says for what SDSL says

| SDSL | C# |
|------|----|
| `shader X : A, B<T, 2>, C` | `[Mixin(typeof(B), Generics = "T, 2"), Mixin(typeof(C))] partial class X : A`; generic first base: `[Shader(BaseGenerics = "...")]` |
| `shader X<LinkType TName, int TCount>` | `[Generic] public static LinkType TName; [Generic] public static int TCount;` |
| `internal shader X` | `[Shader(Internal = true)]` |
| `stage`, `stream T x : SEM`, `patchstream`, `compose`, `groupshared`, `clone` | `[Stage]`, `[Stream("SEM")]`, `[PatchStream]`, `[Compose]`, `[GroupShared]`, `[Clone]` |
| `static const T x = v;` | `public const T x = v;`, or `public static readonly T x = v;` for vectors |
| `cbuffer PerMaterial { ... }`, `rgroup`, `tbuffer` | `[CBuffer("PerMaterial")]` on each member (`NewBlock = true` for a second block of the same name) |
| `[Link("X")]`, `[Color]`, any other attribute | `[Link("X")]`, `[Color]`, `[Hlsl("maxvertexcount(3)")]` |
| `T x[N]`, `compose T x[]` | `[Size("N")] T[] x`, `[Compose] T[] x` |
| `T x : SEMANTIC`, `nointerpolation`, parameter `triangle`, `const` | `[Semantic("SEMANTIC")]`, `[Modifiers("nointerpolation")]` |
| `SamplerState S { Filter = ...; }` | `[Sampler(Filter = "...", AddressU = "Wrap")] SamplerState S` |
| `#if C` around a member | `[If("C")]`; versions of one member under different `#if`: `[Variant("SDSL", If = "C")]` |
| `#define N V`, `#ifndef N / #define N V / #endif`, `#error` | `[Define("N", "V")]`, `[Define("N", "V", If = "!defined(N)")]`, `[PreprocessorError("...")]` |
| `#if` / `#elif` / `#else` in a body | `if (Sdsl.If("C")) { } else if (Sdsl.If("D")) { } else { }` |
| a macro in an expression, a macro statement | `Sdsl.Macro("N")`, `Sdsl.MacroStatement("N");` |
| `override` a mixin's method | `[Override]` (C#'s `override` only reaches the C# base class) |
| `streams.x`, `streams.TName` (MemberName), a stream the effect brings | `streams.x`, `streams[TName]`, `streams["x"]` |
| `v.TName` (MemberName), `m._m00_m11` | `Sdsl.Member(v, TName)`, `Sdsl.Member(m, "_m00_m11")` (assignable) |
| `Shader.Method()` without an instance | `Sdsl.Static<Shader>().Method()` (a static method is called directly) |
| `in`, `out`, `inout` | `in`, `out`, `ref` |
| `[unroll]`, `[loop]`, `[branch]`, `[flatten]`, `[fastopt]` | `Unroll();`, `Loop();`, `Branch();`, `Flatten();`, `Attribute("fastopt");` before the statement |
| `discard;` | `discard();` |
| `float3(a, b, c)`, `(float3)x`, `(S)0` | `new float3(a, b, c)`, `(float3)x`, `default(S)` |
| `const` / `static const` locals | `const` when C# can, else `Sdsl.Const(v)` / `Sdsl.StaticConst(v)` |
| `Input`, `Output`, `TriangleStream<T>`… | `[Type("TriangleStream<Output>")] dynamic` |
| `2.0`, `2.0f`, `1u` | `2.0f`, `2.0f`, `1u` (`0u` and `0x10u` are written `(uint)0`, `(uint)0x10`: the 4.4 parser rejects them) |

The HLSL types follow HLSL's conversions: widening (bool < int < uint < half < float < double, same
size) is implicit, narrowing and truncation are explicit, as HLSL only warns about them. Vectors take
any mix of parts in their constructors (`new float4(v.xyz, 1)`), scalars have swizzles (`f.xxx`, C#
14 extension members), matrices have `m[row]`, `m._m01`, `m._12`.

## Validation

`tests/Csl.TestApp`, `dotnet run --project tests/Csl.TestApp -- <command>`:

| Command | Does |
|---------|------|
| `parse` | Parses every engine shader with the full parser. |
| `convert [--out DIR]` | Converts every engine shader SDSL → C# → SDSL; writes both and a report of what fails. |
| `roundtrip [--out DIR]` | Compiles each converted engine shader with the engine's SDSL compiler (`ShaderMixer`, to SPIR-V) from its original source and from its round trip (its bases round-tripped too) and compares the SPIR-V without debug instructions. A shader without an entry point is hosted after `ShaderBase` or `ComputeShaderBase`; a generic one is instantiated with sample arguments. A difference is traced to the base that causes it. |
| `compile NAME...` | Compiles engine shaders (and this app's C# shaders), mixed in this order. |
| `gpu` | A code-only Stride game (hidden window) that runs the C# shaders of `Shaders/` and checks what they compute: a shader written in C#, an engine shader mixed in, an engine shader replaced by its modified C#, the engine's `ImageEffectShader` extended. |

Results on Stride 4.4.0-beta7 (479 shaders in the packages):

- **471** convert and translate back; the SPIR-V of **442** is identical to the original's, none
  differs; the other 29 do not compile on their own in their original form either (a composition
  left empty, an abstract method nothing implements).
- **476** are in `Csl.Engine`.
- Not converted: `FXAAShader` and `SubsurfaceScatteringBlurShader` (function-like macros) and
  `SSLRBlurPass` (`#if` inside an initializer list), which are the three missing from `Csl.Engine`;
  `PositionStream` (the whole shader under `#if`/`#else`, in two versions: in `Csl.Engine` for
  typing, not round-tripped); and four shaders whose bodies do not compile as C# yet.

On the GPU (`gpu`, Direct3D 11, feature level 11_0): the five C# shaders compute what the CPU expects, the modified
`LuminanceUtils` replacing the engine's in the effect that calls it.

`tests/Csl.Tests` (`dotnet test`) checks the generator on sample shaders, the wrappers against the
engine, and compiles the generated SDSL with the engine compiler.

### Engine issues found on the way (Stride 4.4.0-beta8, Direct3D 11)

`Csl.TestApp probes` (CPU) and `gpu` report them as `ENGINE` lines, outside the tests.

- A typed buffer with an unordered access view (`RWBuffer<T>`) needs feature level 11_0 on Direct3D 11,
  and a game without GameSettings runs at `RenderingSettings.DefaultGraphicsProfile`, 10_0: the
  engine then fails with a bare `E_INVALIDARG`. The gpu tests ask for 11_0.
- `float3(i / 4, 0, 0)` with a `uint i` divides in float (`i = 1` gives 0.25): the constructor's
  float type reaches the literal `4` inside the integer division. The same division through a local
  is right.
- An integer suffix after a leading 0 does not parse: `0u`, `0x10u` (`1u`, `10u` do). The translator
  writes `(uint)0`, `(uint)0x10`.

## Compute wrappers

For `shader VoxelWaterStep : ComputeShaderBase, VoxelWaterBricks`, the generator emits
`VoxelWaterStepEffect`:

- **Base class**: the wrapper of the first base declared in the project (abstract, carrying its
  parameters once for every pass that mixes it in), or `Csl.ComputeEffect`. A shader is "compute"
  when its inheritance reaches `ComputeShaderBase`.
- **Constructor**: `new VoxelWaterStepEffect(services, threadNumbers)`, or without thread numbers for
  a C# shader with `[NumThreads]`.
- **One property per parameter**, typed as the engine's key (`float`, `Int3`, `Vector3`, `Texture?`,
  `Buffer?`), documented from the member's `///`.
- **`Slots`**: one `ResourceSlot` per resource with its HLSL type, element type and the access the
  shader makes of it (read from the bodies).
- **`Dispatch(Int3 cells)`**: `ceil(cells / threadNumbers)` groups per axis.

`Textures.New3D<T>(device, size, slots...)`, `Buffers.NewTyped<T>`, `NewStructured<T>`, `NewRaw`
allocate with the format of the element type and the views the slots need; `MipViews()`,
`PingPong<T>`, `UploadRegion`, `FillRegion` cover the rest.

### Diagnostics

| Id | Says |
|----|------|
| CSL001–004 | A `.sdsl` declaration the wrapper parser did not understand, a parameter type or array without a C# key type, a shader declared twice. |
| CSL010 | A texture read and written through its RW view with a format Direct3D 11 cannot load from. |
| CSL100–109 | C# that has no SDSL equivalent, on its line: a non-partial shader class, a statement or type outside the subset, a call outside the intrinsics and shader methods, a base that is not a shader, a C# shader named like a `.sdsl`. |

## Limits

- Function-like macros, `#if` inside an expression, and a whole shader under `#if` are not converted.
- A local declared in one `#if` branch and used after it does not compile in C# (the branch is a block).
- `[Variant]` versions of a member are SDSL text: C# types the member by its main version only.
- The in-memory registration reaches the local `EffectCompiler` through a protected property, by
  reflection; a remote compiler cannot take C# shaders.
- The conversion needs Roslyn 5 (C# 14, for the scalar swizzles): `csl` and the test app carry it;
  the generator itself builds against Roslyn 4.12 and runs in any recent SDK.
