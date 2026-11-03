using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Common;
using HarmonyLib;
using Manager;
using UnityEngine;
using VoxelEngine;
using Debug=UnityEngine.Debug;

namespace GraphicalTweaks
{
    // Current-epoch subset proofs can publish before global discovery completes.
    // Lifecycle callbacks never remove/recompute exact polygon geometry.
    // Controller MUST subscribe to PublicationFailed and synchronously detach
    // every owned near/far command on failure before any recorded draw executes.
    internal static partial class GlassShapeCoverage
    {
        private const int MaximumBuildId=4194303;
        private const long MaskBudget=64L*1024*1024;
        private static readonly GlassPolygonCoverageModel model=new GlassPolygonCoverageModel();
        private static readonly GlassNominalCapModel nominal=new GlassNominalCapModel();
        private static readonly GlassMixedCapCoverage mixed=new GlassMixedCapCoverage();
        private static readonly List<int> mixedTouched=new List<int>();
        private static readonly GlassFaceCoveragePending<EntityBricks> pending=new GlassFaceCoveragePending<EntityBricks>();
        private static readonly GlassRebuildGate rebuild=new GlassRebuildGate();
        private static readonly GlassUploadProof<BrickRenderData> proofs=new GlassUploadProof<BrickRenderData>();
        private static readonly GlassMaskUploadJournal journal=new GlassMaskUploadJournal();
        private static readonly Dictionary<EntityBricks,EntityState> entities=new Dictionary<EntityBricks,EntityState>(RefComparer<EntityBricks>.Instance);
        private static readonly Dictionary<int,Owner> owners=new Dictionary<int,Owner>();
        private static readonly List<GlassPolygonCoverageChange> changes=new List<GlassPolygonCoverageChange>();
        private static readonly List<int> newOwners=new List<int>();
        private static readonly List<ComputeBuffer> maskResources=new List<ComputeBuffer>();
        private static readonly Stopwatch timer=new Stopwatch();
        private static readonly FieldInfo nativePool=AccessTools.Field(typeof(BrickRenderDataResources),"datas");
        private static readonly FieldInfo nativeBuildBuffer=AccessTools.Field(typeof(BrickRenderDataResources),"BrickGeoDataBuffer");
        private static readonly FieldInfo nativeShapeBuffer=AccessTools.Field(typeof(BrickMeshResources),"shapeMeshBuffer");
        private static readonly FieldInfo nativeMaterialBuffer=AccessTools.Field(typeof(VoxelMaterialResources),"materialParamsBuffer");
        private static GlassShapeRuntimeCatalog catalog;
        private static GlassShapeVariantLayout layout;
        private static GlassMaskRecord[] upload=new GlassMaskRecord[1];
        private static ComputeBuffer buffer,uploadedShapeBuffer,uploadedMaterialBuffer;
        private static VoxelMaterial[] uploadedMaterials;
        private static int[] usedMaterialIds=Array.Empty<int>();
        private static int lastMaterialCheckFrame=-1;
        private static bool usedMaterialsMatch;
        private static VoxelEntity[] inventory;
        private static int inventoryEntity,inventorySlot,mainThread,lastWorkFrame=-1;
        private static long nextEntity,maskBytes,rebuildEpoch;
        private static bool installed,needsRebuild=true,shapeReady,materialReady,rotationReady;
        private static volatile bool threadViolation;
        private static float nextCatalogAttempt;
        private static string lastCatalogReason;
        internal static bool Enabled {get; private set;}
        internal static bool PublicationFaulted {get;private set;}
        internal static bool NativeProofFaulted=>proofs.Poisoned;
        internal static bool SnapshotCurrent=>Enabled&&!PublicationFaulted&&!NativeProofFaulted&&!needsRebuild&&rebuild.CanPublish(rebuildEpoch);
        internal static long SnapshotEpoch=>SnapshotCurrent?rebuildEpoch:-1;
        internal static event Action PublicationFailed;
        internal static event Action CatalogInvalidated;
        internal static string NotReadyReason=>lastCatalogReason;
        internal static long CatalogGeneration {get;private set;}
        internal static int TrackedCount=>owners.Count;
        internal static int PendingCount=>pending.Count+(needsRebuild||inventory!=null?1:0);
        internal static long AllocatedMaskBytes=>maskBytes;
        internal static int NominalProfilesCount=>catalog?.NominalProfilesCount??0;
        internal static int NominalOwners=>nominal.OwnerCount;
        internal static bool NominalFaulted=>nominal.Faulted;
        internal static string NominalFailureReason=>nominal.FailureReason;
        internal static int MixedProfilesCount=>catalog==null?0:mixed.ProfilesCount;
        internal static int MixedOwners=>mixed.OwnerCount;
        internal static bool MixedFaulted=>mixed.Faulted;
        internal static string MixedFailureReason=>mixed.FailureReason;
        private static uint GetWholeMask(int buildId)=>model.GetMask(buildId)|nominal.GetMask(buildId)|mixed.GetMask(buildId);
        internal static uint GetMask(int buildId)=>GetAuthorizedPartialMask(buildId,GetWholeMask(buildId));

