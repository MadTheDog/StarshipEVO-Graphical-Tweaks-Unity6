#ifndef GT_SHADOW_VISIBILITY_INCLUDED
#define GT_SHADOW_VISIBILITY_INCLUDED
// Original receiver sampling. ShadowData contains rows, not packed columns.
Texture2D<float4> _GTShadowData;
Texture2D<float> _GTSunShadowAtlas;
float4 _GTFrameStamp;
float _GTCameraToken;
float _GTShadowReady;
float _GTShadowDistance;
float _GTReceiverBias;

float4 GTShadowData(uint index)
{
    return _GTShadowData.Load(int3(index, 0, 0));
}

float4 GTWorldToShadow(uint cascade, float3 worldPosition)
{
    float4 p = float4(worldPosition, 1);
    uint start = cascade * 4;
    return float4(dot(GTShadowData(start), p), dot(GTShadowData(start + 1), p),
                  dot(GTShadowData(start + 2), p), dot(GTShadowData(start + 3), p));
}

float GTDepthVisibility(int2 pixel, float receiverDepth, int2 tileMinimum, int2 tileMaximum)
{
    pixel = clamp(pixel, tileMinimum, tileMaximum);
    float depth = _GTSunShadowAtlas.Load(int3(pixel, 0));
    #if defined(UNITY_REVERSED_Z)
        return receiverDepth + _GTReceiverBias >= depth ? 1 : 0;
    #else
        return receiverDepth - _GTReceiverBias <= depth ? 1 : 0;
    #endif
}

float4 GTActiveShadowRadii(uint cascadeCount)
{
    float4 radii = GTShadowData(20);
    // FXC can lower radii[i] to a dot with a one-hot vector. Inactive native
    // lanes can be -Infinity; 0 * Infinity then poisons a valid selected lane.
    // Remove only INACTIVE values before any dynamic selection. Active lanes
    // retain their original values and all finite/positive checks still apply.
    if (cascadeCount == 2) return float4(radii.xy, 0, 0);
    return radii;
}

bool GTShadowTileBounds(uint selectedCascade, uint cascadeCount, uint width, uint height,
                       out int2 tileMinimum, out int2 tileMaximum)
{
    tileMinimum = int2(0, 0);
    tileMaximum = int2(width - 1, height - 1);
    if (cascadeCount == 1) return true;
    // Verified two-cascade native layout is two square tiles horizontally.
    // Four-cascade support requires a square atlas and independently verified
    // projected sphere centers in its 2x2 cells. Never infer cascade ordering.
    uint2 grid;
    if (cascadeCount == 2 && width == 2 * height) grid = uint2(2, 1);
    else if (cascadeCount == 4 && width == height && (width & 1) == 0) grid = uint2(2, 2);
    else return false;
    float4 radii = GTActiveShadowRadii(cascadeCount);
    uint occupied = 0;
    [unroll] for (uint i = 0; i < 4; i++)
    {
        if (i >= cascadeCount) continue;
        float3 sphereCenter = GTShadowData(16 + i).xyz;
        if (!all(isfinite(sphereCenter)) || !isfinite(radii[i]) || radii[i] <= 0) return false;
        float4 projectedCenter = GTWorldToShadow(i, sphereCenter);
        if (!all(isfinite(projectedCenter)) || abs(projectedCenter.w) < 1e-8) return false;
        float2 centerUV = projectedCenter.xy / projectedCenter.w;
        float2 cell = round(centerUV * float2(grid) - .5);
        if (any(cell < 0) || any(cell >= float2(grid))) return false;
        float2 expectedCenter = (cell + .5) / float2(grid);
        // Stabilized native projections offset sphere centers by fractions of
        // a texel (observed >0.5 atlas pixels). Allow 1.5 pixels plus arithmetic
        // tolerance, capped at a quarter of a tile for tiny test/low-res maps.
        // Centers therefore remain well away from any candidate tile border.
        float2 centerTolerance = min(float2(1.5001, 1.5001), float2(width, height) / float2(grid) * .25);
        if (any(abs(centerUV - expectedCenter) * float2(width, height) > centerTolerance)) return false;
        uint2 tile = (uint2)cell;
        uint bit = 1u << (tile.x + tile.y * grid.x);
        if ((occupied & bit) != 0) return false;
        occupied |= bit;
        if (i == selectedCascade)
        {
            int2 size = int2(width / grid.x, height / grid.y);
            tileMinimum = int2(tile) * size;
            tileMaximum = tileMinimum + size - 1;
        }
    }
    return true;
}

