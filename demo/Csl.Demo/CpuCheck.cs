using System.Runtime.InteropServices;
using System.Text;
using Csl.Cpu;
using Csl.Demo.Shaders;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;
using CslTypes = Csl.Types;

namespace Csl.Demo;

/// <summary>
/// The CPU run measured against the GPU: every demo drawn by both with the same inputs (its own 320x180
/// target, the same time, the checkerboard), DemoBlur dispatched by both over the checkerboard, and
/// the two images compared channel by channel in 8-bit steps. What differs is the CPU run not being
/// representative; the report and the images (GPU, CPU, difference x16) say where.
/// </summary>
internal static class CpuCheck
{
    private const int Width = 320, Height = 180;

    public static int Run(IServiceRegistry services, RenderContext renderContext, RenderDrawContext context, string directory, float time)
    {
        Directory.CreateDirectory(directory);
        var report = new StringBuilder();
        report.AppendLine($"CPU against GPU, {Width}x{Height}, t = {time}: per channel, in 8-bit steps");
        int failures = 0;
        var device = context.GraphicsDevice;
        var checkerPixels = Gallery.CheckerPixels(out int checkerSize);
        using var checker = Texture.New2D(device, checkerSize, checkerSize, PixelFormat.R8G8B8A8_UNorm, checkerPixels);
        var cpuChecker = new CpuTexture(checkerSize, checkerSize, format: TexelFormat.Rgba8UNorm).FillRgba8(MemoryMarshal.AsBytes(checkerPixels.AsSpan()));

        var names = ShaderSourceRegistry.Sources
            .Where(s => string.Equals(Path.GetFileName(Path.GetDirectoryName(s.Value.Path)), "Shaders", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Key).Order(StringComparer.Ordinal).ToList();
        foreach (var name in names)
        {
            var type = typeof(DemoTile).Assembly.GetType($"{typeof(DemoTile).Namespace}.{name}");
            if (LiveCompiler.KindOf(type) != LiveCompiler.ShaderKind.Image)
                continue;

            // GPU: the effect the build registered, into a target of its own.
            using var target = Texture.New2D(device, Width, Height, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            using var effect = new ImageEffectShader(name);
            effect.Initialize(renderContext);
            effect.Parameters.Set(GlobalKeys.Time, time);
            effect.Parameters.Set(DemoTileKeys.Aspect, Width / (float)Height);
            effect.SetInput(0, checker);
            effect.SetOutput(target);
            effect.Draw(context);
            context.CommandList.ResetTargets();
            var gpu = target.GetData<Color>(context.CommandList);

            // CPU: the same C# class, given what the effect had on the GPU (CpuCapture: its parameters,
            // Texture0 read back): nothing set by hand.
            CslTypes.float4[] cpu;
            string? error = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var run = CpuCapture.ImageEffect(effect, context.CommandList, new Viewport(0, 0, Width, Height), type);
                cpu = run.Draw().ToArray();
            }
            catch (Exception e)
            {
                error = e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message;
                cpu = new CslTypes.float4[Width * Height];
            }
            watch.Stop();
            failures += Compare(report, name, gpu, cpu, Width, Height, directory, error, watch.Elapsed);
        }

        failures += CheckBlur(services, context, report, checker, cpuChecker, checkerSize, directory);

        File.WriteAllText(Path.Combine(directory, "report.txt"), report.ToString());
        Console.Write(report.ToString());
        Console.WriteLine($"CPUCHECK {(failures == 0 ? "PASS" : failures + " differ")}; images and report in {directory}");
        return failures;
    }

    /// <summary>DemoBlur over the checkerboard, radius 3: the compute shader on both.</summary>
    private static int CheckBlur(IServiceRegistry services, RenderDrawContext context, StringBuilder report, Texture checker, CpuTexture cpuChecker, int size, string directory)
    {
        const int radius = 3;
        using var output = Texture.New2D(context.GraphicsDevice, size, size, PixelFormat.R8G8B8A8_UNorm, TextureFlags.UnorderedAccess | TextureFlags.ShaderResource);
        using var effect = new DemoBlurEffect(services);
        effect.Input = checker;
        effect.Output = output;
        effect.Size = new Int2(size, size);
        effect.Radius = radius;
        effect.Dispatch(size, size);
        var gpu = output.GetData<Color>(context.CommandList);

        CslTypes.float4[] cpu;
        string? error = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // What the wrapper had (CpuCapture), but a blank output: the one read back holds the GPU's result.
            var run = CpuCapture.Compute(effect, context.CommandList, typeof(DemoBlur));
            var cpuOutput = new CpuTexture(size, size, format: TexelFormat.Rgba8UNorm);
            run.Set("Output", new CslTypes.RWTexture2D<CslTypes.float4>(cpuOutput));
            run.Dispatch((size + run.ThreadsX - 1) / run.ThreadsX, (size + run.ThreadsY - 1) / run.ThreadsY);
            cpu = cpuOutput.ToArray();
        }
        catch (Exception e)
        {
            error = e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message;
            cpu = new CslTypes.float4[size * size];
        }
        watch.Stop();
        return Compare(report, "DemoBlur", gpu, cpu, size, size, directory, error, watch.Elapsed);
    }

