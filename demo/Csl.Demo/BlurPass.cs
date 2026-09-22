using System.Text.RegularExpressions;
using Csl.Demo.Shaders;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;

namespace Csl.Demo;

/// <summary>
/// Shaders/DemoBlur, a compute shader, run over everything the tiles drew: they draw into Scene, the
/// blur writes Blurred, and Blurred is drawn to the back buffer. Reloaded like the tiles, under a new
/// name; the generated DemoBlurEffect keeps setting its parameters, because each new name's keys are
/// registered as aliases of DemoBlurKeys. A parameter added while the app runs needs a rebuild.
/// </summary>
internal sealed class BlurPass : IDisposable
{
    private readonly DemoBlurEffect effect;
    private Texture? scene;
    private Texture? blurred;
    private string sdsl = DemoBlur.SdslSource;
    private (string Sdsl, string Name)? candidate;
    private string? rejected;
    private int version;

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

    /// <summary>A new SDSL for DemoBlur, from the C# saved: tried on the next frame.</summary>
    public void Offer(string newSdsl, string path, bool force)
    {
        if (!force && (newSdsl == sdsl || newSdsl == rejected || newSdsl == candidate?.Sdsl))
            return;
        rejected = null;
        var name = $"{DemoBlur.ShaderName}_{++version}";
        ShaderSourceRegistry.Add(name, Regex.Replace(newSdsl, $@"\bshader\s+{DemoBlur.ShaderName}\b", "shader " + name), path);
        // The compiled effect names its parameters DemoBlur_N.Radius: the same keys under that name.
        foreach (var key in typeof(DemoBlurKeys).GetFields().Select(f => f.GetValue(null)).OfType<ParameterKey>())
            ParameterKeys.Merge(key, null, name + key.Name[key.Name.IndexOf('.')..]);
        candidate = (newSdsl, name);
    }

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
            effect.Shader.ShaderSourceName = next.Name;
            if (TryDispatch() is { } error)
            {
                effect.Shader.ShaderSourceName = previous;
                rejected = next.Sdsl;
                Console.WriteLine($"{DemoBlur.ShaderName}: the SDSL does not compile, the previous one stays:");
                Console.WriteLine("  " + error.Replace("\n", "\n  "));
                TryDispatch();
            }
            else
            {
                sdsl = next.Sdsl;
                Console.WriteLine($"{DemoBlur.ShaderName}: redispatched from its C# ({next.Name})");
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
