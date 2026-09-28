using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Csl.Cpu;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Core.Storage;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;
using Stride.Shaders;
using Stride.Shaders.Compiler;
using CslTypes = Csl.Types;

namespace Csl.Debugging;

/// <summary>
/// Shader debugging for a Stride game, from one line in Program.cs:
/// <code>CslDebug.Register(game, "Shaders");</code>
/// Ctrl+click a pixel: the mesh drawn there is run again on the CPU as the engine mixed its effect
/// (vertex shader, rasterizer, pixel shader), from what the frame gave the GPU, and the debugger stops
/// before that pixel's lane (F11 into the shader). The C# shaders of the folder are reloaded on save,
/// for the GPU and the CPU.
/// </summary>
public static class CslDebug
{
    /// <param name="shaderFolder">The folder of the C# shaders to reload on save, relative to the calling source file (Program.cs).</param>
    public static CslDebugSystem Register(Game game, string? shaderFolder = null, [System.Runtime.CompilerServices.CallerFilePath] string caller = "")
    {
        if (shaderFolder != null && !Path.IsPathRooted(shaderFolder) && caller.Length > 0)
            shaderFolder = Path.Combine(Path.GetDirectoryName(caller)!, shaderFolder);
        var system = new CslDebugSystem(game, shaderFolder);
        game.GameSystems.Add(system);
        return system;
    }

    /// <summary>Where the messages go: the console, the debugger's output, and a log file next to the flat C#.</summary>
    internal static void Log(string message)
    {
        var line = "[Csl] " + message;
        Console.WriteLine(line);
        Debug.WriteLine(line);
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "csl-debug");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "csl-debug.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>What <see cref="CslDebug.Register"/> adds: the picking, the capture, the reload.</summary>
[DebuggerNonUserCode]
public sealed class CslDebugSystem : GameSystemBase
{
    private readonly Game game;
    private readonly ShaderReloader? reloader;
    private EffectRecorder? recorder;
    private CaptureFeature? feature;
    private Vector2? request;
    private int frames;
    private readonly Vector2? autoPick = ParsePick(Environment.GetEnvironmentVariable("CSL_DEBUG_PICK"));
    private readonly bool exitAfterPick = Environment.GetEnvironmentVariable("CSL_DEBUG_EXIT") == "1";
    private static readonly int PickFrame = int.TryParse(Environment.GetEnvironmentVariable("CSL_DEBUG_PICK_FRAME"), out var f) ? f : 180;

    /// <summary>CSL_DEBUG_PICK="0.5,0.5": a pick at that fraction of the window after a few seconds, as a Ctrl+click would (to try it without a mouse); CSL_DEBUG_EXIT=1 then exits.</summary>
    private static Vector2? ParsePick(string? text)
    {
        var parts = text?.Split(',');
        return parts?.Length == 2 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ? new Vector2(x, y) : null;
    }

    internal CslDebugSystem(Game game, string? shaderFolder) : base(game.Services)
    {
        this.game = game;
        Enabled = true;
        Visible = true;
        // After the scene: the capture is there to process.
        DrawOrder = int.MaxValue;
        UpdateOrder = int.MinValue;
        if (shaderFolder != null)
            reloader = new ShaderReloader(Path.GetFullPath(shaderFolder));
    }

    /// <summary>A pick asked for (Ctrl+click), taken by the capture during the next frame's draw.</summary>
    internal Vector2? PendingPick => request;

    internal List<DrawCapture> Captured { get; } = new();

    /// <summary>Copies of the targets the captured draws wrote to, taken once they are drawn.</summary>
    internal Dictionary<Texture, Texture> TargetCopies { get; } = new();