// Diagnostic status distinguishes fail-open/out-of-range from an actual sample.
#define GT_SHADOW_STATUS_INVALID 0u
#define GT_SHADOW_STATUS_OUTSIDE_CASCADE 1u
#define GT_SHADOW_STATUS_OUTSIDE_ATLAS 2u
#define GT_SHADOW_STATUS_SAMPLED 3u
#define GT_SHADOW_STATUS_DISTANCE_FADED 4u

half GTShadowVisibilityDetailed(float3 worldPosition, float3 worldNormal, out uint status)
{
    status = GT_SHADOW_STATUS_INVALID;
    if (_GTShadowReady < .5) return 1;
    float4 stamp = GTShadowData(23);
    if (stamp.w != 1234 || any(stamp.xy != _GTFrameStamp.xy) || stamp.z != _GTCameraToken) return 1;
    float4 atlasStamp = GTShadowData(24);
    if (atlasStamp.w != 4321 || any(atlasStamp.xy != _GTFrameStamp.xy) || atlasStamp.z != _GTCameraToken) return 1;
    float4 info = GTShadowData(21);
    if (!all(isfinite(info))) return 1;
    uint width = (uint)info.x, height = (uint)info.y;
    if (width == 0 || height == 0 || any(info.xy != float2(width, height))) return 1;
    uint boundWidth, boundHeight;
    _GTSunShadowAtlas.GetDimensions(boundWidth, boundHeight);
    if (boundWidth != width || boundHeight != height) return 1;
    uint cascadeCount = (uint)info.z;
    if ((cascadeCount != 1 && cascadeCount != 2 && cascadeCount != 4) || info.z != (float)cascadeCount) return 1;
    if (cascadeCount == 2 && width != 2 * height) return 1;
    if (cascadeCount == 4 && (width != height || (width & 1) != 0)) return 1;
    uint cascade = 0;
    if (cascadeCount > 1)
    {
        float4 radii = GTActiveShadowRadii(cascadeCount);
        bool found = false;
        [unroll] for (uint i = 0; i < 4; i++)
        {
            if (i < cascadeCount && (!isfinite(radii[i]) || radii[i] <= 0 || !all(isfinite(GTShadowData(16 + i).xyz)))) return 1;
            float3 delta = worldPosition - GTShadowData(16 + i).xyz;
            if (!found && i < cascadeCount && dot(delta, delta) <= radii[i])
            { cascade = i; found = true; }
        }
        if (!found) { status = GT_SHADOW_STATUS_OUTSIDE_CASCADE; return 1; }
    }
    int2 tileMinimum, tileMaximum;
    if (!GTShadowTileBounds(cascade, cascadeCount, width, height, tileMinimum, tileMaximum)) return 1;
    float4 coordinate = GTWorldToShadow(cascade, worldPosition);
    if (!all(isfinite(coordinate)) || abs(coordinate.w) < 1e-8) { status = GT_SHADOW_STATUS_OUTSIDE_ATLAS; return 1; }
    float3 uvz = coordinate.xyz / coordinate.w;
    if (any(uvz < 0) || any(uvz > 1)) { status = GT_SHADOW_STATUS_OUTSIDE_ATLAS; return 1; }
    float2 atlasPixel = uvz.xy * float2(width, height);
    if (any(atlasPixel < float2(tileMinimum)) || any(atlasPixel > float2(tileMaximum + 1)))
    { status = GT_SHADOW_STATUS_OUTSIDE_ATLAS; return 1; }
    // Raw depth loads avoid relying on an inherited comparison-sampler state.
    float2 texel = uvz.xy * float2(width, height) - .5;
    int2 basePixel = (int2)floor(texel);
    float2 weight = frac(texel);
    float a = GTDepthVisibility(basePixel, uvz.z, tileMinimum, tileMaximum);
    float b = GTDepthVisibility(basePixel + int2(1, 0), uvz.z, tileMinimum, tileMaximum);
    float c = GTDepthVisibility(basePixel + int2(0, 1), uvz.z, tileMinimum, tileMaximum);
    float d = GTDepthVisibility(basePixel + int2(1, 1), uvz.z, tileMinimum, tileMaximum);
    float visibility = lerp(lerp(a, b, weight.x), lerp(c, d, weight.x), weight.y);
    visibility = lerp(1, visibility, saturate(info.w));
    status = GT_SHADOW_STATUS_SAMPLED;
    if (_GTShadowDistance > 0)
    {
        float distanceToCamera = distance(worldPosition, _WorldSpaceCameraPos);
        float fade = saturate((distanceToCamera - .9 * _GTShadowDistance) / max(.1 * _GTShadowDistance, .001));
        visibility = lerp(visibility, 1, fade);
        if (fade >= 1) status = GT_SHADOW_STATUS_DISTANCE_FADED;
    }
    return visibility;
}

