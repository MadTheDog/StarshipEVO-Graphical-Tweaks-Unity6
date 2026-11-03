using System;
using System.Collections.Generic;
using Common;
using UnityEngine;
using UnityEngine.Rendering;

namespace GraphicalTweaks
{
    // Experimental, opt-in capture. No instance is created by Plugin.
    internal sealed class SunShadowCapture : IDisposable
    {
        private static readonly int AtlasId = Shader.PropertyToID("_GTSunShadowAtlas");
        private static readonly int AtlasStampId = Shader.PropertyToID("_GTAtlasCaptureStamp");
        private static readonly int DataId = Shader.PropertyToID("_GTShadowData");
        private static readonly int FrameId = Shader.PropertyToID("_GTFrameStamp");
        private static readonly int CameraId = Shader.PropertyToID("_GTCameraToken");
        private static readonly int ReadyId = Shader.PropertyToID("_GTShadowReady");
        private static readonly int DistanceId = Shader.PropertyToID("_GTShadowDistance");
        private static readonly int BiasId = Shader.PropertyToID("_GTReceiverBias");
        private static readonly int CascadesId = Shader.PropertyToID("_GTCascadeCount");
        private static readonly int StrengthId = Shader.PropertyToID("_GTShadowStrength");
        private readonly Shader exportShader;
        private readonly List<CameraIdentity> cameraIdentities = new List<CameraIdentity>();
        private Material exportMaterial;
        private RenderTexture data;
        private CommandBuffer atlasCommands;
        private CommandBuffer dataCommands;
        private Light attachedSun;
        private Camera activeCamera;
        private Camera verifiedGameCamera;
        private Vector4 frameStamp;
        private int captureFrame = -1;
        private int cameraToken;
        private int nextCameraToken;
        private bool enabled;
        private bool disposed;
        private bool attached;
        private bool diagnosticArmed;
        private int diagnosticSerial;
        private DiagnosticTrace pendingDiagnostic;

        private sealed class DiagnosticTrace
        {
            internal readonly string Context;
            internal readonly int Frame;
            internal int AtlasReported, ExportReported;
            internal DiagnosticTrace(string context, int frame) { Context = context; Frame = frame; }
        }

        private struct CameraIdentity
        {
            internal Camera Camera;
            internal int Token;
        }

        internal SunShadowCapture(Shader exportShader)
        {
            if (exportShader == null) throw new ArgumentNullException(nameof(exportShader));
            this.exportShader = exportShader;
        }

        internal RenderTexture ShadowData => data;
        internal Camera ActiveCamera => activeCamera;
        internal bool IsAttached => attached && attachedSun != null && activeCamera != null;
        internal float ReceiverBias { get; set; } = 0.0001f;
        internal string LastError { get; private set; }
        internal int AttachmentCount { get; private set; }

        // Optional one-shot event proof. No trace requests or per-frame readbacks
        // are recorded unless explicitly armed by the diagnostic probe.
        internal void ArmDiagnosticCapture()
        {
            if (disposed) throw new ObjectDisposedException(nameof(SunShadowCapture));
            diagnosticArmed = true;
            diagnosticSerial++;
            Debug.Log("GT shadow event trace armed id=" + diagnosticSerial + "; waiting for an eligible player-camera capture.");
        }

        // The caller must verify this reference against the game's actual GameCamera.
        // Camera.main and similarly named preview/sky cameras are never inferred.
        internal void SetVerifiedGameCamera(Camera camera)
        {
            if (verifiedGameCamera == camera) return;
            Detach();
            verifiedGameCamera = camera;
            activeCamera = null;
        }

        internal void SetEnabled(bool value)
        {
            if (disposed) throw new ObjectDisposedException(nameof(SunShadowCapture));
            if (enabled == value) return;
            enabled = value;
            if (value)
            {
                Camera.onPreCull += OnCameraPreCull;
                Camera.onPostRender += OnCameraPostRender;
            }
            else
            {
                Camera.onPreCull -= OnCameraPreCull;
                Camera.onPostRender -= OnCameraPostRender;
                Detach();
                activeCamera = null;
                Shader.SetGlobalTexture(AtlasId, null);
            }
        }

