using System.Collections.Generic;
using System.Linq;
using Common;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;

namespace GraphicalTweaks
{
    // Camera-aware culling only. Shading dimensions/tolerance are managed by
    // DecalProjection; neither path resizes the texture artwork.
    internal static class DecalVisibility
    {
        private sealed class Entry
        {
            internal RenderData Original;
            internal Aabb AppliedBounds;
            internal float AppliedScale = 1;
        }
        private sealed class Group
        {
            internal readonly HashSet<int> Ids = new HashSet<int>();
            internal readonly DecalWorkEnvelope Envelope = new DecalWorkEnvelope();
            internal DecalWorkState Work;
            internal bool CountedSkippedEntries;
            internal Matrix4x4 LastView;
            internal bool HasView;
        }
        private sealed class Pool
        {
            internal readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
            internal readonly Dictionary<int, Group> Groups = new Dictionary<int, Group>();
            internal readonly HashSet<int> Dirty = new HashSet<int>();
            internal bool UntrackedClone;
        }
        private struct View
        {
            internal Matrix4x4 Matrix;
            internal DecalWorkMatrix Local;
            internal bool UploadMatches;
            internal bool Valid;
        }
        private static readonly Dictionary<BrickCullingBuffer, Pool> tracked = new Dictionary<BrickCullingBuffer, Pool>();
        private static readonly Dictionary<int, View> views = new Dictionary<int, View>();
        private static readonly List<int> stale = new List<int>();
        private static readonly DecalRangeModel settings = new DecalRangeModel();
        private static bool pending, allPending, applying, cameraObserved;
        private static Camera observedCamera;
        private static Camera observedFarCamera;
        private static Matrix4x4 cameraView;
        private static Matrix4x4 renderView, renderNear, renderFar;
        private static int renderFrame = -1;
        private static int renderTransformLength, transformUploadFrame = -1, transformUploadLength, transformUploadRawLength;
        private static bool renderCaptureValid, renderHasFar, filterCaptureValid;
        private static DecalWorkMatrix filterNear, filterFar, filterCamera;
        private static bool proxyWorkFilteringEnabled, skippedGroupsPending;
        private static long testedGroups, skippedGroups, skippedEntries, wakeups;
        private static int lastTestedGroups, lastSkippedGroups, lastSkippedEntries, lastWakeups;
        private static string fallbackReason = "disabled";
        internal static bool ProxyWorkFilteringEnabled => proxyWorkFilteringEnabled;
        // Formatting happens only on an explicit diagnostic/reflection request.
        internal static string ProxyWorkFilteringStatus => $"enabled={proxyWorkFilteringEnabled};tested={testedGroups};skipped={skippedGroups};entries={skippedEntries};wakeups={wakeups};lastTested={lastTestedGroups};lastSkipped={lastSkippedGroups};lastEntries={lastSkippedEntries};lastWakeups={lastWakeups};fallback={fallbackReason}";
        private static Vector3 cameraPosition, lastScanPosition;
        private static Quaternion cameraRotation, lastScanRotation;
        private static float focalX, focalY, nextScan;

