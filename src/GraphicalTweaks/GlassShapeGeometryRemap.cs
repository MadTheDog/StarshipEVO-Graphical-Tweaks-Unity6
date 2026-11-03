using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
#if !GLASS_PARTIAL_HOST_POLICY_ONLY
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using VoxelEngine;
#endif

namespace GraphicalTweaks
{
#if !GLASS_PARTIAL_HOST_POLICY_ONLY
    // General-shape host, deliberately unconnected until the geometry/contact
    // adapter and lifecycle owner pass their own native acceptance. All writes
    // target owned buffers; source records and native draw arguments are read-only.
    internal static class GlassShapeGeometryRemap
    {
        // This helper's owned resources only; the coverage owner separately
        // bounds its shared masks and retired mask buffers.
        private const long MaximumBytes=256L*1024*1024;
        private const int DispatchLimit=65535*64;
        private static readonly FieldInfo RendererField=AccessTools.Field(typeof(EvoRenderer.DrawPass),"evoRenderer");
        private static readonly FieldInfo MappingField=AccessTools.Field(typeof(EvoRenderer),"drawMappingBuffer");
        private static readonly FieldInfo ClipField=AccessTools.Field(typeof(EvoRenderer),"drawMappingClipZBuffer");
        private static readonly FieldInfo ArgsField=AccessTools.Field(typeof(EvoRenderer),"argsBuffer");
        private static readonly List<ComputeBuffer> owned=new List<ComputeBuffer>();
        private static readonly List<GlassPartialTail> retainedTails=new List<GlassPartialTail>();
        private static readonly Dictionary<EvoRenderer,Mapping[]> mappings=new Dictionary<EvoRenderer,Mapping[]>();
        private static readonly GlassPartialHostSelection partialSelection=new GlassPartialHostSelection();
        private static ComputeShader shader;
        private static int copyKernel,variantKernel,remapKernel,partialKernel=-1;
        private static ComputeBuffer nativeShape,shape,triangleBits,clusters;
        private static GlassShapeVariantLayout layout;
        private static PartialRequest partialRequest,shapeRequest,failedPartialRequest;
        private static PartialGeneration partialGeneration;
        private static string partialFailureReason;
        private static long allocatedBytes,partialAllocatedBytes;
        private static bool faulted,initializing,checkingPartial;
        internal static long AllocatedBytes=>allocatedBytes;
        internal static long PartialAllocatedBytes=>partialAllocatedBytes;
        internal static bool Faulted=>faulted;
        internal static bool SupportsPartialCaps=>shader!=null&&partialKernel>=0&&!faulted;
        internal static string PartialFailureReason=>partialFailureReason??
            (shader!=null&&partialKernel<0?"Compatible partial-cap V2 kernel is unavailable; V1 remains supported.":null);
        // Typed publisher boundary. Do not invoke the lifetime predicate here:
        // transient staleness/first-draw initialization is not permanent failure.
        internal static bool PartialPermanentlyUnavailable(GlassPartialGeometryCache cache)
        {
            var request=partialRequest;
            return !SupportsPartialCaps||cache==null||request==null||!ReferenceEquals(request.Cache,cache)||
                request.InputFailure!=null||ReferenceEquals(failedPartialRequest,request);
        }

        internal static void Configure(ComputeShader value)
        {
            if(value==null)throw new ArgumentNullException(nameof(value));
            if(shader!=null&&!ReferenceEquals(shader,value)&&allocatedBytes!=0)
                throw new InvalidOperationException("Detach general glass command buffers and dispose before changing shaders.");
            faulted=true;
            // An older cube-only bundle cannot satisfy this ABI.
            int copy=value.FindKernel("GTShapeCopyNativeV1");
            int variants=value.FindKernel("GTShapeBuildVariantsV1");
            int remap=value.FindKernel("GTShapeRemapCompactV1");
            if(copy<0||variants<0||remap<0)throw new InvalidOperationException("General glass remap kernels are missing.");
            // Optional discovery must not reject the supported V1 bundle.
            int partial=value.HasKernel("GTShapeRemapCompactV2")?value.FindKernel("GTShapeRemapCompactV2"):-1;
            shader=value;copyKernel=copy;variantKernel=variants;remapKernel=remap;partialKernel=partial;faulted=false;
            if(partial<0)partialFailureReason="Compatible partial-cap V2 kernel is unavailable; V1 remains supported.";
        }