    public override void Update(GameTime gameTime)
    {
        var effectSystem = game.EffectSystem;
        if (recorder == null && effectSystem?.Compiler is EffectCompilerBase compiler)
        {
            // Every effect compiled from now on is known by its mixin.
            recorder = new EffectRecorder(compiler);
            effectSystem.Compiler = recorder;
            ShaderSourceRegistry.InstallInto(effectSystem);
            CslDebug.Log("registered: Ctrl+click a pixel to run it on the CPU" + (reloader != null ? $"; C# shaders of {reloader.Directory} reloaded on save" : string.Empty));
        }
        if (reloader != null && effectSystem != null)
            reloader.Apply(effectSystem);

        if (autoPick is { } auto && ++frames == PickFrame)
        {
            request = auto;
            Captured.Clear();
        }
        var input = game.Input;
        if (input.IsMouseButtonPressed(MouseButton.Left) && (input.IsKeyDown(Keys.LeftCtrl) || input.IsKeyDown(Keys.RightCtrl)))
        {
            request = input.MousePosition;
            Captured.Clear();
        }
        InstallFeature();
    }

    /// <summary>
    /// The capture, as a sub-feature of the compositor's mesh feature: it sees every draw with its data.
    /// And the forward renderers' post effects tapped, for the colour before them.
    /// </summary>
    private void InstallFeature()
    {
        var compositor = game.SceneSystem?.GraphicsCompositor;
        if (compositor?.Game != null)
            TapPostEffects(compositor.Game);
        var meshFeature = compositor?.RenderFeatures.OfType<MeshRenderFeature>().FirstOrDefault();
        if (meshFeature == null || meshFeature.RenderFeatures.Contains(feature!))
            return;
        feature = new CaptureFeature(this);
        meshFeature.RenderFeatures.Add(feature);
        PostEffectsTap.Hook = CopyColor;
    }

    private static void TapPostEffects(Stride.Rendering.Compositing.ISceneRenderer renderer)
    {
        switch (renderer)
        {
            case Stride.Rendering.Compositing.ForwardRenderer forward when forward.PostEffects != null && !PostEffectsTap.IsWrapped(forward.PostEffects):
                forward.PostEffects = PostEffectsTap.Wrap(forward.PostEffects);
                break;
            case Stride.Rendering.Compositing.SceneCameraRenderer camera when camera.Child != null:
                TapPostEffects(camera.Child);
                break;
            case Stride.Rendering.Compositing.SceneRendererCollection collection:
                foreach (var child in collection.Children)
                    TapPostEffects(child);
                break;
        }
    }

    /// <summary>The scene's colour before the post effects, in the frame of the pick (once, the main camera's).</summary>
    internal Texture? ColorBeforePost { get; private set; }

    private void CopyColor(RenderDrawContext context, Span<Texture> inputs)
    {
        if (PendingPick == null || Captured.Count == 0 || ColorBeforePost != null || inputs.Length == 0 || inputs[0] is not { } color)
            return;
        var copy = Texture.New2D(context.GraphicsDevice, color.Width, color.Height, color.ViewFormat, TextureFlags.ShaderResource);
        if (color.MultisampleCount != MultisampleCount.None)
            context.CommandList.CopyMultisample(color, 0, copy, 0, color.ViewFormat);
        else
            context.CommandList.Copy(color, copy);
        ColorBeforePost = copy;
    }

    public override void Draw(GameTime gameTime)
    {
        DrawOverlay();
        if (request is not { } at || Captured.Count == 0)
            return;
        request = null;
        var draws = Captured.ToList();
        Captured.Clear();
        var copies = TargetCopies.Values.ToList();
        try
        {
            // The GPU decodes the textures (any format); read again for each pick, they may have changed.
            CpuCapture.GraphicsContext = game.GraphicsContext;
            CpuCapture.ClearCache();
            Pick(draws, at);
        }
        catch (Exception e)
        {
            CslDebug.Log("the CPU run failed: " + e.GetBaseException());
            Show("Csl: the CPU run failed: " + e.GetBaseException().Message, Color.Red);
        }
        foreach (var copy in copies)
            copy.Dispose();
        TargetCopies.Clear();
        ColorBeforePost?.Dispose();
        ColorBeforePost = null;
        if (exitAfterPick)
            game.Exit();
    }