        internal static void Install(Harmony harmony)
        {
            if(installed)return;
            mainThread=Thread.CurrentThread.ManagedThreadId;
            if(nativePool==null||nativeBuildBuffer==null||nativeShapeBuffer==null||nativeMaterialBuffer==null||
               System.Runtime.InteropServices.Marshal.SizeOf(typeof(BrickRenderData))!=44)
                throw new InvalidOperationException("Native glass lifecycle ABI changed.");
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.AddBrickAt),nameof(OnChanging),null);
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.Update),nameof(OnChanging),null);
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.UpdateIds),nameof(OnIdsChanging),null);
            Patch(harmony,typeof(EntityBricks),"UpdateBrickHpStatus",nameof(OnChanging),null);
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.Remove),nameof(OnChanging),null);
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.OnWillUnspawnEntity),nameof(OnEntityRemoving),null);
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.OnDestroy),nameof(OnEntityRemoving),null);
            Patch(harmony,typeof(BrickRenderDataResources),nameof(BrickRenderDataResources.FreeIndex),nameof(OnIndexFree),null);
            Patch(harmony,typeof(BrickRenderDataResources),nameof(BrickRenderDataResources.OnGameReload),nameof(OnBuildReset),nameof(OnBuildResetCompleted));
            Patch(harmony,typeof(BrickRenderDataResources),nameof(BrickRenderDataResources.UnloadResources),nameof(OnBuildReset),null);
            Patch(harmony,typeof(BrickRenderDataResources),nameof(BrickRenderDataResources.AllocateResources),nameof(OnBuildReset),nameof(OnBuildResetCompleted));
            Patch(harmony,typeof(BrickRenderDataResources),nameof(BrickRenderDataResources.SetData),nameof(OnBuildWriting),null);
            Patch(harmony,typeof(BrickRenderDataResources),nameof(BrickRenderDataResources.SetDataImmediate),nameof(OnBuildWriting),nameof(OnImmediateWritten));
            Patch(harmony,typeof(BrickRenderDataResources),nameof(BrickRenderDataResources.PushBufferToGpu),null,nameof(OnBuildPushed));
            // RegisterIndex can replace the global build buffer before the final push.
            Patch(harmony,typeof(BrickRenderDataResources),"CheckForContainerSize",null,nameof(OnBuildIdentity));
            Patch(harmony,typeof(BrickMeshResources),nameof(BrickMeshResources.AllocateResources),nameof(OnShapeReset),nameof(OnShapeUploaded));
            Patch(harmony,typeof(BrickMeshResources),nameof(BrickMeshResources.ReleaseResources),nameof(OnShapeReset),null);
            Patch(harmony,typeof(VoxelMaterialResources),nameof(VoxelMaterialResources.SetMaterialParamDatas),nameof(OnMaterialReset),null);
            Patch(harmony,typeof(VoxelMaterialResources),"LoadBuffers",nameof(OnMaterialReset),nameof(OnMaterialUploaded));
            Patch(harmony,typeof(VoxelMaterialResources),nameof(VoxelMaterialResources.UnloadResources),nameof(OnMaterialReset),null);
            Patch(harmony,typeof(VoxelRotation),nameof(VoxelRotation.BuildIndex),nameof(OnRotationReset),nameof(OnRotationUploaded));
            Patch(harmony,typeof(GameloopManager),nameof(GameloopManager.OnAfterRendering),null,nameof(OnLatePublication));
            installed=true;
        }
        private static void Patch(Harmony h,Type type,string original,string prefix,string postfix)
        {
            var method=AccessTools.Method(type,original);
            if(method==null)throw new MissingMethodException(type.FullName,original);
            h.Patch(method,prefix==null?null:new HarmonyMethod(typeof(GlassShapeCoverage),prefix),
                postfix==null?null:new HarmonyMethod(typeof(GlassShapeCoverage),postfix),finalizer:
                new HarmonyMethod(typeof(GlassShapeCoverage),nameof(OnNativeFailure)));
        }
        internal static void SetEnabled(bool value)
        {
            if(value&&!installed)throw new InvalidOperationException("Install general glass coverage before enabling.");
            if(value&&(PublicationFailed==null||CatalogInvalidated==null))throw new InvalidOperationException("Controller command-detachment handlers for publication failure and catalog invalidation are required before enabling.");
            if(Enabled==value)return;
            Enabled=value&&!PublicationFaulted&&!NativeProofFaulted;
            // Partial faces are part of the seamless option, including a fresh
            // startup and an off/on transition; all native eligibility gates stay.
            partialRenderingEnabled=Enabled;Invalidate();proofs.Clear();
            // Retain an unchanged layout object across toggles to avoid needless
            // host variant generations. Native generation events invalidate it.
            PublishInvalidations();
            GlassShapeGeometryRemap.SetPartialSelectionEnabled(partialRenderingEnabled&&Enabled&&!PublicationFaulted);
        }
        private static bool Eligible(ushort id)=>id==166||id==168||id==314||id==315||id==316||
            id==167||id==118||id==313||id==317||id==207||id==209
