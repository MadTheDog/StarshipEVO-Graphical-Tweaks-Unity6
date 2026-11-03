using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine;

namespace GraphicalTweaks
{
    // Owns only rendering buffers. Native geometry, global bindings, build data,
    // physical meshes and indirect arguments are never changed by this helper.
    internal static class GlassGeometryRemap
    {
        private const int ShapeStride = 124;
        private const int BuildStride = 44;
        private const int CubeVertices = 36;
        private const int VariantVertices = 64 * CubeVertices;
        private const int RotationCount = 24;
        private const int DispatchLimit = 65535 * 64;
        private const long DefaultBudget = 256L * 1024 * 1024;

        private static readonly FieldInfo ShapeBufferField = AccessTools.Field(typeof(BrickMeshResources), "shapeMeshBuffer");
        private static readonly FieldInfo BuildBufferField = AccessTools.Field(typeof(BrickRenderDataResources), "BrickGeoDataBuffer");
        private static readonly FieldInfo RendererField = AccessTools.Field(typeof(EvoRenderer.DrawPass), "evoRenderer");
        private static readonly FieldInfo MappingField = AccessTools.Field(typeof(EvoRenderer), "drawMappingBuffer");
        private static readonly FieldInfo ClipField = AccessTools.Field(typeof(EvoRenderer), "drawMappingClipZBuffer");
        private static readonly FieldInfo ArgsField = AccessTools.Field(typeof(EvoRenderer), "argsBuffer");
        private static readonly FieldInfo NormalField = AccessTools.Field(typeof(BrickMeshResources.ShapeMeshData), "normal");
        private static readonly Dictionary<EvoRenderer, MappingState[]> mappings = new Dictionary<EvoRenderer, MappingState[]>();
        private static readonly List<ComputeBuffer> retired = new List<ComputeBuffer>();
        private static readonly List<ComputeBuffer> ownedResources = new List<ComputeBuffer>();

        private static ComputeShader compute;
        private static int copyKernel, remapKernel;
        private static ComputeBuffer nativeShape, ownedShape, rotationLookup;
        private static uint[] lookupContents;
        private static int shapeVertexCount, cubeStartTriangle;
        private static long allocatedBytes, budgetBytes = DefaultBudget;
        private static bool faulted;

        internal static long AllocatedBytes => allocatedBytes;
        internal static bool Faulted => faulted;

        // Call once after loading the original compute asset. Dispose only after
        // the caller has detached command buffers that reference owned resources.
        internal static void Configure(ComputeShader shader, long maximumAdditionalBytes = DefaultBudget)
        {
            if (shader == null) throw new ArgumentNullException(nameof(shader));
            if (maximumAdditionalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAdditionalBytes));
            if (compute != null && !ReferenceEquals(compute, shader) && allocatedBytes != 0)
                throw new InvalidOperationException("Detach glass command buffers and Dispose before changing the remap shader.");
            faulted = true;
            int newCopy = shader.FindKernel("CopyAndBuildVariants");
            // A distinct kernel name rejects an older global-offset bundle before
            // it can write native indices into a compact owned allocation.
            int newRemap = shader.FindKernel("RemapDrawCompact");
            if (newCopy < 0 || newRemap < 0)
                throw new InvalidOperationException("The glass remap compute asset is missing required kernels.");
            compute = shader;
            budgetBytes = maximumAdditionalBytes;
            copyKernel = newCopy;
            remapKernel = newRemap;
            faulted = false;
        }

