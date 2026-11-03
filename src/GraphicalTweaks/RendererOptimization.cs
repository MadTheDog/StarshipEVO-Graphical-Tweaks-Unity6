using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Common;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine;

namespace GraphicalTweaks
{
    internal static class RendererOptimization
    {
        internal static bool Enabled = false;
        internal static long SkippedDispatches;
        internal static long IssuedDispatches;

        internal static void DispatchDirect(CommandBuffer cb, ComputeShader shader, int kernel, int x, int y, int z, EvoRenderer.DrawPass pass)
        {
            if (Enabled && pass.ElementCount == 0) { SkippedDispatches++; return; }
            cb.DispatchCompute(shader, kernel, x, y, z);
            IssuedDispatches++;
        }

        internal static void DispatchIndirect(CommandBuffer cb, ComputeShader shader, int kernel, ComputeBuffer args, uint offset, EvoRenderer.DrawPass pass)
        {
            if (Enabled && pass.ElementCount == 0) { SkippedDispatches++; return; }
            cb.DispatchCompute(shader, kernel, args, offset);
            IssuedDispatches++;
        }

        [HarmonyPatch(typeof(EvoRenderer.DrawPass), nameof(EvoRenderer.DrawPass.FillBuffer))]
        private static class EmptyPassPatch
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var original = instructions.ToList();
                var direct = AccessTools.Method(typeof(CommandBuffer), nameof(CommandBuffer.DispatchCompute), new[] { typeof(ComputeShader), typeof(int), typeof(int), typeof(int), typeof(int) });
                var indirect = AccessTools.Method(typeof(CommandBuffer), nameof(CommandBuffer.DispatchCompute), new[] { typeof(ComputeShader), typeof(int), typeof(ComputeBuffer), typeof(uint) });
                var directWrapper = AccessTools.Method(typeof(RendererOptimization), nameof(DispatchDirect));
                var indirectWrapper = AccessTools.Method(typeof(RendererOptimization), nameof(DispatchIndirect));
                int directCount = 0, indirectCount = 0, dispatchCount = 0;
                bool valid = direct != null && indirect != null && directWrapper != null && indirectWrapper != null;
                for (int i = 0; i < original.Count; i++)
                {
                    var method = original[i].operand as MethodInfo;
                    if (method == null || method.DeclaringType != typeof(CommandBuffer) || method.Name != nameof(CommandBuffer.DispatchCompute)) continue;
                    dispatchCount++;
                    if (original[i].opcode != OpCodes.Callvirt || (i > 0 && original[i - 1].opcode.OpCodeType == OpCodeType.Prefix)) valid = false;
                    if (method == direct) directCount++;
                    else if (method == indirect) indirectCount++;
                    else valid = false;
                }
                // Validate the entire method before replacing anything. An unfamiliar
                // game build retains its original implementation, never a partial patch.
                if (!valid || dispatchCount != 2 || directCount != 1 || indirectCount != 1)
                {
                    Debug.LogWarning("Graphical Tweaks: empty-pass optimisation not installed; unexpected DrawPass.FillBuffer dispatch shape.");
                    return original;
                }
                var result = new List<CodeInstruction>(original.Count + 2);
                foreach (CodeInstruction instruction in original)
                {
                    var method = instruction.operand as MethodInfo;
                    if (method != direct && method != indirect) { result.Add(instruction); continue; }
                    var loadPass = new CodeInstruction(OpCodes.Ldarg_0);
                    instruction.MoveLabelsTo(loadPass);
                    var call = new CodeInstruction(OpCodes.Call, method == direct ? directWrapper : indirectWrapper);
                    foreach (ExceptionBlock block in instruction.blocks)
                    {
                        // Branches and block entries must also execute the extra argument
                        // load. A block end stays after the replacement call.
                        if (block.blockType == ExceptionBlockType.EndExceptionBlock) call.blocks.Add(block);
                        else loadPass.blocks.Add(block);
                    }
                    result.Add(loadPass);
                    result.Add(call);
                }
                return result;
            }
        }

        [HarmonyPatch(typeof(RenderPipelineBuffers), nameof(RenderPipelineBuffers.RemoveFromCamera))]
        private static class DetachPatch
        {
            private static void Prefix(RenderPipelineBuffers __instance, Camera camera)
            {
                if (camera == null) return;
                // Native removal can throw on a missing far camera. Detach the
                // primary camera's two wrongly matched stages before that call.
                camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, __instance.BeforeLighting);
                camera.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, __instance.TerrainDensityCompute);
            }
        }
    }
}
