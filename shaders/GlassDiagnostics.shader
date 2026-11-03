// Original diagnostic stage shared with the GPU-validated loss geometry adapter.
Shader "GraphicalTweaks/GlassDiagnostics"
{
    Properties
    {
        _GTDiagnosticMode ("Diagnostic output", Int) = 0
        _GTCullMode ("Cull: 0 both sides, 2 native back-face culling", Int) = 2
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "GEOMETRY_DIAGNOSTIC"
            Cull [_GTCullMode]
            ZWrite Off
            ZTest LEqual
            Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex GTSurfaceVertex
            #pragma fragment DiagnosticFragment
            #include "GlassSurfaceGeometry.hlsl"
            int _GTDiagnosticMode;

            float4 DiagnosticFragment(GTSurfaceVaryings input) : SV_Target
            {
                // Numeric output requires a floating-point render target.
                // Alpha identifies supported geometry in every mode.
                if (_GTDiagnosticMode == 1)
                    return float4(input.worldPosition, input.supported);
                if (_GTDiagnosticMode == 2)
                {
                    float depth = -mul(UNITY_MATRIX_V, float4(input.worldPosition, 1)).z;
                    return float4(depth, depth, depth, input.supported);
                }
                if (_GTDiagnosticMode == 3)
                    return float4((float)input.materialIndex, (float)input.vertexMode,
                                  (float)input.resizeMode, input.supported);
                if (input.supported < 0.5)
                    return float4(1, 0, 1, 0);
                return float4(normalize(input.worldNormal) * 0.5 + 0.5, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
