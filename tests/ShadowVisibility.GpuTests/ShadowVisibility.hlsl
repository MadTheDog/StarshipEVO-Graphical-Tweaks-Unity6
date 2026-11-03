// Original standalone wrapper; the production include is tested without Unity.
float3 _WorldSpaceCameraPos;
float4 _GTTestWorldPosition;
#include "ShadowVisibility.cginc"

float4 FixtureVertex(uint id : SV_VertexID) : SV_POSITION
{
    float2 uv = float2((id << 1) & 2, id & 2);
    return float4(uv * 2 - 1, 0, 1);
}

float4 FixtureFragment(float4 position : SV_POSITION) : SV_Target
{
    #if defined(GT_TEST_STATUS) || defined(GT_TEST_INVALID_REASON)
    uint status;
    float visibility = GTShadowVisibilityDetailed(_GTTestWorldPosition.xyz, float3(0, 1, 0), status);
    #if defined(GT_TEST_INVALID_REASON)
    if (status == GT_SHADOW_STATUS_INVALID) status = 100u + GTShadowInvalidReason(_GTTestWorldPosition.xyz);
    #endif
    return float4(visibility, (float)status, 0, 1);
    #else
    float visibility = GTShadowVisibility(_GTTestWorldPosition.xyz, float3(0, 1, 0));
    return visibility.xxxx;
    #endif
}
