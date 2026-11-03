// One-shot diagnostic only. Exports actual rasterized glass positions for an
// independent CPU voxel-ray comparison; never part of the shipping draw path.
Shader "GraphicalTweaks/GlassOcclusionProbe"
{
    SubShader
    {
        Pass
        {
            Cull Back ZWrite Off ZTest LEqual Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11
            #pragma vertex GTSurfaceVertex
            #pragma fragment Fragment
            #include "GlassSurfaceMaterial.hlsl"
            #include "ShadowVisibility.cginc"
            struct ProbeOutput { float4 world : SV_Target0; float4 normalVisibility : SV_Target1; };
            ProbeOutput Fragment(GTSurfaceVaryings input)
            {
                GTMaterialRecord material = MaterialParams[input.materialIndex];
                if (material.alphaMode != 2 && material.alphaMode != 5 && material.alphaMode != 7) discard;
                if (material.vertexMode != 0 && material.vertexMode != 3) discard; // Animated geometry cannot be frozen by entity transforms alone.
                GTGlassSurface surface = GTReadGlassSurface(input, material);
                if (surface.supported < .5) discard;
                ProbeOutput output;
                output.world = float4(input.worldPosition, input.buildIndex + 1);
                uint status;
                half visibility = GTShadowVisibilityDetailed(input.worldPosition, surface.normal, status);
                if (status == GT_SHADOW_STATUS_INVALID) status = 100u + GTShadowInvalidReason(input.worldPosition);
                output.normalVisibility = float4(BrickBuildData[input.buildIndex].transformIndex, input.materialIndex, visibility, status);
                return output;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
