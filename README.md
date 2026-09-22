# C#SL: Stride shaders from C#

Tooling for writing and driving Stride compute shaders from C# without GPU plumbing. Two bricks:

1. **Typed wrappers** (`Csl.Generators` + `Csl.Runtime`): every compute shader of the project gets a
   `<Shader>Effect` class with a property per parameter, resource slots that say what to allocate,
   and a `Dispatch(cells)` that computes the thread groups. Generated at build time from the
   `.sdsl` files, so nothing to run and nothing to commit.
2. **C# to SDSL** (`Csl.Types` + the same generator): a `partial class` marked `[Shader]` becomes an
   SDSL shader, with its `*Keys` class and its wrapper, and reaches the effect compiler in memory.

Built against Stride 4.4 (the `StrideVersion` in `Directory.Build.props`). The engine already
declares `**/*.sdsl` as `AdditionalFiles` and generates the `*Keys` classes from them with its own
Roslyn generator; `Csl.Generators` reads the same files and builds on those keys.

## Projects

| Project | Target | Role |
|---------|--------|------|
| `Csl.Generators` | netstandard2.0 | Roslyn source generator (`ShaderEffectGenerator`: wrappers from `.sdsl`, and SDSL + keys + wrappers from `[Shader]` classes) and analyzer (`TypedUavFormatAnalyzer`). Has its own SDSL declaration parser: no engine dependency. |
| `Csl.Runtime` | net10.0 | `ComputeEffect` base class, `ShaderContext`, `ResourceSlot`, `Textures`/`Buffers` allocation, `MipChain`, `PingPong<T>`, region uploads, `ShaderSourceRegistry`. |
| `Csl.Types` | net10.0 | What shader code is written with: `Csl.Hlsl` (the HLSL types and intrinsics), the attributes, `Csl.Engine` (stubs of the engine's base shaders). No Stride dependency. |
| `Csl.Stubs` | net10.0, tool | Writes the `Csl.Engine` stubs from the engine's `.sdsl` files. |
| `Csl.Tests` | net10.0, xunit | Parser, generator, translator and analyzer tests, without a GPU. Compiles the demo's C# shaders and `Demo/VoxelWater.cs` against the engine, and compiles every generated SDSL with the engine's own shader compiler (`ShaderMixer`, to SPIR-V). |

Reference them from the project that owns the shaders:

```xml
<ProjectReference Include="..\CSharpShaders\Csl.Runtime\Csl.Runtime.csproj" />
<ProjectReference Include="..\CSharpShaders\Csl.Types\Csl.Types.csproj" />
<ProjectReference Include="..\CSharpShaders\Csl.Generators\Csl.Generators.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

## The wrappers

For `shader VoxelWaterStep : ComputeShaderBase, VoxelWaterBricks` in namespace `Demo`, the generator
emits `Demo.VoxelWaterStepEffect`:

- **Base class**: the wrapper of the first base declared in the project (`VoxelWaterBricksEffect`,
  abstract, carrying `ActiveBricks`, `SampleCount`, `BrickCount` once for every pass that mixes it
  in), or `Csl.ComputeEffect` when there is none. Other project mixins are flattened into the class.
  Engine shaders (`ComputeShaderBase`) are not wrapped; a shader is "compute" when its inheritance
  reaches `ComputeShaderBase`.
- **Constructor**: `new VoxelWaterStepEffect(services, threadNumbers)` with an `Int3` or a single
  `int` for all axes. The `.sdsl` does not know its thread numbers (they are macros the effect sets),
  so they are given here; changing `ThreadNumbers` later recompiles the effect on the next dispatch.
- **One property per parameter**: every member that is not `stream`, `compose`, `const`, `static` or
  `groupshared`, typed as the engine's key is (`float`, `Int3`, `Vector3`, `Texture?`, `Buffer?`,
  `SamplerState?`). Setting one sets the key. The `///` comment of the member is the property's doc.
- **`Slots`**: a nested static class with one `ResourceSlot` per resource: its HLSL type, its element
  type and the access the shader makes of it, found by reading the method bodies (`Load`/`Sample`/
  `Res[i]` read, `Res[i] = ` write, `Interlocked*` both). `Texture3D<T>` is always a read slot;
  `RWTexture3D<T>` a write slot, or read-write when the body reads it back.
- **`Dispatch(Int3 cells)`**: thread group counts are `ceil(cells / threadNumbers)` per axis;
  `DispatchGroups(groups)` takes the counts directly. `Parameters` and `Shader` expose the engine
  objects underneath.
- **`IDisposable`**: disposes the `ComputeEffectShader`.

`ShaderContext.Get(services)` holds the `RenderContext`, one `RenderDrawContext` and the command
list, made once per game and registered as a service; every effect shares it.

### Allocation helpers

```csharp
// Format from the element type, views from the slots: R16_Float, SRV|UAV.
var amounts = Textures.New3D<Half>(device, size, VoxelWaterStepEffect.Slots.Amounts, VoxelWaterStepEffect.Slots.AmountsOut);
// Whole mip chain, R8G8_UNorm.
var field = Textures.New3D<Texels.Rg8>(device, size, MipMapCount.Auto, VoxelWaterComposeEffect.Slots.FieldOut, VoxelWaterMipEffect.Slots.Source);
var levels = field.MipViews();          // MipChain: one single-mip view per level, levels[i], levels.SizeAt(i)
var pair = new PingPong<Texture>(() => Textures.New3D<Half>(device, size, slots)); // pair.Current, pair.Next, pair.Swap()
terrain.UploadRegion(commandList, MemoryMarshal.Cast<byte, Texels.Rg8>(texels), size, lo, hi); // a box of a whole-texture array
changed.FillRegion(commandList, 1u, lo, hi);                                                   // one value over a box
```

Element types: `float`, `Half`, `uint`, `int`, `byte`, `ushort`, `Vector2/4`, `Half2/4`, `Int2/4`,
`Color`, and `Csl.Texels.Rg8/R8/R16` for the unorm formats without a struct of their own.
`Buffers.NewTyped<T>`, `NewStructured<T>` and `NewRaw` do the same for buffers.

### Diagnostics

| Id | Where | Says |
|----|-------|------|
| CSL001 | `.sdsl` | A declaration the parser did not understand; the wrapper may be incomplete. The engine's own parser still validates the shader. |
| CSL002 | `.sdsl` | A parameter whose type has no C# key type (a struct, `float3x3`): use `Parameters`. |
| CSL003 | `.sdsl` | An array parameter, not wrapped: use `Parameters`. |
| CSL004 | `.sdsl` | The same shader name in two files; only the first is wrapped. |
| CSL010 | C# | `Textures.New3D<T>(…, slot)` with a slot the shader both reads and writes through its RW view and a `T` other than `float`, `uint`, `int`: Direct3D 11 only allows a typed UAV load on R32 single-channel formats. The same check runs when a texture is bound to such a slot, whatever it was allocated with. |

Binding a texture that lacks a view the slot needs (no UAV on a `RW` slot) throws with the slot's
name; so does allocating with the wrong element type on a read-write slot.

## Shaders in C#

A shader is a `partial class` marked `[Shader]`, in a file that uses `Csl.Hlsl` and the intrinsics:

```csharp
using Csl; using Csl.Engine; using Csl.Hlsl; using static Csl.Hlsl.Intrinsics;

[Shader, NumThreads(8, 8, 8), Mixin(typeof(VoxelWaterBricks))]
public partial class VoxelWaterMip : ComputeShaderBase
{
    [Stage] public Texture3D<float2> Source;
    [Stage] public RWTexture3D<float2> Target;
    [Stage] public int3 SourceSize, TargetSize;
    [Stage] public int Level;

    public override void Compute()
    {
        int3 brick = (int3)GroupId;              // a [Stream] of the base: streams.GroupId
        int3 lo = brick * 8, hi = lo + 7;
        Loop();                                  // [loop] on the for that follows
        for (int i = 0; i < Level; i++) { lo = lo * 2 - 1; hi = hi * 2 + 1; }
        if (!BoxDirty(lo, hi))                   // a method of the mixin
            return;
        ...
        Target[c] = new float2(sum / weight, Source.Load(new int4(centre, 0)).g);
    }
}
```

The generator writes, next to the class:

- the SDSL (`shader VoxelWaterMip : ComputeShaderBase, VoxelWaterBricks { ... }`), kept as
  `VoxelWaterMip.SdslSource` and registered at start-up in `Csl.ShaderSourceRegistry`;
- `VoxelWaterMipKeys`, in the shape the engine gives a `.sdsl` (the engine's assembly processor
  names the keys `VoxelWaterMip.Source` etc. from the class name, exactly as for a file);
- `VoxelWaterMipEffect`, the wrapper of the section above, with a constructor that takes the
  `[NumThreads]` and `DefaultThreadNumbers`;
- C# stubs of the members of every `[Mixin]` shader, so `BoxDirty` and `ActiveBricks` compile.

**How the SDSL reaches the engine.** There is no `.sdsl` file. Each assembly's module initializer
adds its shaders to `ShaderSourceRegistry`; the first `ShaderContext` (created by any
`ComputeEffect`) hands them to the game's `EffectCompiler` through `ShaderSourceManager.AddShaderSource`,
reached by reflection through the `EffectCompilerCache` the engine wraps it in. The effect cache
keys on the source hash, so an edit recompiles. A shader used before any `ComputeEffect` exists
(in a material) needs `ShaderSourceRegistry.InstallInto(effectSystem)` first. `--dump-sdsl=DIR`
in the demo, or `ShaderSourceRegistry.DumpTo`, writes the sources as files. Game Studio does not
see these shaders; assets cannot reference them by name.

### Mapping

| C# | SDSL |
|----|------|
| `partial class X : Base` | `shader X : Base` (the base must be a `[Shader]` class; `Csl.Engine.ComputeShaderBase` is one) |
| `[Mixin(typeof(A), typeof(B))]` | `shader X : Base, A, B`; their members are stubbed on the class. Not virtual: to `override` a method, make its shader the C# base. |
| `[Shader(Name = "Y")]`, `[Shader(External = true)]` | the SDSL name; a class that only describes a shader that exists elsewhere (no output) |
| `[Stage] T f;` `[Stream("SV_X")] T f;` `[Compose] S f;` `[GroupShared] T f;` `const T f = v;` `[Link("K")]` `[Color]` | `stage T f;` `stream T f : SV_X;` `compose S f;` `groupshared T f;` `static const T f = v;` `[Link("K")]` `[Color]` |
| reading a `[Stream]` field, `base.M()`, `override`, `abstract` | `streams.f`, `base.M()`, `override`, `abstract` |
| `float`, `int`, `uint`, `bool`, `double`, `float3`, `int4`, `uint2`, `bool3`, `float4x4`, `Texture3D<T>`, `RWTexture3D<T>`, `Buffer<T>`, `StructuredBuffer<T>`, `SamplerState`… | the same names |
| `new float3(a, b, c)`, `(int3)v`, `default(float2)` | `float3(a, b, c)`, `(int3)v`, `(float2)0` |
| `v.xyz`, `v.rg`, `v[i]` | the same swizzles and indexing |
| `min`, `max`, `any`, `all`, `floor`, `saturate`, `dot`, `InterlockedOr(ref x, v)`, `GroupMemoryBarrierWithGroupSync()`… | the same intrinsic, `ref`/`out` dropped |
| `Loop();` `Unroll();` `Unroll(n);` `Branch();` `Flatten();` as the statement before a loop or an `if` | `[loop]` `[unroll]` `[unroll(n)]` `[branch]` `[flatten]` |
| `2.0f`, `1e-4f`, `1u`, `8` | `2.0`, `1e-4`, `1`, `8` |
| a `struct` nested in the class | `struct` at shader scope |
| `///` comments | `///` comments, and the wrapper's documentation |

Allowed in methods: locals (`var` resolves to its type), assignments, the operators, `if`/`else`,
`for`, `while`, `do`, `switch` on constants, `break`/`continue`/`return`, the ternary, casts, calls
to the class's methods, the mixins', the resources' and the intrinsics.

Not allowed, each a `CSL1xx` error on its line: any other type (`string`, `object`, classes,
arrays, delegates, `Half`, Stride's `Vector3`), `new` of a reference type, calls outside the
shader (`MathF`, `Math`, LINQ), `foreach`, `try`/`throw`, lambdas, `is`/`as`/`??`, tuples, `this`
on its own, properties, events, constructors, nested classes, generic or nested shader classes,
a non-`partial` shader class, a base that is not a `[Shader]`, and a C# shader with the same name
as a `.sdsl` of the project.

| Id | Says |
|----|------|
| CSL100 | The shader class must be partial. |
| CSL101 | A statement or expression outside the subset. |
| CSL102 | A type with no SDSL equivalent. |
| CSL103 | `new` of something that is not a vector, a matrix or a shader struct. |
| CSL104 | A member kind a shader cannot have (property, event, constructor, nested class). |
| CSL105 / CSL106 | Generic / nested shader class. |
| CSL107 | A call outside the intrinsics, the resources and the shader's own methods. |
| CSL108 | A base or a `[Mixin]` that is not a `[Shader]` class. |
| CSL109 | The same shader name in C# and in a `.sdsl` file. |

`Csl.Engine.ComputeShaderBase` is written by `Csl.Stubs` from the engine's `ComputeShaderBase.sdsl`
(streams with their semantics, `ThreadGroupCountGlobal`, `Compute()` and `IsFirstThreadOfGroup()`
as virtuals). The tool takes any `.sdsl`: `dotnet run --project CSharpShaders/Csl.Stubs -- --out
Csl.Types/Engine --namespace Csl.Engine path/to/X.sdsl`. A shader that uses the preprocessor for
more than defaults gets a partial stub, to finish by hand.

### Validation

- **Compiles with the engine**: `Csl.Tests` feeds every generated SDSL (the three samples and the
  seven demo shaders) to `Stride.Shaders.Compilers.ShaderMixer` with the engine's `ComputeShaderBase`,
  as `ComputeEffectShader` would, and requires SPIR-V out. Runs on the CPU, no GPU.
- **Bit-exact against the original SDSL**: the seven water shaders were rewritten in C#
  (`Demo/Shaders/*.cs`); the originals are kept in `Demo/Effects/Reference/*.sdsl.txt`, embedded
  in the demo. Two runs on the same terrain, then a byte comparison of the amounts (half floats),
  the drawn field and the occupancy base:

  ```
  Demo.exe --voxelgrid --earth --water-steps=200 --water-shaders=reference --out=Water
  Demo.exe --voxelgrid --earth --water-steps=200 --out=Water
  ```

  The second run prints `[water] IDENTICAL` or `DIFFERENT` with the first differing byte, and sets
  the exit code. Textually, the generated SDSL of all seven is identical to the originals apart from
  comments, whitespace and parentheses.
- **Visually**: `--shot=DIR --shot-pose=front` (or any pose) with and without `--water-shaders=reference`.

## Before and after: `Demo/VoxelWater.cs`

Before, each pass was a `ComputeEffectShader` created lazily, its parameters set by key on every
step, the thread group counts computed by a local `Dispatch`, and each texture created with its
format and flags spelled out:

```csharp
step ??= new ComputeEffectShader(renderContext) { ShaderSourceName = "VoxelWaterStep" };
...
Bricks(step.Parameters, active);
step.Parameters.Set(VoxelWaterStepKeys.ChangedOut, changed);
step.Parameters.Set(VoxelWaterStepKeys.Terrain, Terrain);
step.Parameters.Set(VoxelWaterStepKeys.Amounts, amounts[current]);
step.Parameters.Set(VoxelWaterStepKeys.AmountsOut, amounts[next]);
step.Parameters.Set(VoxelWaterStepKeys.IsoLevel, isoLevel);
... seven more ...
Dispatch(step, new Int3(BrickSize), SampleCount);
current = next;
```

After, the constants are set once at construction and a step reads as the algorithm:

```csharp
step = new VoxelWaterStepEffect(services, BrickSize) { ActiveBricks = active, ChangedOut = changed, Terrain = Terrain, IsoLevel = isoLevel };
...
step.Amounts = amounts.Current;
step.AmountsOut = amounts.Next;
step.MaxCompress = MaxCompress;
step.Quantum = Quantum;
step.Soak = Soak;
step.PourCentre = pourCentre;
step.PourRadius = pourRadius;
step.PourAmount = pourAmount;
step.Dispatch(SampleCount);
amounts.Swap();
```

The orchestration (sub-steps, the ping-pong, the mip loop, the pyramid, the brick marking) is
unchanged and still explicit. The texture creation, the mip views, the region upload, the
`ParameterCollection` calls, the group count arithmetic and the effect lifetime are gone: 326 lines
to 281, most of the difference in the constructor and `Step`. The seven `.sdsl` files became seven
`.cs` files of the same length, one for one.

## Limits

- The declaration parser covers what the wrappers need (namespaces, shaders, bases, members,
  attributes, cbuffers, method signatures, bodies as tokens). Generic shaders get no wrapper;
  effects (`.sdfx`) and structs are skipped.
- C# shaders: no arrays yet (fields or locals), no `switch` on patterns, no generic methods, no
  method default values; `static` methods become plain shader methods; a `[Mixin]` member cannot be
  overridden; the engine's SDSL parser takes no `u` suffix, so `uint` literals are emitted bare.
- The in-memory registration walks a `protected` property of `EffectCompilerChain` by reflection
  and needs the local `EffectCompiler` (not the remote one); it fails with a clear message otherwise.
- Only `ComputeShaderBase` is stubbed. Other engine bases go through `Csl.Stubs`, whose output is
  partial where the `.sdsl` leans on the preprocessor.
- Resource usage is syntactic: a resource passed to a function is counted as read; a resource used
  only inside a mixin's methods is classified by that mixin. Neither can produce a false "read-only".
- Thread numbers are not in the `.sdsl`; they are given at construction.
- Only cubic mip sizes were exercised (the demo's textures); `MipChain.SizeAt` computes each axis.
- Building the demo needs the engine fork it targets (`Stride.Voxels` with `VoxelGridOccupancy`);
  the published 4.4 packages build `Csl.*` and the tests, not the demo.
