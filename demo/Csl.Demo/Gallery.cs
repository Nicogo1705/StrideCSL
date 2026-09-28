using System.Text.RegularExpressions;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Input;
using Stride.Rendering;
using Stride.Rendering.Images;

namespace Csl.Demo;

/// <summary>
/// The image shaders of Shaders/ drawn side by side, and redrawn from their C# as it is saved: the
/// folder is compiled again (<see cref="LiveCompiler"/>) and each shader whose SDSL changed comes back
/// under a new name, so the effect compiler has nothing cached for it; so do the shaders that use it
/// (a tile inheriting DemoTile when DemoTile changes), their SDSL pointing to the new names. One that
/// does not compile leaves the previous one on screen and its errors in the console.
/// </summary>
internal sealed class Gallery : IDisposable
{
    private sealed class Tile(string name, ImageEffectShader? effect)
    {
        public string Name { get; } = name;
        /// <summary>The C# class name without the prefix, for the title.</summary>
        public string Label => Name.StartsWith("Demo", StringComparison.Ordinal) ? Name[4..] : Name;
        public ImageEffectShader? Effect = effect;
        public ImageEffectShader? Candidate;
    }

    private readonly List<Tile> tiles = new();
    /// <summary>Every shader of the folder: the SDSL last taken from its C#, and the name it is registered under.</summary>
    private readonly Dictionary<string, (string Sdsl, string Registered)> shaders = new(StringComparer.Ordinal);
    private int generation;
    private readonly RenderContext renderContext;
    private readonly Texture checker;
    private readonly BlurPass blur;
    private readonly LiveCompiler? compiler;
    private readonly FileSystemWatcher? watcher;
    private readonly object changeLock = new();
    private DateTime? changedAt;
    private Task<LiveCompiler.Result>? compiling;
    private bool hadErrors;
    private int solo = -1;
    /// <summary>The classes of the last compilation of Shaders/, which the CPU runs: what was saved last.</summary>
    private System.Reflection.Assembly? liveAssembly;
    /// <summary>Where each tile was drawn last, for a click to find its pixel.</summary>
    private readonly List<(Tile Tile, Viewport Viewport)> layout = new();
    /// <summary>A Ctrl+click waiting for the next draw: where, as a fraction of the window.</summary>
    private Vector2? pixelRequest;
    private Csl.Cpu.CpuTexture? cpuChecker;

