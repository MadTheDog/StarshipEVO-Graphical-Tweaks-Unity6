using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Common.Rendering;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine;
using SEVO.GlassRenderingProbe;

namespace GraphicalTweaks
{
    // Keeps the game's transparent material, ordering, emission and distortion
    // draw. Direct sunlight loss and the optional baked probe override are separate.
    internal sealed class GlassRenderingController : IDisposable
    {
#if SEVO_UNITY6
        // Managed layout/source hooks match; catalog admission still proves
        // live strides, materials, rotations and shape geometry independently.
        private const string SupportedAssembly="00859162-dd57-4798-be35-54252142455d";
        private static readonly int ReflectionCube0=Shader.PropertyToID("unity_SpecCube0");
        private static readonly int ReflectionCube1=Shader.PropertyToID("unity_SpecCube1");
        private readonly Cubemap[] neutralProbes=new Cubemap[4];
        private readonly bool[] neutralProbeFaults=new bool[4];
        private int neutralSheen;
        private bool removeBakedHorizon,bakedHorizonFaulted;
#else
        private const string SupportedAssembly="f4666bd3-9a44-486c-b149-36d0a9c82e72";
#endif
        private static GlassRenderingController instance;
        private static readonly FieldInfo MappingField=AccessTools.Field(typeof(EvoRenderer),"drawMappingBuffer");
        private static readonly FieldInfo ClipField=AccessTools.Field(typeof(EvoRenderer),"drawMappingClipZBuffer");
        private static readonly FieldInfo ArgsField=AccessTools.Field(typeof(EvoRenderer),"argsBuffer");
        private static readonly FieldInfo ShapeField=AccessTools.Field(typeof(BrickMeshResources),"shapeMeshBuffer");
        private static readonly FieldInfo ManagerField=AccessTools.Field(typeof(VoxelRenderingManager),"instance");
        private readonly ManualLogSource log;
        private readonly string bundlePath;
        private readonly MaterialPropertyBlock nativeProperties=new MaterialPropertyBlock(),lossProperties=new MaterialPropertyBlock();
        private readonly MaterialPropertyBlockCopy propertyCopy=new MaterialPropertyBlockCopy();
        private readonly List<RetiredTarget> retiredTargets=new List<RetiredTarget>();
        private readonly List<RetiredComparison> retiredComparisons=new List<RetiredComparison>();
        private readonly MaterialPropertyBlock comparisonNative=new MaterialPropertyBlock(),comparisonRemapped=new MaterialPropertyBlock();
        private GlassSeamValidation comparison;
        private enum ComparisonKind { Seams, CapNative, CapLoss, CapNormal, CapDirect }
        private ComparisonKind comparisonKind;
        private GlassCapInterpolationBuffers comparisonCapBuffers;
        private RenderTexture comparisonDepth;
        private int comparisonWidth,comparisonHeight;
        private bool comparisonRecorded,comparisonCancelled;
        private long comparisonGeneration=-1,comparisonEpoch,comparisonCatalogGeneration,comparisonProofEpoch;
        private AssetBundle bundle;
        private Material lossMaterial,compositeMaterial;
        private SunShadowCapture capture;
        private RenderTexture lossTarget;
        private Camera player;
        private bool installed,glare,seams,faulted,disposed,quitting,diagnosticSession,released,loggedDraw;
        private bool expandedObservers,expandedUnavailable,expandedAssets,detachingExpanded;
        private bool expandedRecordingSuppressed,legacyFallbackTrusted=true;
        private long expandedDetachmentEpoch;
        private long lastExpandedSubmission=-1;
        private long clearGeneration,lastOwnedSubmission=-1;
        private string lastSupportReason;

