using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Csl.Types;

namespace Csl.Cpu;

/// <summary>
/// The vertices of a draw, as the input assembler reads them: per vertex, each attribute by its
/// semantic (POSITION0, NORMAL0, TEXCOORD0…) expanded to a float4 with (0, 0, 0, 1) for what the
/// format lacks; and the indices of a triangle list.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public sealed class CpuMesh
{
    public CpuMesh(int vertexCount, int[] indices)
    {
        VertexCount = vertexCount;
        Indices = indices;
    }

    public int VertexCount { get; }

    public int[] Indices { get; }

    public Dictionary<string, float4[]> Attributes { get; } = new Dictionary<string, float4[]>(StringComparer.OrdinalIgnoreCase);

    /// <summary>An attribute's values: <c>Set("POSITION", positions)</c>, index 0 when the semantic has none.</summary>
    public CpuMesh Set(string semantic, float4[] values)
    {
        Attributes[Normalize(semantic)] = values;
        return this;
    }

    public static string Normalize(string semantic) => semantic.Length > 0 && char.IsDigit(semantic[semantic.Length - 1]) ? semantic.ToUpperInvariant() : semantic.ToUpperInvariant() + "0";
}

public enum CullMode { None, Front, Back }

/// <summary>
/// A draw call of a mesh run on the CPU, as Direct3D 11 rasterizes it: VSMain for every vertex, then
/// for each triangle in order its vertices snapped to 1/256 of a pixel, back faces culled (clockwise is
/// the front, Stride's default), the pixels whose centre it covers (top-left rule), the depth tested
/// (LessEqual) and written, the streams interpolated with perspective, and PSMain run in 2x2 quads
/// whose uncovered pixels are helpers, their streams extrapolated. <see cref="CpuScene.DebugPixel"/>
/// finds the triangle a pixel shows and stops in the debugger before its lane.
/// </summary>
[System.Diagnostics.DebuggerNonUserCode]
public sealed class CpuMeshDraw : Run
{
    private readonly Action<object> vertexEntry;
    private readonly (FieldInfo Field, string Semantic)[] inputs;
    private readonly FieldInfo[] interpolated;
    private readonly FieldInfo[] flat;
    private readonly Action<object, float4>? setPosition;
    private readonly Func<object, float4>? getPosition;
    private readonly Func<object, float4>? getColor;
    private readonly FieldInfo? frontFace;
    private volatile bool lockstep;

    public CpuMeshDraw(Type shader, CpuMesh mesh) : base(shader, "PSMain")
    {
        Mesh = mesh;
        vertexEntry = CompileEntry(shader, "VSMain");
        var type = Shader.GetType();
        var graph = ShaderInstances.MixinGraph(shader);
        var streams = new List<(FieldInfo Field, StreamAttribute Attribute)>();
        foreach (var field in Members.InstanceFields(type).Values)
        {
            var attribute = field.GetCustomAttribute<StreamAttribute>()
                ?? graph.Select(t => t.GetField(field.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)?.GetCustomAttribute<StreamAttribute>()).FirstOrDefault(a => a != null);
            if (attribute != null)
                streams.Add((field, attribute));
        }
        inputs = streams.Where(s => s.Attribute.Semantic != null && !s.Attribute.Semantic.StartsWith("SV_", StringComparison.OrdinalIgnoreCase))
            .Select(s => (s.Field, CpuMesh.Normalize(s.Attribute.Semantic!))).ToArray();
        bool Interpolable(Type t) => t == typeof(float) || t == typeof(float2) || t == typeof(float3) || t == typeof(float4);
        bool System(StreamAttribute a) => a.Semantic != null && a.Semantic.StartsWith("SV_", StringComparison.OrdinalIgnoreCase);
        interpolated = streams.Where(s => !System(s.Attribute) && Interpolable(s.Field.FieldType)).Select(s => s.Field).ToArray();
        flat = streams.Where(s => !System(s.Attribute) && !Interpolable(s.Field.FieldType) && s.Field.FieldType.IsValueType).Select(s => s.Field).ToArray();
        setPosition = Members.Setter<float4>(type, "ShadingPosition");
        getPosition = Members.Getter<float4>(type, "ShadingPosition");
        getColor = Members.Getter<float4>(type, "ColorTarget");
        frontFace = Members.InstanceFields(type).TryGetValue("IsFrontFace", out var f) && f.FieldType == typeof(bool) ? f : null;
    }

    public CpuMesh Mesh { get; }

    public CullMode Cull { get; set; } = CullMode.Back;

    /// <summary>Counter-clockwise triangles are the front ones (Stride's default: clockwise).</summary>
    public bool FrontCounterClockwise { get; set; }

