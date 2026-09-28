using System.Text;
using Csl.Cpu;
using Csl.Demo.MeshShaders;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Graphics.GeometricPrimitives;
using Stride.Rendering;
using CslTypes = Csl.Types;

namespace Csl.Demo;

/// <summary>
/// Meshes drawn with a C# shader (<see cref="DemoLitMesh"/>): a turning cube and a sphere, lit, their
/// albedo a mipmapped checkerboard. Ctrl+click runs the clicked pixel on the CPU from what the GPU
/// had (CpuCapture: parameters, textures, vertex and index buffers): the vertex shader, the rasterizer
/// and the pixel shader, stopping in the debugger before that pixel. <see cref="Check"/> compares a
/// whole frame of both.
/// </summary>
internal sealed class MeshScene : IDisposable
{
    private sealed class Model(string name, GeometricPrimitive<VertexPositionNormalTexture> primitive, EffectInstance effect)
    {
        public string Name { get; } = name;
        public GeometricPrimitive<VertexPositionNormalTexture> Primitive { get; } = primitive;
        public EffectInstance Effect { get; } = effect;
        public CpuMesh? Mesh;
    }

    private static readonly Color4 Clear = new Color4(0.05f, 0.05f, 0.07f, 1.0f);
    private readonly List<Model> models = new();
    private readonly Texture albedo;
    private Viewport lastViewport;

    public MeshScene(GraphicsDevice device, RenderContext renderContext)
    {
        albedo = MipmappedChecker(device);
        var cube = GeometricPrimitive.Cube.New(device, 1.0f);
        var sphere = GeometricPrimitive.Sphere.New(device, 0.65f, 32);
        foreach (var (name, primitive) in new[] { ("cube", cube), ("sphere", sphere) })
        {
            var effect = new EffectInstance(renderContext.Effects.LoadEffect(DemoLitMesh.ShaderName).WaitForResult());
            effect.UpdateEffect(device);
            models.Add(new Model(name, primitive, effect));
        }
    }

    /// <summary>Draws the scene at <paramref name="time"/> into <paramref name="target"/>, depth tested.</summary>
    public void Draw(RenderDrawContext context, Texture target, Texture depth, float time)
    {
        var commandList = context.CommandList;
        commandList.Clear(target, Clear);
        commandList.Clear(depth, DepthStencilClearOptions.DepthBuffer, 1.0f, 0);
        commandList.SetRenderTargetAndViewport(depth, target);
        lastViewport = new Viewport(0, 0, target.Width, target.Height);

        var eye = new Vector3(0.0f, 1.3f, 3.2f);
        var view = Matrix.LookAtRH(eye, Vector3.Zero, Vector3.UnitY);
        var projection = Matrix.PerspectiveFovRH(MathUtil.DegreesToRadians(50.0f), target.Width / (float)target.Height, 0.1f, 20.0f);
        var viewProjection = view * projection;
        var worlds = new[]
        {
            Matrix.RotationY(time * 0.6f) * Matrix.RotationX(0.4f) * Matrix.Translation(-0.85f, 0.0f, 0.0f),
            Matrix.RotationY(-time * 0.3f) * Matrix.Translation(0.85f, 0.0f, 0.0f),
        };
        var materials = new[] { (Roughness: 0.5f, Metalness: 0.0f), (Roughness: 0.25f, Metalness: 1.0f) };
        for (int i = 0; i < models.Count; i++)
        {
            var parameters = models[i].Effect.Parameters;
            parameters.Set(DemoLitMeshKeys.World, worlds[i]);
            parameters.Set(DemoLitMeshKeys.WorldViewProjection, worlds[i] * viewProjection);
            parameters.Set(DemoLitMeshKeys.Eye, eye);
            parameters.Set(DemoLitMeshKeys.LightDirection, Vector3.Normalize(new Vector3(-0.5f, -1.0f, -0.6f)));
            parameters.Set(DemoLitMeshKeys.LightColor, new Vector3(2.4f, 2.3f, 2.1f));
            parameters.Set(DemoLitMeshKeys.Roughness, materials[i].Roughness);
            parameters.Set(DemoLitMeshKeys.Metalness, materials[i].Metalness);
            parameters.Set(DemoLitMeshKeys.Albedo, albedo);
            models[i].Primitive.Draw(context.GraphicsContext, models[i].Effect);
        }
    }

    /// <summary>The draws as the CPU runs them, from what the GPU had for the last <see cref="Draw"/>.</summary>
    private List<CpuMeshDraw> CpuDraws(CommandList commandList)
    {
        var draws = new List<CpuMeshDraw>();
        foreach (var model in models)
        {
            // The geometry does not change: read back once.
            var primitive = model.Primitive;
            var layout = new VertexPositionNormalTexture().GetLayout();
            model.Mesh ??= CpuCapture.Mesh(primitive.VertexBuffer, layout, primitive.VertexBuffer.SizeInBytes / layout.VertexStride,
                primitive.IndexBuffer, primitive.IsIndex32Bits, primitive.IndexBuffer.SizeInBytes / (primitive.IsIndex32Bits ? 4 : 2), commandList);
            draws.Add(CpuCapture.MeshDraw(model.Effect, typeof(DemoLitMesh), model.Mesh, lastViewport, commandList));
        }
        return draws;
    }

