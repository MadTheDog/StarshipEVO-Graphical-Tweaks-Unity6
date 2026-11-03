Shader "GraphicalTweaks/ShadowDataExport"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex vert_img
            #pragma fragment Fragment
            #include "UnityCG.cginc"
            Texture2D<float> _GTSunShadowAtlas;
            float4 _GTFrameStamp;
            float4 _GTAtlasCaptureStamp;
            float _GTCameraToken;
            float _GTCascadeCount;
            float _GTShadowStrength;
            float4 Fragment(v2f_img input) : SV_Target
            {
                uint index = (uint)input.pos.x;
                if (index < 16) return unity_WorldToShadow[index / 4][index % 4];
                if (index < 20) return unity_ShadowSplitSpheres[index - 16];
                if (index == 20) return unity_ShadowSplitSqRadii;
                if (index == 21)
                {
                    uint width, height;
                    _GTSunShadowAtlas.GetDimensions(width, height);
                    return float4(width, height, _GTCascadeCount, _GTShadowStrength);
                }
                if (index == 22) return float4(_WorldSpaceCameraPos, unity_OrthoParams.w);
                if (index == 23) return float4(_GTFrameStamp.xy, _GTCameraToken, 1234);
                return _GTAtlasCaptureStamp;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
