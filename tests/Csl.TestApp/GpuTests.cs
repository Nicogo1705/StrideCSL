using Csl.TestApp.Shaders;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;
using Buffer = Stride.Graphics.Buffer;

namespace Csl.TestApp;

/// <summary>
/// The C# shaders on the GPU: a game that runs each test on its second frame, reads the results back,
/// compares them with what the CPU computes, and exits. The window stays hidden.
/// </summary>
internal sealed class GpuTests : Game
{
    private const int Count = 64;
    private readonly List<(string Name, bool Passed, string Detail)> results = new();
    private int frame;

    public static int Run(string[] args)
    {
        using var game = new GpuTests();
        game.Run();
        return game.results.Count > 0 && game.results.All(r => r.Passed) ? 0 : 1;
    }

    private GpuTests()
    {
        // No GameSettings to follow (their default profile is 10_0): typed UAVs (RWBuffer<T>) need 11_0 on Direct3D 11.
        AutoLoadDefaultSettings = false;
        GraphicsDeviceManager.PreferredGraphicsProfile = new[] { GraphicsProfile.Level_11_0 };
        GraphicsDeviceManager.PreferredBackBufferWidth = 64;
        GraphicsDeviceManager.PreferredBackBufferHeight = 64;
    }

    protected override void BeginRun()
    {
        base.BeginRun();
        Window.Visible = false;
    }

    protected override void Draw(GameTime gameTime)
    {
        base.Draw(gameTime);
        // The first frame sets the device up; the tests run on the second, inside a frame.
        if (++frame != 2)
            return;
        RunTests();
        Exit();
    }

    private void RunTests()
    {
        Test("a shader written in C#", Squares);
        Test("an engine shader mixed in (ColorUtility)", ColorLinear);
        Test("an engine shader replaced by its modified C# (LuminanceUtils)", Luma);
        Test("an engine graphics shader extended (ImageEffectShader)", Invert);
        Test("a typed buffer with unordered access (RWBuffer<T>)", TypedBuffer);
        Test("the CPU run's arithmetic against the GPU's (CslPrecision)", Precision);
        foreach (var (name, passed, detail) in results)
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : string.Empty)}");
        if (results.Count == 0)
            Console.WriteLine("FAIL no test ran");
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
    /// <summary>
    /// CslPrecision on both: per operation, how many ULPs the CPU run is from the GPU, and whether the
    /// GPU fused a*b+c. Informative: it fails only when the CPU run throws.
    /// </summary>
    private string? Precision()
    {
        const int threads = 512;
        int count = threads * CslPrecision.Columns;
        // From 0.01 to 10000, logarithmically, both signs.
        var inputs = Enumerable.Range(0, threads).Select(t => (t / 256 % 2 == 0 ? 1 : -1) * MathF.Pow(10f, t % 256 / 256f * 6f - 2f)).ToArray();
        using var input = Buffer.Structured.New(GraphicsDevice, inputs);
        using var output = Csl.Buffers.NewStructured<float>(GraphicsDevice, count, CslPrecisionEffect.Slots.Output);
        using var effect = new CslPrecisionEffect(Services) { Input = input, Output = output, Offset = 1.0f / 4096.0f };
        effect.Dispatch(threads);
        var gpu = output.GetData<float>(CommandList);

        var cpu = new float[count];
        var run = new Csl.Cpu.CpuComputeShader(typeof(CslPrecision));
        run.Set("Output", new Csl.Types.RWStructuredBuffer<float>(cpu));
        run.Set("Input", new Csl.Types.StructuredBuffer<float>(inputs));
        run.Set("Offset", 1.0f / 4096.0f);
        run.Dispatch(threads / 64);

        string[] names = { "x", "sin", "cos", "exp", "log", "rsqrt", "hash", "pow", "a*a-1", "x*0.1+1/3", "dot2", "dot3", "lerp", "smoothstep", "normalize.x", "length" };
        for (int column = 0; column < CslPrecision.Columns; column++)
        {
            long worst = 0;
            int differing = 0;
            float worstX = 0, worstGpu = 0, worstCpu = 0;
            for (int t = 0; t < threads; t++)
            {
                float g = gpu[t * CslPrecision.Columns + column], c = cpu[t * CslPrecision.Columns + column];
                long ulps = Ulps(g, c);
                if (ulps > 0) differing++;
                if (ulps > worst)
                {
                    worst = ulps;
                    worstX = gpu[t * CslPrecision.Columns];
                    worstGpu = g;
                    worstCpu = c;
                }
            }
            Console.WriteLine($"  {names[column],-10} {differing,4}/{threads} differ, worst {worst} ulps" + (worst > 0 ? $" at x = {worstX:R}: GPU {worstGpu:R}, CPU {worstCpu:R}" : string.Empty));
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "csl-precision.csv"), string.Join("\n", Enumerable.Range(0, threads).Select(t => string.Join(";", Enumerable.Range(0, CslPrecision.Columns).Select(k => gpu[t * CslPrecision.Columns + k].ToString("R", System.Globalization.CultureInfo.InvariantCulture))))));
        Console.WriteLine($"  a*a-1 on the GPU: {gpu[8]:R} ({(gpu[8] == MathF.Pow(2f, -11f) ? "not fused" : "fused: mad is an FMA")}), on the CPU: {cpu[8]:R}");
        return null;
    }

    private static long Ulps(float a, float b)
    {
        if (a == b || (float.IsNaN(a) && float.IsNaN(b)))
            return 0;
        if (float.IsNaN(a) || float.IsNaN(b))
            return long.MaxValue;
        int ia = BitConverter.SingleToInt32Bits(a), ib = BitConverter.SingleToInt32Bits(b);
        if (ia < 0) ia = int.MinValue - ia;
        if (ib < 0) ib = int.MinValue - ib;
        return Math.Abs((long)ia - ib);
    }
}
