using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace GraphicalTweaks
{
    // Native AO lifecycle repair. Never owns or clears native AO resources;
    // only exact component-owned camera attachments are normalized.
    internal static class AmbientOcclusionLifecycle
    {
#if SEVO_UNITY6
        // Field/registration IL and native pointer identity revalidated separately.
        private const string NativeMvid = "00859162-dd57-4798-be35-54252142455d";
#else
        private const string NativeMvid = "f4666bd3-9a44-486c-b149-36d0a9c82e72";
#endif
        private const CameraEvent Stage = CameraEvent.BeforeLighting;
        private static Type nativeType;
        private static FieldInfo cameraField, commandField, pointerField;
        private static MethodInfo register, unregister;
        private static Harmony owner;
        private static Action<string> logger;
        private static int mainThread;
        internal static bool Installed { get; private set; }
        internal static bool Supported { get; private set; }
        internal static bool Enabled { get; private set; }
        internal static string Fault { get; private set; }

        internal static void Install(Harmony harmony, Action<string> log)
        {
            if (harmony == null) throw new ArgumentNullException(nameof(harmony));
            if (Installed) return;
            logger = log; owner = harmony; Enabled = Supported = false; Fault = null;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                var gameAssembly=typeof(VoxelEngine.Brick).Assembly;
                if (gameAssembly.ManifestModule.ModuleVersionId.ToString("D") != NativeMvid)
                    throw new NotSupportedException("native Assembly-CSharp MVID differs");
                nativeType=gameAssembly.GetType("MiniEngineAO.AmbientOcclusion",false);
                if(nativeType==null || !typeof(Behaviour).IsAssignableFrom(nativeType))
                    throw new NotSupportedException("native AO component type is unavailable");
                const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                cameraField = nativeType.GetField("_camera", fields);
                commandField = nativeType.GetField("_renderCommand", fields);
                if (cameraField == null || !cameraField.IsPrivate || cameraField.FieldType != typeof(Camera) ||
                    commandField == null || !commandField.IsPrivate || commandField.FieldType != typeof(CommandBuffer))
                    throw new NotSupportedException("native private camera/command layout differs");
                register = ExactMethod("RegisterCommandBuffers"); unregister = ExactMethod("UnregisterCommandBuffers");
                // GetCommandBuffers returns fresh wrappers on this player.
                // Require native identity so an already-correct attachment is
                // preserved in place instead of changing unrelated ordering.
                pointerField = typeof(CommandBuffer).GetField("m_Ptr", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pointerField == null || pointerField.FieldType != typeof(IntPtr))
                    throw new NotSupportedException("native command-buffer identity is unavailable");
                owner.Patch(register, prefix: Callback(nameof(BeforeRegister)), finalizer: Callback(nameof(NativeFailed)));
                owner.Patch(unregister, postfix: Callback(nameof(AfterUnregister)), finalizer: Callback(nameof(NativeFailed)));
                Installed = Supported = true;
                Log("AO lifecycle candidate installed, disabled; guarded native MVID=" + NativeMvid + "; pointer identity available=" + (pointerField != null));
            }
            catch (Exception error)
            {
                Fault = error.GetType().Name + ": " + error.Message;
                UnpatchOwned(); Installed = Supported = Enabled = false;
                Log("AO lifecycle unsupported; native behavior retained: " + Fault);
            }
        }
        private static MethodInfo ExactMethod(string name)
        {
            var method = nativeType.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, Type.EmptyTypes, null);
            if (method == null || !method.IsPrivate || method.IsStatic || method.ReturnType != typeof(void) || method.DeclaringType != nativeType)
                throw new NotSupportedException("native method signature differs: " + name);
            return method;
        }
        private static HarmonyMethod Callback(string name) => new HarmonyMethod(typeof(AmbientOcclusionLifecycle), name);
        internal static void SetEnabled(bool value)
        {
            if (!value) { Enabled = false; Log("AO lifecycle repair OFF; existing corrected attachments are retained, native lifecycle resumes"); return; }
            if (!Installed || !Supported || Fault != null)
            { Log("AO lifecycle repair remains OFF: " + (Fault ?? "candidate is not installed/supported")); return; }
            if (Thread.CurrentThread.ManagedThreadId != mainThread) { Fail(new InvalidOperationException("enable is not on the installation thread")); return; }
            if (Enabled) { Log("AO lifecycle repair already ON"); return; }
            Enabled = true;
            try
            {
                // Once per explicit enable, including disabled components on
                // active objects whose stale BeforeLighting commands survive.
#if SEVO_UNITY6
                var candidates = UnityEngine.Object.FindObjectsByType(nativeType);
#else
                var candidates = UnityEngine.Object.FindObjectsOfType(nativeType);
#endif
                foreach (var item in candidates)
                {
                    var component=item as Behaviour;
                    if (component == null) continue;
                    NormalizeExisting(component);
                }
                Log("AO lifecycle repair ON; existing active AO has one owned BeforeLighting attachment, disabled AO has none when initialized");
            }
            catch (Exception error) { Fail(error); }
        }
        private static bool CanRepair()
        {
            if (!Enabled || !Installed || !Supported) return false;
            if (Thread.CurrentThread.ManagedThreadId == mainThread) return true;
            Fail(new InvalidOperationException("native AO lifecycle callback is not on the installation thread")); return false;
        }
        private static bool Pair(object component, out Camera camera, out CommandBuffer command)
        {
            camera = cameraField.GetValue(component) as Camera;
            command = commandField.GetValue(component) as CommandBuffer;
            return camera != null && command != null;
        }
        private static void BeforeRegister(object __instance)
        {
            if (!CanRepair()) return;
            try
            {
                if (Pair(__instance, out var camera, out var command)) RemoveOwnedBounded(camera, command);
                // The original native method appends its single registration.
                // Native registration order is retained for genuine lifecycle
                // calls; no unrelated command is detached or re-added.
            }
            catch (Exception error) { Fail(error); }
        }
        private static void AfterUnregister(object __instance)
        {
            if (!CanRepair()) return;
            try { if (Pair(__instance, out var camera, out var command)) RemoveOwnedBounded(camera, command); }
            catch (Exception error) { Fail(error); }
        }
        private static Exception NativeFailed(Exception __exception)
        {
            if (__exception != null && Enabled) Fail(new InvalidOperationException("native AO lifecycle threw; repair disabled", __exception));
            return __exception; // Preserve original game exceptions unchanged.
        }
        private static void NormalizeExisting(Behaviour component)
        {
            if (!Pair(component, out var camera, out var command)) return; // Native lazy initialization retains ownership.
            int count = OwnedCount(camera.GetCommandBuffers(Stage), command);
            if(count<0) throw new InvalidOperationException("native command-buffer identity is no longer valid");
            bool active = component.isActiveAndEnabled;
            if ((active && count == 1) || (!active && count == 0))
            {
                Log("AO lifecycle existing component=" + ObjectIdentity.Describe(component) + "; active=" + active + "; owned=" + count + "; already correct, attachment order unchanged");
                return;
            }
            RemoveOwnedBounded(camera, command);
            if (active) camera.AddCommandBuffer(Stage, command);
            int after = OwnedCount(camera.GetCommandBuffers(Stage), command);
            if (after >= 0 && after != (active ? 1 : 0)) throw new InvalidOperationException("owned AO attachment postcondition failed");
            Log("AO lifecycle existing component=" + ObjectIdentity.Describe(component) + "; active=" + active + "; ownedBefore=" + count + "; ownedAfter=" + after +
                "; duplicate repair appends only this AO command; unrelated relative order preserved");
        }
        private static void RemoveOwnedBounded(Camera camera, CommandBuffer command)
        {
            CommandBuffer[] current = camera.GetCommandBuffers(Stage);
            int owned = OwnedCount(current, command);
            if(owned<0) throw new InvalidOperationException("native command-buffer identity is no longer valid");
            if (owned == 0) return;
            // Some Unity versions remove one occurrence, others remove all.
            // This is bounded by the initial total count, and never removes by
            // name or removes all camera command buffers.
            for (int i = 0; i < current.Length; i++) camera.RemoveCommandBuffer(Stage, command);
            int remaining = OwnedCount(camera.GetCommandBuffers(Stage), command);
            if (remaining > 0) throw new InvalidOperationException("owned AO command remained after bounded removal");
        }
        private static int OwnedCount(CommandBuffer[] commands, CommandBuffer owned)
        {
            IntPtr pointer = Pointer(owned);
            if (pointer != IntPtr.Zero)
            {
                int count = 0;
                foreach (var command in commands) if (Pointer(command) == pointer) count++;
                return count;
            }
            // A wrapper reference can establish a positive match, but cannot
            // establish absence: GetCommandBuffers can return fresh wrappers.
            if (commands.Length == 0) return 0;
            return -1;
        }
        private static IntPtr Pointer(CommandBuffer command)
        {
            if (command == null || pointerField == null) return IntPtr.Zero;
            try { return (IntPtr)pointerField.GetValue(command); } catch { return IntPtr.Zero; }
        }
        private static void Fail(Exception error)
        {
            Enabled = false; Fault = error.GetType().Name + ": " + error.Message;
            Log("AO lifecycle repair disabled after failure; native lifecycle retained: " + Fault);
        }
        private static void Log(string message) { try { logger?.Invoke(message); } catch { } }
        private static void UnpatchOwned()
        {
            if (owner == null) return;
            try
            {
                if (register != null)
                {
                    owner.Unpatch(register, AccessTools.Method(typeof(AmbientOcclusionLifecycle), nameof(BeforeRegister)));
                    owner.Unpatch(register, AccessTools.Method(typeof(AmbientOcclusionLifecycle), nameof(NativeFailed)));
                }
                if (unregister != null)
                {
                    owner.Unpatch(unregister, AccessTools.Method(typeof(AmbientOcclusionLifecycle), nameof(AfterUnregister)));
                    owner.Unpatch(unregister, AccessTools.Method(typeof(AmbientOcclusionLifecycle), nameof(NativeFailed)));
                }
            }
            catch (Exception error) { Log("AO lifecycle unpatch failure: " + error.Message); }
        }
        internal static void Dispose()
        {
            Enabled = false; UnpatchOwned(); Installed = Supported = false;
            owner = null; logger = null; cameraField = commandField = pointerField = null; register = unregister = null; Fault = null;
            nativeType=null;
            // No native command buffers, materials, textures or attachments are
            // disposed, recreated or restored to their previous duplicate state.
        }
    }
}