half GTShadowVisibility(float3 worldPosition, float3 worldNormal)
{
    uint status;
    return GTShadowVisibilityDetailed(worldPosition, worldNormal, status);
}

// Diagnostic-only recheck of INVALID exits. Production Detailed and its scalar
// wrapper do not call this helper. Return 0 when no invalid exit is reproduced.
uint GTShadowInvalidReason(float3 worldPosition)
{
    if (_GTShadowReady < .5) return 1;
    float4 stamp = GTShadowData(23);
    if (stamp.w != 1234) return 2;
    if (any(stamp.xy != _GTFrameStamp.xy)) return 3;
    if (stamp.z != _GTCameraToken) return 4;
    float4 atlasStamp = GTShadowData(24);
    if (atlasStamp.w != 4321) return 5;
    if (any(atlasStamp.xy != _GTFrameStamp.xy)) return 6;
    if (atlasStamp.z != _GTCameraToken) return 7;
    float4 info = GTShadowData(21);
    if (!all(isfinite(info))) return 8;
    uint width = (uint)info.x, height = (uint)info.y;
    if (width == 0 || height == 0 || any(info.xy != float2(width, height))) return 9;
    uint boundWidth, boundHeight;
    _GTSunShadowAtlas.GetDimensions(boundWidth, boundHeight);
    if (boundWidth != width || boundHeight != height) return 10;
    uint cascadeCount = (uint)info.z;
    if ((cascadeCount != 1 && cascadeCount != 2 && cascadeCount != 4) || info.z != (float)cascadeCount) return 11;
    if (cascadeCount == 2 && width != 2 * height) return 12;
    if (cascadeCount == 4 && (width != height || (width & 1) != 0)) return 12;
    if (cascadeCount == 1) return 0;
    float4 radii = GTActiveShadowRadii(cascadeCount);
    bool found = false;
    [unroll] for (uint i = 0; i < 4; i++)
    {
        if (i < cascadeCount)
        {
            if (!isfinite(radii[i]) || radii[i] <= 0) return 13;
            if (!all(isfinite(GTShadowData(16 + i).xyz))) return 14;
        }
        float3 delta = worldPosition - GTShadowData(16 + i).xyz;
        if (!found && i < cascadeCount && dot(delta, delta) <= radii[i]) found = true;
    }
    if (!found) return 0; // Detailed reports OUTSIDE_CASCADE, not INVALID.
    uint2 grid = cascadeCount == 2 ? uint2(2, 1) : uint2(2, 2);
    uint occupied = 0;
    [unroll] for (uint tileCascade = 0; tileCascade < 4; tileCascade++)
    {
        if (tileCascade >= cascadeCount) continue;
        float3 sphereCenter = GTShadowData(16 + tileCascade).xyz;
        float4 projectedCenter = GTWorldToShadow(tileCascade, sphereCenter);
        if (!all(isfinite(projectedCenter)) || abs(projectedCenter.w) < 1e-8) return 15;
        float2 centerUV = projectedCenter.xy / projectedCenter.w;
        float2 cell = round(centerUV * float2(grid) - .5);
        if (any(cell < 0) || any(cell >= float2(grid))) return 16;
        float2 expectedCenter = (cell + .5) / float2(grid);
        float2 tolerance = min(float2(1.5001, 1.5001), float2(width, height) / float2(grid) * .25);
        if (any(abs(centerUV - expectedCenter) * float2(width, height) > tolerance)) return 17;
        uint2 tile = (uint2)cell;
        uint bit = 1u << (tile.x + tile.y * grid.x);
        if ((occupied & bit) != 0) return 18;
        occupied |= bit;
    }
    return 0;
}
#endif