        // No GPU work or disposal here. The lifecycle owner must first revoke
        // published masks/detach commands before changing source generations.
        internal static void ConfigurePartialInputs(GlassShapeVariantLayout requested,GlassPartialGeometryCache cache,
            IReadOnlyList<GlassPartialTailSource> sources,Func<bool> current)
        {
            if(cache==null)
            {
                partialRequest=null;failedPartialRequest=null;partialFailureReason=null;partialSelection.SetRequest(null);return;
            }
            var next=new PartialRequest(requested,cache,sources,current);
            if(next.Same(partialRequest))return;
            partialRequest=next;failedPartialRequest=null;partialFailureReason=next.InputFailure;partialSelection.SetRequest(next);
        }
        internal static void SetPartialSelectionEnabled(bool enabled)=>partialSelection.SetEnabled(enabled);

        // Readiness deliberately excludes the UI mode. Tail resources can be
        // initialized once while selection remains off across world-mask epochs.
        internal static bool PartialResourcesReady(GlassPartialGeometryCache cache)
        {
            if(checkingPartial)return false;
            var request=partialRequest;
            checkingPartial=true;
            try
            {
                var generation=partialGeneration;
                if(!SupportsPartialCaps||cache==null||request==null||!ReferenceEquals(request.Cache,cache)||
                    ReferenceEquals(failedPartialRequest,request)||generation==null||!ReferenceEquals(generation.Request,request)||
                    !ReferenceEquals(generation.Shape,shape)||!ReferenceEquals(generation.Source,nativeShape)||
                    !ReferenceEquals(layout,request.Layout)||!ReferenceEquals(generation.Tail.Cache,cache)||!RequestCurrent(request))return false;
                RequirePartialBuffers(generation);
                if(triangleBits==null||!triangleBits.IsValid()||triangleBits.stride!=4||triangleBits.count!=request.Layout.SourceVertices/3||
                    clusters==null||!clusters.IsValid()||clusters.stride!=16||clusters.count!=request.Layout.SourceVertices/3)
                    throw new InvalidOperationException("Actual partial static face/cluster lookup resource changed.");
                return ReferenceEquals(partialGeneration,generation)&&RequestCurrent(request)&&ReferenceEquals(partialGeneration,generation);
            }
            catch(Exception exception)
            {
                string failure="Partial resources unavailable: "+exception.GetType().Name+": "+exception.Message;
                if(request!=null&&ReferenceEquals(partialRequest,request))FailPartial(request,failure);
                return false;
            }
            finally{checkingPartial=false;}
        }
        internal static bool TryUploadPartial(GlassPartialGeometryCache cache,GlassPartialCacheEntry entry,
            out uint encodedMask,out string reason)
        {
            var request=partialRequest;var generation=partialGeneration;
            encodedMask=0;
            if(!SupportsPartialCaps){reason=PartialFailureReason??"Partial V2 resources are unavailable or the remap host faulted.";return false;}
            if(request==null||cache==null||!ReferenceEquals(request.Cache,cache))
            {reason="Partial cache request is not current.";return false;}
            if(ReferenceEquals(failedPartialRequest,request))
            {reason=partialFailureReason??"Partial resources are permanently unavailable for this request; V1 remains available.";return false;}
            if(generation==null||!ReferenceEquals(generation.Request,request))
            {reason="Partial resources are not initialized; wait for a supported native draw.";return false;}
            bool accepted=partialSelection.TryAuthorize(request,
                ()=>PartialResourcesReady(cache)&&ReferenceEquals(partialGeneration,generation),
                (out uint code,out string failure)=>
                {
                    if(generation==null){code=0;failure="Partial resources are not initialized.";return false;}
                    return generation.Tail.TryUpload(entry,out code,out failure);
                },out encodedMask,out reason);
            if(!accepted)
            {
                if(ReferenceEquals(failedPartialRequest,request)&&partialFailureReason!=null)reason=partialFailureReason;
                else partialFailureReason=reason;
                return false;
            }
            partialFailureReason=null;return true;
        }

