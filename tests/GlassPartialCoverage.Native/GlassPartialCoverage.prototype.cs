using System;
using System.Collections.Generic;
#if !GLASS_PARTIAL_PUBLICATION_POLICY_ONLY
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using VoxelEngine;
#endif

namespace GraphicalTweaks
{
    internal sealed class GlassPartialPendingWork
    {
        private readonly LinkedList<int> order=new LinkedList<int>();
        private readonly Dictionary<int,LinkedListNode<int>> nodes=new Dictionary<int,LinkedListNode<int>>();
        internal int Count=>nodes.Count;
        internal bool Retry(int id)
        {if(id<1||id>4194303)return false;if(nodes.ContainsKey(id))return true;if(nodes.Count>=4096)return false;nodes.Add(id,order.AddLast(id));return true;}
        internal void Cancel(int id){if(nodes.TryGetValue(id,out var node)){order.Remove(node);nodes.Remove(id);}}
        internal void Take(int maximum,List<int> output)
        {if(output==null)throw new ArgumentNullException(nameof(output));for(int n=0;n<Math.Min(8,maximum)&&order.First!=null;n++){int id=order.First.Value;Cancel(id);output.Add(id);}}
        internal void Clear(){nodes.Clear();order.Clear();}
    }
    internal sealed class GlassPartialCaptureBudget
    {
        private readonly long duration;private long started;private bool active;
        internal int Entered {get;private set;}
        internal GlassPartialCaptureBudget(long frequency){if(frequency<1)throw new ArgumentOutOfRangeException(nameof(frequency));duration=Math.Max(1,frequency/500);}
        internal void Begin(long now){started=now;Entered=0;active=true;}
        internal bool TryEnter(long now){if(!active||Entered>=8||now<started||now-started>=duration)return false;Entered++;return true;}
    }
    internal sealed class GlassPartialSelectionLedger
    {
        private readonly Dictionary<int,Selection> values=new Dictionary<int,Selection>();
        internal int Count=>values.Count;
        internal bool Record(int id,long epoch,GlassPartialGeometryCache cache,uint source,uint full,uint encoded,GlassPartialCacheEntry entry)
        {
            if(id<1||id>4194303||epoch<0||cache==null||!cache.Owns(entry)||entry.SourceStart!=source||entry.FullMask!=full||full>255||
               (encoded&255)!=full||(encoded>>8)!=(uint)entry.Index+1||(encoded>>8)>256||(!values.ContainsKey(id)&&values.Count>=4096))return false;
            values[id]=new Selection(epoch,cache,source,full,encoded,entry);return true;
        }
        internal bool TryRead(int id,long epoch,GlassPartialGeometryCache cache,uint source,uint full,out uint encoded)
        {
            encoded=0;if(!values.TryGetValue(id,out var s)||s.Epoch!=epoch||!ReferenceEquals(s.Cache,cache)||s.Source!=source||s.Full!=full||
                !cache.Owns(s.Entry)||s.Entry.SourceStart!=source||s.Entry.FullMask!=full||(s.Encoded>>8)!=(uint)s.Entry.Index+1)return false;
            encoded=s.Encoded;return true;
        }
        internal void Remove(int id)=>values.Remove(id);
        internal void CopyIds(List<int> output){foreach(int id in values.Keys)output.Add(id);}
        internal void Clear()=>values.Clear();
        private readonly struct Selection
        {internal readonly long Epoch;internal readonly GlassPartialGeometryCache Cache;internal readonly GlassPartialCacheEntry Entry;internal readonly uint Source,Full,Encoded;
         internal Selection(long epoch,GlassPartialGeometryCache cache,uint source,uint full,uint encoded,GlassPartialCacheEntry entry){Epoch=epoch;Cache=cache;Source=source;Full=full;Encoded=encoded;Entry=entry;}}
    }

#if !GLASS_PARTIAL_PUBLICATION_POLICY_ONLY
    internal static partial class GlassShapeCoverage
    {
        private static bool partialRenderingEnabled;
        private static PartialContext partialContext;
        private static string partialFailureReason;
        private static readonly GlassPartialPendingWork partialPending=new GlassPartialPendingWork();
        private static readonly GlassPartialSelectionLedger partialSelected=new GlassPartialSelectionLedger();
        private static readonly GlassPartialCaptureBudget partialBudget=new GlassPartialCaptureBudget(Stopwatch.Frequency);
        private static readonly Dictionary<int,PartialCaptureResult> partialCaptureResults=new Dictionary<int,PartialCaptureResult>();
        private static readonly List<int> partialRetryIds=new List<int>();
        internal static bool PartialRenderingEnabled=>partialRenderingEnabled;
        internal static int PartialCacheEntries=>partialContext?.Cache.Count??0;
        internal static int PartialSelectedOwners=>partialSelected.Count;
        internal static int PartialPendingOwners=>partialPending.Count;
        internal static long PartialRawBytes=>partialContext?.Cache.RawBytes??0;
        internal static string PartialFailureReason=>partialFailureReason??GlassShapeGeometryRemap.PartialFailureReason;
        internal static GlassPartialGeometryCache PartialCacheIdentity=>partialContext?.Cache;
        internal static bool PartialContextCurrent=>IsPartialContextCurrent(partialContext);