        // true means capture is armed for this camera/frame, not GPU-validated.
        // The receiver additionally checks the exported GPU stamp before sampling.
        internal bool Bind(MaterialPropertyBlock props, Camera camera)
        {
            if (props == null) throw new ArgumentNullException(nameof(props));
            bool ready = enabled && !disposed && IsAttached && camera != null &&
                camera == activeCamera && captureFrame == Time.frameCount &&
                IsEligibleCamera(camera) && IsUsableSun(attachedSun, camera) &&
                attachedSun == CurrentSun && IsSupportedProjection() &&
                data != null && data.IsCreated();
            WriteBindings(props, ready, ready ? frameStamp : CurrentFrameStamp(), ready ? cameraToken : 0);
            return ready;
        }

        // Some native near-camera command buffers are recorded while the sky
        // camera is current, before the actual player-camera pre-cull. Prepare
        // expected identity/data only; attaching still happens in player pre-cull.
        internal bool BindForUpcomingCamera(MaterialPropertyBlock props, Camera verifiedPlayer)
        {
            if (props == null) throw new ArgumentNullException(nameof(props));
            bool expected = enabled && !disposed && IsEligibleCamera(verifiedPlayer) &&
                IsUsableSun(CurrentSun, verifiedPlayer) && IsSupportedProjection();
            if (expected)
            {
                try { expected = EnsureResources(); }
                catch (Exception error)
                {
                    LastError = "Upcoming shadow capture resource creation failed: " + error;
                    expected = false;
                }
            }
            int token = expected ? GetStableCameraToken(verifiedPlayer) : 0;
            WriteBindings(props, expected, CurrentFrameStamp(), token);
            // Only the GPU export's matching texel 23 proves capture occurred.
            return expected;
        }

        private void WriteBindings(MaterialPropertyBlock props, bool ready, Vector4 expectedFrame, int expectedToken)
        {
            props.SetFloat(ReadyId, ready ? 1f : 0f);
            props.SetVector(FrameId, expectedFrame);
            props.SetFloat(CameraId, expectedToken);
            props.SetFloat(DistanceId, ready ? QualitySettings.shadowDistance : 0f);
            props.SetFloat(BiasId, Mathf.Max(0f, ReceiverBias));
            if (data != null) props.SetTexture(DataId, data);
            // AtlasId intentionally remains a command-buffer global: it aliases a
            // native temporary depth target, which has no owned Texture wrapper.
        }

        private int GetStableCameraToken(Camera camera)
        {
            for (int i = cameraIdentities.Count - 1; i >= 0; i--)
            {
                CameraIdentity identity = cameraIdentities[i];
                if (identity.Camera == null) cameraIdentities.RemoveAt(i);
                else if (identity.Camera == camera) return identity.Token;
            }
            nextCameraToken = nextCameraToken % 65535 + 1;
            cameraIdentities.Add(new CameraIdentity { Camera = camera, Token = nextCameraToken });
            return nextCameraToken;
        }

        private bool IsEligibleCamera(Camera camera)
        {
            return camera != null && camera.isActiveAndEnabled &&
                camera.cameraType == CameraType.Game &&
                (camera.name == "02-PlayerCamera" || camera == verifiedGameCamera);
        }

        private static Light CurrentSun => PositionManager.instance != null ? PositionManager.instance.sunLight : null;

        private bool IsSupportedProjection()
        {
            if (QualitySettings.shadowCascades <= 1 || QualitySettings.shadowProjection == ShadowProjection.StableFit) return true;
            LastError = "CloseFit multi-cascade shadows are unsupported by the sphere-only shadow export; capture is disabled.";
            return false;
        }