    public Gallery(IServiceRegistry services, GraphicsDevice device, RenderContext renderContext)
    {
        this.renderContext = renderContext;
        blur = new BlurPass(services);
        // The demos are the C# shaders of this app that live in the Shaders folder: their SDSL was
        // registered at start-up by the build, so they draw before anything is compiled here.
        var demos = ShaderSourceRegistry.Sources
            .Where(s => string.Equals(Path.GetFileName(Path.GetDirectoryName(s.Value.Path)), "Shaders", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .ToList();
        // The image shaders are tiles; the compute one, DemoBlur, runs over all of them; the others
        // (DemoTile, DemoCommon) are only used by those.
        foreach (var (name, (source, _)) in demos)
        {
            shaders[name] = (source, name);
            if (KindOf(name) == LiveCompiler.ShaderKind.Image)
                tiles.Add(new Tile(name, NewEffect(name)));
        }

        checker = MakeChecker(device);

        var directory = demos.Select(s => Path.GetDirectoryName(s.Value.Path)).FirstOrDefault();
        if (directory != null && Directory.Exists(directory))
        {
            compiler = new LiveCompiler(directory);
            watcher = new FileSystemWatcher(directory, "*.cs") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            watcher.Changed += (_, _) => Touch();
            watcher.Created += (_, _) => Touch();
            watcher.Renamed += (_, _) => Touch();
            watcher.EnableRaisingEvents = true;
            // Roslyn takes a few seconds the first time: warm it up now rather than on the first save.
            compiling = Task.Run(() => compiler.Compile());
            Console.WriteLine($"Watching {directory}: save a .cs there and its tile is redrawn from it.");
        }
        else
        {
            Console.WriteLine("The Shaders folder is not where the build found it: no reload, the demos stay as built.");
        }
    }

    /// <summary>What the window title says.</summary>
    public string Title
    {
        get
        {
            var names = string.Join("  ", tiles.Select((t, i) => $"{i + 1} {t.Label}"));
            var shown = solo >= 0 ? $"{tiles[solo].Label} (0: all)" : names;
            var post = blur.Enabled ? $"B blur r{blur.Radius} (+/-)" : "B blur off";
            return $"StrideCSL demo - {shown} - {post} - edit Shaders/*.cs, it redraws";
        }
    }

    /// <summary>The blur's radius; 0 turns it off.</summary>
    public void SetBlur(int radius)
    {
        blur.Radius = Math.Clamp(radius, 0, 16);
        blur.Enabled = radius > 0;
    }

    private void Touch()
    {
        lock (changeLock)
            changedAt = DateTime.UtcNow;
    }

    /// <summary>Keys, and the compilation of what was saved; on the game's thread.</summary>
    public void Update(InputManager input)
    {
        for (int i = 0; i < Math.Min(tiles.Count, 9); i++)
        {
            if (input.IsKeyPressed(Keys.D1 + i) || input.IsKeyPressed(Keys.NumPad1 + i))
                solo = solo == i ? -1 : i;
        }
        if (input.IsKeyPressed(Keys.D0) || input.IsKeyPressed(Keys.NumPad0) || input.IsKeyPressed(Keys.Space))
            solo = -1;
        if (input.IsKeyPressed(Keys.B))
            blur.Enabled = !blur.Enabled;
        if (input.IsMouseButtonPressed(MouseButton.Left) && (input.IsKeyDown(Keys.LeftCtrl) || input.IsKeyDown(Keys.RightCtrl)))
            pixelRequest = input.MousePosition;
        if (input.IsKeyPressed(Keys.Add) || input.IsKeyPressed(Keys.OemPlus))
            blur.Radius = Math.Min(blur.Radius + 1, 16);
        if (input.IsKeyPressed(Keys.Subtract) || input.IsKeyPressed(Keys.OemMinus))
            blur.Radius = Math.Max(blur.Radius - 1, 0);

        if (compiler == null)
            return;
        if (compiling == null)
        {
            lock (changeLock)
            {
                // Editors write a file in several steps: wait for them to be done.
                if (changedAt is { } at && DateTime.UtcNow - at > TimeSpan.FromMilliseconds(200))
                {
                    changedAt = null;
                    compiling = Task.Run(() => compiler.Compile());
                }
            }
        }
        else if (compiling.IsCompleted)
        {
            var task = compiling;
            compiling = null;
            if (task.IsFaulted)
                Console.WriteLine("Compiling Shaders/ failed: " + task.Exception!.GetBaseException().Message);
            else
                Apply(task.Result, force: false);
        }
    }

    /// <summary>Compiles Shaders/ now and takes every shader of it, changed or not: the whole reload path at once.</summary>
    public bool ReloadAllNow()
    {
        if (compiler == null)
            return false;
        compiling?.Wait();
        compiling = null;
        return Apply(compiler.Compile(), force: true);
    }

    private bool Apply(LiveCompiler.Result result, bool force)
    {
        if (result.Errors.Count > 0)
        {
            Console.WriteLine($"C# errors in Shaders/ ({result.Errors.Count}), the tiles stay as they were:");
            foreach (var error in result.Errors)
                Console.WriteLine("  " + error);
            hadErrors = true;
            return false;
        }
        if (hadErrors)
            Console.WriteLine("Shaders/ compiles again.");
        hadErrors = false;
        liveAssembly = result.Assembly ?? liveAssembly;

        var compiled = result.Shaders.ToDictionary(r => r.ShaderName, StringComparer.Ordinal);
        var changed = compiled.Values
            .Where(r => force || !shaders.TryGetValue(r.ShaderName, out var known) || known.Sdsl != r.Sdsl)
            .Select(r => r.ShaderName)
            .ToHashSet(StringComparer.Ordinal);
        // A shader that uses a changed one is compiled again with it: DemoTile changed, every tile
        // inheriting it too.
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var r in compiled.Values)
            {
                if (!changed.Contains(r.ShaderName) && changed.Any(c => Regex.IsMatch(r.Sdsl, $@"\b{Regex.Escape(c)}\b")))
                    grew = changed.Add(r.ShaderName);
            }
        }
        if (changed.Count == 0)
            return true;

        // A new name each time: the effect compiler caches by shader, and these are new to it.
        generation++;
        foreach (var name in changed)
            shaders[name] = (compiled[name].Sdsl, $"{name}_{generation}");
        var anyName = new Regex(@"\b(" + string.Join("|", shaders.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")\b");
        foreach (var name in changed.Order(StringComparer.Ordinal))
        {
            var (_, sdsl, path, kind) = compiled[name];
            var registered = shaders[name].Registered;
            ShaderSourceRegistry.Add(registered, anyName.Replace(sdsl, m => shaders[m.Value].Registered), path);
            AliasKeys(name, registered);
            switch (kind)
            {
                case LiveCompiler.ShaderKind.Image:
                    var tile = tiles.FirstOrDefault(t => t.Name == name);
                    if (tile == null)
                    {
                        tile = new Tile(name, null);
                        tiles.Add(tile);
                        Console.WriteLine($"{name}: new demo, tile {tiles.Count}");
                    }
                    tile.Candidate?.Dispose();
                    tile.Candidate = NewEffect(registered);
                    break;
                case LiveCompiler.ShaderKind.Compute when name == Shaders.DemoBlur.ShaderName:
                    blur.Offer(registered);
                    break;
                case LiveCompiler.ShaderKind.Compute:
                    Console.WriteLine($"{name}: a compute shader; only DemoBlur has a place in the gallery");
                    break;
                default:
                    if (!force)
                        Console.WriteLine($"{name}: changed, the shaders using it follow");
                    break;
            }
        }
        return true;
    }

    /// <summary>
    /// The compiled effect names a shader's parameters after the shader, DemoTile_3.Aspect: registered as
    /// aliases of the keys the build generated (DemoTileKeys.Aspect), what the app sets keeps working.
    /// </summary>
    private static void AliasKeys(string shaderName, string registered)
    {
        var keys = typeof(Shaders.DemoTile).Assembly.GetType($"{typeof(Shaders.DemoTile).Namespace}.{shaderName}Keys");
        foreach (var key in keys?.GetFields().Select(f => f.GetValue(null)).OfType<ParameterKey>() ?? [])
            ParameterKeys.Merge(key, null, registered + key.Name[key.Name.IndexOf('.')..]);
    }

    public void Draw(RenderDrawContext context, Texture backBuffer, float time)
    {
        // With the blur on, the tiles draw into its input and it draws the back buffer.
        var target = blur.Enabled ? blur.SceneFor(backBuffer) : backBuffer;
        DrawTiles(context, target, time);
        if (pixelRequest is { } request)
        {
            pixelRequest = null;
            DebugPixel(context, target, request, time);
        }
        if (blur.Enabled)
            blur.Apply(context, backBuffer);
    }

    private void DrawTiles(RenderDrawContext context, Texture target, float time)
    {
        context.CommandList.Clear(target, Background);
        layout.Clear();
        foreach (var (tile, viewport) in Layout(target.Width, target.Height))
        {
            DrawTile(context, tile, target, viewport, time);
            layout.Add((tile, viewport));
        }
    }

    private static readonly Color4 Background = new Color4(0.08f, 0.08f, 0.09f, 1.0f);

    /// <summary>Where each tile goes in a target of this size: a grid, or the one shown alone.</summary>
    private List<(Tile Tile, Viewport Viewport)> Layout(int targetWidth, int targetHeight)
    {
        var result = new List<(Tile, Viewport)>();
        if (tiles.Count == 0)
            return result;
        if (solo >= tiles.Count)
            solo = -1;
        const int gap = 4;
        int count = solo >= 0 ? 1 : tiles.Count;
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (count + columns - 1) / columns;
        float width = (targetWidth - gap * (columns + 1)) / (float)columns;
        float height = (targetHeight - gap * (rows + 1)) / (float)rows;
        for (int slot = 0; slot < count; slot++)
        {
            int column = slot % columns, row = slot / columns;
            result.Add((tiles[solo >= 0 ? solo : slot], new Viewport(gap + column * (width + gap), gap + row * (height + gap), width, height)));
        }
        return result;
    }

    private void DrawTile(RenderDrawContext context, Tile tile, Texture target, Viewport viewport, float time)
    {
        if (tile.Candidate is { } candidate)
        {
            tile.Candidate = null;
            var error = TryDraw(context, candidate, target, viewport, time);
            if (error == null)
            {
                tile.Effect?.Dispose();
                tile.Effect = candidate;
                Console.WriteLine($"{tile.Name}: redrawn from its C# ({candidate.EffectName})");
                return;
            }
            candidate.Dispose();
            Console.WriteLine($"{tile.Name}: the SDSL does not compile, the previous one stays:");
            Console.WriteLine("  " + error.Replace("\n", "\n  "));
        }
        if (tile.Effect != null && TryDraw(context, tile.Effect, target, viewport, time) is { } failure)
        {
            Console.WriteLine($"{tile.Name}: {failure}");
            tile.Effect.Dispose();
            tile.Effect = null;
        }
    }

    /// <summary>
    /// Ctrl+click: the pixel under the mouse run again on the CPU (Csl.Cpu), from the C# last saved, with
    /// the tile's viewport, time and inputs; its colour printed next to the GPU's. With a debugger
    /// attached, it stops right before that pixel's lane: step into (F11) to walk the shader's C#.
    /// </summary>
    private void DebugPixel(RenderDrawContext context, Texture target, Vector2 at, float time)
    {
        int px = (int)(at.X * target.Width), py = (int)(at.Y * target.Height);
        var (tile, viewport) = layout.FirstOrDefault(l => px >= l.Viewport.X && px < l.Viewport.X + l.Viewport.Width && py >= l.Viewport.Y && py < l.Viewport.Y + l.Viewport.Height);
        if (tile == null)
            return;
        var type = CpuType(tile);
        if (type == null)
            return;
        var gpu = target.GetData<Color>(context.CommandList)[py * target.Width + px];
        try
        {
            var run = CpuTile(type, viewport, time, out int left, out int top);
            run.Break = System.Diagnostics.Debugger.IsAttached;
            var cpu = run.DrawPixel(px - left, py - top);
            var cpuColor = new Color(cpu.x, cpu.y, cpu.z, cpu.w);
            Console.WriteLine($"{tile.Name} pixel ({px}, {py}) at t = {time:F3}: GPU {gpu.R} {gpu.G} {gpu.B} {gpu.A}, CPU {cpuColor.R} {cpuColor.G} {cpuColor.B} {cpuColor.A}"
                + (liveAssembly == null ? " (CPU: the build's C#)" : string.Empty));
        }
        catch (Exception e)
        {
            Console.WriteLine($"{tile.Name} pixel ({px}, {py}): the CPU run failed: {e.GetBaseException().Message}");
        }
    }

    /// <summary>The C# class of a tile the CPU runs: the one last saved, else the build's.</summary>
    private Type? CpuType(Tile tile)
        => liveAssembly?.GetType($"{typeof(Shaders.DemoTile).Namespace}.{tile.Name}") ?? Type.GetType($"{typeof(Shaders.DemoTile).Namespace}.{tile.Name}");

    /// <summary>
    /// A tile as the CPU runs it: over the pixels its viewport covers (from <paramref name="left"/>,
    /// <paramref name="top"/>), TexCoord across the viewport, SV_Position in the window, the GPU draw's inputs.
    /// </summary>
    private Csl.Cpu.CpuImageEffect CpuTile(Type type, Viewport viewport, float time, out int left, out int top)
    {
        cpuChecker ??= new Csl.Cpu.CpuTexture(256, 256, format: Csl.Cpu.TexelFormat.Rgba8UNorm).FillRgba8(System.Runtime.InteropServices.MemoryMarshal.AsBytes(CheckerPixels(out _).AsSpan()));
        int x0 = (int)MathF.Floor(viewport.X), y0 = (int)MathF.Floor(viewport.Y);
        int width = (int)MathF.Ceiling(viewport.X + viewport.Width) - x0, height = (int)MathF.Ceiling(viewport.Y + viewport.Height) - y0;
        var run = new Csl.Cpu.CpuImageEffect(type, width, height)
        {
            PixelInputs = (shader, x, y) =>
            {
                Csl.Cpu.Members.Set(shader, "TexCoord", new Csl.Types.float2((x0 + x + 0.5f - viewport.X) / viewport.Width, (y0 + y + 0.5f - viewport.Y) / viewport.Height));
                Csl.Cpu.Members.Set(shader, "ShadingPosition", new Csl.Types.float4(x0 + x + 0.5f, y0 + y + 0.5f, 0f, 1f));
            },
        };
        run.Set("Time", time);
        if (Csl.Cpu.Members.InstanceFields(type).ContainsKey("Aspect"))
            run.Set("Aspect", viewport.Width / viewport.Height);
        run.Set("Texture0", new Csl.Types.Texture2D(cpuChecker));
        left = x0;
        top = y0;
        return run;
    }

    /// <summary>
    /// The whole frame computed by the CPU (Csl.Cpu), no shader on the GPU: every tile run over the pixels
    /// its viewport covers, the blur dispatched over them, the result uploaded and drawn. Computed once,
    /// then drawn as it is until the layout, the blur or the C# changes.
    /// </summary>
    public void DrawOnCpu(RenderDrawContext context, Texture backBuffer, float time)
    {
        if (cpuFrame == null || cpuFrame.Width != backBuffer.Width || cpuFrame.Height != backBuffer.Height)
        {
            cpuFrame?.Dispose();
            cpuFrame = Texture.New2D(context.GraphicsDevice, backBuffer.Width, backBuffer.Height, PixelFormat.R8G8B8A8_UNorm, ComputeCpuFrame(backBuffer.Width, backBuffer.Height, time));
        }
        context.CommandList.SetRenderTargetAndViewport(null, backBuffer);
        context.GraphicsContext.DrawTexture(cpuFrame);
    }

    private Texture? cpuFrame;
    private object? cpuFrameSignature;

    private Color[] ComputeCpuFrame(int width, int height, float time)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var frame = new Csl.Cpu.CpuTexture(width, height, format: Csl.Cpu.TexelFormat.Rgba8UNorm);
        var clear = new Csl.Types.float4(Background.R, Background.G, Background.B, Background.A);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                frame.Write(0, x, y, 0, clear);
        foreach (var (tile, viewport) in Layout(width, height))
        {
            var type = CpuType(tile);
            if (type == null)
                continue;
            try
            {
                var image = CpuTile(type, viewport, time, out int left, out int top).Draw();
                // The pixels whose centre is in the viewport, as the rasterizer covers them.
                for (int y = 0; y < image.Height; y++)
                    for (int x = 0; x < image.Width; x++)
                    {
                        float cx = left + x + 0.5f, cy = top + y + 0.5f;
                        if (cx >= viewport.X && cx < viewport.X + viewport.Width && cy >= viewport.Y && cy < viewport.Y + viewport.Height && left + x < width && top + y < height)
                            frame.Write(0, left + x, top + y, 0, image.Read(0, x, y));
                    }
            }
            catch (Exception e)
            {
                Console.WriteLine($"{tile.Name}: the CPU run failed: {e.GetBaseException().Message}");
            }
        }
        var tilesTime = watch.Elapsed;
        if (blur.Enabled)
        {
            var output = new Csl.Cpu.CpuTexture(width, height, format: Csl.Cpu.TexelFormat.Rgba8UNorm);
            var run = new Csl.Cpu.CpuComputeShader(typeof(Shaders.DemoBlur));
            run.Set("Input", new Csl.Types.Texture2D<Csl.Types.float4>(frame));
            run.Set("Output", new Csl.Types.RWTexture2D<Csl.Types.float4>(output));
            run.Set("Size", new Csl.Types.int2(width, height));
            run.Set("Radius", blur.Radius);
            run.Dispatch((width + run.ThreadsX - 1) / run.ThreadsX, (height + run.ThreadsY - 1) / run.ThreadsY);
            frame = output;
        }
        Console.WriteLine($"CPU frame {width}x{height} at t = {time:F3}: tiles {tilesTime.TotalSeconds:F2} s" + (blur.Enabled ? $", blur r{blur.Radius} {(watch.Elapsed - tilesTime).TotalSeconds:F2} s" : string.Empty));
        var texels = frame.ToArray();
        var pixels = new Color[texels.Length];
        for (int i = 0; i < texels.Length; i++)
            pixels[i] = new Color(texels[i].x, texels[i].y, texels[i].z, texels[i].w);
        return pixels;
    }

