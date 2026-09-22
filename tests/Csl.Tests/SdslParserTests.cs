using Csl.Generators.Sdsl;

namespace Csl.Tests;

public class SdslParserTests
{
    [Fact]
    public void ReadsTheStepShader()
    {
        var file = SdslParser.Parse(TestData.ShaderPath("VoxelWaterStep"), TestData.Shader("VoxelWaterStep"));

        Assert.Empty(file.Errors);
        Assert.Equal("Demo", file.Namespace);
        var shader = Assert.Single(file.Shaders);
        Assert.Equal("VoxelWaterStep", shader.Name);
        Assert.Equal(new[] { "ComputeShaderBase", "VoxelWaterBricks" }, shader.Bases);
        Assert.Contains("One flow step of the water", shader.Doc[0]);

        var changedOut = shader.Members.Single(m => m.Name == "ChangedOut");
        Assert.Equal("RWTexture3D", changedOut.Type);
        Assert.Equal("uint", changedOut.GenericArgument);
        Assert.True(changedOut.IsStage);
        Assert.Contains("Set to one for the group's brick", changedOut.Doc[0]);

        var anyChanged = shader.Members.Single(m => m.Name == "anyChanged");
        Assert.True(anyChanged.IsGroupShared);

        Assert.Equal(SdslAccess.Write, shader.Usage["ChangedOut"]);
        Assert.Equal(SdslAccess.Read, shader.Usage["Terrain"]);
        Assert.Equal(SdslAccess.Read, shader.Usage["Amounts"]);
        Assert.Equal(SdslAccess.Write, shader.Usage["AmountsOut"]);
        Assert.Contains(shader.Methods, m => m.Name == "Compute");
        Assert.Contains(shader.Methods, m => m.Name == "Lateral");
    }

    [Fact]
    public void SeesAReadBackThroughTheRwView()
    {
        var file = SdslParser.Parse(TestData.ShaderPath("VoxelWaterSpread"), TestData.Shader("VoxelWaterSpread"));
        var shader = Assert.Single(file.Shaders);

        // DrawnOut[b] = Reset != 0 ? on : (DrawnOut[b] | on): written, and read.
        Assert.Equal(SdslAccess.ReadWrite, shader.Usage["DrawnOut"]);
        Assert.Equal(SdslAccess.Write, shader.Usage["ActiveOut"]);
        Assert.Equal(SdslAccess.Read, shader.Usage["ChangedBricks"]);
    }

    [Fact]
    public void ReadsAMixinWithoutComputeBase()
    {
        var file = SdslParser.Parse(TestData.ShaderPath("VoxelWaterBricks"), TestData.Shader("VoxelWaterBricks"));
        var shader = Assert.Single(file.Shaders);
        Assert.Empty(shader.Bases);
        Assert.Equal(3, shader.Members.Count);
        Assert.Equal(SdslAccess.Read, shader.Usage["ActiveBricks"]);
    }

    [Fact]
    public void ReadsTheEngineComputeBase()
    {
        var file = SdslParser.Parse(TestData.DataPath("ComputeShaderBase"), File.ReadAllText(TestData.DataPath("ComputeShaderBase")));
        Assert.Empty(file.Errors);
        Assert.Null(file.Namespace);
        var shader = Assert.Single(file.Shaders);

        var groupId = shader.Members.Single(m => m.Name == "GroupId");
        Assert.True(groupId.IsStream);
        Assert.True(groupId.IsStage);
        Assert.Equal("uint3", groupId.Type);

        var count = shader.Members.Single(m => m.Name == "ThreadGroupCountGlobal");
        Assert.False(count.IsStream);
        Assert.Contains(count.Attributes, a => a.Name == "Link" && a.Argument == "ComputeShaderBase.ThreadGroupCountGlobal");

        Assert.Contains(shader.Methods, m => m.Name == "CSMain");
        Assert.Contains(shader.Methods, m => m.Name == "Compute");
    }