#if SEVO_UNITY6
            ||id==GlassPipeEndCapPolicy.BrickId
#endif
            ;
        private static bool MainThread()
        {
            if(Thread.CurrentThread.ManagedThreadId==mainThread)return true;
            threadViolation=true;return false;
        }
        private static bool CanUpdate()=>MainThread()&&Enabled;
        private static void OnChanging(EntityBricks __instance,BrickInstanceData brickData)
        {
            if(CanUpdate()&&(Eligible(brickData.brickId)||
                (entities.TryGetValue(__instance,out var state)&&state.BuildByInstance.ContainsKey(brickData.instanceId))))Guard(Invalidate);
        }
        private static void OnIdsChanging(EntityBricks __instance,int brickInstanceId)
        {if(CanUpdate())Guard(()=>OnChanging(__instance,__instance.GetBrickData(brickInstanceId)));}
        private static void OnEntityRemoving(EntityBricks __instance)
        {
            if(!CanUpdate())return;
            Guard(()=>
            {
                pending.RemoveEntity(__instance);
                // Unrelated entity churn must not restart a global glass rebuild.
                // Unspawned entries encountered later are rejected by TrackNow.
                if(entities.TryGetValue(__instance,out var state)&&state.BuildByInstance.Count>0)Invalidate();
            });
        }
        private static void OnIndexFree(int i)
        {if(!MainThread())return;Guard(()=>{if(owners.ContainsKey(i)||proofs.IsWatched(i))Invalidate();proofs.Forget(i);});}
        private static void OnBuildReset()
        {if(!MainThread())return;Guard(()=>{proofs.Clear();Invalidate();});}
        private static void OnBuildResetCompleted(){if(MainThread())Guard(()=>proofs.ResetTrust());}
        private static void OnBuildWriting(int id,out long __state)
        {
            __state=-1;if(!MainThread())return;
            try{__state=proofs.BeginWrite(id);if(__state>=0||owners.ContainsKey(id))Invalidate();}
            catch(Exception e){Fault(e);}
        }
        private static void OnImmediateWritten(int id,BrickRenderData brickData,long __state)
        {
            if(!MainThread()||__state<0)return;
            Guard(()=>
            {
                if(CheckBuildIdentity())return;
                if(TryReadRecord(id,out var actual)&&actual.Equals(brickData))proofs.CompleteWrite(id,__state,brickData);
            });
        }
        private static bool CheckBuildIdentity()
        {
            var current=(ComputeBuffer)nativeBuildBuffer.GetValue(null);
            bool changed=proofs.ChangeSource(current);
            if(changed)Invalidate();return changed;
        }
        private static void OnBuildIdentity(){if(MainThread())Guard(()=>CheckBuildIdentity());}
        private static void OnBuildPushed()
        {
            if(!MainThread())return;
            Guard(()=>
            {
                CheckBuildIdentity();
                var current=(ComputeBuffer)nativeBuildBuffer.GetValue(null);
                if(current==null||!current.IsValid())return;
                // Native successful Push flushed every dirty CPU record, including
                // the full range after replacement. Read only watched candidates.
                foreach(int id in proofs.PendingIds)
                    if(TryReadRecord(id,out var actual))proofs.ConfirmSnapshot(id,actual);
            });
        }
        private static bool TryReadRecord(int id,out BrickRenderData value)
        {
            value=default;
            var pool=(NativeRecyclableContainer<BrickRenderData>)nativePool.GetValue(null);
            if(!pool.IsCreated||id<0||id>=pool.Length)return false;
            value=pool[id];return true;
        }
        private static void ResetCatalog()
        {CatalogGeneration++;catalog=null;layout=null;usedMaterialIds=Array.Empty<int>();lastMaterialCheckFrame=-1;nextCatalogAttempt=0;Invalidate();ResetPartialContext();
            try{CatalogInvalidated?.Invoke();}catch(Exception e){FailPublication();Debug.LogException(e);}}
        private static void OnShapeReset(){if(MainThread())Guard(()=>{shapeReady=false;uploadedShapeBuffer=null;ResetCatalog();});}
        private static void OnShapeUploaded(){if(MainThread())Guard(()=>{uploadedShapeBuffer=(ComputeBuffer)nativeShapeBuffer.GetValue(null);shapeReady=uploadedShapeBuffer!=null&&uploadedShapeBuffer.IsValid();});}
        private static void OnMaterialReset(){if(MainThread())Guard(()=>{materialReady=false;uploadedMaterials=null;uploadedMaterialBuffer=null;ResetCatalog();});}
        private static void OnMaterialUploaded()
        {
            if(MainThread())Guard(()=>
            {
                // LoadBuffers may be invoked without SetMaterialParamDatas. A
                // new successful upload is a resource generation boundary even
                // when its geometry fields happen to be unchanged.
                if(catalog!=null)ResetCatalog();
                uploadedMaterialBuffer=(ComputeBuffer)nativeMaterialBuffer.GetValue(null);
                uploadedMaterials=(VoxelMaterial[])VoxelMaterialResources.materialParamDatas.Clone();
                materialReady=uploadedMaterialBuffer!=null&&uploadedMaterialBuffer.IsValid();
            });
        }
        private static void OnRotationReset(){if(MainThread())Guard(()=>{rotationReady=false;ResetCatalog();});}
        private static void OnRotationUploaded(){if(MainThread())rotationReady=true;}
        private static bool CatalogInputsReady(ComputeBuffer source)
        {
            if(!shapeReady)return NotReady("waiting for successful native ShapeMeshData upload after Install");
            if(!materialReady)return NotReady("waiting for successful native MaterialParams LoadBuffers upload");
            if(!rotationReady)return NotReady("waiting for successful native VoxelRotation.BuildIndex");
            if(!ReferenceEquals(source,uploadedShapeBuffer)||!ReferenceEquals(source,nativeShapeBuffer.GetValue(null)))return NotReady("native shape source identity changed");
            if(!ReferenceEquals(uploadedMaterialBuffer,nativeMaterialBuffer.GetValue(null)))return NotReady("native material buffer identity changed");
            return catalog==null||ValidateUsedMaterials(false);
        }
        private static bool NotReady(string reason){lastCatalogReason=reason;return false;}
        private static bool SelectUsedMaterials(GlassShapeRuntimeCatalog created)
        {
            var used=new HashSet<int>();
            foreach(var source in created.Sources)
            {
                if(!created.TryGetShape((ushort)source.BrickId,out var descriptor))return false;
                for(int t=0;t<descriptor.ActualTriangles;t++)used.Add(checked((int)descriptor.Records[t*3].materialID));
            }
            if(used.Count==0||used.Count>256)return NotReady("used clear-glass material set exceeds admission bound");
            usedMaterialIds=new int[used.Count];used.CopyTo(usedMaterialIds);lastMaterialCheckFrame=-1;
            return ValidateUsedMaterials(true);
        }
        private static bool ValidateUsedMaterials(bool force)
        {
            if(!force&&lastMaterialCheckFrame==Time.frameCount)return usedMaterialsMatch;
            lastMaterialCheckFrame=Time.frameCount;usedMaterialsMatch=false;
            var current=VoxelMaterialResources.materialParamDatas;
            if(current==null||uploadedMaterials==null)return NotReady("native material table missing");
            // Compare only actual clear-shape geometry/eligibility fields, without
            // boxing whole VoxelMaterial structs or scanning unrelated materials.
            foreach(int id in usedMaterialIds)
            {
                if(id<0||id>=current.Length||id>=uploadedMaterials.Length||id>=VoxelMaterialResources.MaterialCount)
                    return NotReady("used clear-glass material index is unavailable");
                var a=current[id];var b=uploadedMaterials[id];
                if(a.Id!=b.Id||a.AlphaMode!=b.AlphaMode||a.dynDataVert!=b.dynDataVert||a.BorderId!=b.BorderId||
                   a.normalID!=b.normalID||a.buildingOffset!=b.buildingOffset||a.scalingDeformLinkMode!=b.scalingDeformLinkMode||
                   a.sizeUnit!=b.sizeUnit||a.thresholdGeoLow!=b.thresholdGeoLow||a.thresholdGeoHigh!=b.thresholdGeoHigh)
                    return NotReady("used clear-glass geometry material differs from successful native upload");
#if SEVO_UNITY6
                if((id==488||id==489)&&catalog!=null&&catalog.TryGetShape(GlassPipeEndCapPolicy.BrickId,out _)&&!GlassMixedCapCoverage.SameMaterial(a,b))
                    return NotReady("pinned pipe material differs from its admitted native upload");
#endif
            }
            // Mixed nominal interfaces depend on the actual complete clear style,
            // not just deformation. A late style-only change must revoke every
            // recorded proof through the same catalog reset/detachment boundary.
            if(catalog!=null&&!mixed.MaterialsCurrent(catalog,CatalogGeneration,current,uploadedMaterials,VoxelMaterialResources.MaterialCount))
                return NotReady("mixed used-cap material style differs from its admitted successful native upload");
            usedMaterialsMatch=true;return true;
        }
        private static void Invalidate()
        {
            if(!needsRebuild)Array.Clear(upload,0,upload.Length);
            RevokePartialSelections();
            rebuild.Invalidate();needsRebuild=true;journal.RequireFullUpload();
        }
        private static void Guard(Action action)
        {try{action();}catch(Exception e){Fault(e);}}
        private static void Fault(Exception exception)
        {
            Enabled=false;Invalidate();
            Debug.LogWarning("Graphical Tweaks: general glass coverage disabled; native fallback required. "+exception);
        }
        private static void FailPublication()
        {
            bool first=!PublicationFaulted;PublicationFaulted=true;Enabled=false;Invalidate();
            partialRenderingEnabled=false;GlassShapeGeometryRemap.SetPartialSelectionEnabled(false);
            // A retry cannot prove a previously recorded command did not execute.
            // Sticky fault demands controller detachment and Dispose/restart.
            if(first)try{PublicationFailed?.Invoke();}catch(Exception e){Debug.LogException(e);}
        }
        private static Exception OnNativeFailure(Exception __exception)
        {
            if(__exception==null)return null;
            if(MainThread())
            {
                proofs.Poison();Fault(__exception);
                try{PublishInvalidations();}catch(Exception e){Debug.LogException(e);}
            }
            return __exception; // Never swallow or rewrite native failures.
        }
        private static void OnLatePublication()
        {
            if(!MainThread())return;
            if(threadViolation){threadViolation=false;proofs.Poison();Enabled=false;Invalidate();FailPublication();}
            Guard(()=>{if(catalog!=null&&!ValidateUsedMaterials(true))ResetCatalog();});
            PublishInvalidations(); // No inventory, model drain, or positive proof.
        }
        private static void PublishInvalidations()
        {
            if(maskResources.Count==0)return; // Preserve full-upload obligation until allocation.
            try
            {
                if(!journal.Publish(maskResources,r=>r.count,(r,first,count)=>r.SetData(upload,first,first,count)))FailPublication();
            }
            catch(Exception e){FailPublication();Debug.LogException(e);}
        }
        // Called while native near/far commands are recorded. Only this boundary
        // performs exact work; final native callbacks subsequently publish zeros.
        internal static bool TryGetResources(ComputeBuffer source,out GlassShapeVariantLayout result,out ComputeBuffer masks,out int maskCount)
        {
            result=null;masks=null;maskCount=0;
            if(threadViolation){threadViolation=false;proofs.Poison();Enabled=false;Invalidate();FailPublication();}
            if(!Enabled||PublicationFaulted||source==null)return false;
            Guard(()=>
            {
                if(!CatalogInputsReady(source))
                {if(catalog!=null)ResetCatalog();PublishInvalidations();return;}
                if(catalog!=null&&(!ReferenceEquals(source,catalog.SourceBuffer)||source.count!=catalog.SourceCount))ResetCatalog();
                if(catalog==null)
                {
                    if(Time.unscaledTime<nextCatalogAttempt)return;
                    nextCatalogAttempt=Time.unscaledTime+1;
                    if(!GlassShapeRuntimeCatalog.TryCreate(source,out var created,out string reason))
                    {
                        if(lastCatalogReason!=reason){lastCatalogReason=reason;Debug.LogWarning("Graphical Tweaks: general glass catalog not ready: "+reason);}
                        return;
                    }
                    if(!SelectUsedMaterials(created))return;
                    if(!GlassShapeVariantLayout.TryCreate(source.count,created.Sources,256L*1024*1024,out var planned))
                        throw new InvalidOperationException("General glass catalog exceeds validated variant layout/budget.");
                    catalog=created;layout=planned;lastCatalogReason=null;
                    mixed.Bind(created,CatalogGeneration,VoxelMaterialResources.materialParamDatas,uploadedMaterials,VoxelMaterialResources.MaterialCount);
                    ConfigurePartialContext(created,planned);
                }
                if(lastWorkFrame!=Time.frameCount&&(needsRebuild||inventory!=null||pending.Count>0||PartialWorkPending))
                {
                    lastWorkFrame=Time.frameCount;timer.Restart();
                    if(needsRebuild)BeginRebuild();
                    InventoryStep();
                    pending.Process(8,(bricks,instance)=>TrackNow(bricks,instance),
                        ()=>SnapshotCurrent&&GlassCoverageWorkBudget.CanProcess(timer.ElapsedTicks,Stopwatch.Frequency));
                    timer.Stop();
                    CaptureCurrentMasks();
                    if(inventory==null)rebuild.TryComplete(rebuildEpoch,pending.Count);
                }
                PublishInvalidations();
            });
            if(!SnapshotCurrent||catalog==null||owners.Count==0||buffer==null)return false;
            result=layout;masks=buffer;maskCount=upload.Length;return true;
        }
        private static void BeginRebuild()
        {
            model.Clear();nominal.Clear();mixed.BeginScene();mixedTouched.Clear();pending.Clear();entities.Clear();owners.Clear();newOwners.Clear();
            RevokePartialSelections();
            changes.Clear();model.DrainChanges(changes);nominal.DrainChanges(changes);changes.Clear();
            needsRebuild=false;rebuildEpoch=rebuild.Begin();
            // FindObjectsOfType itself is a separate synchronous discovery cost;
            // it runs only here, never in native lifecycle callbacks.
#if SEVO_UNITY6
            inventory=UnityEngine.Object.FindObjectsByType<VoxelEntity>();
#else
            inventory=UnityEngine.Object.FindObjectsOfType<VoxelEntity>();
#endif
            inventoryEntity=0;inventorySlot=1;
        }
        private static void InventoryStep()
        {
            int inspected=0;
            // Reserve the remainder of the shared soft 2ms for exact insertion.
            // 8192 is an absolute safety ceiling, not a throughput target.
            while(SnapshotCurrent&&inventory!=null&&inventoryEntity<inventory.Length&&
                GlassCoverageWorkBudget.CanDiscover(inspected,timer.ElapsedTicks,Stopwatch.Frequency))
            {
                var entity=inventory[inventoryEntity];
                if(entity==null||entity.WillBeUnspawned||entity.Bricks==null||!entity.Bricks.brickDatas.IsCreated||inventorySlot>=entity.Bricks.brickDatas.Length)
                {inventoryEntity++;inventorySlot=1;inspected++;continue;}
                var data=entity.Bricks.GetBrickData(inventorySlot++);inspected++;
                if(data.IsValid&&Eligible(data.brickId)&&data.healthScore==255)pending.Add(entity.Bricks,data.instanceId);
            }
            if(inventory!=null&&inventoryEntity>=inventory.Length)inventory=null;
        }
        private static bool TrackNow(EntityBricks bricks,int instance)
        {
            if(bricks==null||bricks.entity==null||bricks.entity.WillBeUnspawned||!bricks.brickDatas.IsCreated)return true;
            var data=bricks.GetBrickData(instance);
            if(!data.IsValid||!Eligible(data.brickId)||data.healthScore!=255)return true;
            if(GlobalRenderer.renderer==null)return false;
            var ids=bricks.GetBrickIds(data.instanceId);
            if(ids.renderId1==0)return false;
            var render=GlobalRenderer.renderer.GetElement(ids.renderId1);
            if(!render.IsValid||render.dataId<=0)return false;
            if(render.pass!=RenderablePass.transparent||render.transformId!=bricks.entity.transformId||render.dataId>MaximumBuildId)return true;
            if(!catalog.TryGetShape(data.brickId,out var descriptor))return true;
            if(render.clusterStart!=descriptor.SourceStart||(long)render.clusterCount*12!=descriptor.PaddedTriangles||
               render.clusterStartLOD!=0||render.clusterCountLOD!=0)return true;
            if(!proofs.Watch(render.dataId))return true;
            var expected=new BrickRenderData((uint)ids.pixelDataId,(uint)ids.vertexDataId,data,bricks.entity.transformId);
            if(!proofs.Matches(render.dataId,expected))return false;
            if(!catalog.TryBuildGroups(data,out var groups))return true;
            if(!entities.TryGetValue(bricks,out var state))entities.Add(bricks,state=new EntityState(++nextEntity,bricks));
            int buildId=render.dataId;
            if(owners.TryGetValue(buildId,out var previous))previous.State.BuildByInstance.Remove(previous.Instance);
            model.Remove(buildId);nominal.Remove(buildId); // Scheduled, never callback-time recomputation.
            state.BuildByInstance[data.instanceId]=buildId;
            owners[buildId]=new Owner(state,data.instanceId,(uint)descriptor.SourceStart);
            newOwners.Add(buildId);
            model.Upsert(new GlassPolygonCoverageBrick(state.Key,buildId,data.healthScore,groups));
            if(catalog.TryBuildNominalCaps(data,out var caps))nominal.Upsert(state.Key,buildId,data.healthScore,caps);
            if(!mixed.Track(catalog,CatalogGeneration,state.Key,buildId,data))
                throw new InvalidOperationException("Mixed whole-cap coverage revoked: "+mixed.FailureReason);
            return true;
        }
        private static void CaptureCurrentMasks()
        {
            if(!SnapshotCurrent)return;
            BeginPartialCapture();
            if(!GlassMaskSnapshot.Capture(rebuild,rebuildEpoch,model,changes,newOwners,
                id=>owners.TryGetValue(id,out var owner)?owner.SourceStart:(uint?)null,
                ref upload,journal,MaximumBuildId,nominal,mixed,mixedTouched,partialRenderingEnabled?(Func<int,uint,uint,uint>)TransformPartialMask:null))
            {if(SnapshotCurrent)Invalidate();return;}
            if(owners.Count==0)return;
            if(buffer==null||buffer.count<upload.Length)
            {
                long bytes=(long)upload.Length*8;
                if(bytes>MaskBudget-maskBytes)throw new InvalidOperationException("General glass retained mask buffers exceed 64 MiB budget.");
                var replacement=new ComputeBuffer(upload.Length,8);maskResources.Add(replacement);maskBytes+=bytes;buffer=replacement;
            }
        }
        // Controller must first detach ALL recorded draws referencing buffers.
        internal static void Dispose()
        {
            Enabled=false;Invalidate();model.Clear();nominal.Clear();mixed.Reset();mixedTouched.Clear();pending.Clear();entities.Clear();owners.Clear();proofs.Clear();inventory=null;
            partialRenderingEnabled=false;ResetPartialContext();
            changes.Clear();model.DrainChanges(changes);nominal.DrainChanges(changes);changes.Clear();newOwners.Clear();
            foreach(var resource in maskResources)resource.Release();maskResources.Clear();
            buffer=null;upload=new GlassMaskRecord[1];maskBytes=0;catalog=null;layout=null;nextCatalogAttempt=0;lastWorkFrame=-1;PublicationFaulted=false;
        }
        private sealed class EntityState
        {internal readonly long Key;internal readonly EntityBricks Bricks;internal readonly Dictionary<int,int> BuildByInstance=new Dictionary<int,int>();internal EntityState(long key,EntityBricks bricks){Key=key;Bricks=bricks;}}
        private readonly struct Owner
        {internal readonly EntityState State;internal readonly int Instance;internal readonly uint SourceStart;internal Owner(EntityState state,int instance,uint source){State=state;Instance=instance;SourceStart=source;}}
        private sealed class RefComparer<T>:IEqualityComparer<T> where T:class
        {internal static readonly RefComparer<T> Instance=new RefComparer<T>();public bool Equals(T a,T b)=>ReferenceEquals(a,b);public int GetHashCode(T value)=>RuntimeHelpers.GetHashCode(value);}
    }
}