        // Append this immediately after native culling/sorting and immediately
        // before the intercepted transparent draw in that same command buffer.
        internal static bool TryPrepare(CommandBuffer cb, EvoRenderer.DrawPass pass, bool isFar,
                                        out GlassGeometryRemapBindings bindings)
        {
            bindings = default;
            if (cb == null || pass == null || compute == null || faulted ||
                !GlassFaceCoverage.Enabled ||
                !GlassFaceCoverageRemapGate.ShouldPoll(GlassFaceCoverage.TrackedCubeCount, GlassFaceCoverage.PendingCubeCount) ||
                pass.ElementCount <= 0 || !SystemInfo.supportsComputeShaders)
                return false;
            try
            {
                if (!GlassFaceCoverage.TryGetBuffer(out var masks, out int maskCount) ||
                    !GlassFaceCoverageRemapGate.IsReady(GlassFaceCoverage.TrackedCubeCount, maskCount))
                    return false;
                RequireAccessors();
                var renderer = RendererField.GetValue(pass) as EvoRenderer;
                if (renderer == null || !ReferenceEquals(renderer, GlobalRenderer.renderer) ||
                    !ReferenceEquals(pass, renderer.pass_Transparent))
                    return false;
                var originalMapping = MappingField.GetValue(renderer) as ComputeBuffer;
                var originalClip = ClipField.GetValue(renderer) as ComputeBuffer;
                var args = ArgsField.GetValue(renderer) as ComputeBuffer;
                var build = BuildBufferField.GetValue(null) as ComputeBuffer;
                var sourceShape = ShapeBufferField.GetValue(null) as ComputeBuffer;
                if (originalMapping == null || originalClip == null || args == null ||
                    build == null || sourceShape == null)
                    return false;
                if (originalMapping.stride != 8 || originalClip.stride != 4 || build.stride != BuildStride || sourceShape.stride != ShapeStride || args.stride != 4)
                    throw new InvalidOperationException("Native glass buffer stride changed.");
                int start = pass.GetIndexStart(isFar);
                int count = pass.ElementCount;
                int argsOffset = checked(16 * pass.passId + (isFar ? 432 : 0));
                if (start < 0 || (long)start + count > originalMapping.count || (long)start + count > originalClip.count ||
                    argsOffset < 0 || (long)argsOffset + 16 > (long)args.count * 4)
                    throw new InvalidOperationException("Native glass draw range is outside its buffers.");
                if (!EnsureShape(sourceShape)) return false;
                MappingState destination = EnsureMapping(renderer, count, isFar);

                cb.SetComputeBufferParam(compute, remapKernel, "_GTOriginalTriBrickIDMapping", originalMapping);
                cb.SetComputeBufferParam(compute, remapKernel, "_GTRemappedTriBrickIDMapping", destination.Owned);
                cb.SetComputeBufferParam(compute, remapKernel, "_GTOriginalDrawMappingClipZ", originalClip);
                cb.SetComputeBufferParam(compute, remapKernel, "_GTRemappedDrawMappingClipZ", destination.Clip);
                cb.SetComputeBufferParam(compute, remapKernel, "BrickBuildData", build);
                cb.SetComputeBufferParam(compute, remapKernel, "_GTGlassFaceMasks", masks);
                cb.SetComputeBufferParam(compute, remapKernel, "_GTLocalFaceMaskLookup", rotationLookup);
                cb.SetComputeIntParam(compute, "_GTOriginalShapeVertexCount", shapeVertexCount);
                cb.SetComputeIntParam(compute, "_GTCubeStartTriangle", cubeStartTriangle);
                cb.SetComputeIntParam(compute, "_GTBuildDataCount", build.count);
                cb.SetComputeIntParam(compute, "_GTGlassFaceMaskCount", maskCount);
                for (int offset = 0; offset < count;)
                {
                    int batch = Math.Min(count - offset, DispatchLimit);
                    cb.SetComputeIntParam(compute, "_GTMappingStart", start + offset);
                    cb.SetComputeIntParam(compute, "_GTMappingOutputStart", offset);
                    cb.SetComputeIntParam(compute, "_GTMappingCount", batch);
                    cb.DispatchCompute(compute, remapKernel, (batch + 63) / 64, 1, 1);
                    offset += batch;
                }
                bindings = new GlassGeometryRemapBindings(ownedShape, destination.Owned, originalMapping,
                    destination.Clip, args, 0, count, argsOffset);
                return true;
            }
            catch (Exception exception)
            {
                faulted = true;
                Debug.LogWarning("Graphical Tweaks: glass geometry remap disabled; native draw retained. " + exception);
                return false;
            }
        }