        internal static bool TryPrepare(CommandBuffer cb,EvoRenderer.DrawPass pass,bool isFar,
            ComputeBuffer source,GlassShapeVariantLayout requestedLayout,ComputeBuffer masks,int maskCount,
            out GlassGeometryRemapBindings bindings)
        {
            bindings=default;
            if(cb==null||pass==null||shader==null||faulted||pass.ElementCount<=0||
               source==null||requestedLayout==null||masks==null||maskCount<=0||!SystemInfo.supportsComputeShaders)return false;
            try
            {
                if(RendererField==null||MappingField==null||ClipField==null||ArgsField==null)
                    throw new MissingFieldException("General glass native draw interface changed.");
                var renderer=RendererField.GetValue(pass) as EvoRenderer;
                if(renderer==null||!ReferenceEquals(renderer,GlobalRenderer.renderer)||
                   !ReferenceEquals(pass,renderer.pass_Transparent))return false;
                var nativeMapping=MappingField.GetValue(renderer) as ComputeBuffer;
                var nativeClip=ClipField.GetValue(renderer) as ComputeBuffer;
                var args=ArgsField.GetValue(renderer) as ComputeBuffer;
                if(nativeMapping==null||nativeClip==null||args==null)return false;
                if(!source.IsValid()||!masks.IsValid()||!nativeMapping.IsValid()||!nativeClip.IsValid()||!args.IsValid()||
                   source.stride!=124||source.count!=requestedLayout.SourceVertices||masks.stride!=8||maskCount>masks.count||
                   nativeMapping.stride!=8||nativeClip.stride!=4||args.stride!=4)
                    throw new InvalidOperationException("General glass source/mask/draw buffer layout changed.");
                int start=pass.GetIndexStart(isFar),count=pass.ElementCount;
                int argsOffset=checked(16*pass.passId+(isFar?432:0));
                if(start<0||(long)start+count>nativeMapping.count||(long)start+count>nativeClip.count||
                   argsOffset<0||(long)argsOffset+16>(long)args.count*4)
                    throw new InvalidOperationException("General glass draw range exceeds native buffers.");
                if(!EnsureShape(source,requestedLayout))return false;
                Mapping destination=EnsureMapping(renderer,count,isFar);
                RequireOwnedBuffers(destination,count);
                int kernel=remapKernel;var selectedRequest=partialRequest;var selectedGeneration=partialGeneration;object selectionTicket;
                bool usePartial=partialSelection.TryCapture(selectedRequest,out selectionTicket)&&selectedRequest!=null&&
                    PartialResourcesReady(selectedRequest.Cache)&&ReferenceEquals(partialGeneration,selectedGeneration)&&
                    partialSelection.StillAuthorized(selectedRequest,selectionTicket);
                if(usePartial)
                {
                    RequirePartialBuffers(selectedGeneration);kernel=partialKernel;
                    cb.SetComputeBufferParam(shader,kernel,"_GTShapeCustomVariants",selectedGeneration.Tail.Descriptors);
                    cb.SetComputeIntParam(shader,"_GTShapeCustomDescriptorCount",selectedGeneration.Tail.UploadedCount);
                    cb.SetComputeIntParam(shader,"_GTShapeCustomTailTriangleStart",selectedGeneration.Tail.TailTriangleStart);
                }
                BindGeometry(cb,kernel);
                cb.SetComputeBufferParam(shader,kernel,"_GTShapeClusterLookup",clusters);
                cb.SetComputeBufferParam(shader,kernel,"_GTShapeBuildMasks",masks);
                cb.SetComputeBufferParam(shader,kernel,"_GTShapeNativeMapping",nativeMapping);
                cb.SetComputeBufferParam(shader,kernel,"_GTShapeNativeClipBits",nativeClip);
                cb.SetComputeBufferParam(shader,kernel,"_GTShapeOwnedMapping",destination.Ids);
                cb.SetComputeBufferParam(shader,kernel,"_GTShapeOwnedClipBits",destination.Clip);
                cb.SetComputeIntParam(shader,"_GTShapeBuildMaskCount",maskCount);
                cb.SetComputeIntParam(shader,"_GTShapeLookupCount",clusters.count);
                for(int offset=0;offset<count;)
                {
                    int batch=Math.Min(count-offset,DispatchLimit);
                    cb.SetComputeIntParam(shader,"_GTShapeMappingStart",start+offset);
                    cb.SetComputeIntParam(shader,"_GTShapeMappingOutputStart",offset);
                    cb.SetComputeIntParam(shader,"_GTShapeMappingCount",batch);
                    if(usePartial&&!SelectionCurrent(selectedRequest,selectedGeneration,selectionTicket))return false;
                    cb.DispatchCompute(shader,kernel,(batch+63)/64,1,1);
                    offset+=batch;
                }
                if(usePartial&&!SelectionCurrent(selectedRequest,selectedGeneration,selectionTicket))return false;
                bindings=new GlassGeometryRemapBindings(shape,destination.Ids,nativeMapping,destination.Clip,args,0,count,argsOffset);
                return true;
            }
            catch(Exception exception)
            {
                faulted=true;
                Debug.LogWarning("Graphical Tweaks: general glass remap disabled; native draw retained. "+exception);
                return false;
            }
        }

