// Experimental direct-light loss buffer. Native scene rendering stays intact.
// Enable only after validating every alpha-contributing layer in the draw.
Shader "GraphicalTweaks/GlassSurfaceLoss"
{
    Properties
    {
        _GTLossDiagnosticMode ("0 loss, 1 direct, 2 alpha, 3 support, 4 visibility, 5 face, 6 light, 7 albedo, 8 specular, 9 diffuse, 10 normal", Int) = 0
        _GTBRDFMode ("Native BRDF: -1 automatic, 0 GGX, 1 lookup", Int) = -1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "DIRECT_LIGHT_LOSS"
            Tags { "LightMode"="ForwardBase" }
            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile __ SHADOWS_SCREEN
            #pragma only_renderers d3d11
            #pragma vertex GTSurfaceVertex
            #pragma fragment GlassLossFragment
            #include "GlassSurfaceMaterial.hlsl"
            #include "ShadowVisibility.cginc"

            int _GTLossDiagnosticMode;

            float4 GlassLossFragment(GTSurfaceVaryings input) : SV_Target
            {
                GTMaterialRecord material = MaterialParams[input.materialIndex];
                if (_GTLossDiagnosticMode == 4)
                {
                    half visibility = GTShadowVisibility(input.worldPosition, normalize(input.worldNormal));
                    return float4(visibility, visibility, visibility, 1);
                }
                if (_GTLossDiagnosticMode == 5)
                    return float4(normalize(input.entityNormal) * 0.5 + 0.5, 1);
                // These native emissive-only modes use additive transmission.
                // They do not attenuate direct loss accumulated behind them.
                if (material.alphaMode == 10 || material.alphaMode == 11 ||
                    material.alphaMode == 17 || material.alphaMode == 20 || material.alphaMode == 21)
                {
                    if (_GTLossDiagnosticMode == 3)
                        return float4(0, 1, 0, 1);
                    return 0;
                }
                bool materialSupported = material.alphaMode == 9 ? GTGlassGlowMaterialSupported(material) :
                                          GTGlassOrdinaryMaterialSupported(material);
                if (input.supported < 0.5 || !materialSupported)
                {
                    if (_GTLossDiagnosticMode == 3)
                        return float4(1, 0, 0, 1);
                    if (_GTLossDiagnosticMode == 2)
                        return 1;
                    // Erase loss behind an unknown foreground response. Native
                    // scene RGB is retained there, including reflection/emission.
                    // This MUST precede texture sampling and alpha clipping.
                    return float4(0, 0, 0, 1);
                }
                if (material.alphaMode == 9)
                {
                    half supported;
                    half alpha = GTGlowTransmissionAlpha(input, material, supported);
                    if (_GTLossDiagnosticMode == 3)
                        return float4(supported > 0.5 ? 0 : 1, supported > 0.5 ? 1 : 0, 0, 1);
                    if (_GTLossDiagnosticMode == 2)
                        return alpha.xxxx;
                    return float4(0, 0, 0, supported > 0.5 ? alpha : 1);
                }
                GTGlassSurface surface = GTReadGlassSurface(input, material);
                if (_GTLossDiagnosticMode == 3)
                    return float4(surface.supported > 0.5 ? 0 : 1,
                                  surface.supported > 0.5 ? 1 : 0, 0, 1);
                if (_GTLossDiagnosticMode == 2)
                    return surface.outputAlpha.xxxx;
                if (_GTLossDiagnosticMode == 6)
                    return float4(_LightColor0.rgb, surface.outputAlpha);
                if (_GTLossDiagnosticMode == 7)
                    return float4(surface.albedo, surface.outputAlpha);
                if (_GTLossDiagnosticMode == 8)
                    return float4(surface.specular, surface.outputAlpha);
                if (_GTLossDiagnosticMode == 9)
                    return float4(surface.diffuse, surface.outputAlpha);
                if (_GTLossDiagnosticMode == 10)
                    return float4(surface.normal, surface.outputAlpha);
                if (_GTLossDiagnosticMode == 1)
                    return float4(GTGlassDirect(surface, input.worldPosition, 1), surface.outputAlpha);

                // CPU geometry guards reject the entire draw when geometry is
                // unknown; this is an additional conservative material barrier.
                if (surface.supported < 0.5)
                    return float4(0, 0, 0, 1);
                half visibility = GTShadowVisibility(input.worldPosition, surface.normal);
                half3 blockedDirect = GTGlassDirect(surface, input.worldPosition, 1 - visibility);
                return float4(blockedDirect, surface.outputAlpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
