// Original material adapter for the documented procedural-buffer interface.
// Lighting equations are supplied by the matching Unity Editor's own includes.
#ifndef GRAPHICAL_TWEAKS_GLASS_SURFACE_MATERIAL_INCLUDED
#define GRAPHICAL_TWEAKS_GLASS_SURFACE_MATERIAL_INCLUDED

#ifndef _ALPHAPREMULTIPLY_ON
#define _ALPHAPREMULTIPLY_ON 1
#endif
#ifndef UNITY_SPECCUBE_BOX_PROJECTION
#define UNITY_SPECCUBE_BOX_PROJECTION 1
#endif
#ifndef UNITY_SPECCUBE_BLENDING
#define UNITY_SPECCUBE_BLENDING 1
#endif
// Match the inspected ordinary D3D11 directional-with-screen-shadow variant.
// Other native BRDF tiers require their own visual equivalence check.
#ifndef UNITY_BRDF_PBS
#define UNITY_BRDF_PBS BRDF1_Unity_PBS
#endif
#include "UnityPBSLighting.cginc"
#include "UnityLightingCommon.cginc"
#include "GlassSurfaceGeometry.hlsl"

UNITY_DECLARE_TEX2DARRAY(MAIN_TEX);
UNITY_DECLARE_TEX2DARRAY(NORMAL_TEX);
UNITY_DECLARE_TEX2DARRAY(SPECULAR_TEX);

struct GTPaletteRecord
{
    float4 color;
    float roughness;
    float metallic;
};
StructuredBuffer<GTPaletteRecord> PaintData;
StructuredBuffer<float> _EntityCanBuild;
StructuredBuffer<uint> _GTGlassFaceMasks;
int _GTGlassFaceMaskCount;
float _LightWobble;
float4 _GlowChromaVarying;
float OverHeat;
float _DamagedItemPreview;
// -1: select from the GPU SHADOWS_SCREEN variant, matching native pass 5.
// 0: force GGX/BRDF1; 1: force lookup/BRDF3 for diagnostic comparisons.
int _GTBRDFMode;

struct GTGlassSurface
{
    half3 albedo;
    half3 normal;
    half metallic;
    half smoothness;
    half inputAlpha;
    half outputAlpha;
    half3 specular;
    half3 diffuse;
    half oneMinusReflectivity;
    half distortionAlpha;
    half3 emission;
    half supported;
};

void GTClipCoveredFace(GTSurfaceVaryings input)
{
    if (input.buildIndex >= (uint)max(_GTGlassFaceMaskCount, 0))
        return;
    uint mask = _GTGlassFaceMasks[input.buildIndex];
    if (mask == 0)
        return;
    float3 normal = normalize(input.entityNormal);
    uint face;
    if (abs(normal.x) > 0.999)
        face = normal.x > 0 ? 0 : 1;
    else if (abs(normal.y) > 0.999)
        face = normal.y > 0 ? 2 : 3;
    else if (abs(normal.z) > 0.999)
        face = normal.z > 0 ? 4 : 5;
    else
        return;
    if ((mask & (1u << face)) != 0)
        clip(-1);
}

half3 GTPaintOverlay(half3 textureColor, half3 paintColor)
{
    half3 lower = 2 * textureColor * paintColor;
    half3 upper = 1 - 2 * (1 - textureColor) * (1 - paintColor);
    return half3(textureColor.r < 0.5 ? lower.r : upper.r,
                 textureColor.g < 0.5 ? lower.g : upper.g,
                 textureColor.b < 0.5 ? lower.b : upper.b);
}

bool GTGlassOrdinaryMaterialSupported(GTMaterialRecord material)
{
    bool uniformClear = material.diffuseIndex == 183 && material.specularIndex == 154 &&
                        material.normalIndex < 0 && material.borderIndex < 0;
    bool projectionSupported = material.projectionMode == 0 ||
                               (uniformClear && material.projectionMode == 1);
    bool borderLayout = material.borderMode == 5 ||
        (material.borderMode == 3 && material.vWidth > 0) ||
        (material.borderMode == 4 && material.uWidth > 0 && material.vWidth > 0);
    bool borderSupported = material.borderIndex < 0 || (material.projectionMode == 0 && borderLayout);
    return borderSupported && projectionSupported && material.pixelMode == 0 &&
           (material.alphaMode == 2 || material.alphaMode == 5 || material.alphaMode == 7);
}

float GTGlassBorderAxis(float coordinate,float expansion,float scale,float low,float high,float width)
{
    if(coordinate>high+scale*expansion)return coordinate-scale*expansion;
    if(coordinate>high)return frac((coordinate-high)/width)*width+low;
    return frac(coordinate);
}

bool GTGlassGlowMaterialSupported(GTMaterialRecord material)
{
    return material.alphaMode == 9 && material.borderIndex < 0 && material.projectionMode == 0 &&
           (material.pixelMode == 0 || material.pixelMode == 1);
}

