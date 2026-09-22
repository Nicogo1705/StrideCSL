using System;
using Stride.Core;
using Stride.Games;
using Stride.Graphics;
using Stride.Rendering;

namespace Csl;

/// <summary>
/// The contexts every compute dispatch needs, made once per game and shared by all effects: the
/// engine's RenderContext, a RenderDrawContext over the game's graphics context, and the command list.
/// </summary>
public sealed class ShaderContext
{
    private ShaderContext(IServiceRegistry services)
    {
        Services = services;
        Game = services.GetSafeServiceAs<IGame>();
        RenderContext = RenderContext.GetShared(services);
        DrawContext = new RenderDrawContext(services, RenderContext, Game.GraphicsContext);
        // The shaders written in C# reach the effect compiler here, before the first dispatch.
        ShaderSourceRegistry.InstallInto(services.GetSafeServiceAs<EffectSystem>());
    }

    public IServiceRegistry Services { get; }
    public IGame Game { get; }
    public RenderContext RenderContext { get; }
    public RenderDrawContext DrawContext { get; }
    public GraphicsDevice GraphicsDevice => Game.GraphicsDevice;
    public CommandList CommandList => Game.GraphicsContext.CommandList;

    /// <summary>The context of these services, created on first use and registered with them.</summary>
    public static ShaderContext Get(IServiceRegistry services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));
        var context = services.GetService<ShaderContext>();
        if (context == null)
        {
            context = new ShaderContext(services);
            services.AddService(context);
        }
        return context;
    }

    public static ShaderContext Get(IGame game) => Get(game.Services);
}