        internal static void Request(int percent)
        {
            if (!settings.TrySet(percent)) return;
            pending = true; allPending = true;
        }
        internal static void SetProxyWorkFiltering(bool enabled)
        {
            if (proxyWorkFilteringEnabled == enabled) return;
            proxyWorkFilteringEnabled = enabled;
            // Full requests bypass filtering, including disable and stock reset.
            pending = true; allPending = true;
            fallbackReason = enabled ? "awaiting-render-capture" : "disabled";
        }
        private static DecalWorkBounds WorkBounds(Aabb b) => new DecalWorkBounds(b.Min.x, b.Min.y, b.Min.z, b.Max.x, b.Max.y, b.Max.z);
        private static DecalWorkMatrix WorkMatrix(Matrix4x4 m) => new DecalWorkMatrix
        {
            M00=m.m00,M01=m.m01,M02=m.m02,M03=m.m03,M10=m.m10,M11=m.m11,M12=m.m12,M13=m.m13,
            M20=m.m20,M21=m.m21,M22=m.m22,M23=m.m23,M30=m.m30,M31=m.m31,M32=m.m32,M33=m.m33
        };
        private static void IncludeOriginal(Group group, Aabb original)
        {
            var maximum = original;
            // Use the exact native float Size/Center/SetSize path, not a double
            // approximation or the currently applied (possibly stale) proxy.
            maximum.SetSize(original.Size * (DecalRangeModel.Maximum / 100f));
            group.Envelope.Include(WorkBounds(original), WorkBounds(maximum));
        }
        private static bool IsDecal(RenderData data) => data.pass == RenderablePass.decals;
        private static bool SameBounds(Aabb a, Aabb b) => a.Min.Equals(b.Min) && a.Max.Equals(b.Max);
        private static bool SameIdentity(RenderData a, RenderData b) => a.pass == b.pass && a.dataId == b.dataId && a.transformId == b.transformId;

        internal static void Record(BrickCullingBuffer buffer, int id, RenderData data, bool authoritativeBounds = false)
        {
            if (applying || !BrickCullingBuffer.brickCullingBuffers.Contains(buffer)) return;
            if (!IsDecal(data)) { Forget(buffer, id); return; }
            if (!tracked.TryGetValue(buffer, out var pool)) tracked[buffer] = pool = new Pool();
            if (pool.Entries.TryGetValue(id, out var old))
            {
                if (old.Original.transformId != data.transformId) RemoveFromGroup(pool, old.Original.transformId, id);
                if (!authoritativeBounds && SameIdentity(old.Original, data) && SameBounds(old.AppliedBounds, data.bounds))
                {
                    var baseline = old.Original.bounds;
                    old.Original = data; old.Original.bounds = baseline;
                }
                else pool.Entries[id] = new Entry { Original = data, AppliedBounds = data.bounds };
            }
            else pool.Entries[id] = new Entry { Original = data, AppliedBounds = data.bounds };
            if (!pool.Groups.TryGetValue(data.transformId, out var group)) pool.Groups[data.transformId] = group = new Group();
            group.Ids.Add(id);
            IncludeOriginal(group, pool.Entries[id].Original.bounds);
            pool.Dirty.Add(id);
            pending = true;
        }
        private static void RemoveFromGroup(Pool pool, int transform, int id)
        {
            if (!pool.Groups.TryGetValue(transform, out var group)) return;
            group.Ids.Remove(id);
            if (group.Ids.Count == 0) pool.Groups.Remove(transform);
        }
        internal static void Forget(BrickCullingBuffer buffer, int id)
        {
            if (!tracked.TryGetValue(buffer, out var pool)) return;
            if (pool.Entries.TryGetValue(id, out var entry)) RemoveFromGroup(pool, entry.Original.transformId, id);
            pool.Entries.Remove(id); pool.Dirty.Remove(id);
        }
        internal static void Forget(BrickCullingBuffer buffer) => tracked.Remove(buffer);
        private static void RecordClone(BrickCullingBuffer buffer)
        {
            if (!BrickCullingBuffer.brickCullingBuffers.Contains(buffer)) return;
            if (!tracked.TryGetValue(buffer, out var pool)) tracked[buffer] = pool = new Pool();
            // CloneBrick does not expose its new ID. Until reset, this pool's
            // inventory cannot be proved complete, so no group may be skipped.
            pool.UntrackedClone = true;
            pending = true;
        }