    [Fact]
    public void ReadsDeclarationsItDoesNotWrap()
    {
        var file = SdslParser.Parse("Sample.sdsl", @"
namespace Test.Shaders
{
    shader Sample<int Count> : Base<Count>, Other
    {
        compose ComputeColor color;
        static const float Pi = 3.14;
        [Color] float3 Tint = float3(1, 0, 0);
        float Weights[4];
        Texture2D<float4> Albedo, Normal;
        SamplerState LinearSampler { Filter = MIN_MAG_MIP_LINEAR; };
        cbuffer PerDraw { float4x4 World; stage float Scale = 2; }
        StructuredBuffer<float> Data;
        RWStructuredBuffer<float> DataOut;
        abstract float Shade();
        override float4 Shading() { float v = Data[0]; DataOut[1] = v; InterlockedAdd(DataOut[2], 1.0); return Albedo.Sample(LinearSampler, float2(0, 0)); }
    };
}");
        var shader = Assert.Single(file.Shaders);
        Assert.Equal(new[] { "int Count" }, shader.GenericParameters);
        Assert.Equal(new[] { "Base", "Other" }, shader.Bases);
        Assert.True(shader.Members.Single(m => m.Name == "color").IsCompose);
        Assert.True(shader.Members.Single(m => m.Name == "Pi").IsConst);
        Assert.Contains(shader.Members.Single(m => m.Name == "Tint").Attributes, a => a.Name == "Color");
        Assert.Equal("float3(1, 0, 0)", shader.Members.Single(m => m.Name == "Tint").Initializer);
        Assert.True(shader.Members.Single(m => m.Name == "Weights").IsArray);
        Assert.Equal("Texture2D", shader.Members.Single(m => m.Name == "Normal").Type);
        Assert.Equal("float4", shader.Members.Single(m => m.Name == "Normal").GenericArgument);
        Assert.Equal("float4x4", shader.Members.Single(m => m.Name == "World").Type);
        Assert.True(shader.Members.Single(m => m.Name == "Scale").IsStage);
        Assert.Equal(SdslAccess.Read, shader.Usage["Data"]);
        Assert.Equal(SdslAccess.ReadWrite, shader.Usage["DataOut"]);
        Assert.Equal(SdslAccess.Read, shader.Usage["Albedo"]);
        Assert.Equal(SdslAccess.Read, shader.Usage["LinearSampler"]);
        Assert.Equal(SdslAccess.None, shader.Usage["Normal"]);
    }

    [Fact]
    public void MapsTypesLikeTheEngine()
    {
        static string? Map(string type, string? generic = null, bool array = false, params (string, string?)[] attributes)
        {
            var member = new SdslMember(type, "X", 1, 1) { GenericArgument = generic, IsArray = array };
            member.Attributes.AddRange(attributes);
            return SdslTypes.ToCSharp(member);
        }

        Assert.Equal("float", Map("float"));
        Assert.Equal("float", Map("half"));
        Assert.Equal("Int3", Map("int3"));
        Assert.Equal("Int2", Map("uint2"));
        Assert.Equal("Vector3", Map("float3"));
        Assert.Equal("Color3", Map("float3", attributes: ("Color", null)));
        Assert.Equal("Vector2", Map("float2", attributes: ("Color", null)));
        Assert.Equal("Matrix", Map("float4x4"));
        Assert.Null(Map("float3x3"));
        Assert.Equal("Texture", Map("RWTexture3D", "uint"));
        Assert.Equal("Texture", Map("TextureCube"));
        Assert.Equal("Buffer", Map("StructuredBuffer", "float"));
        Assert.Equal("SamplerState", Map("SamplerState"));
        Assert.Equal("float[]", Map("float", array: true));
        Assert.Equal("MyType", Map("Anything", attributes: ("Type", "MyType")));
        Assert.Null(Map("MyStruct"));
    }
}