        private static bool IsUsableSun(Light sun, Camera camera)
        {
            return sun != null && sun.isActiveAndEnabled && sun.type == LightType.Directional &&
                sun.shadows != LightShadows.None && sun.shadowStrength > 0f &&
                QualitySettings.shadows != ShadowQuality.Disable && QualitySettings.shadowDistance > 0f &&
                (sun.cullingMask & camera.cullingMask) != 0;
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (pendingDiagnostic != null)
            {
                if (pendingDiagnostic.Frame == Time.frameCount)
                    Debug.Log("GT shadow event trace PRE_CULL before scoped detach " + pendingDiagnostic.Context +
                        " enteringCamera=" + (camera == null ? "null" : camera.name) + " wasAttached=" + IsAttached);
                else pendingDiagnostic = null;
            }
            // Light buffers have no camera filter. Remove ours before *every*
            // camera, including sky, preview and nested Camera.Render calls.
            Detach();
            if (!enabled || disposed || !IsEligibleCamera(camera)) return;
            Light sun = CurrentSun; // Resolve again after scene/system changes.
            if (!IsUsableSun(sun, camera) || !IsSupportedProjection()) return;
            try
            {
                if (!EnsureResources()) return;
            }
            catch (Exception error)
            {
                LastError = "Shadow capture resource creation failed: " + error;
                return;
            }
            activeCamera = camera;
            // Upcoming bindings and the actual export share this camera's
            // float-exact identity, independent of intervening sky callbacks.
            cameraToken = GetStableCameraToken(camera);
            captureFrame = Time.frameCount;
            frameStamp = CurrentFrameStamp();
            exportMaterial.SetVector(FrameId, frameStamp);
            exportMaterial.SetFloat(CameraId, cameraToken);
            int cascades = QualitySettings.shadowCascades;
            exportMaterial.SetFloat(CascadesId, cascades == 2 || cascades == 4 ? cascades : 1);
            exportMaterial.SetFloat(StrengthId, sun.shadowStrength);

            atlasCommands.Clear();
            atlasCommands.SetGlobalTexture(AtlasId, BuiltinRenderTextureType.CurrentActive);
            atlasCommands.SetGlobalVector(AtlasStampId, new Vector4(frameStamp.x, frameStamp.y, cameraToken, 4321f));
            dataCommands.Clear();
            dataCommands.Blit(BuiltinRenderTextureType.None, data, exportMaterial, 0);
            bool tracing = diagnosticArmed;
            if (tracing)
            {
                var trace = new DiagnosticTrace("id=" + diagnosticSerial + " recordedFrame=" + captureFrame +
                    " frameLow=" + frameStamp.x + " frameHigh=" + frameStamp.y + " cameraToken=" + cameraToken +
                    " camera=" + camera.name + " cameraId=" + ObjectIdentity.Describe(camera) + " sun=" + sun.name +
                    " sunId=" + ObjectIdentity.Describe(sun) + " data=" + data.width + "x" + data.height +
                    " cameraPixelRect=" + camera.pixelRect + " path=" + camera.actualRenderingPath, captureFrame);
                pendingDiagnostic = trace;
                Debug.Log("GT shadow event trace recording " + trace.Context +
                    " currentCpuAtlasStamp=" + Shader.GetGlobalVector(AtlasStampId) + "; callbacks prove event command execution, not exporter writes.");
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    atlasCommands.RequestAsyncReadback(data, request => ReportDiagnosticEvent(trace, request, false));
                    dataCommands.RequestAsyncReadback(data, request => ReportDiagnosticEvent(trace, request, true));
                }
                else Debug.Log("GT shadow event trace unavailable: async GPU readback unsupported; " + trace.Context);
            }
            attachedSun = sun;
            try
            {
                // Set the flag first so partial attachment is also removable.
                attached = true;
                sun.AddCommandBuffer(LightEvent.AfterShadowMap, atlasCommands);
                // Export after the native collector has finished, so this Blit
                // cannot redirect its draw into our tiny data render target.
                sun.AddCommandBuffer(LightEvent.AfterScreenspaceMask, dataCommands);
                AttachmentCount++;
                LastError = null;
                if (tracing) diagnosticArmed = false;
            }
            catch (Exception error)
            {
                Detach();
                LastError = "Shadow capture attachment failed: " + error;
            }
        }

