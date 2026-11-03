// Narrow original replacement for validated ordinary transparent materials.
// The caller must reject unsupported scene material/geometry contracts.
Shader "GraphicalTweaks/GlassSurface"
{
    Properties
    {
        _GTSurfaceDiagnosticMode ("0 lighting, 1 support, 2 alpha", Int) = 0
        _GTBRDFMode ("Native BRDF: -1 automatic, 0 GGX, 1 lookup", Int) = -1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "GLASS_SURFACE"
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
            #pragma fragment GlassSurfaceFragment
            #include "GlassSurfaceMaterial.hlsl"
            #include "ShadowVisibility.cginc"

            int _GTSurfaceDiagnosticMode;

            struct GTGlassTargets
            {
                float4 color : SV_Target0;
                float4 transmission : SV_Target1;
            };

            GTGlassTargets GlassSurfaceFragment(GTSurfaceVaryings input)
            {
                GTClipCoveredFace(input);
                GTMaterialRecord material = MaterialParams[input.materialIndex];
                GTGlassSurface surface = GTReadGlassSurface(input, material);
                GTGlassTargets output = (GTGlassTargets)0;
                if (_GTSurfaceDiagnosticMode == 1)
                {
                    output.color = float4(surface.supported > 0.5 ? 0 : 1,
                                          surface.supported > 0.5 ? 1 : 0, 0, 1);
                    return output;
                }
                if (_GTSurfaceDiagnosticMode == 2)
                {
                    output.color = surface.outputAlpha.xxxx;
                    return output;
                }
                // Magenta makes an accidentally bypassed CPU eligibility gate
                // visible during testing instead of silently losing surfaces.
                if (surface.supported < 0.5)
                {
                    output.color = float4(1, 0, 1, 1);
                    return output;
                }
                half visibility = GTShadowVisibility(input.worldPosition, surface.normal);
                output.color = float4(GTGlassLighting(surface, input.worldPosition, visibility),
                                      surface.outputAlpha);
                output.transmission = float4(0, 0, 0, surface.distortionAlpha);
                return output;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