    private void Pick(List<DrawCapture> captures, Vector2 at)
    {
        var commandList = game.GraphicsContext.CommandList;
        var viewport = captures[0].Viewport;
        int width = (int)viewport.Width, height = (int)viewport.Height;
        int x = Math.Clamp((int)(at.X * width), 0, width - 1), y = Math.Clamp((int)(at.Y * height), 0, height - 1);
        var loader = ShaderSourceRegistry.FindLocalCompiler(game.EffectSystem.Compiler).GetFileShaderLoader();
        var draws = new List<CpuMeshDraw>();
        var sources = new Dictionary<CpuMeshDraw, DrawCapture>();
        var meshes = new Dictionary<(Stride.Graphics.Buffer, int, int, int), CpuMesh>();
        foreach (var capture in captures)
        {
            if (recorder!.Find(capture.Effect) is not { } mixin)
            {
                CslDebug.Log($"{capture.Name}: its effect ({capture.Effect.Name}, {capture.Effect.Bytecode.ComputeId()}) is not among the {recorder.Count} the registration saw compiled, left out");
                continue;
            }
            try
            {
                var flat = FlatEffects.Compile(mixin, loader);
                var mesh = capture.ReadMesh(commandList, meshes);
                if (mesh == null)
                    continue;
                var draw = new CpuMeshDraw(flat.Vertex, flat.Pixel, mesh)
                {
                    Viewport = (capture.Viewport.X, capture.Viewport.Y, capture.Viewport.Width, capture.Viewport.Height, capture.Viewport.MinDepth, capture.Viewport.MaxDepth),
                    Cull = capture.Cull,
                    Break = Debugger.IsAttached,
                };
                capture.Bind(flat, draw, commandList);
                if (Environment.GetEnvironmentVariable("CSL_DEBUG_VERBOSE") == "1")
                    CslDebug.Log(capture.Name + " bindings:\n" + capture.Describe(flat) + "\n  flat C#: " + flat.Directory);
                draws.Add(draw);
                sources[draw] = capture;
            }
            catch (Exception e)
            {
                CslDebug.Log($"{capture.Name}: not run on the CPU: {e.Message}{(e.InnerException != null ? "\n" + e.GetBaseException() : string.Empty)}");
            }
        }
        var result = CpuScene.DebugPixel(draws, width, height, x, y);
        if (result is not { } hit)
        {
            CslDebug.Log($"pixel ({x}, {y}): no triangle of the {draws.Count} draws run on the CPU covers it");
            Show($"Csl pixel ({x}, {y}): no triangle of the {draws.Count} draws run on the CPU covers it", Color.Orange);
            return;
        }
        var color = hit.Color;
        string gpu = string.Empty, gpuLine, diffLine = string.Empty;
        try
        {
            // The colour the post effects were given (the last draw to write the pixel, MSAA resolved), else the
            // target copied at the end of the frame (no post effects: nothing reused it since).
            var target = ColorBeforePost;
            if (target == null && sources[hit.Draw].RenderTarget is { } drawn)
                TargetCopies.TryGetValue(drawn, out target);
            if (target != null && x < target.Width && y < target.Height)
            {
                var texel = CpuCapture.ReadTexture(target, commandList).Read(0, x, y);
                gpu = $"; the GPU's target holds {texel.x:F4} {texel.y:F4} {texel.z:F4} {texel.w:F4}";
                gpuLine = $"GPU  {texel.x:F4} {texel.y:F4} {texel.z:F4} {texel.w:F4}";
                var diff = Math.Max(Math.Max(Math.Abs(color.x - texel.x), Math.Abs(color.y - texel.y)), Math.Max(Math.Abs(color.z - texel.z), Math.Abs(color.w - texel.w)));
                // An 8-bit target rounds to 1/255: below 2/255 the two agree.
                diffLine = $"max diff {diff:F4} ({diff * 255:F1}/255)" + (diff <= 2 / 255f ? "  OK" : "  DIFFERS");
            }
            else
                gpuLine = "GPU  target not captured";
        }
        catch (Exception e)
        {
            gpu = "; the GPU's target could not be read: " + e.GetBaseException().Message;
            gpuLine = "GPU  not readable: " + e.GetBaseException().Message;
        }
        CslDebug.Log($"pixel ({x}, {y}): {sources[hit.Draw].Name}, triangle {hit.Triangle}: the pixel shader wrote {color.x:F4} {color.y:F4} {color.z:F4} {color.w:F4} on the CPU{gpu}");
        Show($"Csl pixel ({x}, {y}): {sources[hit.Draw].Name}, triangle {hit.Triangle}\nCPU  {color.x:F4} {color.y:F4} {color.z:F4} {color.w:F4}\n{gpuLine}\n{diffLine}",
            diffLine.EndsWith("OK") ? Color.LightGreen : Color.Orange);
    }