        private static void RequireAccessors()
        {
            if (ShapeBufferField == null || BuildBufferField == null || RendererField == null ||
                MappingField == null || ClipField == null || ArgsField == null || NormalField == null)
                throw new MissingFieldException("Native glass rendering interface changed.");
        }

        private static bool EnsureShape(ComputeBuffer source)
        {
            if (ReferenceEquals(nativeShape, source) && ownedShape != null && shapeVertexCount == source.count)
                return true;
            var cube = Brick.BRICK_INDEX[166];
            if (cube == null || cube.VoxelMesh == null) return false;
            var indexing = cube.VoxelMesh.transparentData;
            int sourceCount = source.count;
            if (indexing.ClusterCount != 1 || indexing.count != CubeVertices || indexing.startTriangle <= 0 ||
                sourceCount % 3 != 0 || BrickMeshResources.shapeMeshDatas.Count != sourceCount ||
                (long)indexing.startTriangle * 3 + CubeVertices > sourceCount ||
                cube.VoxelMaterials == null || cube.VoxelMaterials.Length != 1 ||
                cube.VoxelMaterials[0].AlphaMode != AlphaMode.transparent ||
                cube.VoxelMaterials[0].BorderId != -1 || cube.VoxelMaterials[0].normalID != -1 ||
                cube.VoxelMaterials[0].dynDataVert != DynData_Vert.none)
                throw new InvalidOperationException("Cube 166 no longer matches the validated plain one-cluster glass contract.");
            int firstVertex = indexing.startTriangle * 3;
            int[] faceCounts = new int[6];
            for (int triangle = 0; triangle < 12; triangle++)
            {
                int face = -1;
                for (int corner = 0; corner < 3; corner++)
                {
                    var vertex = BrickMeshResources.shapeMeshDatas[firstVertex + triangle * 3 + corner];
                    int currentFace = AxisFace((Vector3)NormalField.GetValue(vertex));
                    if (currentFace < 0 || (face >= 0 && face != currentFace) ||
                        vertex.materialID != (uint)cube.VoxelMaterials[0].Id)
                        throw new InvalidOperationException("Cube glass expanded vertices changed.");
                    face = currentFace;
                }
                faceCounts[face]++;
            }
            for (int face = 0; face < 6; face++)
                if (faceCounts[face] != 2) throw new InvalidOperationException("Cube glass does not have two triangles per axis face.");

            uint[] lookup = BuildRotationLookup();
            bool replaceLookup = rotationLookup == null || !SameLookup(lookupContents, lookup);
            long shapeBytes = checked(((long)sourceCount + VariantVertices) * ShapeStride);
            long lookupBytes = RotationCount * 64L * sizeof(uint);
            RequireBudget(checked(shapeBytes + (replaceLookup ? lookupBytes : 0)));
            Debug.Log("Graphical Tweaks: glass render-only clone: native vertices=" + sourceCount +
                ", native bytes=" + ((long)sourceCount * ShapeStride) + ", appended bytes=" +
                ((long)VariantVertices * ShapeStride) + ", owned outstanding bytes after allocation=" +
                (allocatedBytes + shapeBytes + (replaceLookup ? lookupBytes : 0)) +
                ", budget bytes=" + budgetBytes + ". Native mesh assets and physics are unchanged.");
            ComputeBuffer replacement = Allocate(sourceCount + VariantVertices, ShapeStride);
            if (replaceLookup)
            {
                if (rotationLookup != null) retired.Add(rotationLookup);
                rotationLookup = Allocate(RotationCount * 64, sizeof(uint));
                rotationLookup.SetData(lookup);
                lookupContents = lookup;
            }
            using (var initialize = new CommandBuffer { name = "Graphical Tweaks glass render variants" })
            {
                initialize.SetComputeBufferParam(compute, copyKernel, "_GTOriginalShapeMeshData", source);
                initialize.SetComputeBufferParam(compute, copyKernel, "_GTRemappedShapeMeshData", replacement);
                initialize.SetComputeIntParam(compute, "_GTOriginalShapeVertexCount", sourceCount);
                initialize.SetComputeIntParam(compute, "_GTCubeStartTriangle", indexing.startTriangle);
                initialize.DispatchCompute(compute, copyKernel, (sourceCount + VariantVertices + 63) / 64, 1, 1);
                // Submit initialization before any near/far camera command buffer
                // can reference this resource, regardless of recording order.
                Graphics.ExecuteCommandBuffer(initialize);
            }
            if (ownedShape != null) retired.Add(ownedShape);
            nativeShape = source;
            ownedShape = replacement;
            shapeVertexCount = sourceCount;
            cubeStartTriangle = indexing.startTriangle;
            return true;
        }

