using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Common;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace GraphicalTweaks
{
    // Fail open for the complete native global transparent draw if any potentially
    // contributing geometry is unknown. Never changes native rendering state.
    internal static class GlassSceneSupport
    {
        private static readonly FieldInfo CullingField = AccessTools.Field(typeof(EvoRenderer), "brickCullingBuffer");
        private static readonly FieldInfo ShapeField = AccessTools.Field(typeof(BrickMeshResources), "shapeMeshBuffer");
        private static readonly FieldInfo MaterialBufferField = AccessTools.Field(typeof(VoxelMaterialResources), "materialParamsBuffer");
        private static readonly HashSet<int> unsupportedIds = new HashSet<int>();
        private static readonly Dictionary<int, string> reasons = new Dictionary<int, string>();
        private static readonly Dictionary<Span, string> spans = new Dictionary<Span, string>();
        private static readonly Dictionary<uint, string> materialGeometry = new Dictionary<uint, string>();
        private static EvoRenderer tracked, destroyed;
        private static BrickCullingBuffer culling;
        private static ComputeBuffer sourceShape, sourceMaterial;
        private static VoxelMaterial[] materials;
        private static int shapeCount, materialLength, populatedMaterials, mainThread;
        private static bool installed, dirty = true;
        private static volatile bool threadViolation;
        private static string fault;

        private readonly struct Span : IEquatable<Span>
        {
            internal readonly int Start, Count;
            internal Span(int start, int count) { Start = start; Count = count; }
            public bool Equals(Span other) => Start == other.Start && Count == other.Count;
            public override bool Equals(object other) => other is Span span && Equals(span);
            public override int GetHashCode() => unchecked(Start * 397 ^ Count);
        }

        internal static int UnsupportedCount => unsupportedIds.Count;

        internal static void Install(Harmony harmony)
        {
            if (installed) return;
            if (harmony == null) throw new ArgumentNullException(nameof(harmony));
            if (CullingField == null || ShapeField == null || MaterialBufferField == null)
                throw new MissingFieldException("Native scene support accessors unavailable.");
            mainThread = Thread.CurrentThread.ManagedThreadId;
            Patch(harmony, typeof(EvoRenderer), nameof(EvoRenderer.AddElement), nameof(OnAdded), false);
            Patch(harmony, typeof(EvoRenderer), nameof(EvoRenderer.UpdateRenderData), nameof(OnUpdated), false);
            Patch(harmony, typeof(EvoRenderer), nameof(EvoRenderer.RemoveElement), nameof(OnRemoving), true);
            Patch(harmony, typeof(EvoRenderer), nameof(EvoRenderer.MassRemoveElement), nameof(OnRemoving), true);
            Patch(harmony, typeof(EvoRenderer), nameof(EvoRenderer.Reset), nameof(OnRendererInvalidated), true);
            Patch(harmony, typeof(EvoRenderer), nameof(EvoRenderer.CloneElement), nameof(OnRendererInvalidated), false);
            Patch(harmony, typeof(EvoRenderer), nameof(EvoRenderer.OnDestroy), nameof(OnRendererDestroyed), true);
            Patch(harmony, typeof(BrickCullingBuffer), nameof(BrickCullingBuffer.Reset), nameof(OnCullingInvalidated), false);
            Patch(harmony, typeof(BrickCullingBuffer), nameof(BrickCullingBuffer.Dispose), nameof(OnCullingDestroyed), true);
            Patch(harmony, typeof(BrickMeshResources), nameof(BrickMeshResources.AllocateResources), nameof(OnResourcesChanged), false);
            Patch(harmony, typeof(BrickMeshResources), nameof(BrickMeshResources.ReleaseResources), nameof(OnResourcesChanged), true);
            Patch(harmony, typeof(VoxelMaterialResources), nameof(VoxelMaterialResources.SetMaterialParamDatas), nameof(OnResourcesChanged), false);
            Patch(harmony, typeof(VoxelMaterialResources), nameof(VoxelMaterialResources.LoadResourcesRuntime), nameof(OnResourcesChanged), false);
            Patch(harmony, typeof(VoxelMaterialResources), nameof(VoxelMaterialResources.OnGameReload), nameof(OnResourcesChanged), false);
            Patch(harmony, typeof(VoxelMaterialResources), nameof(VoxelMaterialResources.UnloadResources), nameof(OnResourcesChanged), true);
            installed = true;
        }

        private static void Patch(Harmony harmony, Type type, string original, string hook, bool prefix)
        {
            MethodInfo method = AccessTools.Method(type, original);
            if (method == null) throw new MissingMethodException(type.FullName, original);
            var patch = new HarmonyMethod(typeof(GlassSceneSupport), hook);
            var finalizer = type == typeof(EvoRenderer) ? new HarmonyMethod(typeof(GlassSceneSupport), nameof(OnRendererMutationFailed)) : null;
            harmony.Patch(method, prefix ? patch : null, prefix ? null : patch, finalizer: finalizer);
        }

        internal static bool CanRender(EvoRenderer renderer, out string reason)
        {
            reason = null;
            if (!installed) { reason = "scene geometry guard is not installed"; return false; }
            if (Thread.CurrentThread.ManagedThreadId != mainThread || threadViolation)
            { reason = "scene geometry guard saw off-main-thread renderer mutation"; return false; }
            if (renderer == null || !ReferenceEquals(renderer, GlobalRenderer.renderer))
            { reason = "renderer is not the current native GlobalRenderer"; return false; }
            if (ReferenceEquals(renderer, destroyed)) { reason = "global renderer was disposed"; return false; }
            try
            {
                if (!ReferenceEquals(tracked, renderer))
                {
                    Invalidate(); tracked = renderer; culling = null; fault = null;
                }
                if (!RefreshResources(out reason)) return false;
                if (fault != null) { reason = fault; return false; }
                if (dirty)
                {
                    unsupportedIds.Clear(); reasons.Clear();
                    int length = culling.Length;
                    if (length < 0 || length > 4194304) throw new InvalidOperationException("Native culling slot count is invalid.");
                    for (int id = 0; id < length; id++) Evaluate(id, culling.GetElement(id));
                    dirty = false;
                }
                if (unsupportedIds.Count == 0) return true;
                foreach (int id in unsupportedIds)
                {
                    reason = "unsupported geometry at cullingId=" + id + ": " + reasons[id] +
                        " (unsupported entries=" + unsupportedIds.Count + ")";
                    return false;
                }
                return true;
            }
            catch (Exception error)
            {
                fault = "scene geometry guard unavailable: " + Short(error.Message);
                reason = fault;
                return false;
            }
        }

        private static bool RefreshResources(out string reason)
        {
            reason = null;
            var nextCulling = CullingField.GetValue(tracked) as BrickCullingBuffer;
            var nextShape = ShapeField.GetValue(null) as ComputeBuffer;
            var nextMaterial = MaterialBufferField.GetValue(null) as ComputeBuffer;
            var nextMaterials = VoxelMaterialResources.materialParamDatas;
            int nextShapeCount = BrickMeshResources.shapeMeshDatas.Count;
            int nextPopulated = VoxelMaterialResources.MaterialCount;
            if (nextCulling == null || nextShape == null || nextMaterial == null || nextMaterials == null ||
                nextShapeCount <= 0 || nextMaterials.Length <= 0 || nextPopulated <= 0 || nextPopulated > nextMaterials.Length ||
                !nextShape.IsValid() || !nextMaterial.IsValid() || nextShape.stride != 124 || nextMaterial.stride != 132 ||
                nextShape.count != nextShapeCount || nextMaterial.count < nextMaterials.Length)
            { reason = "native shape/material/culling resources unavailable or changed ABI"; return false; }
            if (!ReferenceEquals(nextCulling, culling) || !ReferenceEquals(nextShape, sourceShape) ||
                !ReferenceEquals(nextMaterial, sourceMaterial) || !ReferenceEquals(nextMaterials, materials) ||
                nextShapeCount != shapeCount || nextMaterials.Length != materialLength || nextPopulated != populatedMaterials)
            {
                Invalidate(); fault = null;
                culling = nextCulling; sourceShape = nextShape; sourceMaterial = nextMaterial; materials = nextMaterials;
                shapeCount = nextShapeCount; materialLength = nextMaterials.Length; populatedMaterials = nextPopulated;
            }
            return true;
        }

        private static void Evaluate(int id, RenderData data)
        {
            unsupportedIds.Remove(id); reasons.Remove(id);
            if (!data.IsValid || data.hp <= 0 || data.pass != RenderablePass.transparent) return;
            string reason = CheckSpan(data.clusterStart, data.clusterCount);
            if (reason == null && data.clusterCountLOD != 0) reason = CheckSpan(data.clusterStartLOD, data.clusterCountLOD);
            if (reason == null) return;
            unsupportedIds.Add(id); reasons[id] = reason;
        }

        private static string CheckSpan(int clusterStart, int clusterCount)
        {
            var key = new Span(clusterStart, clusterCount);
            if (spans.TryGetValue(key, out string cached)) return cached;
            if (!GlassSceneSupportModel.TryGetVertexRange(clusterStart, clusterCount, shapeCount, out int first, out int count))
                return spans[key] = "invalid cluster geometry span start=" + clusterStart + " count=" + clusterCount;
            var shape = BrickMeshResources.shapeMeshDatas;
            for (int vertex = first; vertex < first + count; vertex += 3)
            {
                var a = shape[vertex]; var b = shape[vertex + 1]; var c = shape[vertex + 2];
                // Native padding repeats the COMPLETE record three times.
                // Position equality alone could incorrectly skip real geometry.
                if (a.Equals(b) && a.Equals(c)) continue;
                string reason = CheckMaterial(a.materialID) ?? CheckMaterial(b.materialID) ?? CheckMaterial(c.materialID);
                if (reason != null) return spans[key] = reason;
            }
            spans[key] = null;
            return null;
        }

        private static string CheckMaterial(uint index)
        {
            if (materialGeometry.TryGetValue(index, out string cached)) return cached;
            if (index >= populatedMaterials || index >= materials.Length)
                return materialGeometry[index] = "materialID=" + index + " is outside the populated material catalog";
            VoxelMaterial material = materials[(int)index];
            if (GlassSceneSupportModel.IsGeometrySupported((int)material.AlphaMode, (int)material.dynDataVert,
                (int)material.scalingDeformLinkMode, (int)material.recomputeNormal, out string why, (int)material.dynDataPixel))
            { materialGeometry[index] = null; return null; }
            return materialGeometry[index] = "materialID=" + index + " alpha=" + (int)material.AlphaMode + " " + why;
        }

        private static bool Current(EvoRenderer renderer)
        {
            if (!ReferenceEquals(renderer, tracked) || !ReferenceEquals(renderer, GlobalRenderer.renderer)) return false;
            if (Thread.CurrentThread.ManagedThreadId == mainThread) return true;
            threadViolation = true;
            return false;
        }
        private static void Update(EvoRenderer renderer, int id, RenderData data)
        {
            if (!Current(renderer) || dirty || fault != null) return;
            try
            {
                if (!RefreshResources(out _)) { Invalidate(); return; }
                if (!dirty) Evaluate(id, data);
            }
            catch (Exception error) { fault = "scene geometry update unavailable: " + Short(error.Message); }
        }
        private static void OnAdded(EvoRenderer __instance, int __result, RenderData renderData) => Update(__instance, __result, renderData);
        private static void OnUpdated(EvoRenderer __instance, int renderId, RenderData renderData) => Update(__instance, renderId, renderData);
        private static void OnRemoving(EvoRenderer __instance, int cullingId)
        {
            if (!Current(__instance)) return;
            unsupportedIds.Remove(cullingId); reasons.Remove(cullingId);
        }
        private static void OnRendererInvalidated(EvoRenderer __instance) { if (Current(__instance)) { Invalidate(); fault = null; } }
        private static void OnRendererDestroyed(EvoRenderer __instance)
        { if (Current(__instance)) { destroyed = __instance; Invalidate(); } }
        private static void OnCullingInvalidated(BrickCullingBuffer __instance)
        { if (ReferenceEquals(__instance, culling) && Current(tracked)) { Invalidate(); fault = null; } }
        private static void OnCullingDestroyed(BrickCullingBuffer __instance)
        { if (ReferenceEquals(__instance, culling) && Current(tracked)) { destroyed = tracked; Invalidate(); } }
        private static void OnResourcesChanged()
        {
            if (tracked == null) return;
            if (Thread.CurrentThread.ManagedThreadId != mainThread) { threadViolation = true; return; }
            Invalidate(); fault = null;
        }
        private static Exception OnRendererMutationFailed(EvoRenderer __instance, Exception __exception)
        {
            if (__exception != null && Current(__instance))
            {
                Invalidate();
                fault = "native renderer mutation failed; scene geometry inventory unavailable";
            }
            return __exception; // Never suppress a native game exception.
        }
        private static void Invalidate()
        {
            dirty = true; spans.Clear(); materialGeometry.Clear(); unsupportedIds.Clear(); reasons.Clear();
        }
        internal static void Reset()
        {
            Invalidate(); tracked = null; destroyed = null; culling = null;
            sourceShape = sourceMaterial = null; materials = null;
            shapeCount = materialLength = populatedMaterials = 0; fault = null; threadViolation = false;
        }
        internal static void Dispose() => Reset(); // Parent owns Harmony patch lifetime.
        private static string Short(string value) => value == null || value.Length <= 180 ? value : value.Substring(0, 180);
    }
}