        private static void CaptureRender(Camera camera, Camera farCamera)
        {
            observedFarCamera = farCamera;
            renderCaptureValid = camera != null && !camera.orthographic && camera.pixelWidth > 0 && camera.pixelHeight > 0 &&
                (farCamera == null || !farCamera.orthographic);
            renderFrame = Time.frameCount;
            // Native DoTransformMatrixComputation records this dispatch length
            // before DisposeJobs uploads the raw TRS array for this render.
            renderTransformLength = SkyTransformData.previousLength;
            renderHasFar = farCamera != null;
            if (renderCaptureValid)
            {
                renderView = camera.worldToCameraMatrix;
                renderNear = camera.projectionMatrix;
                renderFar = renderHasFar ? farCamera.projectionMatrix : default;
            }
            ObserveCamera(camera);
        }
        private static void CaptureTransformUpload()
        {
            if (!proxyWorkFilteringEnabled) return;
            transformUploadFrame = -1;
            // Postfix runs only after native DisposeJobResources completed its
            // rawTransformBuffer.PushData(TRSJob.rawTransformDatas). The public
            // list aliases that same array; native code does not rewrite/resize
            // it between this upload and ExecuteCommands in the audited build.
            if (!SkyTransformData.datas.IsCreated || !SkyTransformData.rawTransformDatas.IsCreated) return;
            transformUploadLength = SkyTransformData.previousLength;
            transformUploadRawLength = SkyTransformData.rawTransformDatas.Length;
            if (transformUploadLength < 0 || transformUploadRawLength < transformUploadLength) return;
            transformUploadFrame = Time.frameCount;
        }
        private static DecalWorkTransform WorkTransform(Vector3 position, Quaternion rotation, float scale) => new DecalWorkTransform
        { X=position.x,Y=position.y,Z=position.z,QX=rotation.x,QY=rotation.y,QZ=rotation.z,QW=rotation.w,Scale=scale };
        private static void PrepareFilterCapture()
        {
            filterCaptureValid = false;
            if (!proxyWorkFilteringEnabled) { fallbackReason = "disabled"; return; }
            if (!renderCaptureValid || renderFrame != Time.frameCount || observedCamera == null || observedCamera.orthographic ||
                renderHasFar != (observedFarCamera != null) || (observedFarCamera != null && observedFarCamera.orthographic))
            { fallbackReason = "unknown-or-stale-render-capture"; return; }
            // The GPU VP was recorded earlier in RenderOnCamera. A camera/view
            // change during job completion must not silently replace that proof.
            if (!renderView.Equals(cameraView) || !renderNear.Equals(observedCamera.projectionMatrix) ||
                (renderHasFar && !renderFar.Equals(observedFarCamera.projectionMatrix)))
            { fallbackReason = "render-camera-changed"; return; }
            filterCamera = WorkMatrix(renderView); filterNear = WorkMatrix(renderNear); filterFar = WorkMatrix(renderFar);
            filterCaptureValid = true; fallbackReason = "none";
        }