        private static bool EnsureShape(ComputeBuffer source,GlassShapeVariantLayout requested)
        {
            if(initializing)return false;
            initializing=true;
            try
            {
            var request=SupportsPartialCaps&&partialRequest!=null&&ReferenceEquals(partialRequest.Layout,requested)?partialRequest:null;
            if(request!=null&&!RequestCurrent(request))return false;
            if(ReferenceEquals(source,nativeShape)&&ReferenceEquals(requested,layout)&&shape!=null&&ReferenceEquals(shapeRequest,request))return true;
            bool extend=false;int shapeVertices=requested.OwnedVertices;long tailBytes=0,descriptorBytes=0;
            if(request!=null&&!ReferenceEquals(failedPartialRequest,request))
            {
                long expanded=(long)requested.OwnedVertices+3L*request.Cache.MaximumTailTriangles;
                descriptorBytes=16L*Math.Max(1,request.Cache.MaximumEntries);
                tailBytes=3L*request.Cache.MaximumTailTriangles*124;
                string unavailable=null;
                if(request.InputFailure!=null||expanded>int.MaxValue||
                    !GlassPartialTailPolicy.TryCreate((int)expanded,124,requested,request.Cache,request.Sources,
                        ()=>RequestCurrent(request),out _,out unavailable))
                {
                    if(!RequestCurrent(request))return false;
                    FailPartial(request,request.InputFailure??unavailable??"Partial layout bounds are unavailable.");
                }
                else if(requested.StaticBytes+tailBytes+descriptorBytes>MaximumBytes-allocatedBytes)
                    FailPartial(request,"Partial tail and descriptors exceed the retained 256MiB resource budget; V1 remains available.");
                else{extend=true;shapeVertices=(int)expanded;}
            }
            RequireBudget(requested.StaticBytes+(extend?tailBytes+descriptorBytes:0));
            // Keep former resources alive until the controller has detached all
            // recorded buffers; retained allocations also consume the budget.
            var replacementShape=Allocate(shapeVertices,124);
            if(extend)partialAllocatedBytes+=tailBytes;
            var replacementBits=Allocate(requested.TriangleFaceBits.Length,4);
            var replacementClusters=Allocate(requested.Clusters.Length,16);
            replacementBits.SetData(requested.TriangleFaceBits);
            replacementClusters.SetData(requested.Clusters);
            using(var initialize=new CommandBuffer{name="Graphical Tweaks general glass variants"})
            {
                BindGeometry(initialize,copyKernel,source,replacementShape,requested);
                DispatchVertices(initialize,copyKernel,requested.SourceVertices);
                BindGeometry(initialize,variantKernel,source,replacementShape,requested);
                initialize.SetComputeBufferParam(shader,variantKernel,"_GTShapeTriangleFaceBits",replacementBits);
                foreach(var allocation in requested.Allocations)
                {
                    initialize.SetComputeIntParam(shader,"_GTShapeSourceTriangleStart",allocation.SourceStartTriangle);
                    initialize.SetComputeIntParam(shader,"_GTShapePaddedTriangleStride",allocation.PaddedTriangles);
                    initialize.SetComputeIntParam(shader,"_GTShapeVariantBaseTriangle",checked((int)allocation.VariantBaseTriangle));
                    initialize.SetComputeIntParam(shader,"_GTShapeValidMask",(int)allocation.ValidMask);
                    DispatchVertices(initialize,variantKernel,checked(allocation.PaddedTriangles*3*((int)allocation.ValidMask+1)));
                }
                Graphics.ExecuteCommandBuffer(initialize);
            }
            // Every submitted candidate stays retained even if caller code
            // reentrantly changes the catalog/request during initialization.
            if(request!=null&&!RequestCurrent(request))return false;
            GlassPartialTail replacementTail=null;
            if(extend)
            {
                if(GlassPartialTail.TryCreate(replacementShape,requested,request.Cache,request.Sources,
                    ()=>RequestCurrent(request),out replacementTail,out var unavailable))
                {
                    retainedTails.Add(replacementTail);allocatedBytes+=replacementTail.DescriptorBytes;
                    partialAllocatedBytes+=replacementTail.DescriptorBytes;
                }
                else
                {
                    if(!RequestCurrent(request))return false;
                    FailPartial(request,unavailable??"Partial tail construction failed; retaining static variants.");
                }
            }
            if(request!=null&&!RequestCurrent(request))return false;
            // Publish the cache identity only after every upload and submission
            // succeeds. Failed allocations remain owned for safe later disposal,
            // but Configure/retry cannot mistake them for initialized geometry.
            nativeShape=source;layout=requested;shape=replacementShape;
            triangleBits=replacementBits;clusters=replacementClusters;shapeRequest=request;
            partialGeneration=replacementTail==null?null:new PartialGeneration(request,source,replacementShape,replacementTail);
            Debug.Log("Graphical Tweaks: general glass owned records="+shape.count+
                ", shapes="+layout.Allocations.Length+", static bytes="+layout.StaticBytes+
                ", total retained owned bytes="+allocatedBytes+". Native models and draw arguments unchanged.");
            return true;
            }
            finally{initializing=false;}
        }

