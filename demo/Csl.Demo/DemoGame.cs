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
    private Gallery? gallery;

    /// <param name="shotPath">Draw once at <paramref name="shotTime"/>, save the image there and exit, the window hidden.</param>
    public DemoGame(string? shotPath = null, float shotTime = 2.0f)
    {
        this.shotPath = shotPath;
        this.shotTime = shotTime;
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
        Window.Visible = shotPath == null;
        Window.AllowUserResizing = true;
        Window.Title = "StrideCSL demo";
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (gallery != null && shotPath == null)
        {
            gallery.Update(Input);
            Window.Title = gallery.Title;
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        base.Draw(gameTime);
        var context = ShaderContext.Get(Services);
        gallery ??= new Gallery(Services, GraphicsDevice, context.RenderContext);
        var backBuffer = GraphicsDevice.Presenter.BackBuffer;
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