        private static void ObserveCamera(Camera camera)
        {
            if (camera == null || camera.orthographic || camera.pixelWidth <= 0 || camera.pixelHeight <= 0) return;
            Matrix4x4 projection = camera.projectionMatrix;
            float x = Mathf.Abs(projection.m00) * camera.pixelWidth * 0.5f;
            float y = Mathf.Abs(projection.m11) * camera.pixelHeight * 0.5f;
            if (!cameraObserved || focalX != x || focalY != y) { allPending = true; pending = true; }
            focalX = x; focalY = y;
            cameraView = camera.worldToCameraMatrix;
            cameraPosition = camera.transform.position;
            cameraRotation = camera.transform.rotation;
            cameraObserved = true;
            observedCamera = camera;
        }
        internal static void ResetCamera()
        {
            cameraObserved = false; observedCamera = null; nextScan = 0; views.Clear();
            observedFarCamera = null; renderCaptureValid = false; renderFrame = -1;
            transformUploadFrame = -1;
            // Preserve skipped state: the next upload must wake these groups
            // through the unknown-camera fail-open path, even below scan cadence.
            foreach (var pool in tracked.Values)
                foreach (var group in pool.Groups.Values) group.HasView = false;
        }
        private static View GetView(int transformId)
        {
            if (views.TryGetValue(transformId, out var cached)) return cached;
            var view = new View();
            if (cameraObserved && SkyTransformData.datas.IsCreated && transformId > 0 && transformId < SkyTransformData.datas.Length)
            {
                var transform = SkyTransformData.datas[transformId];
                if (transform.scale > 0 && !float.IsNaN(transform.scale) && !float.IsInfinity(transform.scale) && transform.rotation.SqrdLength() > 0.1f)
                {
                    view.Matrix = cameraView * transform.localToWorldMatrix;
                    view.Local = WorkMatrix(transform.localToWorldMatrix);
                    view.Valid = true;
                    if (proxyWorkFilteringEnabled && filterCaptureValid && transformUploadFrame == Time.frameCount &&
                        transformUploadLength == renderTransformLength && transformUploadLength == SkyTransformData.previousLength &&
                        transformId < transformUploadLength && SkyTransformData.rawTransformDatas.IsCreated &&
                        SkyTransformData.rawTransformDatas.Length == transformUploadRawLength)
                    {
                        var raw = SkyTransformData.rawTransformDatas[transformId];
                        view.UploadMatches = DecalVisibilityWorkModel.SameUploadedTransform(
                            WorkTransform(transform.WorldPosition, transform.rotation, transform.scale),
                            WorkTransform(new Vector3(raw.position.x, raw.position.y, raw.position.z), raw.rotation, raw.scale), raw.isValid, true, true);
                    }
                }
            }
            views[transformId] = view;
            return view;
        }
        private static float ScaleFor(Aabb bounds, ref View view)
        {
            if (settings.Percent == 100) return 1;
            if (!view.Valid) return Mathf.Min(settings.Multiplier, 2);
            var center = bounds.Center;
            Vector3 position = view.Matrix.MultiplyPoint3x4(new Vector3(center.x, center.y, center.z));
            if (position.z >= -0.001f) return 1;
            var size = bounds.Size * 0.5f;
            ref Matrix4x4 m = ref view.Matrix;
            var a = new ProjectionVector(m.m00 * size.x, m.m10 * size.x, m.m20 * size.x);
            var b = new ProjectionVector(m.m01 * size.y, m.m11 * size.y, m.m21 * size.y);
            var c = new ProjectionVector(m.m02 * size.z, m.m12 * size.z, m.m22 * size.z);
            float minimumPixels = ProjectionFootprintModel.MinimumPixels(new ProjectionVector(position.x, position.y, position.z), a, b, c, focalX, focalY);
            if (float.IsPositiveInfinity(minimumPixels)) return 1; // already intersects the near plane
            float scale = settings.AdaptiveScale(minimumPixels);
            float depthSpan = Mathf.Abs(a.Z) + Mathf.Abs(b.Z) + Mathf.Abs(c.Z);
            // Do not turn a narrow off-axis box into a camera-crossing volume.
            if (depthSpan > 0) scale = Mathf.Min(scale, Mathf.Max(1, -position.z * 0.9f / depthSpan));
            return scale;
        }