        private static void BindGeometry(CommandBuffer cb,int kernel)
            =>BindGeometry(cb,kernel,nativeShape,shape,layout);
        private static void BindGeometry(CommandBuffer cb,int kernel,ComputeBuffer source,
            ComputeBuffer destination,GlassShapeVariantLayout description)
        {
            cb.SetComputeBufferParam(shader,kernel,"_GTShapeNativeRecords",source);
            cb.SetComputeBufferParam(shader,kernel,"_GTShapeOwnedRecords",destination);
            cb.SetComputeIntParam(shader,"_GTShapeSourceVertexCount",description.SourceVertices);
            cb.SetComputeIntParam(shader,"_GTShapeOwnedVertexCount",destination.count);
        }
        private static bool RequestCurrent(PartialRequest request)
        {
            if(request==null||!ReferenceEquals(partialRequest,request)||request.Current==null)return false;
            try{return request.Current()&&ReferenceEquals(partialRequest,request);}
            catch(Exception exception){partialFailureReason="Partial generation predicate failed: "+exception.GetType().Name+": "+exception.Message;return false;}
        }
        private static bool SelectionCurrent(PartialRequest request,PartialGeneration generation,object ticket)
            =>SupportsPartialCaps&&ReferenceEquals(partialGeneration,generation)&&partialSelection.StillAuthorized(request,ticket)&&
                RequestCurrent(request)&&SupportsPartialCaps&&ReferenceEquals(partialGeneration,generation)&&partialSelection.StillAuthorized(request,ticket);
        private static void FailPartial(PartialRequest request,string reason)
        {failedPartialRequest=request;partialFailureReason=reason;}
        private static void RequireOwnedBuffers(Mapping destination,int count)
        {
            if(shape==null||!shape.IsValid()||shape.stride!=124||shape.count<layout.OwnedVertices||
                triangleBits==null||!triangleBits.IsValid()||triangleBits.stride!=4||triangleBits.count!=layout.TriangleFaceBits.Length||
                clusters==null||!clusters.IsValid()||clusters.stride!=16||clusters.count!=layout.Clusters.Length||
                destination==null||destination.Ids==null||!destination.Ids.IsValid()||destination.Ids.stride!=8||destination.Ids.count<count||
                destination.Clip==null||!destination.Clip.IsValid()||destination.Clip.stride!=4||destination.Clip.count<count)
                throw new InvalidOperationException("Actual owned shape/lookup/mapping/clip resource ABI changed.");
        }
        private static void RequirePartialBuffers(PartialGeneration generation)
        {
            if(generation==null)throw new InvalidOperationException("Partial resources have not initialized.");
            var request=generation.Request;var tail=generation.Tail;var descriptors=tail.Descriptors;
            long expected=(long)request.Layout.OwnedVertices+3L*request.Cache.MaximumTailTriangles;
            if(generation.Source==null||!generation.Source.IsValid()||generation.Source.stride!=124||generation.Source.count!=request.Layout.SourceVertices||
                generation.Shape==null||!generation.Shape.IsValid()||generation.Shape.stride!=124||generation.Shape.count!=expected||
                request.Cache.TailBaseTriangle!=request.Layout.OwnedVertices/3||tail.TailTriangleStart!=request.Cache.TailBaseTriangle||
                tail.DescriptorCapacity!=request.Cache.MaximumEntries||tail.DescriptorCapacity<0||tail.DescriptorCapacity>256||
                descriptors==null||!descriptors.IsValid()||descriptors.stride!=16||descriptors.count!=Math.Max(1,tail.DescriptorCapacity)||
                tail.UploadedCount<0||tail.UploadedCount>tail.DescriptorCapacity)
                throw new InvalidOperationException("Actual partial source/shape/16-byte descriptor ABI or bounds changed.");
        }
        private static void DispatchVertices(CommandBuffer cb,int kernel,int count)
        {
            for(int offset=0;offset<count;)
            {
                int batch=Math.Min(count-offset,DispatchLimit);
                cb.SetComputeIntParam(shader,"_GTShapeWorkStart",offset);
                cb.SetComputeIntParam(shader,"_GTShapeWorkCount",batch);
                cb.DispatchCompute(shader,kernel,(batch+63)/64,1,1);
                offset+=batch;
            }
        }
        private static Mapping EnsureMapping(EvoRenderer renderer,int count,bool far)
        {
            if(!mappings.TryGetValue(renderer,out var cameras))mappings.Add(renderer,cameras=new Mapping[2]);
            int index=far?1:0;
            var result=cameras[index];
            if(result!=null&&result.Ids.count>=count)return result;
            int capacity=1;
            while(capacity<count)capacity=checked(capacity*2);
            RequireBudget((long)capacity*12);
            result=new Mapping(Allocate(capacity,8),Allocate(capacity,4));
            cameras[index]=result;
            return result;
        }
        private static void RequireBudget(long additional)
        {
            if(additional<0||additional>MaximumBytes-allocatedBytes)
                throw new InvalidOperationException("General glass retained allocation exceeds its 256MiB budget.");
        }
        private static ComputeBuffer Allocate(int count,int stride)
        {
            long bytes=checked((long)count*stride);RequireBudget(bytes);
            var result=new ComputeBuffer(count,stride);owned.Add(result);allocatedBytes+=bytes;return result;
        }
        // Explicit diagnostic only, outside FPS samples. Verify the ACTUAL Unity
        // compute output byte-for-byte, including native prefix, all masks,
        // complete hidden records, unmaskable triangles and zero padding.
        internal static bool ValidateVariants(out string reason)
        {
            reason="General glass variants are not initialized.";
            if(shape==null||layout==null||nativeShape==null)return false;
            try
            {
                if(Marshal.SizeOf(typeof(BrickMeshResources.ShapeMeshData))!=124||
                   BrickMeshResources.shapeMeshDatas.Count!=layout.SourceVertices)
                    throw new InvalidOperationException("Native CPU shape pool changed before validation.");
                var nativeRecords=new BrickMeshResources.ShapeMeshData[layout.SourceVertices];
                BrickMeshResources.shapeMeshDatas.CopyTo(nativeRecords);
                var actualRecords=new BrickMeshResources.ShapeMeshData[layout.OwnedVertices];
                shape.GetData(actualRecords,0,0,layout.OwnedVertices);
                byte[] nativeBytes=RecordBytes(nativeRecords),actual=RecordBytes(actualRecords);
                var expected=new byte[actual.Length];
                Buffer.BlockCopy(nativeBytes,0,expected,0,nativeBytes.Length);
                foreach(var allocation in layout.Allocations)
                {
                    int sourceStart=allocation.SourceStartTriangle*3;
                    int vertices=allocation.PaddedTriangles*3;
                    for(int mask=0;mask<=(int)allocation.ValidMask;mask++)
                        for(int vertex=0;vertex<vertices;vertex++)
                        {
                            uint bit=layout.TriangleFaceBits[allocation.SourceStartTriangle+vertex/3];
                            int source=sourceStart+(((uint)mask&bit)!=0?vertex/3*3:vertex);
                            int destination=checked((int)allocation.VariantBaseTriangle*3+mask*vertices+vertex);
                            Buffer.BlockCopy(nativeBytes,source*124,expected,destination*124,124);
                        }
                }
                for(int i=0;i<actual.Length;i++)if(actual[i]!=expected[i])
                {reason="General glass GPU byte mismatch at vertex "+i/124+", record byte "+i%124+".";return false;}
                reason="Byte-exact native prefix and every padded variant: "+layout.Allocations.Length+
                    " shapes, "+layout.OwnedVertices+" records, "+actual.Length+" bytes. Explicit readback only; no visual acceptance claim.";
                return true;
            }
            catch(Exception exception){reason=exception.ToString();return false;}
        }
        private static byte[] RecordBytes(BrickMeshResources.ShapeMeshData[] records)
        {
            var result=new byte[checked(records.Length*124)];
            IntPtr temporary=Marshal.AllocHGlobal(124);
            try
            {
                for(int i=0;i<records.Length;i++)
                {Marshal.StructureToPtr(records[i],temporary,false);Marshal.Copy(temporary,result,i*124,124);}
            }
            finally{Marshal.FreeHGlobal(temporary);}
            return result;
        }
        // Caller must detach all command buffers using these resources first.
        internal static void Dispose()
        {
            // Descriptors belong ONLY to their tail wrapper, never the raw list.
            // Invalidate callbacks first, then release each retained owner once.
            partialRequest=shapeRequest=failedPartialRequest=null;partialGeneration=null;partialSelection.Reset();
            var tails=retainedTails.ToArray();retainedTails.Clear();var raw=owned.ToArray();owned.Clear();
            mappings.Clear();shader=null;nativeShape=shape=triangleBits=clusters=null;layout=null;partialKernel=-1;
            allocatedBytes=partialAllocatedBytes=0;faulted=initializing=checkingPartial=false;partialFailureReason=null;
            foreach(var tail in tails)try{tail.Dispose();}catch(Exception e){Debug.LogWarning("Graphical Tweaks: retained partial-tail disposal failed: "+e);}
            foreach(var resource in raw)try{resource.Release();}catch(Exception e){Debug.LogWarning("Graphical Tweaks: retained glass resource disposal failed: "+e);}
        }
        private sealed class PartialRequest
        {
            internal readonly GlassShapeVariantLayout Layout;
            internal readonly GlassPartialGeometryCache Cache;
            internal readonly GlassPartialTailSource[] Sources;
            internal readonly Func<bool> Current;
            internal readonly string InputFailure;
            internal PartialRequest(GlassShapeVariantLayout layout,GlassPartialGeometryCache cache,IReadOnlyList<GlassPartialTailSource> sources,Func<bool> current)
            {
                Layout=layout;Cache=cache;Current=current;
                if(layout==null||sources==null||sources.Count>256||current==null){InputFailure="Partial layout/source bindings or current-generation predicate are unavailable.";return;}
                Sources=new GlassPartialTailSource[sources.Count];for(int i=0;i<Sources.Length;i++)Sources[i]=sources[i];
            }
            internal bool Same(PartialRequest other)
            {
                if(other==null||!ReferenceEquals(Layout,other.Layout)||!ReferenceEquals(Cache,other.Cache)||!Equals(Current,other.Current)||
                    Sources==null||other.Sources==null||Sources.Length!=other.Sources.Length)return false;
                for(int i=0;i<Sources.Length;i++)
                {
                    var a=Sources[i];var b=other.Sources[i];
                    if(a.SourceStart!=b.SourceStart||a.ActualTriangles!=b.ActualTriangles||a.PaddedTriangles!=b.PaddedTriangles||
                        a.ValidMask!=b.ValidMask||!string.Equals(a.SourceHash,b.SourceHash,StringComparison.Ordinal))return false;
                }
                return true;
            }
        }
        private sealed class PartialGeneration
        {
            internal readonly PartialRequest Request;
            internal readonly ComputeBuffer Source,Shape;
            internal readonly GlassPartialTail Tail;
            internal PartialGeneration(PartialRequest request,ComputeBuffer source,ComputeBuffer shape,GlassPartialTail tail)
            {Request=request;Source=source;Shape=shape;Tail=tail;}
        }
        private sealed class Mapping
        {
            internal readonly ComputeBuffer Ids,Clip;
            internal Mapping(ComputeBuffer ids,ComputeBuffer clip){Ids=ids;Clip=clip;}
        }
    }
#endif
    // Authorization is separate from resource lifetime: disabling selection must
    // revoke an upload even when its lifetime callback truthfully stays current.
    internal sealed class GlassPartialHostSelection
    {
        internal delegate bool Upload(out uint encoding,out string reason);
        private object request,ticket=new object();
        private bool enabled,authorizing;
        internal void SetRequest(object value)
        {if(ReferenceEquals(request,value))return;request=value;ticket=new object();}
        internal void SetEnabled(bool value)
        {if(enabled==value)return;enabled=value;ticket=new object();}
        internal void Reset(){request=null;enabled=false;ticket=new object();}
        internal bool TryCapture(object value,out object token)
        {token=null;if(!enabled||value==null||!ReferenceEquals(request,value))return false;token=ticket;return true;}
        internal bool StillAuthorized(object value,object token)=>enabled&&value!=null&&ReferenceEquals(request,value)&&ReferenceEquals(ticket,token);
        internal bool TryAuthorize(object request,Func<bool> ready,Upload upload,out uint encoding,out string reason)
        {
            encoding=0;reason=null;
            if(authorizing)return Fail("Another partial authorization is in progress.",out reason);
            if(!TryCapture(request,out var token))return Fail("Partial selection is disabled or its cache request changed.",out reason);
            if(ready==null||upload==null)return Fail("Current resources and upload callbacks are required.",out reason);
            authorizing=true;
            try
            {
                if(!ready()||!StillAuthorized(request,token))return Fail("Partial resources are not ready or selection changed before upload.",out reason);
                if(!upload(out var candidate,out reason))return false;
                if(!StillAuthorized(request,token)||!ready()||!StillAuthorized(request,token))
                    return Fail("Partial selection/resources changed during upload; encoding was revoked.",out reason);
                if((candidate>>8)<1||(candidate>>8)>256)return Fail("Partial helper returned an invalid custom encoding.",out reason);
                encoding=candidate;return true;
            }
            catch(Exception exception){return Fail("Partial authorization unavailable: "+exception.GetType().Name+": "+exception.Message,out reason);}
            finally{authorizing=false;}
        }
        private static bool Fail(string message,out string reason){reason=message;return false;}
    }
}