    /// <summary>The viewport, in pixels of the target, and its depth range.</summary>
    public (float X, float Y, float Width, float Height, float MinDepth, float MaxDepth) Viewport { get; set; } = (0, 0, 1, 1, 0, 1);

    public bool DepthTest { get; set; } = true;
    public bool DepthWrite { get; set; } = true;

    /// <summary>Whether a pixel shader needed its quads in lockstep: it takes derivatives.</summary>
    public bool NeedsQuads => lockstep;

    // -- vertices ------------------------------------------------------------------------------------

    internal sealed class Vertex
    {
        public object Shader = null!;
        public float4 Clip;
        public float X, Y, Z, InverseW;
        public bool Behind;
    }

    /// <summary>VSMain for each vertex, then its position in the target's pixels.</summary>
    internal Vertex[] RunVertices()
    {
        var vertices = new Vertex[Mesh.VertexCount];
        var (vx, vy, vw, vh, near, far) = Viewport;
        Parallel.For(0, Mesh.VertexCount, i =>
        {
            var shader = ShaderInstances.Clone(Shader);
            foreach (var (field, semantic) in inputs)
                if (Mesh.Attributes.TryGetValue(semantic, out var values))
                    field.SetValue(shader, Narrow(values[i], field.FieldType));
            var lane = new Lane(0, null, Macros, isPixel: false);
            Lane.Enter(lane);
            try
            {
                Invoke(vertexEntry, shader, false);
            }
            finally
            {
                Lane.Leave();
            }
            var clip = getPosition?.Invoke(shader) ?? default;
            var v = new Vertex { Shader = shader, Clip = clip, Behind = clip.w <= 0f };
            if (!v.Behind)
            {
                v.InverseW = 1f / clip.w;
                float nx = clip.x * v.InverseW, ny = clip.y * v.InverseW, nz = clip.z * v.InverseW;
                // To pixels, snapped to the rasterizer's 8 bits of sub-pixel precision.
                v.X = Snap(vx + (nx + 1f) * 0.5f * vw);
                v.Y = Snap(vy + (1f - ny) * 0.5f * vh);
                v.Z = near + nz * (far - near);
            }
            vertices[i] = v;
        });
        return vertices;
    }

    private static float Snap(float v) => MathF.Round(v * 256f) / 256f;

    private static object Narrow(float4 v, Type type)
    {
        if (type == typeof(float4)) return v;
        if (type == typeof(float3)) return new float3(v.x, v.y, v.z);
        if (type == typeof(float2)) return new float2(v.x, v.y);
        if (type == typeof(float)) return v.x;
        return typeof(HlslConvert).GetMethod(nameof(HlslConvert.Convert))!.MakeGenericMethod(type).Invoke(null, new object[] { v })!;
    }

    // -- triangles -----------------------------------------------------------------------------------

    /// <summary>A triangle set up for rasterizing: its vertices in clockwise order, its area.</summary>
    internal struct Triangle
    {
        public int Index;
        public Vertex A, B, C;
        public Vertex Provoking;
        public float Area;
        public bool Front;
    }

    internal bool Setup(Vertex[] vertices, int triangle, out Triangle t)
    {
        t = default;
        var indices = Mesh.Indices;
        var a = vertices[indices[triangle * 3]];
        var b = vertices[indices[triangle * 3 + 1]];
        var c = vertices[indices[triangle * 3 + 2]];
        // No clipping against the near plane: a triangle with a vertex behind the eye is left out.
        if (a.Behind || b.Behind || c.Behind)
            return false;
        float area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
        if (area == 0f)
            return false;
        bool clockwise = area > 0f;
        bool front = clockwise != FrontCounterClockwise;
        if ((Cull == CullMode.Back && !front) || (Cull == CullMode.Front && front))
            return false;
        t = clockwise
            ? new Triangle { Index = triangle, A = a, B = b, C = c, Provoking = a, Area = area, Front = front }
            : new Triangle { Index = triangle, A = a, B = c, C = b, Provoking = a, Area = -area, Front = front };
        return true;
    }

    private static float Edge(float ax, float ay, float bx, float by, float px, float py) => (bx - ax) * (py - ay) - (by - ay) * (px - ax);

    /// <summary>Top or left edge of a clockwise triangle (y down): a pixel centre exactly on it is covered.</summary>
    private static bool TopLeft(Vertex from, Vertex to) => (from.Y == to.Y && to.X > from.X) || to.Y < from.Y;