        internal static void ApplyQueued()
        {
            if (applying) return;
            // Wakeup checks must never consume last frame's local transform.
            views.Clear();
            if (observedCamera != null) ObserveCamera(observedCamera);
            PrepareFilterCapture();
            lastTestedGroups = lastSkippedGroups = lastSkippedEntries = lastWakeups = 0;
            bool periodic = settings.Percent > 100 && cameraObserved &&
                (Time.unscaledTime >= nextScan || (cameraPosition - lastScanPosition).sqrMagnitude > 25 || Quaternion.Angle(cameraRotation, lastScanRotation) > 3);
            bool hasSkipped = skippedGroupsPending;
            if (!pending && !periodic && !hasSkipped) return;
            skippedGroupsPending = false;
            bool full = allPending;
            pending = false; allPending = false;
            views.Clear();
            int changed = 0, examined = 0;
            applying = true;
            try
            {
                foreach (var pair in tracked.ToArray()) // pools are few; never copy the whole decal registry
                {
                    var buffer = pair.Key; var pool = pair.Value;
                    if (!BrickCullingBuffer.brickCullingBuffers.Contains(buffer)) { tracked.Remove(buffer); continue; }
                    stale.Clear();
                    if (full || periodic || hasSkipped)
                    {
                        foreach (var groupPair in pool.Groups)
                        {
                            var group = groupPair.Value;
                            group.CountedSkippedEntries = false;
                            if (!full && !periodic && !group.Work.Skipped) continue;
                            var view = GetView(groupPair.Key);
                            bool moved = !group.HasView || !view.Valid || group.LastView != view.Matrix;
                            bool outside = false;
                            if (proxyWorkFilteringEnabled && settings.Percent > 100 && !full && (group.Work.Skipped || (periodic && moved)))
                            {
                                lastTestedGroups++; testedGroups++;
                                if (filterCaptureValid && view.Valid && view.UploadMatches && group.Envelope.Usable && !pool.UntrackedClone)
                                    outside = DecalVisibilityWorkModel.IsOutside(group.Envelope.Bounds, filterNear, renderHasFar, filterFar, filterCamera, view.Local);
                                else if (pool.UntrackedClone) fallbackReason = "untracked-clone-pool";
                                else if (!group.Envelope.Usable) fallbackReason = "unknown-aggregate";
                                else if (filterCaptureValid && !view.UploadMatches) fallbackReason = "unknown-or-divergent-upload-transform";
                            }
                            bool refresh = group.Work.Decide(proxyWorkFilteringEnabled, settings.Percent > 100, full, periodic, moved, outside, out bool woke);
                            if (woke) { lastWakeups++; wakeups++; }
                            if (group.Work.Skipped)
                            {
                                skippedGroupsPending = true;
                                lastSkippedGroups++; skippedGroups++;
                                // Rechecking a sleeping group below cadence is
                                // not a saved per-entry scan. Count omitted IDs
                                // only when baseline would have scanned them.
                                if (periodic && moved)
                                {
                                    group.CountedSkippedEntries = true;
                                    int count = group.Ids.Count;
                                    lastSkippedEntries += count; skippedEntries += count;
                                }
                            }
                            if (refresh)
                            {
                                foreach (int id in group.Ids)
                                {
                                    if (!ApplyEntry(buffer, pool, id, ref view, ref changed, ref examined)) stale.Add(id);
                                    pool.Dirty.Remove(id);
                                }
                                group.LastView = view.Matrix; group.HasView = view.Valid;
                            }
                        }
                    }
                    // Newly added/changed IDs still need applying when their group is stationary.
                    foreach (int id in pool.Dirty)
                    {
                        var transform = pool.Entries[id].Original.transformId;
                        if (pool.Groups.TryGetValue(transform, out var dirtyGroup) && dirtyGroup.CountedSkippedEntries)
                        { lastSkippedEntries--; skippedEntries--; }
                        var view = GetView(transform);
                        if (!ApplyEntry(buffer, pool, id, ref view, ref changed, ref examined)) stale.Add(id);
                    }
                    foreach (int id in stale) Forget(buffer, id);
                    pool.Dirty.Clear();
                }
            }
            finally { applying = false; views.Clear(); stale.Clear(); }
            if (periodic || full)
            {
                nextScan = Time.unscaledTime + 0.125f;
                lastScanPosition = cameraPosition; lastScanRotation = cameraRotation;
            }
            if (full) Plugin.Instance?.LogDecal($"Decal culling: {settings.Multiplier:F1}x adaptive, examined={examined}, updated={changed}");
        }
        private static bool ApplyEntry(BrickCullingBuffer buffer, Pool pool, int id, ref View view, ref int changed, ref int examined)
        {
            if (!pool.Entries.TryGetValue(id, out var entry)) return false;
            examined++;
            float scale = ScaleFor(entry.Original.bounds, ref view);
            if (entry.AppliedScale == scale && !pool.Dirty.Contains(id)) return true;
            if (id < 0 || id >= buffer.Length) return false;
            var current = buffer.GetElement(id);
            if (!IsDecal(current) || current.transformId == 0 || !SameIdentity(current, entry.Original)) return false;
            if (!SameBounds(current.bounds, entry.AppliedBounds))
            {
                entry.Original.bounds = current.bounds;
                if (pool.Groups.TryGetValue(entry.Original.transformId, out var group)) IncludeOriginal(group, entry.Original.bounds);
            }
            var bounds = entry.Original.bounds;
            if (scale != 1) bounds.SetSize(bounds.Size * scale);
            if (!SameBounds(current.bounds, bounds))
            {
                current.bounds = bounds; buffer.Update(id, current); changed++;
            }
            entry.AppliedBounds = bounds; entry.AppliedScale = scale;
            return true;
        }
        internal static void Shutdown()
        {
            tracked.Clear(); views.Clear(); stale.Clear();
            settings.TrySet(100); pending = false; allPending = false;
            proxyWorkFilteringEnabled = false;
            skippedGroupsPending = false;
            testedGroups = skippedGroups = skippedEntries = wakeups = 0;
            lastTestedGroups = lastSkippedGroups = lastSkippedEntries = lastWakeups = 0;
            fallbackReason = "disabled";
            ResetCamera();
        }