        private static MappingState EnsureMapping(EvoRenderer renderer, int count, bool isFar)
        {
            if (!mappings.TryGetValue(renderer, out var cameras))
                mappings.Add(renderer,cameras=new MappingState[2]);
            int camera=isFar?1:0;
            MappingState state=cameras[camera];
            if(state!=null && state.Owned.count>=count)return state;
            int capacity=1;
            while(capacity<count)capacity=checked(capacity*2);
            RequireBudget(checked((long)capacity*12));
            var replacement=new MappingState(Allocate(capacity,8),Allocate(capacity,4));
            if(state!=null){retired.Add(state.Owned);retired.Add(state.Clip);}
            cameras[camera]=replacement;
            Debug.Log("Graphical Tweaks: compact glass "+(isFar?"far":"near")+" draw range="+count+
                ", capacity="+capacity+", mapping+clip bytes="+((long)capacity*12)+
                ", owned outstanding bytes="+allocatedBytes+". Near/far storage is independent.");
            return replacement;
        }

        private static uint[] BuildRotationLookup()
        {
            var result = new uint[RotationCount * 64];
            Vector3[] directions = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
            var permutations = new HashSet<int>();
            if (VoxelRotation.Quaternions == null || VoxelRotation.Quaternions.Length < RotationCount)
                throw new InvalidOperationException("Native voxel rotations are unavailable.");
            for (int rotation = 0; rotation < RotationCount; rotation++)
            {
                Quaternion quaternion = VoxelRotation.Quaternions[rotation];
                float norm = quaternion.x * quaternion.x + quaternion.y * quaternion.y +
                             quaternion.z * quaternion.z + quaternion.w * quaternion.w;
                if (Mathf.Abs(norm - 1) > 0.0001f)
                    throw new InvalidOperationException("Native voxel rotation initialization is incomplete.");
                int[] entityFaces = new int[6];
                uint visited = 0;
                int permutation = 0;
                for (int localFace = 0; localFace < 6; localFace++)
                {
                    int entityFace = AxisFace(VoxelRotation.Quaternions[rotation] * directions[localFace]);
                    if (entityFace < 0 || (visited & (1u << entityFace)) != 0)
                        throw new InvalidOperationException("Native voxel rotation is not an axis permutation.");
                    entityFaces[localFace] = entityFace;
                    visited |= 1u << entityFace;
                    permutation |= entityFace << (3 * localFace);
                }
                if (!permutations.Add(permutation))
                    throw new InvalidOperationException("Native voxel rotations do not contain 24 unique axis permutations.");
                for (uint entityMask = 0; entityMask < 64; entityMask++)
                {
                    uint localMask = 0;
                    for (int localFace = 0; localFace < 6; localFace++)
                        if ((entityMask & (1u << entityFaces[localFace])) != 0) localMask |= 1u << localFace;
                    result[rotation * 64 + entityMask] = localMask;
                }
            }
            return result;
        }