    /// <summary>The weights of the three vertices at a point (outside the triangle too: helpers extrapolate), and whether it covers it.</summary>
    internal static bool Weights(in Triangle t, float px, float py, out float w0, out float w1, out float w2)
    {
        float e0 = Edge(t.B.X, t.B.Y, t.C.X, t.C.Y, px, py);
        float e1 = Edge(t.C.X, t.C.Y, t.A.X, t.A.Y, px, py);
        float e2 = Edge(t.A.X, t.A.Y, t.B.X, t.B.Y, px, py);
        w0 = e0 / t.Area;
        w1 = e1 / t.Area;
        w2 = e2 / t.Area;
        return (e0 > 0f || (e0 == 0f && TopLeft(t.B, t.C)))
            && (e1 > 0f || (e1 == 0f && TopLeft(t.C, t.A)))
            && (e2 > 0f || (e2 == 0f && TopLeft(t.A, t.B)));
    }

    internal static float DepthAt(in Triangle t, float w0, float w1, float w2) => w0 * t.A.Z + w1 * t.B.Z + w2 * t.C.Z;

    // -- pixels --------------------------------------------------------------------------------------

    /// <summary>The whole draw into <paramref name="target"/>, testing and writing <paramref name="depth"/> (one per pixel, 1 when cleared).</summary>
    public void Draw(CpuTexture target, float[] depth)
    {
        var vertices = RunVertices();
        var teams = new ConcurrentBag<LaneTeam>();
        try
        {
            for (int triangle = 0; triangle < Mesh.Indices.Length / 3; triangle++)
            {
                if (!Setup(vertices, triangle, out var t))
                    continue;
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)))) & ~1;
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)))) & ~1;
                int x1 = Math.Min(target.Width - 1, (int)MathF.Ceiling(MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X))));
                int y1 = Math.Min(target.Height - 1, (int)MathF.Ceiling(MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y))));
                var quads = new List<(int X, int Y)>();
                for (int qy = y0; qy <= y1; qy += 2)
                    for (int qx = x0; qx <= x1; qx += 2)
                        if (AnyCovered(t, qx, qy, target.Width, target.Height))
                            quads.Add((qx, qy));
                var tri = t;
                Parallel.ForEach(quads, quad =>
                {
                    if (!lockstep && DirectQuad(tri, quad.X, quad.Y, target, depth))
                        return;
                    lockstep = true;
                    if (!teams.TryTake(out var team))
                        team = new LaneTeam(4, Macros, isPixel: true);
                    try
                    {
                        TeamQuad(team, tri, quad.X, quad.Y, target, depth, -1, -1);
                    }
                    finally
                    {
                        teams.Add(team);
                    }
                });
            }
        }
        finally
        {
            foreach (var team in teams)
                team.Dispose();
        }
    }

    private static bool AnyCovered(in Triangle t, int qx, int qy, int width, int height)
    {
        for (int i = 0; i < 4; i++)
        {
            int x = qx + (i & 1), y = qy + (i >> 1);
            if (x < width && y < height && Weights(t, x + 0.5f, y + 0.5f, out _, out _, out _))
                return true;
        }
        return false;
    }

    private bool DirectQuad(in Triangle t, int qx, int qy, CpuTexture target, float[] depth)
    {
        var lanes = new Lane[4];
        var shaders = new object[4];
        for (int i = 0; i < 4; i++)
        {
            lanes[i] = new Lane(i, null, Macros, isPixel: true);
            shaders[i] = Prepare(lanes[i], t, qx, qy, target);
        }
        for (int i = 0; i < 4; i++)
        {
            Lane.Enter(lanes[i]);
            try
            {
                Invoke(shaders[i], false);
            }
            catch (NeedsLockstep)
            {
                return false;
            }
            finally
            {
                Lane.Leave();
            }
        }
        for (int i = 0; i < 4; i++)
            Finish(lanes[i], shaders[i], target, depth);
        return true;
    }

    internal void TeamQuad(LaneTeam team, Triangle t, int qx, int qy, CpuTexture target, float[] depth, int breakX, int breakY)
    {
        var shaders = new object[4];
        team.Run(lane =>
        {
            Invoke(shaders[lane.Index], Break && lane.Pixel.x == breakX && lane.Pixel.y == breakY);
            Finish(lane, shaders[lane.Index], target, depth);
        }, lane => shaders[lane.Index] = Prepare(lane, t, qx, qy, target));
    }

    /// <summary>A pixel's shader: the streams interpolated at its centre (extrapolated for a helper), SV_Position, the face.</summary>
    private object Prepare(Lane lane, in Triangle t, int qx, int qy, CpuTexture target)
    {
        int x = qx + (lane.Index & 1), y = qy + (lane.Index >> 1);
        lane.Pixel = new int2(x, y);
        float px = x + 0.5f, py = y + 0.5f;
        bool covered = Weights(t, px, py, out float w0, out float w1, out float w2);
        float z = DepthAt(t, w0, w1, w2);
        lane.IsHelper = !covered || x >= target.Width || y >= target.Height || z < 0f || z > 1f;
        lane.Depth = z;
        // Perspective: the weights over w, normalized.
        float p0 = w0 * t.A.InverseW, p1 = w1 * t.B.InverseW, p2 = w2 * t.C.InverseW;
        float sum = p0 + p1 + p2;
        p0 /= sum;
        p1 /= sum;
        p2 /= sum;
        var shader = ShaderInstances.Clone(Shader);
        foreach (var field in interpolated)
            field.SetValue(shader, Interpolate(field, t.A.Shader, t.B.Shader, t.C.Shader, p0, p1, p2));
        foreach (var field in flat)
            field.SetValue(shader, field.GetValue(t.Provoking.Shader));
        setPosition?.Invoke(shader, new float4(px, py, z, 1f / sum));
        frontFace?.SetValue(shader, t.Front);
        return shader;
    }

    private static object Interpolate(FieldInfo field, object a, object b, object c, float p0, float p1, float p2)
    {
        var va = field.GetValue(a)!;
        var vb = field.GetValue(b)!;
        var vc = field.GetValue(c)!;
        switch (va)
        {
            case float fa:
                return fa * p0 + (float)vb * p1 + (float)vc * p2;
            case float2 f2:
                return f2 * p0 + (float2)vb * p1 + (float2)vc * p2;
            case float3 f3:
                return f3 * p0 + (float3)vb * p1 + (float3)vc * p2;
            default:
                return (float4)va * p0 + (float4)vb * p1 + (float4)vc * p2;
        }
    }

    private void Finish(Lane lane, object shader, CpuTexture target, float[] depth)
    {
        if (lane.Discarded || lane.IsHelper || getColor == null)
            return;
        int index = lane.Pixel.y * target.Width + lane.Pixel.x;
        if (DepthTest && lane.Depth > depth[index])
            return;
        if (DepthWrite)
            depth[index] = lane.Depth;
        target.Write(0, lane.Pixel.x, lane.Pixel.y, 0, getColor(shader));
    }
}

