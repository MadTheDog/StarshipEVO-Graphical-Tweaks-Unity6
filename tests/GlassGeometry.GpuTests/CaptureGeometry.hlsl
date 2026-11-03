// Original pass-through geometry stage for stream-output comparison.
struct FixtureInput
{
    float4 position : SV_POSITION;
    #ifdef GT_NATIVE_INPUTS
        float4 uv : TEXCOORD0;
        float3 normal : TEXCOORD1;
        uint4 ids : TEXCOORD2;
        float3 rendererPosition : TEXCOORD3;
        float3 tangent : TEXCOORD4;
        float3 rendererNormal : TEXCOORD5;
        float3 extra6 : TEXCOORD6;
        float4 extra7 : TEXCOORD7;
        float3 binormal : TEXCOORD8;
        float3 worldPosition : TEXCOORD10;
        float3 extra11 : TEXCOORD11;
        float3 extra12 : TEXCOORD12;
    #else
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
    #endif
};
struct FixtureCapture
{
    float4 position : CAPTURE0;
    float4 normal : CAPTURE1;
    float4 worldPosition : CAPTURE2;
    float4 uv : CAPTURE3;
    float4 material : CAPTURE4;
};
[maxvertexcount(1)]
void CaptureGeometry(point FixtureInput input[1], inout PointStream<FixtureCapture> output)
{
    FixtureCapture capture;
    capture.position = input[0].position;
    capture.normal = float4(input[0].normal, 0);
    capture.worldPosition = float4(input[0].worldPosition, 1);
    #ifdef GT_NATIVE_INPUTS
        capture.uv = input[0].uv;
        capture.material = float4(input[0].ids.w, 0, 0, 1);
    #else
        capture.uv = float4(input[0].uv, input[0].uvExpansion);
        capture.material = float4(input[0].material, 0, 0, input[0].supported);
    #endif
    output.Append(capture);
}
