using Csl.TestApp.Shaders;
using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Csl.TestApp;

/// <summary>
/// The C# shaders on the GPU: a game that runs each test on its second frame, reads the results
/// back and compares them with what the CPU computes. Then, unless --check, the window shows the
/// image shaders of Demos/ and redraws each one from its C# when it is saved (<see cref="DemoGallery"/>).
/// </summary>
internal sealed class GpuTests : Game
{
    private enum Mode
    {
        /// <summary>The tests, then the demos in a window until it is closed.</summary>
        Show,
        /// <summary>The tests only, in a hidden window.</summary>
        Check,
        /// <summary>The tests, then the demos compiled from their files and drawn once, saved as an image; hidden.</summary>
        Shot,
    }

    private const int Count = 64;
    private readonly List<(string Name, bool Passed, string Detail)> results = new();
    private readonly List<(string Name, string Observed)> probes = new();
    private readonly Mode mode;
    private readonly string? shotPath;
    private readonly float shotTime;
    private DemoGallery? gallery;
    private int frame;

    public static int Run(string[] args)
    {
        var mode = args.Contains("--check") ? Mode.Check : Mode.Show;
        string? shotPath = null;
        float shotTime = 2.0f;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--shot")
            {
                mode = Mode.Shot;
                shotPath = Path.GetFullPath(args[i + 1]);
            }
            else if (args[i] == "--time")
            {
                shotTime = float.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        using var game = new GpuTests(mode, shotPath, shotTime);
        game.Run();
        return game.results.Count > 0 && game.results.All(r => r.Passed) ? 0 : 1;
    }

    private GpuTests(Mode mode, string? shotPath, float shotTime)
    {
        this.mode = mode;
        this.shotPath = shotPath;
        this.shotTime = shotTime;
        // No GameSettings to follow (their default profile is 10_0): typed UAVs (RWBuffer<T>) need 11_0 on Direct3D 11.
        AutoLoadDefaultSettings = false;
        GraphicsDeviceManager.PreferredGraphicsProfile = new[] { GraphicsProfile.Level_11_0 };
        GraphicsDeviceManager.PreferredBackBufferWidth = mode == Mode.Check ? 64 : 1280;
        GraphicsDeviceManager.PreferredBackBufferHeight = mode == Mode.Check ? 64 : 720;
        // The demos' colours as they write them, no sRGB curve on top.
        GraphicsDeviceManager.PreferredColorSpace = ColorSpace.Gamma;
        // The effect compiler's notes on every demo recompiled would bury the C# errors; warnings stay.
        ConsoleLogLevel = LogMessageType.Warning;
    }

    protected override void BeginRun()
    {
        base.BeginRun();
        Window.Visible = mode == Mode.Show;
        Window.AllowUserResizing = true;
        Window.Title = "StrideCSL gpu";
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (gallery != null && mode == Mode.Show)
        {
            gallery.Update(Input);
            Window.Title = gallery.Title;
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        base.Draw(gameTime);
        // The first frame sets the device up; the tests run on the second, inside a frame.
        if (++frame == 2)
        {
            RunTests();
            if (mode == Mode.Check)
            {
                Exit();
                return;
            }
            gallery = new DemoGallery(Services, GraphicsDevice, Csl.ShaderContext.Get(Services).RenderContext);
        }
        if (gallery == null)
            return;
        var context = Csl.ShaderContext.Get(Services);
        var backBuffer = GraphicsDevice.Presenter.BackBuffer;
        if (mode == Mode.Shot)
        {
            if (!gallery.ReloadAllNow())
                Console.WriteLine("SHOT the demos did not compile from their files: drawn as built");
            gallery.Draw(context.DrawContext, backBuffer, shotTime);
            using (var file = File.Create(shotPath!))
                backBuffer.Save(CommandList, file, ImageFileType.Png);
            Console.WriteLine($"SHOT {shotPath} at t = {shotTime}");
            Exit();
            return;
        }
        gallery.Draw(context.DrawContext, backBuffer, (float)gameTime.Total.TotalSeconds);
    }

    protected override void Destroy()
    {
        gallery?.Dispose();
        base.Destroy();
    }

    private void RunTests()
    {
        Test("a shader written in C#", Squares);
        Test("an engine shader mixed in (ColorUtility)", ColorLinear);
        Test("an engine shader replaced by its modified C# (LuminanceUtils)", Luma);
        Test("an engine graphics shader extended (ImageEffectShader)", Invert);
        Test("a typed buffer with unordered access (RWBuffer<T>)", TypedBuffer);
        Probe("uint division inside a vector constructor", ProbeDivision);
        foreach (var (name, passed, detail) in results)
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : string.Empty)}");
        if (results.Count == 0)
            Console.WriteLine("FAIL no test ran");
        // What the engine does, whatever the C# shaders: reported, not counted.
        foreach (var (name, observed) in probes)
            Console.WriteLine($"ENGINE {name}: {observed}");
    }

    private void Test(string name, Func<string?> test)
    {
        try
        {
            var failure = test();
            results.Add((name, failure == null, failure ?? string.Empty));
        }
        catch (Exception e)
        {
            var frames = (e.StackTrace ?? string.Empty).Split('\n').Take(6).Select(l => l.Trim());
            results.Add((name, false, e.GetType().Name + ": " + e.Message + Environment.NewLine + "      " + string.Join(Environment.NewLine + "      ", frames)));
        }
    }