/// <summary>Draw calls run on the CPU together, sharing a target and a depth buffer, in order.</summary>
[System.Diagnostics.DebuggerNonUserCode]
public static class CpuScene
{
    /// <summary>Every draw into a new target, depth cleared to 1.</summary>
    public static CpuTexture Draw(IReadOnlyList<CpuMeshDraw> draws, int width, int height, TexelFormat format = TexelFormat.Rgba8UNorm, float4 clear = default)
    {
        var target = new CpuTexture(width, height, format: format);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                target.Write(0, x, y, 0, clear);
        var depth = Enumerable.Repeat(1f, width * height).ToArray();
        foreach (var draw in draws)
            draw.Draw(target, depth);
        return target;
    }

    /// <summary>
    /// The pixel the draws leave at (<paramref name="x"/>, <paramref name="y"/>): each draw's triangles
    /// tested there in order, depth included, and the last one to win it run for its quad, stopping in
    /// the debugger before that pixel when its draw has <see cref="Run.Break"/>. Null when no triangle
    /// covers it.
    /// </summary>
    public static (float4 Color, CpuMeshDraw Draw, int Triangle)? DebugPixel(IReadOnlyList<CpuMeshDraw> draws, int width, int height, int x, int y)
    {
        float depth = 1f;
        (CpuMeshDraw Draw, CpuMeshDraw.Triangle Triangle)? winner = null;
        foreach (var draw in draws)
        {
            var vertices = draw.RunVertices();
            for (int triangle = 0; triangle < draw.Mesh.Indices.Length / 3; triangle++)
            {
                if (!draw.Setup(vertices, triangle, out var t) || !CpuMeshDraw.Weights(t, x + 0.5f, y + 0.5f, out var w0, out var w1, out var w2))
                    continue;
                float z = CpuMeshDraw.DepthAt(t, w0, w1, w2);
                if (z < 0f || z > 1f || (draw.DepthTest && z > depth))
                    continue;
                if (draw.DepthWrite)
                    depth = z;
                winner = (draw, t);
            }
        }
        if (winner is not { } found)
            return null;
        var target = new CpuTexture(width, height);
        var depths = Enumerable.Repeat(1f, width * height).ToArray();
        using var team = new LaneTeam(4, found.Draw.Macros, isPixel: true);
        found.Draw.TeamQuad(team, found.Triangle, x & ~1, y & ~1, target, depths, x, y);
        return (target.Read(0, x, y), found.Draw, found.Triangle.Index);
    }
}
