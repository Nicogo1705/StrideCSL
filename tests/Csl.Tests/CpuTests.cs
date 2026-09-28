using Csl.Cpu;
using Csl.Engine;
using Csl.Types;
using static Csl.Types.Intrinsics;

namespace Csl.Tests
{
    /// <summary>Shader code run on the CPU (Csl.Cpu): the pieces, then shaders run whole.</summary>
    public class CpuTests
    {
        [Theory]
        [InlineData("defined(A) && B > 2", true)]
        [InlineData("defined(C) || !defined(A)", false)]
        [InlineData("STRIDE_GRAPHICS_PROFILE >= GRAPHICS_PROFILE_LEVEL_10_0", true)]
        [InlineData("B == 3 && (A + 1) * 2 == 4", true)]
        [InlineData("UNKNOWN", false)]
        public void MacroConditionsEvaluateAsThePreprocessor(string condition, bool expected)
        {
            var macros = Macros.Direct3D11().Set("A", 1).Set("B", 3);
            Assert.Equal(expected, macros.Evaluate(condition));
        }

        [Fact]
        public void MacroValuesConvertAsHlslWould()
        {
            dynamic value = Macros.Direct3D11().Set("N", 8).Value("N");
            uint asUint = value * value;
            float asFloat = value / 2.5f;
            Assert.Equal(64u, asUint);
            Assert.Equal(3.2f, asFloat, 5);
        }

        [Theory]
        [InlineData("MIN_MAG_MIP_POINT", FilterMode.Point, FilterMode.Point, FilterMode.Point)]
        [InlineData("MIN_MAG_POINT_MIP_LINEAR", FilterMode.Point, FilterMode.Point, FilterMode.Linear)]
        [InlineData("MIN_POINT_MAG_LINEAR_MIP_POINT", FilterMode.Point, FilterMode.Linear, FilterMode.Point)]
        [InlineData("COMPARISON_MIN_MAG_LINEAR_MIP_POINT", FilterMode.Linear, FilterMode.Linear, FilterMode.Point)]
        [InlineData("Point", FilterMode.Point, FilterMode.Point, FilterMode.Point)]
        [InlineData("MinPointMagMipLinear", FilterMode.Point, FilterMode.Linear, FilterMode.Linear)]
        public void FiltersParseInD3DAndStrideWords(string text, FilterMode min, FilterMode mag, FilterMode mip)
        {
            var description = new SamplerDescription();
            SamplerDescription.ParseFilter(text, description);
            Assert.Equal((min, mag, mip), (description.MinFilter, description.MagFilter, description.MipFilter));
        }

        private static CpuTexture Checker2x2() => new CpuTexture(2, 2).Fill(new[]
        {
            new float4(0f, 0f, 0f, 1f), new float4(1f, 0f, 0f, 1f),
            new float4(0f, 1f, 0f, 1f), new float4(1f, 1f, 0f, 1f),
        });

        [Fact]
        public void PointSamplingReadsTheTexelUnderTheCoordinate()
        {
            var texture = new Texture2D(Checker2x2());
            var point = new SamplerState(new SamplerDescription { MinFilter = FilterMode.Point, MagFilter = FilterMode.Point, MipFilter = FilterMode.Point });
            Assert.Equal(1f, texture.SampleLevel(point, new float2(0.75f, 0.25f), 0f).x);
            Assert.Equal(1f, texture.SampleLevel(point, new float2(0.25f, 0.75f), 0f).y);
        }

        [Fact]
        public void LinearSamplingBlendsTheFourTexelsWithWrapOrClamp()
        {
            var texture = new Texture2D(Checker2x2());
            var clamp = new SamplerState(SamplerDescription.Default);
            var wrap = new SamplerState(new SamplerDescription { AddressU = AddressMode.Wrap, AddressV = AddressMode.Wrap });
            // Between the four texel centres: their average.
            Assert.Equal(0.5f, texture.SampleLevel(clamp, new float2(0.5f, 0.5f), 0f).x);
            // At the left edge: clamped, the left column only; wrapped, half of the right column.
            Assert.Equal(0f, texture.SampleLevel(clamp, new float2(0f, 0.25f), 0f).x);
            Assert.Equal(0.5f, texture.SampleLevel(wrap, new float2(0f, 0.25f), 0f).x);
        }

        [Fact]
        public void TexturesWithoutCpuDataStillThrow()
        {
            var texture = default(Texture2D);
            Assert.Throws<NotSupportedException>(() => texture.SampleLevel(default, new float2(0f, 0f), 0f));
        }

        [Fact]
        public void RenderTargetWritesRoundToTheFormat()
        {
            var target = new CpuTexture(1, 1, format: TexelFormat.Rgba8UNorm);
            target.Write(0, 0, 0, 0, new float4(0.5f, 1.5f, -1f, float.NaN));
            var stored = target.Read(0, 0, 0);
            Assert.Equal(128f / 255f, stored.x);
            Assert.Equal((1f, 0f, 0f), (stored.y, stored.z, stored.w));
        }