    private void Probe(string name, Func<string> probe)
    {
        try
        {
            probes.Add((name, probe()));
        }
        catch (Exception e)
        {
            probes.Add((name, e.GetType().Name + ": " + e.Message));
        }
    }

    private CommandList CommandList => GraphicsContext.CommandList;

    /// <summary>The first element that differs from the expected value, or null.</summary>
    private static string? Compare<T>(T[] actual, Func<int, T> expected, Func<T, T, bool> equal)
    {
        for (int i = 0; i < actual.Length; i++)
        {
            var wanted = expected(i);
            if (!equal(actual[i], wanted))
                return $"element {i} is {actual[i]}, expected {wanted}";
        }
        return null;
    }

    private static bool Close(float a, float b) => MathF.Abs(a - b) <= 1e-5f * MathF.Max(1f, MathF.Abs(b));

    private string? Squares()
    {
        using var output = Csl.Buffers.NewStructured<uint>(GraphicsDevice, Count, CslSquaresEffect.Slots.Output);
        using var effect = new CslSquaresEffect(Services) { Output = output, Offset = 7 };
        effect.Dispatch(Count);
        return Compare(output.GetData<uint>(CommandList), i => (uint)(i * i + 7), (a, b) => a == b);
    }

    private string? TypedBuffer()
    {
        using var output = Csl.Buffers.NewTyped<uint>(GraphicsDevice, Count, CslTypedBufferEffect.Slots.Output);
        using var effect = new CslTypedBufferEffect(Services) { Output = output };
        effect.Dispatch(Count);
        return Compare(output.GetData<uint>(CommandList), i => (uint)(i * 3), (a, b) => a == b);
    }

    private string? ColorLinear()
    {
        using var output = Csl.Buffers.NewStructured<float>(GraphicsDevice, Count, CslColorLinearEffect.Slots.Output);
        using var effect = new CslColorLinearEffect(Services) { Output = output };
        effect.Dispatch(Count);
        // ColorUtility.ToLinear, the engine's approximation of the sRGB curve.
        return Compare(output.GetData<float>(CommandList), i =>
        {
            float s = i / 63.0f;
            return s * (s * (s * 0.305306011f + 0.682171111f) + 0.012522878f);
        }, Close);
    }

    private string? Luma()
    {
        using var output = Csl.Buffers.NewStructured<float>(GraphicsDevice, Count, CslLumaEffect.Slots.Output);
        using var effect = new CslLumaEffect(Services) { Output = output };
        effect.Dispatch(Count);
        var actual = output.GetData<float>(CommandList);
        Vector3 Color(int i) => new Vector3(i % 4, i / 4 % 4, i / 16 % 4) / 3.0f;
        // The modified shader's result: the brightest channel, not the 601 luma the engine computes.
        var failure = Compare(actual, i => { var c = Color(i); return MathF.Max(c.X, MathF.Max(c.Y, c.Z)); }, Close);
        if (failure != null)
        {
            var engine = Compare(actual, i => MathF.Max(Vector3.Dot(Color(i), new Vector3(0.299f, 0.587f, 0.114f)), 0.0001f), Close);
            return failure + (engine == null ? " (the engine's LuminanceUtils ran: the C# replacement was not used)" : string.Empty);
        }
        return null;
    }

    private string ProbeDivision()
    {
        using var output = Csl.Buffers.NewStructured<Vector2>(GraphicsDevice, Count, EngineProbeDivisionEffect.Slots.Output);
        using var effect = new EngineProbeDivisionEffect(Services) { Output = output };
        effect.Dispatch(Count);
        var actual = output.GetData<Vector2>(CommandList);
        // HLSL: float3(i / 4, 0, 0).x is floor(i / 4), as the local is.
        var wrong = Enumerable.Range(0, Count).Where(i => actual[i].X != actual[i].Y).ToList();
        if (wrong.Count == 0)
            return "divides as integers, as HLSL does";
        int first = wrong[0];
        return $"float3(i / 4, 0, 0).x is {actual[first].X} for i = {first}, the local i / 4 is {actual[first].Y}: the division is done in float ({wrong.Count} of {Count} threads)";
    }

    private string? Invert()
    {
        const int size = 8;
        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color((byte)(i * 4), (byte)(255 - i * 3), (byte)(i * 2 + 10), (byte)200);
        using var input = Texture.New2D(GraphicsDevice, size, size, PixelFormat.R8G8B8A8_UNorm, pixels);
        using var output = Texture.New2D(GraphicsDevice, size, size, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        using var effect = new Stride.Rendering.Images.ImageEffectShader(nameof(CslInvert));
        var context = Csl.ShaderContext.Get(Services);
        effect.Initialize(context.RenderContext);
        effect.SetInput(0, input);
        effect.SetOutput(output);
        effect.Draw(context.DrawContext);
        var actual = output.GetData<Color>(CommandList);
        return Compare(actual, i => new Color((byte)(255 - pixels[i].R), (byte)(255 - pixels[i].G), (byte)(255 - pixels[i].B), pixels[i].A),
            (a, b) => Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1 && Math.Abs(a.A - b.A) <= 1);
    }
}
