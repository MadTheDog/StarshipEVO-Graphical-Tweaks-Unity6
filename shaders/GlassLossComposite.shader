Shader "GraphicalTweaks/GlassLossComposite"
{
    Properties { _MainTex ("Blocked direct light", 2D) = "black" {} }
    SubShader
    {
        Pass
        {
            Name "SUBTRACT_BLOCKED_DIRECT"
            Cull Off ZWrite Off ZTest Always
            Blend One One
            BlendOp RevSub
            ColorMask RGB
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment Fragment
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 Fragment(v2f_img input) : SV_Target
            {
                return float4(max(0, tex2D(_MainTex, input.uv).rgb), 0);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DISPLAY_DIAGNOSTIC"
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment Fragment
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 Fragment(v2f_img input) : SV_Target { return tex2D(_MainTex, input.uv); }
            ENDHLSL
        }
    }
    Fallback Off
}