        [Fact]
        public void HlslConversionsTruncateSplatAndSaturate()
        {
            Assert.Equal(new float3(1f, 2f, 3f), HlslConvert.Convert<float3>(new float4(1f, 2f, 3f, 4f)));
            Assert.Equal(new float3(5f, 5f, 5f), HlslConvert.Convert<float3>(5));
            Assert.Equal(0u, HlslConvert.Convert<uint>(float.NaN));
            Assert.Equal(uint.MaxValue, HlslConvert.Convert<uint>(-1));
            Assert.True(HlslConvert.Convert<bool>(2));
            Assert.Equal(new int2(1, -2), HlslConvert.Convert<int2>(new float2(1.9f, -2.9f)));
        }

        [Fact]
        public void NanFollowsD3D()
        {
            Assert.Equal(1f, min(float.NaN, 1f));
            Assert.Equal(1f, max(1f, float.NaN));
            Assert.Equal(0f, saturate(float.NaN));
        }

        [Fact]
        public void QuadLanesExchangeDerivatives()
        {
            using var team = new LaneTeam(4, Macros.Direct3D11());
            var fine = new float4[4];
            var coarse = new float4[4];
            team.Run(lane =>
            {
                // A value that grows 1 per pixel along x and 10 along y, plus a lane-dependent bend.
                float v = (lane.Index & 1) + 10f * (lane.Index >> 1) + (lane.Index == 3 ? 5f : 0f);
                var (dx, dy) = lane.DerivativesFine(new float4(v, 0f, 0f, 0f));
                fine[lane.Index] = new float4(dx.x, dy.x, 0f, 0f);
                var (cx, cy) = lane.DerivativesCoarse(new float4(v, 0f, 0f, 0f));
                coarse[lane.Index] = new float4(cx.x, cy.x, 0f, 0f);
            });
            // Fine: each row and column its own difference; coarse: the top row's and the left column's.
            Assert.Equal(new float4(1f, 10f, 0f, 0f), fine[0]);
            Assert.Equal(new float4(6f, 15f, 0f, 0f), fine[3]);
            Assert.Equal(new float4(1f, 10f, 0f, 0f), coarse[3]);
        }

        [Fact]
        public void ImageEffectRunsPixelsWithDerivativesDiscardAndAnEngineMixin()
        {
            var run = new CpuImageEffect(typeof(CpuShaders.CpuTestEffect), 4, 2) { Format = TexelFormat.Rgba32Float };
            run.Set("Scale", 2f);
            var image = run.Draw().ToArray();
            // Red: ddx of TexCoord.x times Scale, one pixel of 4 is 0.25. Green: BlendUtils.Overlay (a
            // mixin, run through its stub) of 0.5 over 0.5, which is 0.5. Blue: the engine's static Luma of white.
            Assert.Equal(0.5f, image[0].x, 5);
            Assert.Equal(0.5f, image[0].y, 5);
            Assert.Equal(1f, image[0].z, 3);
            // The last pixel discards itself: left as the target was, 0.
            Assert.Equal(0f, image[7].w);
            Assert.Equal(1f, image[6].w);
        }

        [Fact]
        public void ComputeGroupsShareMemoryAcrossABarrier()
        {
            var output = new uint[16];
            var run = new CpuComputeShader(typeof(CpuShaders.CpuTestReverse));
            run.Set("Output", new RWStructuredBuffer<uint>(output));
            run.Dispatch(2);
            // Each group of 8 writes its thread ids reversed, read from group-shared memory after the barrier.
            Assert.Equal(new uint[] { 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8 }, output);
        }

        [Fact]
        public void MembersOfTheSameNameAreOne()
        {
            var from = new CpuShaders.CpuTestEffect { Scale = 3f };
            var to = (CpuShaders.CpuTestEffect)ShaderInstances.Create(typeof(CpuShaders.CpuTestEffect));
            Members.Copy(from, to);
            Assert.Equal(3f, to.Scale);
        }
    }
}

namespace Csl.Tests.CpuShaders
{
    [Shader, Mixin(typeof(BlendUtils))]
    public partial class CpuTestEffect : ImageEffectShader
    {
        [Stage] public float Scale;

        [Stage]
        public override float4 Shading()
        {
            float dx = ddx(streams.TexCoord.x) * Scale;
            float overlay = Overlay(new float4(0.5f, 0.5f, 0.5f, 0.5f), new float4(0.5f, 0.5f, 0.5f, 0.5f)).x;
            float luma = LuminanceUtils.Luma(new float3(1.0f, 1.0f, 1.0f));
            if (streams.ShadingPosition.x > 3.0f && streams.ShadingPosition.y > 1.0f)
                discard();
            return new float4(dx, overlay, luma, 1.0f);
        }
    }

    [Shader, NumThreads(8)]
    public partial class CpuTestReverse : ComputeShaderBase
    {
        [Stage] public RWStructuredBuffer<uint> Output;
        [GroupShared, Size("8")] public static uint[] Ids;

        public override void Compute()
        {
            uint i = streams.GroupThreadId.x;
            Ids[i] = streams.DispatchThreadId.x;
            GroupMemoryBarrierWithGroupSync();
            Output[streams.DispatchThreadId.x] = Ids[7 - i];
        }
    }
}
