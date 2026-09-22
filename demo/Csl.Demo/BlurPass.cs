using Csl.Demo.Shaders;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace Csl.Demo;

/// <summary>
/// Shaders/DemoBlur, a compute shader, run over everything the tiles drew: they draw into Scene, the
/// blur writes Blurred, and Blurred is drawn to the back buffer. Reloaded like the tiles, under a new
/// name whose keys the gallery registers as aliases of DemoBlurKeys: the generated DemoBlurEffect keeps
/// setting them. A parameter added while the app runs needs a rebuild.
/// </summary>
internal sealed class BlurPass : IDisposable
{
    private readonly DemoBlurEffect effect;
    private Texture? scene;
    private Texture? blurred;
    private string? candidate;

    public BlurPass(IServiceRegistry services) => effect = new DemoBlurEffect(services);

    public bool Enabled { get; set; } = true;

    public int Radius { get; set; } = 4;

    /// <summary>Where the tiles draw when the blur is on: a texture the size of the back buffer.</summary>
    public Texture SceneFor(Texture backBuffer)
    {
        if (scene == null || scene.Width != backBuffer.Width || scene.Height != backBuffer.Height)
        {
            scene?.Dispose();
            blurred?.Dispose();
            scene = Texture.New2D(backBuffer.GraphicsDevice, backBuffer.Width, backBuffer.Height, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
            blurred = Texture.New2D(backBuffer.GraphicsDevice, backBuffer.Width, backBuffer.Height, PixelFormat.R8G8B8A8_UNorm, TextureFlags.UnorderedAccess | TextureFlags.ShaderResource);
        }
        return scene;
    }

    /// <summary>DemoBlur registered again under this name (by the gallery, with its keys): used from the next frame.</summary>
    public void Offer(string versionName) => candidate = versionName;

    /// <summary>Blurs Scene and draws the result over the back buffer.</summary>
    public void Apply(RenderDrawContext context, Texture backBuffer)
    {
        // Scene is still the render target: Direct3D would not let the blur read it.
        context.CommandList.ResetTargets();
        effect.Input = scene;
        effect.Output = blurred;
        effect.Size = new Int2(scene!.Width, scene.Height);
        effect.Radius = Radius;
        if (candidate is { } next)
        {
            candidate = null;
            var previous = effect.Shader.ShaderSourceName;
            effect.Shader.ShaderSourceName = next;
            if (TryDispatch() is { } error)
            {
                effect.Shader.ShaderSourceName = previous;
                Console.WriteLine($"{DemoBlur.ShaderName}: the SDSL does not compile, the previous one stays:");
                Console.WriteLine("  " + error.Replace("\n", "\n  "));
                TryDispatch();
            }
            else
            {
                Console.WriteLine($"{DemoBlur.ShaderName}: redispatched from its C# ({next})");
            }
        }
        else if (TryDispatch() is { } failure)
        {
            Console.WriteLine($"{DemoBlur.ShaderName}: {failure}; blur off");
            Enabled = false;
        }
        context.CommandList.SetRenderTargetAndViewport(null, backBuffer);
        context.GraphicsContext.DrawTexture(blurred!);
    }

    private string? TryDispatch()
    {
        try
        {
            effect.Dispatch(scene!.Width, scene.Height);
            return null;
        }
        catch (Exception e)
        {
            return e.GetBaseException().Message.Trim();
        }
    }

    public void Dispose()
    {
        effect.Dispose();
        scene?.Dispose();
        blurred?.Dispose();
    }
}
