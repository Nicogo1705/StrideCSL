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
        if (blur.Enabled)
            blur.Apply(context, backBuffer);
    }

    private void DrawTiles(RenderDrawContext context, Texture target, float time)
    {
        context.CommandList.Clear(target, new Color4(0.08f, 0.08f, 0.09f, 1.0f));
        if (tiles.Count == 0)
            return;
        if (solo >= tiles.Count)
            solo = -1;

        const int gap = 4;
        int count = solo >= 0 ? 1 : tiles.Count;
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (count + columns - 1) / columns;
        float width = (target.Width - gap * (columns + 1)) / (float)columns;
        float height = (target.Height - gap * (rows + 1)) / (float)rows;
        for (int slot = 0; slot < count; slot++)
        {
            var tile = tiles[solo >= 0 ? solo : slot];
            int column = slot % columns, row = slot / columns;
            var viewport = new Viewport(gap + column * (width + gap), gap + row * (height + gap), width, height);
            DrawTile(context, tile, target, viewport, time);
        }
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
        const int size = 256, cells = 8;
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
        return Texture.New2D(device, size, size, PixelFormat.R8G8B8A8_UNorm, pixels);
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
        blur.Dispose();
    }
}