        internal GlassRenderingController(ManualLogSource logger,string assetBundlePath)
        {
            log=logger??throw new ArgumentNullException(nameof(logger));bundlePath=assetBundlePath;
            Application.quitting+=OnQuitting;
        }
        // Installation only: no resource loading, geometry inventory or drawing.
        internal void InstallBootstrapObservers(Harmony harmony)
        {
            if(disposed||expandedObservers||expandedUnavailable)return;
            if(Chainloader.PluginInfos.ContainsKey("local.sevo.graphicaltweaks.glassrenderingprobe"))return;
            try
            {
                if(typeof(VoxelEntity).Assembly.ManifestModule.ModuleVersionId.ToString("D")!=SupportedAssembly)
                    throw new NotSupportedException("Unvalidated game assembly for expanded glass observers.");
                GlassShapeCoverage.PublicationFailed+=OnExpandedPublicationFailure;
                GlassShapeCoverage.CatalogInvalidated+=OnExpandedCatalogInvalidated;
                GlassShapeCoverage.Install(harmony);expandedObservers=true;
                log.LogInfo("Expanded glass bootstrap observers installed before native resource uploads; rendering is not yet active.");
            }
            catch(Exception error)
            {
                expandedUnavailable=true;
                GlassShapeCoverage.PublicationFailed-=OnExpandedPublicationFailure;
                GlassShapeCoverage.CatalogInvalidated-=OnExpandedCatalogInvalidated;
                log.LogWarning("Expanded glass unavailable; cube fallback retained: "+error);
            }
        }
        internal void Install(Harmony harmony)
        {
            if(installed||disposed)return;
            installed=true;
            // Development probes own these static rendering services exclusively.
            // The release contains only the main plugin and never takes this path.
            diagnosticSession=Chainloader.PluginInfos.ContainsKey("local.sevo.graphicaltweaks.glassrenderingprobe");
            if(diagnosticSession){log.LogInfo("Glass development probe owns rendering; main glass controller suspended.");return;}
            try
            {
                if(typeof(VoxelEntity).Assembly.ManifestModule.ModuleVersionId.ToString("D")!=SupportedAssembly)
                    throw new NotSupportedException("This game assembly has not been validated for glass rendering.");
                if(MappingField==null||ClipField==null||ArgsField==null||ShapeField==null)
                    throw new MissingFieldException("The native glass buffer interface changed.");
                instance=this;
                InstallBootstrapObservers(harmony);
                GlassFaceCoverage.Install(harmony);GlassSceneSupport.Install(harmony);
                harmony.Patch(AccessTools.Method(typeof(EvoRenderer.DrawPass),nameof(EvoRenderer.DrawPass.Draw),new[]{typeof(CommandBuffer),typeof(DrawCallParams),typeof(ShaderPass),typeof(bool)}),
                    prefix:new HarmonyMethod(typeof(GlassRenderingController),nameof(BeforeDraw)));
                harmony.Patch(AccessTools.Method(typeof(VoxelRenderingManager),nameof(VoxelRenderingManager.ClearBuffers)),
                    postfix:new HarmonyMethod(typeof(GlassRenderingController),nameof(AfterClear)));
            }
            catch(Exception error){Fault("Glass rendering unavailable; native rendering retained",error);}
            Apply();
        }
        internal void SetOptions(bool sunlightOcclusion,bool seamlessGlass)
        {
            if(glare!=sunlightOcclusion||seams!=seamlessGlass)loggedDraw=false;
            glare=sunlightOcclusion;seams=seamlessGlass;Apply();
        }
#if SEVO_UNITY6
        internal void SetBakedHorizonOptions(bool enabled,int sheen)
        {
            removeBakedHorizon=enabled;
            neutralSheen=BakedGlassHorizonPolicy.NormalizeSheen(sheen);
        }

        private Cubemap BakedHorizonOverride(CommandBuffer buffer)
        {
            if(!removeBakedHorizon||bakedHorizonFaulted||!ReferenceEquals(buffer,VoxelRenderingManager.DebugBuffer))return null;
            try
            {
                var probe=RenderSettings.customReflectionTexture as Cubemap;
                if(probe==null||!BakedGlassHorizonPolicy.Matches(RenderSettings.defaultReflectionMode==DefaultReflectionMode.Custom,
                    probe.name,probe.width,probe.height,probe.mipmapCount,probe.format==TextureFormat.BC6H))return null;
                return GetNeutralProbe(neutralSheen)??(neutralSheen==0?null:GetNeutralProbe(0));
            }
            catch(Exception error)
            {
                bakedHorizonFaulted=true;
                log.LogWarning("Glass baked horizon override unavailable: "+error);
                return null;
            }
        }

        private Cubemap GetNeutralProbe(int choice)
        {
            if(neutralProbeFaults[choice])return null;
            if(neutralProbes[choice]!=null)return neutralProbes[choice];
            Cubemap replacement=null;
            try
            {
                var format=choice==0?TextureFormat.RGBA32:TextureFormat.RGBAHalf;
                replacement=new Cubemap(1,format,false){name="GT neutral glass environment",hideFlags=HideFlags.HideAndDontSave};
                float radiance=BakedGlassHorizonPolicy.SheenRadiance(choice);
                var color=new Color(radiance,radiance,radiance,1f);
                for(int face=0;face<6;face++)replacement.SetPixel((CubemapFace)face,0,0,color);
                replacement.Apply(false,true);
                neutralProbes[choice]=replacement;
                log.LogInfo("Glass neutral probe ready; sheen="+choice);
                return replacement;
            }
            catch(Exception error)
            {
                if(replacement!=null)UnityEngine.Object.Destroy(replacement);
                neutralProbeFaults[choice]=true;
                log.LogWarning("Glass neutral sheen preset unavailable: "+error);
                return null;
            }
        }