    /// <summary>The pick's result in the window, until the next pick or for a few seconds.</summary>
    private void Show(string text, Color color)
    {
        overlay = text.Split('\n');
        overlayColor = color;
        overlayUntil = DateTime.Now.AddSeconds(15);
    }

    private string[]? overlay;
    private Color overlayColor;
    private DateTime overlayUntil;
    private FastTextRenderer? textRenderer;
    private SpriteBatch? spriteBatch;
    private Texture? white;
    private const int OverlayScale = 2;

    /// <summary>The overlay, drawn at twice the debug font's size over a dark box, on the back buffer.</summary>
    private void DrawOverlay()
    {
        if (overlay == null || DateTime.Now > overlayUntil)
            return;
        var context = game.GraphicsContext;
        var backBuffer = game.GraphicsDevice.Presenter.BackBuffer;
        context.CommandList.SetRenderTargetAndViewport(null, backBuffer);
        textRenderer ??= new FastTextRenderer(context) { DebugSpriteFont = Content.Load<Texture>("/Stride.Engine/StrideDebugSpriteFont") };
        spriteBatch ??= new SpriteBatch(game.GraphicsDevice);
        white ??= Texture.New2D(game.GraphicsDevice, 1, 1, PixelFormat.R8G8B8A8_UNorm, new[] { Color.White });

        int glyphWidth = textRenderer.GlyphWidth * OverlayScale, lineHeight = textRenderer.GlyphHeight * OverlayScale, margin = 10;
        var width = overlay.Max(l => l.Length) * glyphWidth + 2 * margin;
        spriteBatch.Begin(context);
        spriteBatch.Draw(white, new RectangleF(0, 0, width, overlay.Length * lineHeight + 2 * margin), new Color(0, 0, 0, 190));
        spriteBatch.End();

        // The renderer lays the glyphs out in clip space at their size: scaled about the top left corner.
        textRenderer.MatrixTransform = Matrix.Scaling(OverlayScale, OverlayScale, 1) * Matrix.Translation(OverlayScale - 1, 1 - OverlayScale, 0);
        textRenderer.TextColor = overlayColor;
        textRenderer.Begin(context);
        for (int i = 0; i < overlay.Length; i++)
            textRenderer.DrawString(context, overlay[i], margin / OverlayScale, (margin + i * lineHeight) / OverlayScale);
        textRenderer.End(context);
    }
}

/// <summary>
/// Every effect the game compiles, known by its bytecode: the mixin tree the effect system built for it.
/// A link in the compiler chain, before the cache, which hands the mixin even when the bytecode is cached.
/// </summary>
[DebuggerNonUserCode]
internal sealed class EffectRecorder(EffectCompilerBase compiler) : EffectCompilerChain(compiler)
{
    private readonly ConcurrentDictionary<ObjectId, ShaderMixinSource> mixins = new();

    public override TaskOrResult<EffectBytecodeCompilerResult> Compile(ShaderMixinSource mixinTree, EffectCompilerParameters effectParameters, CompilerParameters compilerParameters, ObjectId effectInputHash)
    {
        var copy = new ShaderMixinSource();
        copy.DeepCloneFrom(mixinTree);
        var result = base.Compile(mixinTree, effectParameters, compilerParameters, effectInputHash);
        // Compiled at once, or (a reload, a new permutation) on a task.
        if (result.Task == null)
            Remember(result.Result, copy);
        else
            result.Task.ContinueWith(t => Remember(t.Result, copy), TaskContinuationOptions.OnlyOnRanToCompletion);
        return result;
    }

