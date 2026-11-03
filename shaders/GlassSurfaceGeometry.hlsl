#ifndef GRAPHICAL_TWEAKS_GLASS_SURFACE_GEOMETRY_INCLUDED
#define GRAPHICAL_TWEAKS_GLASS_SURFACE_GEOMETRY_INCLUDED




            #include "UnityCG.cginc"

            struct GTBuildRecord
            {
                uint pixelData;
                uint vertexData;
                uint rotationAndMaterial;
                uint resizeCounts;
                float3 position;
                uint paint;
                float unitSize;
                float health;
                int transformIndex;
            };

            struct GTMeshVertex
            {
                float3 position;
                float3 normal;
                float4 tangent;
                float3 binormal;
                float2 uv;
                uint materialIndex;
                float3 boundsCenter;
                float3 boundsSize;
                float3 passMinimum;
                float3 passMaximum;
                float3 vertexOffset;
            };

            struct GTMaterialRecord
            {
                int id;
                int alphaMode;
                int vertexMode;
                int diffuseIndex;
                int borderIndex;
                int tiling;
                float resizeUnit;
                float uLow;
                float uHigh;
                float uWidth;
                float vLow;
                float vHigh;
                float vWidth;
                float geometryHigh;
                float geometryLow;
                int borderMode;
                int normalIndex;
                int specularIndex;
                int borderNormalIndex;
                int borderSpecularIndex;
                int projectionMode;
                int paintMode;
                int blendMode;
                int normalMode;
                float2 uvScale;
                int buildingOffset;
                int pixelMode;
                int resizeMode;
                float3 translationPath;
                int fuelOverlay;
            };

            // The producer writes column vectors consecutively into its
            // Unity.Mathematics float4x4 buffer. Explicit vectors avoid relying
            // on HLSL structured-matrix storage conventions.
            struct GTEntityTransform
            {
                float4 column0;
                float4 column1;
                float4 column2;
                float4 column3;
            };

            StructuredBuffer<float4> VectorDynData;
            StructuredBuffer<float> FloatDynData;
            StructuredBuffer<GTEntityTransform> _TransformDatasBuffer;
            StructuredBuffer<GTMaterialRecord> MaterialParams;
            StructuredBuffer<GTBuildRecord> BrickBuildData;
            StructuredBuffer<uint2> TriBrickIDMapping;
            StructuredBuffer<GTMeshVertex> ShapeMeshData;
            float4x4 VoxelRotations[64];
            int ClusterMappingStartIndex;


            struct GTSurfaceVaryings
            {
                float4 position : SV_POSITION;
                float3 worldPosition : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                nointerpolation uint materialIndex : TEXCOORD2;
                nointerpolation float supported : TEXCOORD3;
                nointerpolation uint vertexMode : TEXCOORD4;
                nointerpolation uint resizeMode : TEXCOORD5;
                float2 uv : TEXCOORD6;
                float3 worldTangent : TEXCOORD7;
                float3 worldBinormal : TEXCOORD8;
                nointerpolation uint buildIndex : TEXCOORD9;
                nointerpolation uint packedColor : TEXCOORD10;
                float3 entityNormal : TEXCOORD11;
                float3 rendererPosition : TEXCOORD12;
                float2 uvExpansion : TEXCOORD13;
            };

            float3 GTEntityDirection(GTEntityTransform transform, float3 direction)
            {
                return transform.column0.xyz * direction.x
                     + transform.column1.xyz * direction.y
                     + transform.column2.xyz * direction.z;
            }

            float3 GTThresholdResize(float3 relative, float3 extension,
                                     float low, float high)
            {
                float3 factor = float3(relative.x > high ? 1 : (relative.x > low ? 0.5 : 0),
                                       relative.y > high ? 1 : (relative.y > low ? 0.5 : 0),
                                       relative.z > high ? 1 : (relative.z > low ? 0.5 : 0));
                return relative + extension * factor;
            }

            float2 GTDiagonalWedgeResize(float2 relative, float2 size,
                                          float2 counts, float2 extension)
            {
                // Move a point toward one of the lower edges in the original
                // diagonal frame, then reconstruct it on the resized diagonal.
                const float rootTwo = 1.4142135623730951;
                const float inverseRootTwo = 0.7071067811865475;
                float2 edgeDistance = (relative + 0.5 * size) * rootTwo;
                bool fromBottom = edgeDistance.y < edgeDistance.x;
                float along = fromBottom ? edgeDistance.y : -edgeDistance.x;
                float2 edge = relative + along * float2(inverseRootTwo, -inverseRootTwo);
                edge += fromBottom ? float2(extension.x, 0) : float2(0, extension.y);
                float2 diagonal = normalize(float2(-size.x - counts.x, size.y + counts.y));
                return edge + along * diagonal;
            }

            float2 GTEndpointWedgeResize(float2 relative, float2 size,
                                          float2 counts, float2 extension)
            {
                // Lower outside edges extend independently. Interior points
                // retain their perpendicular displacement from the diagonal
                // and their distance from the nearer diagonal endpoint.
                float2 extended = relative + float2(relative.x < 0 ? 0 : extension.x,
                                                    relative.y < 0 ? 0 : extension.y);
                if (any(relative < -0.49 * size))
                    return extended;
                const float2 perpendicular = float2(0.7071067811865475, 0.7071067811865475);
                float2 endpoint = float2(0.5 * size.x, -0.5 * size.y);
                float displacement = dot(relative - endpoint, perpendicular);
                float2 onLine = relative - displacement * perpendicular;
                float2 distances = float2(length(onLine - endpoint), length(onLine + endpoint));
                bool fromBottom = distances.x < distances.y;
                float2 anchor = fromBottom ? endpoint + float2(extension.x, 0)
                                           : -endpoint + float2(0, extension.y);
                float2 direction = normalize(float2(-counts.x - 1, counts.y + 1));
                float distance = fromBottom ? distances.x : -distances.y;
                return anchor + distance * direction + displacement * perpendicular;
            }

            GTSurfaceVaryings GTSurfaceVertex(uint vertexId : SV_VertexID,
                                                  uint instanceId : SV_InstanceID)
            {
                GTSurfaceVaryings output = (GTSurfaceVaryings)0;
                uint2 mapping = TriBrickIDMapping[instanceId + (uint)ClusterMappingStartIndex];
                if (mapping.x == 0)
                {
                    output.position = float4(0, 0, 0, -1);
                    return output;
                }

                GTBuildRecord brick = BrickBuildData[mapping.y];
                GTMeshVertex mesh = ShapeMeshData[3 * mapping.x + vertexId];
                GTMaterialRecord material = MaterialParams[mesh.materialIndex];
                float3 counts = float3(brick.resizeCounts & 15,
                                       (brick.resizeCounts >> 4) & 15,
                                       (brick.resizeCounts >> 8) & 15);
                uint rotationIndex = brick.rotationAndMaterial & 31;
                float3 localPosition = mesh.position;
                float3 localNormal = mesh.normal;
                float3 localTangent = mesh.tangent.xyz;
                float2 uv = mesh.uv;
                float2 uvExpansion = 1;
                float supported = 1;

                // Offset-normal is a small geometry offset, not a normal map.
                if (material.vertexMode == 3)
                    localPosition += 0.001 * mesh.normal;

                if (material.resizeMode == 2 || material.resizeMode == 3 || material.resizeMode == 4)
                {
                    localPosition = localPosition * (counts + 1)
                                  + material.resizeUnit * counts * float3(0.5, 0, 0.5);
                }
                else if (material.resizeMode == 7 || material.resizeMode == 8)
                {
                    float3 anchor = mesh.boundsCenter - 0.5 * mesh.boundsSize;
                    float3 radial = localPosition - anchor;
                    if (material.resizeMode == 7)
                    {
                        float radius = length(radial.xy);
                        if (radius > 0.01)
                            radial.xy *= (radius + counts.x) / radius;
                        if (radial.z > 0.5)
                            radial.z += counts.z;
                    }
                    else
                    {
                        float radius = length(radial);
                        if (radius > 0.01)
                            radial *= (radius + counts.x) / radius;
                    }
                    localPosition = anchor + radial;
                }
                else
                {
                    float3 relative = localPosition - mesh.boundsCenter;
                    float3 resized = GTThresholdResize(relative, material.resizeUnit * counts,
                                                       material.geometryLow, material.geometryHigh);
                    if (material.resizeMode == 1)
                    {
                        resized.xz = localPosition.xz * (counts.xz + 1)
                                   + 0.5 * material.resizeUnit * counts.xz;
                    }
                    else if (material.resizeMode == 5)
                        resized.xy = GTEndpointWedgeResize(relative.xy, mesh.boundsSize.xy,
                                                           counts.xy, material.resizeUnit * counts.xy);
                    else if (material.resizeMode == 6)
                        resized.xy = GTDiagonalWedgeResize(relative.xy, mesh.boundsSize.xy,
                                                           counts.xy, material.resizeUnit * counts.xy);
                    else if (material.resizeMode != 0)
                    {
                        // Unknown linkage modes explicitly mark the surface unsupported.
                        supported = 0;
                    }
                    if (material.alphaMode == 20 && (material.resizeMode == 0 || material.resizeMode == 1))
                    {
                        // The squared hologram keeps equal X/Z resize counts
                        // by moving the longer dimension's edges inward.
                        float halfUnit = 0.5 * material.resizeUnit;
                        if (counts.x > counts.z)
                            resized.x += halfUnit * (counts.x - counts.z)
                                       * (resized.x > material.geometryHigh ? -1 : 1);
                        if (counts.z > counts.x)
                            resized.z += halfUnit * (counts.z - counts.x)
                                       * (resized.z > material.geometryHigh ? -1 : 1);
                    }
                    localPosition = resized + mesh.boundsCenter;
                }

                if (material.normalMode == 1)
                {
                    float3 scale = counts + 1;
                    localNormal = normalize(localNormal * scale.yzx * scale.zxy);
                }

                localPosition *= brick.unitSize;
                uint dynamicPointer = brick.vertexData;
                if (material.vertexMode == 2)
                    localPosition += VectorDynData[dynamicPointer].xyz;
                else if (material.vertexMode == 5)
                {
                    if (mesh.uv.y > 0.5)
                        localPosition += VectorDynData[dynamicPointer].xyz;
                }
                else if (material.vertexMode == 4)
                {
                    float3 pivot = VectorDynData[dynamicPointer + 3].xyz;
                    float3 relative = localPosition - pivot;
                    float3 row0 = VectorDynData[dynamicPointer].xyz;
                    float3 row1 = VectorDynData[dynamicPointer + 1].xyz;
                    float3 row2 = VectorDynData[dynamicPointer + 2].xyz;
                    localPosition = float3(dot(row0, relative), dot(row1, relative), dot(row2, relative)) + pivot;
                    localNormal = float3(dot(row0, localNormal), dot(row1, localNormal), dot(row2, localNormal));
                    localTangent = float3(dot(row0, localTangent), dot(row1, localTangent), dot(row2, localTangent));
                }
                else if (material.vertexMode == 6)
                    localPosition += FloatDynData[dynamicPointer] * material.translationPath;
                else if (material.vertexMode != 0 && material.vertexMode != 1 &&
                         material.vertexMode != 3 && material.vertexMode != 7)
                    supported = 0;

                float3 brickPosition = mul((float3x3)VoxelRotations[rotationIndex], localPosition) + brick.position;
                float3 brickNormal = mul((float3x3)VoxelRotations[rotationIndex], localNormal);
                float3 brickTangent = mul((float3x3)VoxelRotations[rotationIndex], localTangent);
                GTEntityTransform entity = _TransformDatasBuffer[brick.transformIndex];
                float3 entityPosition = GTEntityDirection(entity, brickPosition) + entity.column3.xyz;
                float3 entityNormal = GTEntityDirection(entity, brickNormal);
                output.worldPosition = mul(unity_ObjectToWorld, float4(entityPosition, 1)).xyz;
                output.worldNormal = mul((float3x3)unity_ObjectToWorld, entityNormal);
                output.worldTangent = normalize(mul((float3x3)unity_ObjectToWorld,
                    GTEntityDirection(entity, brickTangent)));
                output.worldBinormal = cross(output.worldNormal, output.worldTangent)
                                     * mesh.tangent.w * unity_WorldTransformParams.w;
                output.position = mul(UNITY_MATRIX_VP, float4(output.worldPosition, 1));
                output.materialIndex = mesh.materialIndex;
                if (material.vertexMode == 1)
                    output.materialIndex = (uint)((float)output.materialIndex + VectorDynData[dynamicPointer].z);
                if (material.pixelMode == 11)
                    output.materialIndex = (uint)((float)output.materialIndex + VectorDynData[brick.pixelData].z);
                // Texture-coordinate expansion uses the number of resize units
                // along the tangent and binormal, independently of brick scale.
                if (material.projectionMode == 0 && material.resizeMode != 2 && material.resizeMode != 4)
                {
                    uvExpansion = round(abs(float2(dot(normalize(mesh.tangent.xyz), counts),
                                                        dot(normalize(mesh.binormal), counts))));
                    float2 fraction = float2(uv.x > material.uHigh ? 1 : (uv.x > material.uLow ? 0.5 : 0),
                                            uv.y > material.vHigh ? 1 : (uv.y > material.vLow ? 0.5 : 0));
                    uv += uvExpansion * material.uvScale * fraction;
                }
                if (material.vertexMode == 1)
                    uv += VectorDynData[dynamicPointer].xy;
                else if (material.vertexMode == 7)
                    uv.y += FloatDynData[dynamicPointer];
                if (material.pixelMode == 11)
                    uv += VectorDynData[brick.pixelData].xy;
                output.uv = uv;
                output.buildIndex = mapping.y;
                output.packedColor = brick.paint;
                output.entityNormal = normalize(brickNormal);
                output.rendererPosition = brickPosition;
                output.uvExpansion = uvExpansion;
                output.supported = supported;
                output.vertexMode = material.vertexMode;
                output.resizeMode = material.resizeMode;
                return output;
            }


#endif