        private bool TryBindBakedHorizonOverride(MaterialPropertyBlock properties,Texture replacement)
        {
            try
            {
                properties.SetTexture(ReflectionCube0,replacement);
                properties.SetTexture(ReflectionCube1,replacement);
                return true;
            }
            catch(Exception error)
            {
                neutralProbeFaults[ReferenceEquals(replacement,neutralProbes[0])?0:neutralSheen]=true;
                log.LogWarning("Glass baked horizon override unavailable: "+error);
                return false;
            }
        }
#endif
        internal void Apply()
        {
            if(disposed||!installed||diagnosticSession)return;
            try
            {
                RetireClearedTargets();
                if(!faulted&&(glare||seams)&&bundle==null)LoadAssets();
                bool usable=!faulted&&bundle!=null;
                GlassFaceCoverage.SetEnabled(usable&&seams&&legacyFallbackTrusted);
                if(expandedObservers)GlassShapeCoverage.SetEnabled(usable&&seams&&expandedAssets);
                if(player==null||!player.isActiveAndEnabled)
                    player=Camera.allCameras.FirstOrDefault(camera=>camera.name=="02-PlayerCamera"&&camera.cameraType==CameraType.Game);
                if(capture!=null)
                {
                    if(player!=null)capture.SetVerifiedGameCamera(player);
                    capture.SetEnabled(usable&&glare&&QualitySettings.activeColorSpace==ColorSpace.Linear);
                }
            }
            catch(Exception error){Fault("Glass settings could not be applied; native rendering retained",error);}
        }
        private void LoadAssets()
        {
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11)
                throw new NotSupportedException("The current glass shaders require Direct3D11.");
            if(!File.Exists(bundlePath))throw new FileNotFoundException("Original glass shader bundle is missing",bundlePath);
            bundle=AssetBundle.LoadFromMemory(File.ReadAllBytes(bundlePath));
            if(bundle==null)throw new InvalidOperationException("Glass shader bundle could not be loaded.");
            Shader Find(string name)
            {
                var shader=bundle.LoadAllAssets<Shader>().FirstOrDefault(value=>value.name==name);
                if(shader==null||!shader.isSupported)throw new NotSupportedException("Unsupported glass shader "+name);
                return shader;
            }
            lossMaterial=new Material(Find("GraphicalTweaks/GlassSurfaceLoss")){name="GT glass direct-light loss",hideFlags=HideFlags.HideAndDontSave};
            lossMaterial.SetInt("_GTBRDFMode",-1);lossMaterial.SetInt("_GTLossDiagnosticMode",0);
            compositeMaterial=new Material(Find("GraphicalTweaks/GlassLossComposite")){name="GT glass loss composite",hideFlags=HideFlags.HideAndDontSave};
            capture=new SunShadowCapture(Find("GraphicalTweaks/ShadowDataExport")){ReceiverBias=0};
            var remap=bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(value=>value.name=="GlassSeamRemap");
            if(remap==null)throw new NotSupportedException("Glass face-remap compute asset is missing.");
            GlassGeometryRemap.Configure(remap);
            var expanded=bundle.LoadAllAssets<ComputeShader>().FirstOrDefault(value=>value.name=="GlassShapeRemap");
            if(expanded!=null&&expandedObservers)
            {GlassShapeGeometryRemap.Configure(expanded);expandedAssets=true;}
            else log.LogWarning("General glass compute asset/observers unavailable; existing cube seam path retained.");
            log.LogInfo("Original glass shaders loaded; sunlight BRDF follows the native shadow keyword.");
        }
        private static bool BeforeDraw(EvoRenderer.DrawPass __instance,CommandBuffer cb,ref DrawCallParams drawCallParams,ShaderPass pass,bool isFar)
        {
            var controller=instance;
            if(controller==null||controller.diagnosticSession||(int)pass!=5||__instance.ElementCount<=0||
               drawCallParams.material!=VoxelMaterialResources.MaterialScene||GlobalRenderer.renderer==null||
               !ReferenceEquals(__instance,GlobalRenderer.renderer.pass_Transparent))return true;
            if(controller.expandedRecordingSuppressed)return false;
            if(controller.disposed||controller.faulted)return true;
#if SEVO_UNITY6
            var probeOverride=isFar||drawCallParams.props==null||
                (controller.removeBakedHorizon&&(drawCallParams.props.HasTexture(ReflectionCube0)||drawCallParams.props.HasTexture(ReflectionCube1)))
                ?null:controller.BakedHorizonOverride(cb);
            bool drawNative=(!controller.glare&&!controller.seams)||(!controller.seams&&isFar)||
                controller.Draw(__instance,cb,drawCallParams,isFar,probeOverride);
            if(drawNative&&probeOverride!=null&&!controller.faulted&&drawCallParams.props!=null)
            {
                try
                {
                    controller.propertyCopy.Copy(drawCallParams.props,controller.nativeProperties);
                    if(controller.TryBindBakedHorizonOverride(controller.nativeProperties,probeOverride))
                    {
                        drawCallParams.props=controller.nativeProperties;
                        controller.lastOwnedSubmission=controller.clearGeneration;
                    }
                }
                catch(Exception error)
                {
                    controller.bakedHorizonFaulted=true;
                    controller.log.LogWarning("Glass baked horizon override unavailable: "+error);
                }
            }
            return drawNative;
#else
            if(!controller.glare&&!controller.seams)return true;
            if(!controller.seams&&isFar)return true;
            return controller.Draw(__instance,cb,drawCallParams,isFar);
#endif
        }
        private bool Draw(EvoRenderer.DrawPass pass,CommandBuffer cb,DrawCallParams call,bool far
#if SEVO_UNITY6
            ,Texture probeOverride
#endif
            )
        {
            bool nativeIssued=false,targetChanged=false;
            long recordingEpoch=expandedDetachmentEpoch;
            try
            {
                if(bundle==null||lossMaterial==null||compositeMaterial==null)return true;
                var renderer=GlobalRenderer.renderer;
                var mapping=MappingField.GetValue(renderer) as ComputeBuffer;
                var clip=ClipField.GetValue(renderer) as ComputeBuffer;
                var args=ArgsField.GetValue(renderer) as ComputeBuffer;
                var shape=ShapeField.GetValue(null) as ComputeBuffer;
                if(mapping==null||clip==null||args==null||shape==null)return true;
                int start=pass.GetIndexStart(far),offset=checked(16*pass.passId+(far?432:0));
                var originalMapping=mapping;var originalClip=clip;var originalShape=shape;
                var originalArgs=args;int originalStart=start,originalOffset=offset;
                bool remapped=false;
                bool expandedRemapped=false;
                GlassGeometryRemapBindings remap=default;
                if(seams&&expandedAssets&&GlassShapeCoverage.TryGetResources(shape,out var generalLayout,out var masks,out int maskCount))
                {
                    if(expandedRecordingSuppressed||recordingEpoch!=expandedDetachmentEpoch)return false;
                    // Preparation itself can append compute commands, even when
                    // a later draw fails. Account for their lifetime immediately.
                    lastOwnedSubmission=clearGeneration;lastExpandedSubmission=clearGeneration;
                    expandedRemapped=GlassShapeGeometryRemap.TryPrepare(cb,pass,far,shape,generalLayout,masks,maskCount,out remap);
                }
                // A resource callback may have cleared BOTH native command lists
                // inside preparation. Do not append a partial draw to that frame.
                if(expandedRecordingSuppressed||recordingEpoch!=expandedDetachmentEpoch)return false;
                bool legacyRemapped=false;
                if(seams&&!expandedRemapped&&legacyFallbackTrusted)
                {lastOwnedSubmission=clearGeneration;legacyRemapped=GlassGeometryRemap.TryPrepare(cb,pass,far,out remap);}
                if(expandedRemapped||legacyRemapped)
                {
                    mapping=remap.TriBrickIDMapping;clip=remap.DrawMappingClipZBuffer;shape=remap.ShapeMeshData;
                    args=remap.ArgsBuffer;start=remap.MappingStartIndex;offset=remap.ArgsOffsetBytes;remapped=true;
                }
                var comparisonCandidateDepth=VoxelRenderingManager.rtDepth;
                if(comparison!=null&&!comparisonRecorded&&!comparisonCancelled&&!far&&expandedRemapped&&
                   comparisonCandidateDepth!=null&&comparisonCandidateDepth.IsCreated()&&
                   comparisonCandidateDepth.width==Screen.width&&comparisonCandidateDepth.height==Screen.height&&
                   comparisonCandidateDepth.antiAliasing==1)
                {
                    try
                    {
                        targetChanged=true;
                        var full=new GlassGeometryRemapBindings(originalShape,originalMapping,originalMapping,originalClip,
                            originalArgs,originalStart,pass.ElementCount,originalOffset);
                        RecordComparison(cb,call,full,remap,comparisonCandidateDepth);
                    }
                    catch(Exception error){log.LogWarning("Same-frame glass comparison unavailable: "+error);}
                    finally{RestoreTarget(cb);targetChanged=false;}
                }
                bool correct=glare&&!far&&player!=null&&player.isActiveAndEnabled&&player.allowHDR&&
                    QualitySettings.activeColorSpace==ColorSpace.Linear&&VoxelRenderingManager.rtDepth!=null&&capture!=null;
                string reason=null;
                if(correct&&!GlassSceneSupport.CanRender(renderer,out reason))correct=false;
                if(reason!=lastSupportReason)
                {
                    lastSupportReason=reason;
                    if(reason!=null)log.LogWarning("Glass sunlight correction temporarily uses native rendering: "+reason);
                }
                bool copied=false;
                if(correct)
                {
                    propertyCopy.Copy(call.props,nativeProperties,lossProperties);copied=true;
                    BindNative(lossProperties,call,mapping,clip,remapped?shape:null,start);
                    lossProperties.SetInt("_GTGlassFaceMaskCount",0);
                    correct=capture.BindForUpcomingCamera(lossProperties,player);
                }
                if(!remapped&&!correct)return true;
                // Preserve every native/custom property without adding a shape
                // override to the game's shared property block after disable.
                if(!copied)propertyCopy.Copy(call.props,nativeProperties);
                BindNative(nativeProperties,call,mapping,clip,remapped?shape:null,start);
#if SEVO_UNITY6
                if(probeOverride!=null&&!TryBindBakedHorizonOverride(nativeProperties,probeOverride))
                {
                    // Discard even a partial override while retaining the remap.
                    propertyCopy.Copy(call.props,nativeProperties);
                    BindNative(nativeProperties,call,mapping,clip,remapped?shape:null,start);
                }
#endif
                cb.DrawProceduralIndirect(call.trs,call.material,5,MeshTopology.Triangles,args,offset,nativeProperties);
                nativeIssued=true;lastOwnedSubmission=clearGeneration;
                if(expandedRemapped)lastExpandedSubmission=clearGeneration;
                if(!loggedDraw&&!far&&(!glare||correct))
                {
                    loggedDraw=true;
                    log.LogInfo("Glass native draw integrated: remapped="+remapped+", sunlight capture armed="+correct+
                        ", far="+far+", expanded shapes="+expandedRemapped+", owned remap bytes="+
                        (GlassGeometryRemap.AllocatedBytes+GlassShapeGeometryRemap.AllocatedBytes)+
                        ". GPU stamps still determine whether sunlight loss is sampled.");
                }
                if(!correct)return false;
                var depth=VoxelRenderingManager.rtDepth;
                // Native buffers can lag a resize by a frame. Preserve that
                // frame's original image rather than bind mismatched targets.
                if(depth.width!=Screen.width||depth.height!=Screen.height)return false;
                EnsureLossTarget(depth.width,depth.height);
                cb.SetRenderTarget(lossTarget,NearOpaqueDepth);targetChanged=true;
                cb.SetViewport(new Rect(0,0,depth.width,depth.height));cb.ClearRenderTarget(false,true,Color.clear);
                lossProperties.SetInt("_GTLossDiagnosticMode",0);
                cb.DrawProceduralIndirect(call.trs,lossMaterial,0,MeshTopology.Triangles,args,offset,lossProperties);
                cb.Blit(lossTarget,BuiltinRenderTextureType.CameraTarget,compositeMaterial,0);
                RestoreTarget(cb);targetChanged=false;
                return false;
            }
            catch(Exception error){Fault("Glass draw disabled; native rendering retained",error);return !nativeIssued;}
            finally
            {
                if(targetChanged)
                    try{RestoreTarget(cb);}
                    catch(Exception error){Fault("Glass target restoration failed; subsequent draws use native rendering",error);}
            }
        }
        // Keep the diagnostic callback's closure outside the ordinary Draw
        // method: unarmed rendering must not allocate a display class per pass.
        private void RecordComparison(CommandBuffer cb,DrawCallParams call,GlassGeometryRemapBindings full,
            GlassGeometryRemapBindings mapped,RenderTexture depth)
        {
            comparisonRecorded=true;comparisonGeneration=clearGeneration;comparisonEpoch=expandedDetachmentEpoch;
            comparisonCatalogGeneration=GlassShapeCoverage.CatalogGeneration;comparisonProofEpoch=GlassShapeCoverage.SnapshotEpoch;
            comparisonDepth=depth;comparisonWidth=depth.width;comparisonHeight=depth.height;
            lastOwnedSubmission=clearGeneration;lastExpandedSubmission=clearGeneration;
            if(comparisonKind!=ComparisonKind.Seams)
            {
                RecordCapComparison(cb,call,full,depth);return;
            }
            propertyCopy.Copy(call.props,comparisonNative,comparisonRemapped);
            BindNative(comparisonNative,call,full.TriBrickIDMapping,full.DrawMappingClipZBuffer,full.ShapeMeshData,full.MappingStartIndex);
            BindNative(comparisonRemapped,call,mapped.TriBrickIDMapping,mapped.DrawMappingClipZBuffer,mapped.ShapeMeshData,mapped.MappingStartIndex);
            bool recorded=comparison.Record(cb,comparisonWidth,comparisonHeight,NearOpaqueDepth,(buffer,stage)=>
            {
                buffer.DrawProceduralIndirect(call.trs,call.material,5,MeshTopology.Triangles,
                    stage==0?full.ArgsBuffer:mapped.ArgsBuffer,stage==0?full.ArgsOffsetBytes:mapped.ArgsOffsetBytes,
                    stage==0?comparisonNative:comparisonRemapped);
            });
            log.LogInfo("Same-frame glass comparison recorded="+recorded+"; expanded mapping, native material/args/depth, generation="+comparisonGeneration+
                (recorded?"":"; "+comparison.Error));
        }
        private void RecordCapComparison(CommandBuffer cb,DrawCallParams call,GlassGeometryRemapBindings full,RenderTexture depth)
        {
            if(!GlassShapeCoverage.TryGetCapInterpolationCandidates(out var candidates,out string reason)||
               !GlassCapInterpolationBuffers.TryCreate(full.TriBrickIDMapping,full.DrawMappingClipZBuffer,full.ArgsBuffer,
                   full.MappingStartIndex,full.ArgsOffsetBytes,candidates,out comparisonCapBuffers,out reason))
            {log.LogWarning("CAP_COMPARE unavailable: "+reason);comparison.Abandon();return;}
            var owned=comparisonCapBuffers;
            propertyCopy.Copy(call.props,comparisonNative,comparisonRemapped);
            BindNative(comparisonNative,call,owned.Mapping,owned.Clip,owned.BaseShape,0);
            BindNative(comparisonRemapped,call,owned.Mapping,owned.Clip,owned.SubdivShape,0);
            bool loss=comparisonKind!=ComparisonKind.CapNative;
            if(loss)
            {
                comparisonNative.SetInt("_GTGlassFaceMaskCount",0);comparisonRemapped.SetInt("_GTGlassFaceMaskCount",0);
                int mode=comparisonKind==ComparisonKind.CapNormal?10:comparisonKind==ComparisonKind.CapDirect?1:0;
                comparisonNative.SetInt("_GTLossDiagnosticMode",mode);comparisonRemapped.SetInt("_GTLossDiagnosticMode",mode);
                if(capture==null||player==null||!capture.BindForUpcomingCamera(comparisonNative,player)||
                   !capture.BindForUpcomingCamera(comparisonRemapped,player))
                {log.LogWarning("CAP_COMPARE loss capture unavailable");comparison.Abandon();return;}
            }
            bool recorded=comparison.Record(cb,depth.width,depth.height,NearOpaqueDepth,(buffer,stage)=>
                buffer.DrawProceduralIndirect(call.trs,loss?lossMaterial:call.material,loss?0:5,MeshTopology.Triangles,
                    owned.Args,0,stage==0?comparisonNative:comparisonRemapped));
            log.LogInfo("CAP_COMPARE "+comparisonKind+" recorded="+recorded+"; "+owned.Summary+
                "; generation="+comparisonGeneration+(recorded?"":"; "+comparison.Error));
        }
        private void EnsureLossTarget(int width,int height)
        {
            if(lossTarget!=null&&lossTarget.width==width&&lossTarget.height==height)return;
            if((long)width*height*8>128L*1024*1024)throw new NotSupportedException("Glass loss target exceeds128MiB.");
            if(!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf))throw new NotSupportedException("HDR glass loss target unsupported.");
            var replacement=new RenderTexture(width,height,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear)
            {name="GT blocked glass sunlight",hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,useMipMap=false,antiAliasing=1};
            if(!replacement.Create()){UnityEngine.Object.Destroy(replacement);throw new InvalidOperationException("Glass loss target allocation failed.");}
            if(lossTarget!=null)retiredTargets.Add(new RetiredTarget(lossTarget,clearGeneration));
            lossTarget=replacement;
        }
        private static void BindNative(MaterialPropertyBlock properties,DrawCallParams call,ComputeBuffer mapping,ComputeBuffer clip,ComputeBuffer shape,int start)
        {
            properties.SetInt("ClusterMappingStartIndex",start);
            properties.SetVector("_CamPositionObjectSpace",call.camPosObjectSpace);
            properties.SetBuffer("TriBrickIDMapping",mapping);
            properties.SetBuffer("_DrawMappingClipZBuffer",clip);
            if(shape!=null)properties.SetBuffer("ShapeMeshData",shape);
        }
        private static RenderTargetIdentifier NearOpaqueDepth
        {
            get
            {
#if SEVO_UNITY6
                // The near opaque depth is copied to rtDepth only AFTER the
                // transparent pass. Use the native near target at this stage.
                return BuiltinRenderTextureType.CameraTarget;
#else
                return VoxelRenderingManager.rtDepth.depthBuffer;
#endif
            }
        }
        private static void RestoreTarget(CommandBuffer cb)
        {
            var depth=VoxelRenderingManager.rtDepth;
            if(depth==null)return;
            cb.SetRenderTarget(new[]{new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget),new RenderTargetIdentifier(DistortEffect.DistordRT)},NearOpaqueDepth);
            cb.SetViewport(new Rect(0,0,depth.width,depth.height));
        }
        private static void AfterClear(VoxelRenderingManager __instance)
        {
            var controller=instance;if(controller==null||ManagerField==null||!ReferenceEquals(ManagerField.GetValue(null),__instance))return;
            controller.clearGeneration++;controller.RetireClearedTargets();
            if(!controller.detachingExpanded)controller.expandedRecordingSuppressed=false;
        }
        private void RetireClearedTargets()
        {
            for(int i=retiredTargets.Count-1;i>=0;i--)
                if(clearGeneration>retiredTargets[i].Generation)
                {ReleaseTarget(retiredTargets[i].Target);retiredTargets.RemoveAt(i);}
            for(int i=retiredComparisons.Count-1;i>=0;i--)
                if(retiredComparisons[i].Generation<0||
                   (retiredComparisons[i].Validation.Completed&&clearGeneration>retiredComparisons[i].Generation))
                {retiredComparisons[i].Validation.Dispose();retiredComparisons[i].CapBuffers?.Dispose();retiredComparisons.RemoveAt(i);}
        }
        // Explicit development-probe entry only. Normal rendering never arms it.
        // Both images use one camera/frame, so this does not require moving or
        // freezing the player's camera and never changes a game setting/save.
        internal static IEnumerator CompareSeams(Action<string> trace)=>CompareGlass(trace,ComparisonKind.Seams);
        internal static IEnumerator CompareCapInterpolation(Action<string> trace)=>CompareGlass(trace,ComparisonKind.CapNative);
        internal static IEnumerator CompareCapLossInterpolation(Action<string> trace)=>CompareGlass(trace,ComparisonKind.CapLoss);
        internal static IEnumerator CompareCapNormalInterpolation(Action<string> trace)=>CompareGlass(trace,ComparisonKind.CapNormal);
        internal static IEnumerator CompareCapDirectInterpolation(Action<string> trace)=>CompareGlass(trace,ComparisonKind.CapDirect);
        private static IEnumerator CompareGlass(Action<string> trace,ComparisonKind kind)
        {
            var controller=instance;
            if(trace==null)throw new ArgumentNullException(nameof(trace));
            if(controller==null||controller.disposed||controller.faulted||!controller.seams||
               !controller.expandedAssets||!GlassShapeCoverage.SnapshotCurrent)
            {trace("SEAM_COMPARE not run: expanded glass coverage is not current/enabled");yield break;}
            controller.RetireClearedTargets();
            if(controller.comparison!=null||controller.retiredComparisons.Count!=0)
            {trace("SEAM_COMPARE not run: a previous comparison is active or awaiting safe retirement");yield break;}
            var validation=new GlassSeamValidation{WriteHeatmap=true,ExpectIdentical=kind!=ComparisonKind.Seams,
                RequireColorCoverage=kind==ComparisonKind.CapLoss||kind==ComparisonKind.CapNormal||kind==ComparisonKind.CapDirect};
            controller.comparisonKind=kind;controller.comparisonCapBuffers=null;
            trace("GLASS_COMPARE requested="+kind);
            controller.comparison=validation;controller.comparisonRecorded=false;controller.comparisonCancelled=false;controller.comparisonGeneration=-1;
            float started=Time.realtimeSinceStartup;int frames=0;
            try
            {
                // The probe Update may run after native command recording. Wait
                // for a subsequent eligible draw instead of reading an unarmed RT.
                while(!controller.comparisonRecorded&&!controller.comparisonCancelled&&!controller.disposed&&!controller.faulted&&
                      frames++<120&&Time.realtimeSinceStartup-started<10f)yield return null;
                if(!controller.comparisonRecorded)
                {trace("SEAM_COMPARE inconclusive: no eligible expanded near draw before arming deadline");yield break;}
                bool Valid()=>!controller.comparisonCancelled&&!controller.disposed&&!controller.faulted&&
                    controller.comparisonEpoch==controller.expandedDetachmentEpoch&&
                    controller.comparisonCatalogGeneration==GlassShapeCoverage.CatalogGeneration&&
                    GlassShapeCoverage.SnapshotCurrent&&controller.comparisonProofEpoch==GlassShapeCoverage.SnapshotEpoch&&
                    ReferenceEquals(controller.comparisonDepth,VoxelRenderingManager.rtDepth)&&
                    Screen.width==controller.comparisonWidth&&Screen.height==controller.comparisonHeight;
                yield return validation.Read(trace,Valid);
            }
            finally
            {
                if(!validation.Completed)validation.Abandon();
                if(ReferenceEquals(controller.comparison,validation))controller.comparison=null;
                var capBuffers=controller.comparisonCapBuffers;controller.comparisonCapBuffers=null;
                if(controller.released){validation.Dispose();capBuffers?.Dispose();}
                else
                {
                    controller.retiredComparisons.Add(new RetiredComparison(validation,controller.comparisonGeneration,capBuffers));
                    controller.RetireClearedTargets();
                }
            }
        }
        internal static void CancelSeamComparison(){if(instance!=null)instance.comparisonCancelled=true;}
        private void Fault(string message,Exception error)
        {
            if(faulted)return;faulted=true;
            capture?.SetEnabled(false);
            if(!diagnosticSession)GlassFaceCoverage.SetEnabled(false);
            if(expandedObservers)GlassShapeCoverage.SetEnabled(false);
            log.LogError(message+": "+error);
        }
        private void OnExpandedPublicationFailure()
        {
            if(released||detachingExpanded)return;
            try
            {
                if(!disposed)Fault("Expanded glass mask publication failed; native rendering required",new InvalidOperationException("Mask publication fault"));
            }
            finally{DetachExpandedCommands();}
        }
        private void OnExpandedCatalogInvalidated()
        {
            if(released||detachingExpanded)return;
            bool detach=lastExpandedSubmission>=0||GlassShapeGeometryRemap.AllocatedBytes>0||GlassGeometryRemap.AllocatedBytes>0;
            try
            {
                if(GlassGeometryRemap.AllocatedBytes>0||GlassFaceCoverage.Enabled||
                   GlassFaceCoverage.TrackedCubeCount>0||GlassFaceCoverage.PendingCubeCount>0)
                {
                    // Its source clone and face proofs have a separate lifetime.
                    // Do not bypass a rejected generation through the old cache.
                    legacyFallbackTrusted=false;GlassFaceCoverage.SetEnabled(false);
                }
            }
            finally{if(detach)DetachExpandedCommands();}
        }
        private void DetachExpandedCommands()
        {
            if(detachingExpanded)return;
            detachingExpanded=true;
            expandedRecordingSuppressed=true;expandedDetachmentEpoch++;
            try
            {
                var manager=ManagerField?.GetValue(null) as VoxelRenderingManager;
                if(manager!=null)
                {
                    // This native operation clears BOTH near/far command lists.
                    // A generation/fault transition can omit one frame's draws;
                    // the next native recording uses current resources. Keep all
                    // owned GPU allocations alive until normal controller release.
                    manager.ClearBuffers();lastExpandedSubmission=-1;
                    log.LogWarning("Detached near/far commands after expanded glass generation/publication change; owned buffers retained.");
                }
                else if(lastExpandedSubmission>=0)
                    throw new InvalidOperationException("Native render manager unavailable while expanded commands may remain recorded.");
            }
            finally{detachingExpanded=false;}
        }
        private void OnQuitting()
        {
            quitting=true;if(disposed)Release();
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            if(diagnosticSession){Release();return;}
            capture?.SetEnabled(false);GlassFaceCoverage.SetEnabled(false);
            if(expandedObservers)GlassShapeCoverage.SetEnabled(false);
            if(ReferenceEquals(instance,this))instance=null;
            if(quitting||clearGeneration>lastOwnedSubmission)Release();
            else log.LogInfo("Glass resources retained until application exit because native command buffers may still reference them.");
        }
        private void Release()
        {
            if(released)return;released=true;Application.quitting-=OnQuitting;
            if(diagnosticSession)return;
            propertyCopy.Dispose();
#if SEVO_UNITY6
            foreach(var probe in neutralProbes)
                if(probe!=null)UnityEngine.Object.Destroy(probe);
#endif
            comparison?.Dispose();comparison=null;comparisonCapBuffers?.Dispose();comparisonCapBuffers=null;
            foreach(var retired in retiredComparisons){retired.Validation.Dispose();retired.CapBuffers?.Dispose();}retiredComparisons.Clear();
            GlassShapeCoverage.PublicationFailed-=OnExpandedPublicationFailure;
            GlassShapeCoverage.CatalogInvalidated-=OnExpandedCatalogInvalidated;
            capture?.Dispose();capture=null;GlassGeometryRemap.Dispose();GlassFaceCoverage.Dispose();GlassSceneSupport.Dispose();
            GlassShapeGeometryRemap.Dispose();GlassShapeCoverage.Dispose();
            ReleaseTarget(lossTarget);lossTarget=null;
            foreach(var retired in retiredTargets)ReleaseTarget(retired.Target);retiredTargets.Clear();
            if(lossMaterial!=null)UnityEngine.Object.Destroy(lossMaterial);
            if(compositeMaterial!=null)UnityEngine.Object.Destroy(compositeMaterial);
            if(bundle!=null)bundle.Unload(true);bundle=null;
        }
        private static void ReleaseTarget(RenderTexture target)
        {if(target!=null){target.Release();UnityEngine.Object.Destroy(target);}}
        private readonly struct RetiredTarget
        {
            internal readonly RenderTexture Target;internal readonly long Generation;
            internal RetiredTarget(RenderTexture target,long generation){Target=target;Generation=generation;}
        }
        private readonly struct RetiredComparison
        {
            internal readonly GlassSeamValidation Validation;internal readonly long Generation;
            internal readonly GlassCapInterpolationBuffers CapBuffers;
            internal RetiredComparison(GlassSeamValidation validation,long generation,GlassCapInterpolationBuffers capBuffers)
            {Validation=validation;Generation=generation;CapBuffers=capBuffers;}
        }
    }
}