    private void Remember(EffectBytecodeCompilerResult result, ShaderMixinSource mixin)
    {
        if (result.Bytecode != null)
            mixins[result.Bytecode.ComputeId()] = mixin;
    }

    public int Count => mixins.Count;

    public ShaderMixinSource? Find(Effect effect) => mixins.TryGetValue(effect.Bytecode.ComputeId(), out var mixin) ? mixin : null;

    /// <summary>
    /// A shader changed: the effect system drops what the compilers cached of it, the source manager's
    /// in-memory sources included; the C# shaders' are handed back right away, for the effects about to
    /// compile again to find them.
    /// </summary>
    public override void ResetCache(HashSet<string> modifiedShaders)
    {
        base.ResetCache(modifiedShaders);
        var sources = ShaderSourceRegistry.Sources;
        var manager = ShaderSourceRegistry.FindLocalCompiler(this).GetFileShaderLoader().SourceManager;
        foreach (var name in modifiedShaders)
            if (sources.TryGetValue(name, out var source))
                manager.AddShaderSource(name, source.Source, source.Path);
    }
}

/// <summary>A draw as the frame had it: its effect, its constant buffers' bytes and bound resources, its geometry, its viewport.</summary>
[DebuggerNonUserCode]
internal sealed class DrawCapture
{
    public required string Name { get; init; }
    public required Effect Effect { get; init; }
    public required Viewport Viewport { get; init; }

    /// <summary>What the draw wrote to: read after the frame, the GPU's value of the pixel to set beside the CPU's.</summary>
    public Texture? RenderTarget { get; init; }
    public required Cpu.CullMode Cull { get; init; }
    public required MeshDraw MeshDraw { get; init; }
    public required List<(EffectConstantBufferDescription? Buffer, byte[]? Data, List<(string KeyName, object? Value)> Resources)> Groups { get; init; }

    /// <summary>The draw's vertices and triangles, read back (once per buffer and range).</summary>
    public CpuMesh? ReadMesh(CommandList commandList, Dictionary<(Stride.Graphics.Buffer, int, int, int), CpuMesh> cache)
    {
        var draw = MeshDraw;
        if (draw.PrimitiveType != PrimitiveType.TriangleList || draw.VertexBuffers.Length == 0)
            return null;
        var first = draw.VertexBuffers[0];
        var key = (first.Buffer, first.Offset, draw.StartLocation, draw.DrawCount);
        if (cache.TryGetValue(key, out var known))
            return known;
        int[] indices;
        if (draw.IndexBuffer is { } index)
        {
            var bytes = index.Buffer.GetData<byte>(commandList);
            int size = index.Is32Bit ? 4 : 2;
            indices = new int[draw.DrawCount];
            for (int i = 0; i < indices.Length; i++)
            {
                int at = index.Offset + (draw.StartLocation + i) * size;
                indices[i] = index.Is32Bit ? BitConverter.ToInt32(bytes, at) : BitConverter.ToUInt16(bytes, at);
            }
        }
        else
        {
            indices = Enumerable.Range(draw.StartLocation, draw.DrawCount).ToArray();
        }
        var mesh = new CpuMesh(first.Count, indices);
        foreach (var binding in draw.VertexBuffers)
        {
            var bytes = binding.Buffer.GetData<byte>(commandList);
            int stride = binding.Stride > 0 ? binding.Stride : binding.Declaration.VertexStride;
            foreach (var element in binding.Declaration.EnumerateWithOffsets())
            {
                var values = new CslTypes.float4[binding.Count];
                for (int v = 0; v < binding.Count; v++)
                {
                    int at = binding.Offset + v * stride + element.Offset;
                    if (at < bytes.Length)
                        values[v] = CpuCapture.Decode(element.VertexElement.Format, bytes, at);
                }
                mesh.Set(element.VertexElement.SemanticName + element.VertexElement.SemanticIndex, values);
            }
        }
        cache[key] = mesh;
        return mesh;
    }

