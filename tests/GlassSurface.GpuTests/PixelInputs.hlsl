// Original synthetic raster inputs for pixel-stage interoperability tests.
cbuffer FixtureInputs : register(b0)
{
    float4 fixtureNormal;
    uint fixturePaint; float3 fixturePadding;
    float4 fixtureWorldSpan;
    float4 fixtureNormalDelta;
    float4 fixtureUV;
    float4 fixtureUVSpan;
};
#ifdef GT_NATIVE_INPUTS
struct FixtureOutput
{
    float4 position : SV_POSITION;
    float4 uv : TEXCOORD0;
    float3 normal : TEXCOORD1;
    nointerpolation uint4 ids : TEXCOORD2;
    float3 rendererPosition : TEXCOORD3;
    float3 tangent : TEXCOORD4;
    float3 rendererNormal : TEXCOORD5;
    float3 extra6 : TEXCOORD6;
    float4 extra7 : TEXCOORD7;
    float3 binormal : TEXCOORD8;
    float3 worldPosition : TEXCOORD10;
    float3 extra11 : TEXCOORD11;
    float3 extra12 : TEXCOORD12;
    float4 extra14 : TEXCOORD14;
};
#else
struct FixtureOutput
{
    float4 position : SV_POSITION;
    float3 worldPosition : TEXCOORD0;
    float3 normal : TEXCOORD1;
    nointerpolation uint material : TEXCOORD2;
    nointerpolation float supported : TEXCOORD3;
    nointerpolation uint vertexMode : TEXCOORD4;
    nointerpolation uint resizeMode : TEXCOORD5;
    float2 uv : TEXCOORD6;
    float3 tangent : TEXCOORD7;
    float3 binormal : TEXCOORD8;
    nointerpolation uint build : TEXCOORD9;
    nointerpolation uint paint : TEXCOORD10;
    float3 rendererNormal : TEXCOORD11;
    float3 rendererPosition : TEXCOORD12;
    float2 uvExpansion : TEXCOORD13;
};
#endif
FixtureOutput FixtureVertex(uint id : SV_VertexID)
{
    FixtureOutput output = (FixtureOutput)0;
    output.position = float4(id == 1 ? 3 : -1, id == 2 ? 3 : -1, 0.5, 1);
    output.worldPosition = float3(output.position.xy, 1) * fixtureWorldSpan.xyz;
    output.normal = fixtureNormal.xyz + float3(output.position.xy, 0) * fixtureNormalDelta.xyz;
    output.rendererNormal = output.normal;
    output.tangent = float3(1, 0, 0);
    output.binormal = float3(0, 1, 0);
    #ifdef GT_NATIVE_INPUTS
        output.uv = float4(fixtureUV.xy + fixtureUVSpan.xy * output.position.xy, fixtureUV.zw);
    #else
        output.uv = fixtureUV.xy + fixtureUVSpan.xy * output.position.xy;
        output.uvExpansion = fixtureUV.zw;
        output.supported = 1;
        output.paint = fixturePaint;
    #endif
    return output;
}