        [HarmonyPatch(typeof(VoxelRenderingManager), "RenderOnCamera")]
        private static class CameraPatch { private static void Prefix(Camera camera, Camera farCamera) => CaptureRender(camera, farCamera); }
        [HarmonyPatch(typeof(BrickCullingBuffer), nameof(BrickCullingBuffer.AddElement))]
        private static class AddPatch { private static void Postfix(BrickCullingBuffer __instance, RenderData renderData, int __result) => Record(__instance, __result, renderData, true); }
        [HarmonyPatch(typeof(BrickCullingBuffer), nameof(BrickCullingBuffer.Update))]
        private static class UpdatePatch { private static void Postfix(BrickCullingBuffer __instance, int cullingId, RenderData renderData) => Record(__instance, cullingId, renderData); }
        [HarmonyPatch(typeof(BrickCullingBuffer), nameof(BrickCullingBuffer.ReplaceBrickBounds))]
        private static class BoundsPatch { private static void Postfix(BrickCullingBuffer __instance, int cullingId) => Record(__instance, cullingId, __instance.GetElement(cullingId), true); }
        [HarmonyPatch(typeof(BrickCullingBuffer), nameof(BrickCullingBuffer.RemoveElement))]
        private static class RemovePatch { private static void Prefix(BrickCullingBuffer __instance, int cullingId) => Forget(__instance, cullingId); }
        [HarmonyPatch(typeof(BrickCullingBuffer), nameof(BrickCullingBuffer.CloneBrick))]
        private static class ClonePatch { private static void Postfix(BrickCullingBuffer __instance) => RecordClone(__instance); }
        [HarmonyPatch(typeof(BrickCullingBuffer), nameof(BrickCullingBuffer.Reset))]
        private static class ResetPatch { private static void Prefix(BrickCullingBuffer __instance) => Forget(__instance); }
        [HarmonyPatch(typeof(BrickCullingBuffer), nameof(BrickCullingBuffer.Dispose))]
        private static class DisposePatch { private static void Prefix(BrickCullingBuffer __instance) => Forget(__instance); }
        [HarmonyPatch(typeof(BrickCullingBuffer), nameof(BrickCullingBuffer.ExecuteCommands))]
        private static class UploadPatch { private static void Prefix() => ApplyQueued(); }
        [HarmonyPatch(typeof(SkyTransformData), nameof(SkyTransformData.DisposeJobResources))]
        private static class TransformUploadPatch { private static void Postfix() => CaptureTransformUpload(); }
    }
}