    /// <summary>What each reflected member and resource got from the capture: to see why a value is missing.</summary>
    public string Describe(FlatEffect effect)
    {
        var lines = new List<string>();
        foreach (var (buffer, data, resources) in Groups)
        {
            if (buffer != null)
                foreach (var member in buffer.Members)
                    lines.Add($"  cbuffer {buffer.Name} +{member.Offset} {member.KeyInfo.KeyName} [{member.Type.Elements}]: {(data == null ? "no data" : FlatBinding.FieldsAt(effect, buffer.Name, member.Offset).FirstOrDefault() ?? "no flat member")}");
            foreach (var (keyName, value) in resources)
            {
                var binding = effect.Reflection.ResourceBindings.FirstOrDefault(b => b.KeyInfo.KeyName == keyName);
                lines.Add($"  resource {keyName}: {(binding.RawName ?? "not in the flat effect")} = {value?.GetType().Name ?? "null"}{(value is Texture t ? " " + t.Dimension + " " + t.Format : string.Empty)}");
            }
        }
        return string.Join("\n", lines);
    }
    /// <summary>Each constant buffer member onto the flat member at its offset, each resource onto the member of its raw name.</summary>
    public void Bind(FlatEffect effect, CpuMeshDraw draw, CommandList commandList)
    {
        foreach (var (buffer, data, resources) in Groups)
        {
            if (buffer != null && data != null)
            {
                foreach (var member in buffer.Members)
                    foreach (var field in FlatBinding.FieldsAt(effect, buffer.Name, member.Offset))
                        FlatBinding.Set(draw, field, type => CpuCapture.ValueOf(data, member.Offset, Math.Max(1, member.Type.Elements), type));
            }
            foreach (var (keyName, value) in resources)
            {
                var binding = effect.Reflection.ResourceBindings.FirstOrDefault(b => b.KeyInfo.KeyName == keyName);
                if (binding.RawName == null || value == null)
                    continue;
                FlatBinding.Set(draw, binding.RawName, type => CpuCapture.ResourceOf(value, type, commandList));
            }
        }
        // Samplers the shaders describe themselves, bound by the pipeline rather than a group.
        foreach (var sampler in effect.Reflection.SamplerStates)
        {
            var binding = effect.Reflection.ResourceBindings.FirstOrDefault(b => b.KeyInfo.KeyName == sampler.KeyName);
            if (binding.RawName == null || Groups.Any(g => g.Resources.Any(r => r.KeyName == sampler.KeyName && r.Value != null)))
                continue;
            var description = CpuCapture.Describe(sampler.Description);
            FlatBinding.Set(draw, binding.RawName, type => type == typeof(CslTypes.SamplerComparisonState) ? new CslTypes.SamplerComparisonState(description) : new CslTypes.SamplerState(description));
        }
    }
}

/// <summary>
/// A sub-feature of the mesh feature that, when a pick is pending, copies each draw of the main view as
/// the frame has it, right before the mesh feature draws it: nodes and constant buffers are reset
/// once the frame is drawn.
/// </summary>
[DebuggerNonUserCode]
internal sealed class CaptureFeature(CslDebugSystem system) : SubRenderFeature
{
    private static readonly FieldInfo HeapObjects = typeof(DescriptorSet).GetField("HeapObjects", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StartOffset = typeof(DescriptorSet).GetField("DescriptorStartOffset", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo LayoutEntries = typeof(DescriptorSetLayoutBuilder).GetField("Entries", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// After the mesh feature's draws, before the post effects reuse the targets: a copy of each target a
    /// captured draw wrote to, the GPU's pixels to set beside the CPU's.
    /// </summary>
    public override void Flush(RenderDrawContext context)
    {
        base.Flush(context);
        if (system.PendingPick == null)
            return;
        foreach (var target in system.Captured.Select(c => c.RenderTarget).Where(t => t != null && t.MultisampleCount == MultisampleCount.None).Distinct())
        {
            if (system.TargetCopies.ContainsKey(target!))
                continue;
            var copy = Texture.New2D(context.GraphicsDevice, target!.Width, target.Height, target.Format, TextureFlags.ShaderResource);
            context.CommandList.Copy(target, copy);
            system.TargetCopies[target] = copy;
        }
    }
    public override void Draw(RenderDrawContext context, RenderView renderView, RenderViewStage renderViewStage, int startIndex, int endIndex)
    {
        if (system.PendingPick == null || renderView.GetType() != typeof(RenderView))
            return;
        var stageName = Context.RenderSystem.RenderStages[renderViewStage.Index].Name;
        if (!stageName.Contains("Opaque", StringComparison.OrdinalIgnoreCase) && !stageName.Contains("Transparent", StringComparison.OrdinalIgnoreCase))
            return;
        var root = (RootEffectRenderFeature)RootRenderFeature;
        for (int index = startIndex; index < endIndex; index++)
        {
            var reference = renderViewStage.SortedRenderNodes[index].RenderNode;
            var node = root.GetRenderNode(reference);
            if (node.RenderObject is not RenderMesh mesh || node.RenderEffect?.Effect == null)
                continue;
            var effect = node.RenderEffect;
            var groups = new List<(EffectConstantBufferDescription?, byte[]?, List<(string, object?)>)>();
            int offset = reference.Index * root.EffectDescriptorSetSlotCount;
            for (int slot = 0; slot < root.EffectDescriptorSetSlotCount && slot < effect.Reflection.ResourceGroupDescriptions.Length; slot++)
            {
                var group = root.ResourceGroupPool[offset + slot];
                var description = effect.Reflection.ResourceGroupDescriptions[slot];
                if (group == null)
                    continue;
                byte[]? data = null;
                if (description.ConstantBufferReflection != null && group.ConstantBuffer.Data != IntPtr.Zero && group.ConstantBuffer.Size > 0)
                {
                    data = new byte[group.ConstantBuffer.Size];
                    Marshal.Copy(group.ConstantBuffer.Data, data, 0, data.Length);
                }
                var resources = new List<(string, object?)>();
                if (group.DescriptorSet.IsValid && description.DescriptorSetLayout != null)
                {
                    var heap = (Array?)HeapObjects.GetValue(group.DescriptorSet);
                    int start = (int)StartOffset.GetValue(group.DescriptorSet)!;
                    var entries = (System.Collections.IList)LayoutEntries.GetValue(description.DescriptorSetLayout)!;
                    int at = start;
                    foreach (var entry in entries)
                    {
                        var key = (ParameterKey)entry.GetType().GetProperty("Key")!.GetValue(entry)!;
                        int arraySize = (int)entry.GetType().GetProperty("ArraySize")!.GetValue(entry)!;
                        var value = heap != null && at < heap.Length ? heap.GetValue(at)?.GetType().GetField("Value")?.GetValue(heap.GetValue(at)) : null;
                        resources.Add((key.Name, value));
                        at += Math.Max(1, arraySize);
                    }
                }
                groups.Add((description.ConstantBufferReflection, data, resources));
            }
            var cull = mesh.MaterialPass?.CullMode switch
            {
                Stride.Graphics.CullMode.None => Cpu.CullMode.None,
                Stride.Graphics.CullMode.Front => Cpu.CullMode.Front,
                _ => Cpu.CullMode.Back,
            };
            if (mesh.IsScalingNegative && cull != Cpu.CullMode.None)
                cull = cull == Cpu.CullMode.Back ? Cpu.CullMode.Front : Cpu.CullMode.Back;
            system.Captured.Add(new DrawCapture
            {
                Name = (mesh.Source as ModelComponent)?.Entity?.Name ?? mesh.Mesh?.Name ?? "a mesh",
                Effect = effect.Effect,
                Viewport = context.CommandList.Viewport,
                RenderTarget = context.CommandList.RenderTarget,
                Cull = cull,
                MeshDraw = mesh.ActiveMeshDraw,
                Groups = groups,
            });
        }
    }
}