    private static LiveCompiler.ShaderKind KindOf(string name)
        => LiveCompiler.KindOf(Type.GetType($"{typeof(Shaders.DemoTile).Namespace}.{name}"));

    private ImageEffectShader NewEffect(string shaderName)
    {
        var effect = new ImageEffectShader(shaderName);
        effect.Initialize(renderContext);
        return effect;
    }

    private string? TryDraw(RenderDrawContext context, ImageEffectShader effect, Texture target, Viewport viewport, float time)
    {
        try
        {
            effect.Parameters.Set(GlobalKeys.Time, time);
            effect.Parameters.Set(Shaders.DemoTileKeys.Aspect, viewport.Width / viewport.Height);
            effect.SetInput(0, checker);
            effect.SetOutput(target);
            effect.SetViewport(viewport);
            effect.Draw(context);
            return null;
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message.Trim();
        }
    }

    /// <summary>Texture0 of the demos: 8 by 8 coloured cells.</summary>
    private static Texture MakeChecker(GraphicsDevice device)
    {
        var pixels = CheckerPixels(out int size);
        return Texture.New2D(device, size, size, PixelFormat.R8G8B8A8_UNorm, pixels);
    }

    /// <summary>The checkerboard's pixels, row by row: for the GPU texture, and for the CPU one of <see cref="CpuCheck"/>.</summary>
    public static Color[] CheckerPixels(out int size)
    {
        const int cells = 8;
        size = 256;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int cx = x * cells / size, cy = y * cells / size;
                bool dark = ((cx + cy) & 1) == 0;
                var hue = new Color((byte)(60 + cx * 25), (byte)(60 + cy * 25), (byte)(220 - cx * 12 - cy * 12));
                pixels[y * size + x] = dark ? new Color((byte)(hue.R / 4), (byte)(hue.G / 4), (byte)(hue.B / 4)) : hue;
            }
        }
        return pixels;
    }

    public void Dispose()
    {
        watcher?.Dispose();
        foreach (var tile in tiles)
        {
            tile.Effect?.Dispose();
            tile.Candidate?.Dispose();
        }
        checker.Dispose();
        cpuFrame?.Dispose();
        blur.Dispose();
    }
}