        internal static void SetPartialRendering(bool enabled)
        {
            if(Thread.CurrentThread.ManagedThreadId!=mainThread)throw new InvalidOperationException("Partial selection must change on the installed Unity main thread.");
            if(enabled&&(!installed||!Enabled||PublicationFaulted||NativeProofFaulted))throw new InvalidOperationException("Enable current seamless glass before arming the visual prototype.");
            if(partialRenderingEnabled==enabled)return;
            partialRenderingEnabled=enabled;Invalidate();PublishInvalidations();
            GlassShapeGeometryRemap.SetPartialSelectionEnabled(enabled&&Enabled&&!PublicationFaulted);
        }
        private static void RevokePartialSelections()
        {partialSelected.Clear();partialPending.Clear();partialCaptureResults.Clear();partialRetryIds.Clear();}
        private static void ResetPartialContext()
        {
            RevokePartialSelections();partialContext=null;partialFailureReason=null;
            GlassShapeGeometryRemap.ConfigurePartialInputs(null,null,null,null);GlassShapeGeometryRemap.SetPartialSelectionEnabled(false);
        }
        private static void ConfigurePartialContext(GlassShapeRuntimeCatalog current,GlassShapeVariantLayout planned)
        {
            ResetPartialContext();
            if(!GlassShapeGeometryRemap.SupportsPartialCaps)return;
            try
            {
                var sources=new Dictionary<ushort,PartialSource>();var trusted=new List<GlassPartialTailSource>();
                foreach(ushort id in mixed.AdmittedSourceIds(current,CatalogGeneration))
                {
                    if(!mixed.TryGetSource(current,CatalogGeneration,id,out var token)||!current.TryGetShape(id,out var d)||
                       d.ActualTriangles!=token.ActualTriangles||d.PaddedTriangles!=token.PaddedTriangles)continue;
                    var native=(BrickMeshResources.ShapeMeshData[])d.Records.Clone();var raw=new byte[checked(native.Length*124)];
                    var pin=GCHandle.Alloc(native,GCHandleType.Pinned);try{Marshal.Copy(pin.AddrOfPinnedObject(),raw,0,raw.Length);}finally{pin.Free();}
                    string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(raw)).Replace("-","");
                    if(!string.Equals(hash,token.SourceHash,StringComparison.OrdinalIgnoreCase))continue;
                    uint valid=(1u<<d.GroupCount)-1;var labels=(uint[])d.TriangleFaceBits.Clone();
                    var source=new PartialSource(id,d.SourceStart,d.ActualTriangles,d.PaddedTriangles,valid,hash,raw,labels);
                    sources.Add(id,source);trusted.Add(new GlassPartialTailSource((uint)d.SourceStart,d.ActualTriangles,d.PaddedTriangles,valid,hash));
                }
                if(sources.Count==0){partialFailureReason="No currently admitted mixed source supports partial selection.";return;}
                var cache=new GlassPartialGeometryCache(planned.OwnedVertices/3,256,6144,8L*1024*1024);
                var context=new PartialContext(current,planned,cache,CatalogGeneration,current.SourceBuffer,uploadedMaterialBuffer,uploadedMaterials,sources);
                partialContext=context;
                GlassShapeGeometryRemap.ConfigurePartialInputs(planned,cache,Array.AsReadOnly(trusted.ToArray()),()=>IsPartialContextCurrent(context));
                GlassShapeGeometryRemap.SetPartialSelectionEnabled(partialRenderingEnabled&&Enabled&&!PublicationFaulted);
            }
            catch(Exception e){string failure="Partial inputs unavailable: "+e.GetType().Name+": "+e.Message;ResetPartialContext();partialFailureReason=failure;}
        }
        private static bool IsPartialContextCurrent(PartialContext context,bool requireHostSupport=true)
        {
            if(context==null||!ReferenceEquals(partialContext,context)||!SnapshotCurrent||!ReferenceEquals(catalog,context.Catalog)||
               !ReferenceEquals(layout,context.Layout)||CatalogGeneration!=context.Generation||!shapeReady||!materialReady||!rotationReady||
               (requireHostSupport&&!GlassShapeGeometryRemap.SupportsPartialCaps)||!ReferenceEquals(uploadedShapeBuffer,context.Source)||
               !ReferenceEquals(uploadedMaterialBuffer,context.MaterialBuffer)||!ReferenceEquals(uploadedMaterials,context.Materials))return false;
            return context.Source!=null&&context.Source.IsValid()&&context.Source.stride==124&&context.Source.count==context.Catalog.SourceCount&&
                ReferenceEquals(context.Source,nativeShapeBuffer.GetValue(null))&&context.MaterialBuffer!=null&&context.MaterialBuffer.IsValid()&&context.MaterialBuffer.stride==132&&
                ReferenceEquals(context.MaterialBuffer,nativeMaterialBuffer.GetValue(null))&&
                mixed.MaterialsCurrent(context.Catalog,context.Generation,VoxelMaterialResources.materialParamDatas,uploadedMaterials,VoxelMaterialResources.MaterialCount);
        }
        private static bool PartialWorkPending=>partialRenderingEnabled&&(partialPending.Count>0||
            (partialSelected.Count>0&&(partialContext==null||!IsPartialContextCurrent(partialContext)||GlassShapeGeometryRemap.PartialPermanentlyUnavailable(partialContext.Cache))));
        private static void BeginPartialCapture()
        {
            partialCaptureResults.Clear();partialBudget.Begin(Stopwatch.GetTimestamp());partialRetryIds.Clear();
            var context=partialContext;
            if(!partialRenderingEnabled||context==null){partialPending.Clear();return;}
            if(GlassShapeGeometryRemap.PartialPermanentlyUnavailable(context.Cache))
            {
                partialSelected.CopyIds(newOwners);partialSelected.Clear();partialPending.Clear();return;
            }
            if(!IsPartialContextCurrent(context))
            {
                // Retire a lost proof once. Keeping the stale context would
                // trigger a full world rebuild on every otherwise idle frame.
                ResetPartialContext();partialFailureReason="Partial source/resource proof became stale; waiting for a new native catalog.";Invalidate();return;
            }
            partialPending.Take(8,partialRetryIds);newOwners.AddRange(partialRetryIds);partialRetryIds.Clear();
        }
        private static uint GetAuthorizedPartialMask(int id,uint whole)
        {
            var context=partialContext;
            if(!partialRenderingEnabled||context==null||!IsPartialContextCurrent(context)||GlassShapeGeometryRemap.PartialPermanentlyUnavailable(context.Cache)||
               !owners.TryGetValue(id,out var owner)||!TryPartialOwner(context,SnapshotEpoch,id,owner.SourceStart,out _,out _))return whole;
            return partialSelected.TryRead(id,SnapshotEpoch,context.Cache,owner.SourceStart,whole,out uint encoded)?encoded:whole;
        }
        private static uint TransformPartialMask(int id,uint whole,uint sourceStart)
        {
            var context=partialContext;long epoch=SnapshotEpoch;
            bool uploadStarted=false;
            try{return ComputePartialMask(id,whole,sourceStart,context,epoch,ref uploadStarted);}
            catch(Exception e)
            {
                partialFailureReason="Partial selection failed: "+e.GetType().Name+": "+e.Message;
                if(uploadStarted&&context!=null&&partialRenderingEnabled&&SnapshotEpoch==epoch&&ReferenceEquals(partialContext,context)&&
                   ReferenceEquals(catalog,context.Catalog)&&CatalogGeneration==context.Generation)FailPublication();
                throw;
            }
        }
        private static uint ComputePartialMask(int id,uint whole,uint sourceStart,PartialContext context,long epoch,ref bool uploadStarted)
        {
            if(partialCaptureResults.TryGetValue(id,out var old)&&old.Epoch==epoch&&ReferenceEquals(old.Context,context)&&old.Source==sourceStart&&old.Full==whole)
                return old.Encoded==whole?whole:GetAuthorizedPartialMask(id,whole);
            partialSelected.Remove(id);partialPending.Cancel(id);
            uint Remember(uint value){partialCaptureResults[id]=new PartialCaptureResult(context,epoch,sourceStart,whole,value);return value;}
            if(!partialRenderingEnabled||context==null||SnapshotEpoch!=epoch||!ReferenceEquals(catalog,context.Catalog)||
               !mixed.TryGetInstance(context.Catalog,context.Generation,id,out _))return Remember(whole);
            if(GlassShapeGeometryRemap.PartialPermanentlyUnavailable(context.Cache)){partialFailureReason=GlassShapeGeometryRemap.PartialFailureReason;return Remember(whole);}
            if(!partialBudget.TryEnter(Stopwatch.GetTimestamp())){partialPending.Retry(id);return Remember(whole);}
            if(!TryPartialOwner(context,epoch,id,sourceStart,out var instance,out var source))return Remember(whole);
            if(GlassShapeGeometryRemap.PartialPermanentlyUnavailable(context.Cache)){partialFailureReason=GlassShapeGeometryRemap.PartialFailureReason;return Remember(whole);}
            if(!GlassShapeGeometryRemap.PartialResourcesReady(context.Cache))
            {if(OperationCurrent(context,epoch,id,sourceStart,whole,instance)&&!GlassShapeGeometryRemap.PartialPermanentlyUnavailable(context.Cache))partialPending.Retry(id);return Remember(whole);}
            var edits=new List<GlassNativeTriangleEdit>();
            foreach(var group in instance.Groups)
            {
                if(group.HasCollapsedInputs||(whole&(1u<<group.Bit))!=0)continue;
                if(!mixed.TryGetCapRemainder(context.Catalog,context.Generation,id,group.Bit,out var remainder))
                {partialFailureReason="Current exact remainder query exhausted its bounds or rejected the group.";return Remember(whole);}
                foreach(var triangle in remainder.Triangles)if(triangle.Changed)
                {
                    if(triangle.TriangleIndex<0||triangle.TriangleIndex>=group.NativeTriangleIndices.Count)return Remember(whole);
                    int native=group.NativeTriangleIndices[triangle.TriangleIndex];
                    if(native<0||native>=source.Actual||source.Labels[native]!=(1u<<group.Bit))return Remember(whole);
                    edits.Add(new GlassNativeTriangleEdit(native,triangle.Triangles));
                }
            }
            if(edits.Count==0||!OperationCurrent(context,epoch,id,sourceStart,whole,instance))return Remember(whole);
            if(!context.Cache.TryGetOrAdd(source.Raw,source.Start,source.Actual,source.Padded,whole,source.Labels,edits,true,out var entry,out var reason))
            {partialFailureReason=reason;return Remember(whole);}
            if(!OperationCurrent(context,epoch,id,sourceStart,whole,instance))return Remember(whole);
            if(!GlassShapeGeometryRemap.PartialResourcesReady(context.Cache))
            {if(OperationCurrent(context,epoch,id,sourceStart,whole,instance)&&!GlassShapeGeometryRemap.PartialPermanentlyUnavailable(context.Cache))partialPending.Retry(id);return Remember(whole);}
            uploadStarted=true;
            if(!GlassShapeGeometryRemap.TryUploadPartial(context.Cache,entry,out uint encoded,out reason))
            {
                partialFailureReason=reason;
                // Host failure itself must not disguise a stable-owner upload
                // error as an ordinary source/mode epoch revocation.
                if(OperationCurrent(context,epoch,id,sourceStart,whole,instance,false))FailPublication();
                return Remember(whole);
            }
            if(!OperationCurrent(context,epoch,id,sourceStart,whole,instance))return Remember(whole);
            if(!partialSelected.Record(id,epoch,context.Cache,sourceStart,whole,encoded,entry))
            {partialFailureReason="Uploaded partial entry did not match immutable selection proof.";FailPublication();return Remember(whole);}
            partialFailureReason=null;return Remember(encoded);
        }
        private static bool OperationCurrent(PartialContext context,long epoch,int id,uint source,uint whole,GlassMixedCapInstance instance,bool requireHostSupport=true)
            =>partialRenderingEnabled&&SnapshotEpoch==epoch&&IsPartialContextCurrent(context,requireHostSupport)&&GetWholeMask(id)==whole&&
                TryPartialOwner(context,epoch,id,source,out var now,out _,requireHostSupport)&&ReferenceEquals(now,instance);
        private static bool TryPartialOwner(PartialContext context,long epoch,int id,uint sourceStart,out GlassMixedCapInstance instance,out PartialSource source,bool requireHostSupport=true)
        {
            instance=null;source=null;
            if(Thread.CurrentThread.ManagedThreadId!=mainThread||epoch<0||SnapshotEpoch!=epoch||!IsPartialContextCurrent(context,requireHostSupport)||
               !owners.TryGetValue(id,out var owner)||owner.SourceStart!=sourceStart||owner.State.Bricks==null)return false;
            var bricks=owner.State.Bricks;
            if(bricks.entity==null||bricks.entity.WillBeUnspawned||!bricks.brickDatas.IsCreated||owner.Instance<1||owner.Instance>=bricks.brickDatas.Length)return false;
            var data=bricks.GetBrickData(owner.Instance);var scale=data.Scale;
            if(!data.IsValid||data.instanceId!=owner.Instance||data.healthScore!=255||scale.x!=0||scale.y!=0||scale.z!=0||
               !context.Sources.TryGetValue(data.brickId,out source)||source.Start!=sourceStart||!context.Catalog.TryGetShape(data.brickId,out var descriptor))return false;
            var renderer=GlobalRenderer.renderer;if(renderer==null)return false;var ids=bricks.GetBrickIds(owner.Instance);if(ids.renderId1==0)return false;
            var render=renderer.GetElement(ids.renderId1);var build=(ComputeBuffer)nativeBuildBuffer.GetValue(null);
            if(!render.IsValid||render.dataId!=id||id<1||id>MaximumBuildId||render.pass!=RenderablePass.transparent||render.transformId!=bricks.entity.transformId||
               render.clusterStart!=source.Start||descriptor.SourceStart!=source.Start||(long)render.clusterCount*12!=source.Padded||
               render.clusterStartLOD!=0||render.clusterCountLOD!=0||build==null||!build.IsValid()||build.stride!=44||id>=build.count)return false;
            var expected=new BrickRenderData((uint)ids.pixelDataId,(uint)ids.vertexDataId,data,bricks.entity.transformId);
            return proofs.Matches(id,expected)&&TryReadRecord(id,out var actual)&&actual.Equals(expected)&&
                mixed.TryGetInstance(context.Catalog,context.Generation,id,out instance)&&instance.Entity==owner.State.Key&&instance.BrickId==data.brickId;
        }
        private sealed class PartialSource
        {
            internal readonly ushort Id;internal readonly int Start,Actual,Padded;internal readonly uint ValidMask;internal readonly string Hash;internal readonly byte[] Raw;internal readonly uint[] Labels;
            internal PartialSource(ushort id,int start,int actual,int padded,uint mask,string hash,byte[] raw,uint[] labels){Id=id;Start=start;Actual=actual;Padded=padded;ValidMask=mask;Hash=hash;Raw=raw;Labels=labels;}
        }
        private sealed class PartialContext
        {
            internal readonly GlassShapeRuntimeCatalog Catalog;internal readonly GlassShapeVariantLayout Layout;internal readonly GlassPartialGeometryCache Cache;
            internal readonly long Generation;internal readonly ComputeBuffer Source,MaterialBuffer;internal readonly VoxelMaterial[] Materials;internal readonly Dictionary<ushort,PartialSource> Sources;
            internal PartialContext(GlassShapeRuntimeCatalog catalog,GlassShapeVariantLayout layout,GlassPartialGeometryCache cache,long generation,ComputeBuffer source,ComputeBuffer material,VoxelMaterial[] materials,Dictionary<ushort,PartialSource> sources)
            {Catalog=catalog;Layout=layout;Cache=cache;Generation=generation;Source=source;MaterialBuffer=material;Materials=materials;Sources=sources;}
        }
        private readonly struct PartialCaptureResult
        {
            internal readonly PartialContext Context;internal readonly long Epoch;internal readonly uint Source,Full,Encoded;
            internal PartialCaptureResult(PartialContext context,long epoch,uint source,uint full,uint encoded){Context=context;Epoch=epoch;Source=source;Full=full;Encoded=encoded;}
        }
    }
#endif
}
