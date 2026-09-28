using Stride.Core.Diagnostics;
using Stride.Engine;
using Stride.Games;
using Stride.Graphics;

namespace Csl.Demo;

/// <summary>
/// A code-only Stride game that draws the <see cref="Gallery"/>: no scene, no assets, the shaders
/// written in C# drawn straight to the back buffer.
/// </summary>
internal sealed class DemoGame : Game
{
    private readonly string? shotPath;
    private readonly float shotTime;
    private readonly int? blurRadius;
    private readonly string? cpuCheckDirectory;
    private readonly bool onCpu;
    private Gallery? gallery;

    /// <summary>What <see cref="CpuCheck"/> found: the number of shaders whose CPU run differs from the GPU.</summary>
    public int CpuCheckFailures { get; private set; }

    /// <param name="shotPath">Draw once at <paramref name="shotTime"/>, save the image there and exit, the window hidden.</param>
    /// <param name="blurRadius">The blur's radius at start; 0 turns it off.</param>
    /// <param name="cpuCheckDirectory">Run <see cref="CpuCheck"/> at <paramref name="shotTime"/>, write its images and report there, exit (hidden).</param>
    /// <param name="onCpu">Every shader run by the CPU (Csl.Cpu) at <paramref name="shotTime"/>, the frame uploaded and drawn.</param>
    public DemoGame(string? shotPath = null, float shotTime = 2.0f, int? blurRadius = null, string? cpuCheckDirectory = null, bool onCpu = false)
    {
        this.onCpu = onCpu;
        this.shotPath = shotPath;
        this.cpuCheckDirectory = cpuCheckDirectory;
        this.shotTime = shotTime;
        this.blurRadius = blurRadius;
        AutoLoadDefaultSettings = false;
        GraphicsDeviceManager.PreferredGraphicsProfile = new[] { GraphicsProfile.Level_11_0 };
        GraphicsDeviceManager.PreferredBackBufferWidth = 1280;
        GraphicsDeviceManager.PreferredBackBufferHeight = 720;
        // The shaders' colours as they write them, no sRGB curve on top.
        GraphicsDeviceManager.PreferredColorSpace = ColorSpace.Gamma;
        // The effect compiler's notes on every shader recompiled would bury the C# errors; warnings stay.
        ConsoleLogLevel = LogMessageType.Warning;
    }

    protected override void BeginRun()
    {
        base.BeginRun();
        Window.Visible = shotPath == null && cpuCheckDirectory == null;
        Window.AllowUserResizing = true;
        Window.Title = "StrideCSL demo";
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (gallery != null && shotPath == null && cpuCheckDirectory == null)
        {
            gallery.Update(Input);
            Window.Title = (onCpu ? $"[CPU frame, t = {shotTime}] " : string.Empty) + gallery.Title;
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        base.Draw(gameTime);
        var context = ShaderContext.Get(Services);
        if (cpuCheckDirectory != null)
        {
            CpuCheckFailures = CpuCheck.Run(Services, context.RenderContext, context.DrawContext, cpuCheckDirectory, shotTime);
            Exit();
            return;
        }
        if (gallery == null)
        {
            gallery = new Gallery(Services, GraphicsDevice, context.RenderContext);
            if (blurRadius is { } radius)
                gallery.SetBlur(radius);
        }
        var backBuffer = GraphicsDevice.Presenter.BackBuffer;
        if (onCpu)
        {
            // The CPU's frame, at a fixed time: a frame takes it seconds.
            if (shotPath != null)
                gallery.ReloadAllNow();
            gallery.DrawOnCpu(context.DrawContext, backBuffer, shotTime);
            if (shotPath == null)
                return;
            using (var file = File.Create(shotPath))
                backBuffer.Save(context.CommandList, file, ImageFileType.Png);
            Console.WriteLine($"SHOT {shotPath} computed on the CPU at t = {shotTime}");
            Exit();
            return;
        }
        if (shotPath == null)
        {
            gallery.Draw(context.DrawContext, backBuffer, (float)gameTime.Total.TotalSeconds);
            return;
        }
        // The whole reload path at once: every shader compiled from its file, as a save would.
        if (!gallery.ReloadAllNow())
            Console.WriteLine("SHOT the shaders did not compile from their files: drawn as built");
        gallery.Draw(context.DrawContext, backBuffer, shotTime);
        using (var file = File.Create(shotPath))
            backBuffer.Save(context.CommandList, file, ImageFileType.Png);
        Console.WriteLine($"SHOT {shotPath} at t = {shotTime}");
        Exit();
    }

    protected override void Destroy()
    {
        gallery?.Dispose();
        base.Destroy();
    }
}
