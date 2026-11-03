using System;
using System.Collections.Generic;
using Common;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using UnityEngine;
using VoxelEngine;

namespace GraphicalTweaks
{
    // DecalData is a private native struct. The generated accessor uses SEVO's
    // own container getter/setter and changes only bounds, keeping every live
    // field (including color, health, material and IDs) intact.
    internal static class DecalProjection
    {
        private sealed class Entry
        {
            internal Aabb Original;
            internal int Axis;
            internal int AppliedPercent = 100;
        }
        private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private static readonly HashSet<int> dirty = new HashSet<int>();
        private static readonly DecalRangeModel settings = new DecalRangeModel();
        private static bool allPending;
        private static bool available;
        private static Action<int, Aabb> writer;

        internal static void Install(Harmony harmony)
        {
            writer = BuildWriter();
            available = true;
        }

        internal static void Request(int percent)
        {
            if (settings.TrySet(percent)) allPending = true;
        }

        internal static int NormalAxis(uint rotation)
        {
            Vector3 normal = VoxelRotation.Quaternions[rotation] * Vector3.up;
            int axis = Mathf.Abs(normal.x) > Mathf.Abs(normal.y) ? 0 : 1;
            return Mathf.Abs(normal.z) > Mathf.Abs(normal[axis]) ? 2 : axis;
        }

        internal static Aabb Expand(Aabb original, int axis, float padding)
        {
            if (padding == 0) return original; // exact float baseline on reset
            var min = original.Min;
            var max = original.Max;
            min[axis] -= padding;
            max[axis] += padding;
            return new Aabb { Min = min, Max = max };
        }

        private static void Record(int id, BrickInstanceData brickData)
        {
            // Even an excluded replacement must invalidate an older ordinary
            // decal at this ID before returning.
            entries.Remove(id); dirty.Remove(id);
            if (!available || brickData.Brick.IsDecalDamage) return;
            if (brickData.rotation >= VoxelRotation.Quaternions.Length) return;
            entries[id] = new Entry { Original = brickData.GetAabb(false), Axis = NormalAxis(brickData.rotation) };
            dirty.Add(id);
        }

        private static void ApplyQueued()
        {
            if (!available || (!allPending && dirty.Count == 0)) return;
            int changed = 0;
            bool full = allPending;
            if (full)
            {
                foreach (var pair in entries) Apply(pair.Key, pair.Value, ref changed);
            }
            else
            {
                foreach (int id in dirty)
                    if (entries.TryGetValue(id, out var entry)) Apply(id, entry, ref changed);
            }
            allPending = false;
            dirty.Clear();
            if (full && changed > 0) Plugin.Instance?.LogDecal($"Decal projection: {settings.Percent}%, padding={settings.ProjectionPadding}, updated={changed}");
        }

        private static void Apply(int id, Entry entry, ref int changed)
        {
            if (entry.AppliedPercent == settings.Percent) return;
            WriteBounds(id, Expand(entry.Original, entry.Axis, settings.ProjectionPadding));
            entry.AppliedPercent = settings.Percent;
            changed++;
        }

        internal static void Shutdown()
        {
            ClearWorld();
            settings.TrySet(100);
        }
        private static void ClearWorld() { entries.Clear(); dirty.Clear(); allPending = false; DecalVisibility.ResetCamera(); }

        internal static void WriteBounds(int id, Aabb bounds)
        {
            if (writer == null) throw new InvalidOperationException("Projection accessor is not installed.");
            writer(id, bounds);
        }

        private static Action<int, Aabb> BuildWriter()
        {
            var owner = typeof(DecalRendererResources);
            var storage = AccessTools.Field(owner, "datas");
            var item = storage.FieldType.GetProperty("Item");
            var bounds = AccessTools.Field(item.PropertyType, "bounds");
            if (!storage.IsStatic || !storage.FieldType.IsValueType || bounds.FieldType != typeof(Aabb))
                throw new NotSupportedException("Unexpected SEVO decal container layout.");
            // MonoMod is already supplied by BepInEx/Harmony. Its generated
            // method backend supports this Unity Mono runtime without requiring
            // the framework's Reflection.Emit compiler surface or new packages.
            using (var definition = new DynamicMethodDefinition("GraphicalTweaks.SetDecalBounds", typeof(void), new[] { typeof(int), typeof(Aabb) }))
            {
                var module = definition.Definition.Module;
                var body = definition.Definition.Body;
                var data = new VariableDefinition(module.ImportReference(item.PropertyType));
                body.Variables.Add(data);
                body.InitLocals = true;
                var il = body.GetILProcessor();
                var done = il.Create(OpCodes.Ret);
                il.Emit(OpCodes.Ldsflda, module.ImportReference(storage));
                il.Emit(OpCodes.Call, module.ImportReference(storage.FieldType.GetProperty("IsCreated").GetGetMethod()));
                il.Emit(OpCodes.Brfalse, done);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ble, done);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldsflda, module.ImportReference(storage));
                il.Emit(OpCodes.Call, module.ImportReference(storage.FieldType.GetProperty("Length").GetGetMethod()));
                il.Emit(OpCodes.Bge, done);
                il.Emit(OpCodes.Ldsflda, module.ImportReference(storage));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, module.ImportReference(item.GetGetMethod()));
                il.Emit(OpCodes.Stloc, data);
                il.Emit(OpCodes.Ldloca, data);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, module.ImportReference(bounds));
                il.Emit(OpCodes.Ldsflda, module.ImportReference(storage));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloc, data);
                il.Emit(OpCodes.Call, module.ImportReference(item.GetSetMethod()));
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, module.ImportReference(AccessTools.Method(owner, "SetMinMax")));
                il.Append(done);
                return (Action<int, Aabb>)definition.Generate().CreateDelegate(typeof(Action<int, Aabb>));
            }
        }

        [HarmonyPatch(typeof(DecalRendererResources), nameof(DecalRendererResources.SetDecal))]
        private static class AddPatch
        {
            private static void Postfix(BrickInstanceData brickData, int __result) => Record(__result, brickData);
        }
        [HarmonyPatch(typeof(DecalRendererResources), nameof(DecalRendererResources.ReleaseId))]
        private static class RemovePatch
        {
            private static void Prefix(int id) { entries.Remove(id); dirty.Remove(id); }
        }
        [HarmonyPatch(typeof(DecalRendererResources), nameof(DecalRendererResources.ReleaseResources))]
        private static class ReleasePatch { private static void Prefix() => ClearWorld(); }
        [HarmonyPatch(typeof(DecalRendererResources), nameof(DecalRendererResources.OnGameReload))]
        private static class ReloadPatch { private static void Prefix() => ClearWorld(); }
        [HarmonyPatch(typeof(DecalRendererResources), nameof(DecalRendererResources.AllocateResources))]
        private static class AllocatePatch
        {
            private static void Prefix() { entries.Clear(); dirty.Clear(); }
        }
        [HarmonyPatch(typeof(DecalRendererResources), nameof(DecalRendererResources.ExecuteCommands))]
        private static class UploadPatch { private static void Prefix() => ApplyQueued(); }
    }
}