GTGlassSurface GTReadGlassSurface(GTSurfaceVaryings input, GTMaterialRecord material)
{
    GTGlassSurface surface = (GTGlassSurface)0;
    float2 uv = input.uv;
    half3 tangent = input.worldTangent;
    half3 binormal = input.worldBinormal;
    if (material.projectionMode == 1)
    {
        bool xFace = abs(input.entityNormal.x) > 0.7;
        bool yFace = !xFace && abs(input.entityNormal.y) > 0.7;
        uv = xFace ? input.rendererPosition.yz :
             (yFace ? input.rendererPosition.xz : input.rendererPosition.xy);
        uv += 1000.5;
        tangent = normalize(mul((float3x3)unity_ObjectToWorld,
            xFace ? float3(0, 1, 0) : float3(1, 0, 0)));
        binormal = cross(input.worldNormal, tangent) * unity_WorldTransformParams.w;
    }
    half4 color = UNITY_SAMPLE_TEX2DARRAY(MAIN_TEX, float3(uv, material.diffuseIndex));
    half4 properties = 0;
    if (material.specularIndex >= 0)
        properties = UNITY_SAMPLE_TEX2DARRAY(SPECULAR_TEX, float3(uv, material.specularIndex));

    surface.normal = normalize(input.worldNormal);
    if (material.normalIndex >= 0)
    {
        half4 encoded = UNITY_SAMPLE_TEX2DARRAY(NORMAL_TEX, float3(uv, material.normalIndex));
        half3 tangentNormal = UnpackNormal(encoded);
        surface.normal = normalize(tangentNormal.x * tangent
                                 + tangentNormal.y * binormal
                                 + tangentNormal.z * surface.normal);
    }

    if(material.borderIndex>=0)
    {
        float2 borderUV=frac(input.uv);
        if(material.borderMode==3||material.borderMode==4)
        {
            borderUV.y=GTGlassBorderAxis(input.uv.y,input.uvExpansion.y,material.uvScale.y,
                                        material.vLow,material.vHigh,material.vWidth);
            if(material.borderMode==4)
                borderUV.x=GTGlassBorderAxis(input.uv.x,input.uvExpansion.x,material.uvScale.x,
                                            material.uLow,material.uHigh,material.uWidth);
        }
        // Keep the original UV gradients across remapped border seams.
        float2 gradientX=ddx(input.uv),gradientY=ddy(input.uv);
        half4 border=MAIN_TEX.SampleGrad(samplerMAIN_TEX,float3(borderUV,material.borderIndex),gradientX,gradientY);
        half4 borderProperties=properties;
        if(material.borderSpecularIndex>=0)
            borderProperties=SPECULAR_TEX.SampleGrad(samplerSPECULAR_TEX,float3(borderUV,material.borderSpecularIndex),gradientX,gradientY);
        half3 borderNormal=input.worldNormal;
        if(material.borderNormalIndex>=0)
        {
            half4 encoded=NORMAL_TEX.SampleGrad(samplerNORMAL_TEX,float3(borderUV,material.borderNormalIndex),gradientX,gradientY);
            half3 mapped=UnpackNormal(encoded);
            borderNormal=normalize(mapped.x*input.worldTangent+mapped.y*input.worldBinormal+mapped.z*input.worldNormal);
        }
        // Native border blending retains this interpolated normal's length.
        surface.normal=lerp(surface.normal,borderNormal,border.a);
        color.rgb=material.blendMode!=0?lerp(color.rgb,border.rgb,border.a):GTPaintOverlay(border.rgb,color.rgb);
        properties.ba=lerp(properties.ba,borderProperties.ba,border.a);
        if(material.blendMode!=0)properties.rg=lerp(properties.rg,borderProperties.rg,border.a);
    }

    half3 albedo = color.rgb;
    if (material.paintMode != 1 && properties.b > 0.01)
    {
        GTBuildRecord brick = BrickBuildData[input.buildIndex];
        uint paletteIndex = (brick.rotationAndMaterial >> 5) & 255;
        half3 paint = PaintData[paletteIndex].color.rgb;
        uint3 packed = uint3(input.packedColor & 255,
                            (input.packedColor >> 8) & 255,
                            (input.packedColor >> 16) & 255);
        if (any(packed != 0) && properties.b > 0.7)
            paint = (half3)packed / 255;
        // The native paint mask tints a grayscale signal from RED, including
        // for textures whose compressed RGB channels decode slightly differently.
        albedo = GTPaintOverlay(albedo.rrr, paint);
    }
    GTBuildRecord build = BrickBuildData[input.buildIndex];
    surface.albedo = GammaToLinearSpace(albedo);
    if (abs(_EntityCanBuild[build.transformIndex] - 3) < 0.5 ||
        (build.transformIndex == 0 && _DamagedItemPreview > 0.5))
        surface.albedo *= 0.2;
    surface.metallic = properties.r;
    surface.smoothness = properties.g;
    surface.inputAlpha = material.alphaMode == 7 ? 0.13 : properties.a;
    surface.distortionAlpha = material.alphaMode == 7 ? properties.a : 0;
    surface.emission = 4 * saturate(surface.inputAlpha - 0.02)
                     * VectorDynData[build.pixelData].rgb;
    surface.emission += OverHeat * half3(4, 0.88, 1.2);
    if (material.alphaMode == 5)
        clip(0.5 - surface.inputAlpha);
    surface.diffuse = DiffuseAndSpecularFromMetallic(surface.albedo, surface.metallic,
        surface.specular, surface.oneMinusReflectivity);
    // Preserve specular Fresnel while scaling diffuse transmission opacity.
    surface.diffuse = PreMultiplyAlpha(surface.diffuse, surface.inputAlpha,
        surface.oneMinusReflectivity, surface.outputAlpha);

    surface.supported = GTGlassOrdinaryMaterialSupported(material) ? input.supported : 0;
    return surface;
}