    /// <summary>
    /// Ctrl+click: the pixel under the mouse run on the CPU, vertex to pixel shader, from what the GPU
    /// had; with a debugger attached it stops before that pixel's lane (F11: into DemoLitMesh.PSMain).
    /// </summary>
    public void DebugPixel(RenderDrawContext context, Texture target, Vector2 at)
    {
        int px = Math.Clamp((int)(at.X * target.Width), 0, target.Width - 1), py = Math.Clamp((int)(at.Y * target.Height), 0, target.Height - 1);
        var gpu = target.GetData<Color>(context.CommandList)[py * target.Width + px];
        try
        {
            var draws = CpuDraws(context.CommandList);
            foreach (var draw in draws)
                draw.Break = System.Diagnostics.Debugger.IsAttached;
            var result = CpuScene.DebugPixel(draws, target.Width, target.Height, px, py);
            if (result is not { } hit)
            {
                Console.WriteLine($"Mesh pixel ({px}, {py}): no triangle on the CPU; GPU {gpu.R} {gpu.G} {gpu.B} {gpu.A}");
                return;
            }
            var cpu = new Color(hit.Color.x, hit.Color.y, hit.Color.z, hit.Color.w);
            Console.WriteLine($"Mesh pixel ({px}, {py}): {models[draws.IndexOf(hit.Draw)].Name}, triangle {hit.Triangle}: GPU {gpu.R} {gpu.G} {gpu.B} {gpu.A}, CPU {cpu.R} {cpu.G} {cpu.B} {cpu.A}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"Mesh pixel ({px}, {py}): the CPU run failed: {e.GetBaseException().Message}");
        }
    }

    /// <summary>A frame at <paramref name="time"/> drawn by the GPU and rasterized by the CPU, compared (report and images in <paramref name="directory"/>).</summary>
    public int Check(RenderDrawContext context, string directory, float time)
    {
        const int width = 320, height = 180;
        Directory.CreateDirectory(directory);
        var device = context.GraphicsDevice;
        using var target = Texture.New2D(device, width, height, PixelFormat.R8G8B8A8_UNorm, TextureFlags.RenderTarget | TextureFlags.ShaderResource);
        using var depth = Texture.New2D(device, width, height, PixelFormat.D32_Float, TextureFlags.DepthStencil);
        Draw(context, target, depth, time);
        context.CommandList.ResetTargets();
        var gpu = target.GetData<Color>(context.CommandList);

        var report = new StringBuilder();
        report.AppendLine($"Meshes, CPU against GPU, {width}x{height}, t = {time}: per channel, in 8-bit steps");
        CslTypes.float4[] cpu;
        string? error = null;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var draws = new List<CpuMeshDraw>();
        try
        {
            draws = CpuDraws(context.CommandList);
            cpu = CpuScene.Draw(draws, width, height, TexelFormat.Rgba8UNorm, new CslTypes.float4(Clear.R, Clear.G, Clear.B, Clear.A)).ToArray();
        }
        catch (Exception e)
        {
            error = e.GetBaseException().GetType().Name + ": " + e.GetBaseException().Message;
            cpu = new CslTypes.float4[width * height];
        }
        watch.Stop();
        int failures = CpuCheck.Compare(report, "MeshScene", gpu, cpu, width, height, directory, error, watch.Elapsed);
        report.AppendLine($"quads in lockstep (derivatives): {(draws.Count > 0 && draws.All(d => d.NeedsQuads) ? "yes" : "no")}");
        File.WriteAllText(Path.Combine(directory, "report.txt"), report.ToString());
        Console.Write(report.ToString());
        Console.WriteLine($"MESHCHECK {(failures == 0 ? "PASS" : "differs")}; images and report in {directory}");
        return failures;
    }

    /// <summary>The checkerboard with its whole mip chain, each level the 2x2 average of the one above.</summary>
    private static Texture MipmappedChecker(GraphicsDevice device)
    {
        var pixels = Gallery.CheckerPixels(out int size);
        using var image = Image.New2D(size, size, MipMapCount.Auto, PixelFormat.R8G8B8A8_UNorm);
        var level = pixels;
        for (int mip = 0; mip < image.Description.MipLevels; mip++)
        {
            int s = Math.Max(1, size >> mip);
            if (mip > 0)
            {
                var previous = level;
                int p = s * 2;
                level = new Color[s * s];
                for (int y = 0; y < s; y++)
                    for (int x = 0; x < s; x++)
                    {
                        var a = previous[(2 * y) * p + 2 * x].ToVector4() + previous[(2 * y) * p + 2 * x + 1].ToVector4()
                              + previous[(2 * y + 1) * p + 2 * x].ToVector4() + previous[(2 * y + 1) * p + 2 * x + 1].ToVector4();
                        level[y * s + x] = new Color(a * 0.25f);
                    }
            }
            image.GetPixelBuffer(0, mip).SetPixels(level);
        }
        return Texture.New(device, image);
    }

    public void Dispose()
    {
        foreach (var model in models)
        {
            model.Effect.Dispose();
            model.Primitive.Dispose();
        }
        albedo.Dispose();
    }
}
