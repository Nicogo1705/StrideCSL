using Stride.Core.Diagnostics;
using Stride.Core.Mathematics;
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
    private readonly int cpuBench;
    private readonly bool mesh;
    private readonly string? meshCheckDirectory;
    private Gallery? gallery;
    private MeshScene? meshScene;
    private Vector2? meshPixel;

    /// <summary>What <see cref="CpuCheck"/> found: the number of shaders whose CPU run differs from the GPU.</summary>
    public int CpuCheckFailures { get; private set; }

    /// <param name="shotPath">Draw once at <paramref name="shotTime"/>, save the image there and exit, the window hidden.</param>
    /// <param name="blurRadius">The blur's radius at start; 0 turns it off.</param>
    /// <param name="cpuCheckDirectory">Run <see cref="CpuCheck"/> at <paramref name="shotTime"/>, write its images and report there, exit (hidden).</param>
    /// <param name="onCpu">Every shader run by the CPU (Csl.Cpu), frames computed in the background and drawn as they come (at <paramref name="shotTime"/> for a screenshot).</param>
    /// <param name="cpuBench">Compute that many CPU frames one after the other, print their cost, exit (hidden).</param>
    /// <param name="mesh">The mesh scene instead of the gallery: meshes drawn with a C# shader, Ctrl+click to run a pixel on the CPU.</param>
    /// <param name="meshCheckDirectory">Compare a frame of the mesh scene drawn by the GPU and rasterized by the CPU, write it there, exit (hidden).</param>
    public DemoGame(string? shotPath = null, float shotTime = 2.0f, int? blurRadius = null, string? cpuCheckDirectory = null, bool onCpu = false, int cpuBench = 0,
        bool mesh = false, string? meshCheckDirectory = null)
    {
        this.mesh = mesh;
        this.meshCheckDirectory = meshCheckDirectory;
        this.onCpu = onCpu;
        this.cpuBench = cpuBench;
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
        Window.Visible = shotPath == null && cpuCheckDirectory == null && cpuBench == 0 && meshCheckDirectory == null;
        Window.AllowUserResizing = true;
        Window.Title = "StrideCSL demo";
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (mesh)
        {
            if (Input.IsMouseButtonPressed(Stride.Input.MouseButton.Left) && (Input.IsKeyDown(Stride.Input.Keys.LeftCtrl) || Input.IsKeyDown(Stride.Input.Keys.RightCtrl)))
                meshPixel = Input.MousePosition;
            Window.Title = "StrideCSL demo - meshes drawn with a C# shader - Ctrl+click: that pixel on the CPU (vertex, rasterizer, pixel shader)";
            return;
        }
        if (gallery != null && shotPath == null && cpuCheckDirectory == null)
        {
            gallery.Update(Input);
            Window.Title = (onCpu ? $"[{gallery.CpuStatus}] " : string.Empty) + gallery.Title;
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
        if (mesh || meshCheckDirectory != null)
        {
            meshScene ??= new MeshScene(GraphicsDevice, context.RenderContext);
            if (meshCheckDirectory != null)
            {
                CpuCheckFailures = meshScene.Check(context.DrawContext, meshCheckDirectory, shotTime);
                Exit();
                return;
            }
            var target = GraphicsDevice.Presenter.BackBuffer;
            meshScene.Draw(context.DrawContext, target, GraphicsDevice.Presenter.DepthStencilBuffer, shotPath != null ? shotTime : (float)gameTime.Total.TotalSeconds);
            if (meshPixel is { } at)
            {
                meshPixel = null;
                meshScene.DebugPixel(context.DrawContext, target, at);
            }
            if (shotPath != null)
            {
                using (var file = File.Create(shotPath))
                    target.Save(context.CommandList, file, ImageFileType.Png);
                Console.WriteLine($"SHOT {shotPath} (meshes) at t = {shotTime}");
                Exit();
            }
            return;
        }
        if (gallery == null)
        {
            gallery = new Gallery(Services, GraphicsDevice, context.RenderContext);
            if (blurRadius is { } radius)
                gallery.SetBlur(radius);
        }
        var backBuffer = GraphicsDevice.Presenter.BackBuffer;
        if (cpuBench > 0)
        {
            gallery.BenchCpu(backBuffer.Width, backBuffer.Height, shotTime, cpuBench);
            Exit();
            return;
        }
        if (onCpu)
        {
            // Animated, the frames come from the background as fast as the CPU computes them; a
            // screenshot waits for one at its time.
            if (shotPath != null)
                gallery.ReloadAllNow();
            gallery.DrawOnCpu(context.DrawContext, backBuffer, shotPath != null ? shotTime : (float)gameTime.Total.TotalSeconds, animated: shotPath == null);
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
        meshScene?.Dispose();
        gallery?.Dispose();
        base.Destroy();
    }
}