        private static int AxisFace(Vector3 normal)
        {
            float x = Mathf.Abs(normal.x), y = Mathf.Abs(normal.y), z = Mathf.Abs(normal.z);
            if (Mathf.Abs(x + y + z - 1) > 0.00001f) return -1;
            if (x > 0.99999f) return normal.x > 0 ? 0 : 1;
            if (y > 0.99999f) return normal.y > 0 ? 2 : 3;
            if (z > 0.99999f) return normal.z > 0 ? 4 : 5;
            return -1;
        }

        private static bool SameLookup(uint[] left, uint[] right)
        {
            if (left == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static void RequireBudget(long additional)
        {
            if (additional < 0 || additional > budgetBytes - allocatedBytes)
                throw new InvalidOperationException("Glass rendering buffer allocation exceeds its " +
                    budgetBytes + " byte total budget (including retired buffers); requested " + additional +
                    ", already owned " + allocatedBytes + ".");
        }

        private static ComputeBuffer Allocate(int count, int stride)
        {
            long bytes = checked((long)count * stride);
            RequireBudget(bytes);
            var result = new ComputeBuffer(count, stride);
            ownedResources.Add(result);
            allocatedBytes += bytes;
            return result;
        }

        // Explicit startup diagnostic only: never called from TryPrepare and
        // never read back each frame. Checks the actual compute-produced records.
        internal static bool ValidateVariants(out string reason)
        {
            reason = "Glass geometry has not been initialized.";
            if (ownedShape == null) return false;
            try
            {
                var variants = new BrickMeshResources.ShapeMeshData[VariantVertices];
                ownedShape.GetData(variants, 0, shapeVertexCount, VariantVertices);
                int source = cubeStartTriangle * 3;
                for (int mask = 0; mask < 64; mask++)
                    for (int vertex = 0; vertex < CubeVertices; vertex++)
                    {
                        var original = BrickMeshResources.shapeMeshDatas[source + vertex];
                        int face = AxisFace((Vector3)NormalField.GetValue(original));
                        int expectedVertex = (mask & (1 << face)) != 0 ? (vertex / 3) * 3 : vertex;
                        var expected = BrickMeshResources.shapeMeshDatas[source + expectedVertex];
                        if (!variants[mask * CubeVertices + vertex].Equals(expected))
                        {
                            reason = "GPU glass variant differs at mask " + mask + ", vertex " + vertex + ".";
                            return false;
                        }
                    }
                reason = "All 64 GPU variants preserve complete visible records and degenerate complete covered-triangle records.";
                return true;
            }
            catch (Exception exception) { reason = exception.ToString(); return false; }
        }

        internal static void Dispose()
        {
            foreach (var buffer in ownedResources) buffer.Release();
            mappings.Clear(); retired.Clear(); ownedResources.Clear();
            compute = null; nativeShape = null; ownedShape = null; rotationLookup = null;
            lookupContents = null;
            shapeVertexCount = 0; cubeStartTriangle = 0; allocatedBytes = 0; faulted = false;
        }

        private sealed class MappingState
        {
            internal readonly ComputeBuffer Owned, Clip;
            internal MappingState(ComputeBuffer owned, ComputeBuffer clip) { Owned = owned; Clip = clip; }
        }
    }

    internal readonly struct GlassGeometryRemapBindings
    {
        internal readonly ComputeBuffer ShapeMeshData, TriBrickIDMapping, OriginalMapping, DrawMappingClipZBuffer, ArgsBuffer;
        internal readonly int MappingStartIndex, MappingCount, ArgsOffsetBytes;
        internal GlassGeometryRemapBindings(ComputeBuffer shape, ComputeBuffer mapping, ComputeBuffer originalMapping,
            ComputeBuffer clip, ComputeBuffer args, int start, int count, int argsOffset)
        {
            ShapeMeshData = shape; TriBrickIDMapping = mapping; OriginalMapping = originalMapping;
            DrawMappingClipZBuffer = clip; ArgsBuffer = args;
            MappingStartIndex = start; MappingCount = count; ArgsOffsetBytes = argsOffset;
        }
    }
}