    internal static int Compare(StringBuilder report, string name, Color[] gpu, CslTypes.float4[] cpu, int width, int height, string directory, string? error, TimeSpan elapsed)
    {
        var cpuBytes = new Color[cpu.Length];
        var diff = new Color[cpu.Length];
        int maxDiff = 0, over1 = 0, over4 = 0;
        long sum = 0;
        (int X, int Y, int Diff) worst = default;
        for (int i = 0; i < cpu.Length; i++)
        {
            var c = new Color(ToByte(cpu[i].x), ToByte(cpu[i].y), ToByte(cpu[i].z), ToByte(cpu[i].w));
            cpuBytes[i] = c;
            var g = gpu[i];
            int dr = Math.Abs(c.R - g.R), dg = Math.Abs(c.G - g.G), db = Math.Abs(c.B - g.B), da = Math.Abs(c.A - g.A);
            int d = Math.Max(Math.Max(dr, dg), Math.Max(db, da));
            sum += dr + dg + db + da;
            if (d > 1) over1++;
            if (d > 4) over4++;
            if (d > maxDiff)
            {
                maxDiff = d;
                worst = (i % width, i / width, d);
            }
            diff[i] = new Color((byte)Math.Min(255, dr * 16), (byte)Math.Min(255, dg * 16), (byte)Math.Min(255, db * 16), (byte)255);
        }
        Save(Path.Combine(directory, name + ".gpu.png"), gpu, width, height);
        Save(Path.Combine(directory, name + ".cpu.png"), cpuBytes, width, height);
        Save(Path.Combine(directory, name + ".diff.png"), diff, width, height);
        bool same = error == null && over1 == 0;
        report.Append($"{(same ? "same     " : "DIFFERENT")} {name,-16} ");
        if (error != null)
            report.AppendLine("CPU failed: " + error);
        else
            report.AppendLine($"max {maxDiff} at ({worst.X}, {worst.Y}), {over1} pixels > 1, {over4} > 4, mean {sum / (4.0 * cpu.Length):F3}; CPU {elapsed.TotalMilliseconds:F0} ms");
        return same ? 0 : 1;
    }

    /// <summary>The render target's rounding: UNORM, nearest.</summary>
    private static byte ToByte(float v) => (byte)MathF.Round(Math.Clamp(float.IsNaN(v) ? 0f : v, 0f, 1f) * 255f);

    private static void Save(string path, Color[] pixels, int width, int height)
    {
        using var image = Image.New2D(width, height, 1, PixelFormat.R8G8B8A8_UNorm);
        var bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        Marshal.Copy(bytes, 0, image.PixelBuffer[0].DataPointer, bytes.Length);
        using var file = File.Create(path);
        image.Save(file, ImageFileType.Png);
    }

    /// <summary>
    /// One pixel of a demo on the CPU, no GPU, no window: with a debugger attached it stops right before
    /// the pixel's lane (F11 steps into the shader's C#). For Visual Studio: Csl.Demo --debug-pixel DemoClouds 120 45.
    /// </summary>
    public static int DebugPixel(string name, int x, int y, float time)
    {
        var type = typeof(DemoTile).Assembly.GetType($"{typeof(DemoTile).Namespace}.{name}");
        if (type == null || LiveCompiler.KindOf(type) != LiveCompiler.ShaderKind.Image)
        {
            Console.WriteLine($"No image demo named {name}");
            return 2;
        }
        var checkerPixels = Gallery.CheckerPixels(out int checkerSize);
        var cpuChecker = new CpuTexture(checkerSize, checkerSize, format: TexelFormat.Rgba8UNorm).FillRgba8(MemoryMarshal.AsBytes(checkerPixels.AsSpan()));
        var run = new CpuImageEffect(type, Width, Height) { Break = true };
        run.Set("Time", time);
        if (Members.InstanceFields(type).ContainsKey("Aspect"))
            run.Set("Aspect", Width / (float)Height);
        run.Set("Texture0", new CslTypes.Texture2D(cpuChecker));
        var color = run.DrawPixel(x, y);
        Console.WriteLine($"{name} pixel ({x}, {y}) of {Width}x{Height} at t = {time}: {color.x:F4} {color.y:F4} {color.z:F4} {color.w:F4}"
            + (System.Diagnostics.Debugger.IsAttached ? string.Empty : " (no debugger attached: nothing stopped)"));
        return 0;
    }
}