half GTGlowTransmissionAlpha(GTSurfaceVaryings input, GTMaterialRecord material, out half supported)
{
    supported = input.supported;
    if (!GTGlassGlowMaterialSupported(material))
        supported = 0;
    half diffuseAlpha = UNITY_SAMPLE_TEX2DARRAY(MAIN_TEX, float3(input.uv, material.diffuseIndex)).a;
    half specularAlpha = material.specularIndex < 0 ? 0 :
        UNITY_SAMPLE_TEX2DARRAY(SPECULAR_TEX, float3(input.uv, material.specularIndex)).a;
    GTBuildRecord build = BrickBuildData[input.buildIndex];
    // Zero is the native packed-paint sentinel. The alpha byte contributes to
    // sentinel selection, but only RGB contributes to glow color/nonzero tests.
    bool glowNonzero = input.packedColor != 0 ? (input.packedColor & 0x00ffffffu) != 0 :
        dot(_GlowChromaVarying.rgb, _GlowChromaVarying.rgb) != 0;
    if (!glowNonzero)
        return diffuseAlpha;
    half factor = material.pixelMode == 1 ? FloatDynData[build.pixelData] * _LightWobble : 1;
    return saturate(saturate(specularAlpha - 0.02) * factor);
}

half3 GTGlassEvaluateBRDF(GTGlassSurface surface,float3 viewDirection,UnityLight light,UnityIndirect indirect)
{
    bool useLookup = _GTBRDFMode == 1;
    #if !defined(SHADOWS_SCREEN)
        useLookup = useLookup || _GTBRDFMode < 0;
    #endif
    half3 lookup = BRDF3_Unity_PBS(surface.diffuse, surface.specular,
        surface.oneMinusReflectivity, surface.smoothness, surface.normal,
        viewDirection, light, indirect).rgb;
    half3 ggx = UNITY_BRDF_PBS(surface.diffuse, surface.specular,
        surface.oneMinusReflectivity, surface.smoothness, surface.normal,
        viewDirection, light, indirect).rgb;
    return useLookup ? lookup : ggx;
}

half3 GTGlassDirect(GTGlassSurface surface, float3 worldPosition, half visibility)
{
    UnityLight light;
    light.color = _LightColor0.rgb * visibility;
    // The native directional programs consume this engine vector verbatim.
    // Renormalizing changes both NdotL and the lookup reflection coordinate.
    light.dir = _WorldSpaceLightPos0.xyz;
    light.ndotl = saturate(dot(surface.normal, light.dir));
    UnityIndirect indirect = (UnityIndirect)0;
    half3 viewDirection = normalize(_WorldSpaceCameraPos.xyz - worldPosition);
    return GTGlassEvaluateBRDF(surface, viewDirection, light, indirect);
}

half3 GTGlassLighting(GTGlassSurface surface, float3 worldPosition, half visibility)
{
    half3 viewDirection = normalize(_WorldSpaceCameraPos.xyz - worldPosition);
    UnityGIInput giInput = (UnityGIInput)0;
    giInput.worldPos = worldPosition;
    giInput.worldViewDir = viewDirection;
    giInput.probeHDR[0] = unity_SpecCube0_HDR;
    giInput.probeHDR[1] = unity_SpecCube1_HDR;
    giInput.boxMin[0] = unity_SpecCube0_BoxMin;
    giInput.boxMin[1] = unity_SpecCube1_BoxMin;
    giInput.boxMax[0] = unity_SpecCube0_BoxMax;
    giInput.boxMax[1] = unity_SpecCube1_BoxMax;
    giInput.probePosition[0] = unity_SpecCube0_ProbePosition;
    giInput.probePosition[1] = unity_SpecCube1_ProbePosition;
    Unity_GlossyEnvironmentData glossy = UnityGlossyEnvironmentSetup(
        surface.smoothness, viewDirection, surface.normal, surface.specular);
    UnityIndirect indirect = (UnityIndirect)0;
    indirect.specular = UnityGI_IndirectSpecular(giInput, 1, glossy);
    UnityLight light;
    light.color = _LightColor0.rgb * visibility;
    light.dir = _WorldSpaceLightPos0.xyz;
    light.ndotl = saturate(dot(surface.normal, light.dir));
    return GTGlassEvaluateBRDF(surface, viewDirection, light, indirect) + surface.emission;
}

#endif