        private void ReportDiagnosticEvent(DiagnosticTrace trace, AsyncGPUReadbackRequest request, bool afterExport)
        {
            int previous = afterExport
                ? System.Threading.Interlocked.Exchange(ref trace.ExportReported, 1)
                : System.Threading.Interlocked.Exchange(ref trace.AtlasReported, 1);
            if (previous != 0) return;
            string stage = afterExport ? "AfterScreenspaceMask" : "AfterShadowMap";
            // Deliberately never GetData: the tiny readback callback itself is
            // independent proof that this particular native light event ran.
            Debug.Log("GT shadow event trace EXECUTED " + stage + " " + trace.Context +
                " done=" + request.done + " hasError=" + request.hasError + " serviceDisposed=" + disposed +
                (disposed ? "" : " currentCpuAtlasStamp=" + Shader.GetGlobalVector(AtlasStampId)) +
                "; CPU global state may belong to a later frame and is not the readback payload.");
        }

        private void OnCameraPostRender(Camera camera)
        {
            if (pendingDiagnostic != null && pendingDiagnostic.Frame == Time.frameCount)
                Debug.Log("GT shadow event trace POST_RENDER " + pendingDiagnostic.Context +
                    " exitingCamera=" + (camera == null ? "null" : camera.name) + " wasAttached=" + IsAttached);
            if (camera == activeCamera) Detach();
        }

        private static Vector4 CurrentFrameStamp()
        {
            uint frame = unchecked((uint)Time.frameCount);
            return new Vector4(frame & 65535u, frame >> 16, 0f, 0f);
        }

        private bool EnsureResources()
        {
            if (exportShader == null || !exportShader.isSupported ||
                !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            {
                LastError = "Shadow export shader or ARGBFloat render texture unsupported.";
                return false;
            }
            if (exportMaterial == null)
                exportMaterial = new Material(exportShader) { name = "GT owned shadow export", hideFlags = HideFlags.HideAndDontSave };
            if (data == null)
            {
                data = new RenderTexture(25, 1, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
                {
                    name = "GT owned shadow data", hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false, autoGenerateMips = false, antiAliasing = 1
                };
            }
            if (!data.IsCreated() && !data.Create())
            {
                LastError = "Shadow data render texture creation returned false.";
                return false;
            }
            if (atlasCommands == null) atlasCommands = new CommandBuffer { name = "GT owned sun atlas alias (experimental)" };
            if (dataCommands == null) dataCommands = new CommandBuffer { name = "GT owned sun data export (experimental)" };
            return true;
        }

        private void Detach()
        {
            if (attachedSun != null)
            {
                if (atlasCommands != null) attachedSun.RemoveCommandBuffer(LightEvent.AfterShadowMap, atlasCommands);
                if (dataCommands != null) attachedSun.RemoveCommandBuffer(LightEvent.AfterScreenspaceMask, dataCommands);
            }
            attached = false;
            attachedSun = null;
            captureFrame = -1;
        }

        public void Dispose()
        {
            if (disposed) return;
            SetEnabled(false);
            Detach();
            Shader.SetGlobalTexture(AtlasId, null);
            disposed = true;
            pendingDiagnostic = null;
            activeCamera = null;
            verifiedGameCamera = null;
            cameraIdentities.Clear();
            atlasCommands?.Release(); atlasCommands = null;
            dataCommands?.Release(); dataCommands = null;
            if (data != null) { data.Release(); UnityEngine.Object.Destroy(data); data = null; }
            if (exportMaterial != null) { UnityEngine.Object.Destroy(exportMaterial); exportMaterial = null; }
        }
    }
}
