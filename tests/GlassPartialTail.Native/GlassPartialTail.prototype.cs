using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
#if !GLASS_PARTIAL_TAIL_CPU_ONLY
using UnityEngine;
#endif

namespace GraphicalTweaks
{
    internal readonly struct GlassPartialTailSource
    {
        internal readonly uint SourceStart,ValidMask;
        internal readonly int ActualTriangles,PaddedTriangles;
        internal readonly string SourceHash;
        internal GlassPartialTailSource(uint sourceStart,int actualTriangles,int paddedTriangles,uint validMask,string sourceHash)
        {SourceStart=sourceStart;ActualTriangles=actualTriangles;PaddedTriangles=paddedTriangles;ValidMask=validMask;SourceHash=sourceHash;}
    }
    [StructLayout(LayoutKind.Sequential,Pack=4)]
    internal readonly struct GlassPartialTailDescriptor
    {
        internal readonly uint SourceStart,DestinationStart,PaddedTriangles,FullMask;
        internal GlassPartialTailDescriptor(uint source,uint destination,uint padded,uint mask)
        {SourceStart=source;DestinationStart=destination;PaddedTriangles=padded;FullMask=mask;}
    }
    internal sealed class GlassPartialTailPacket
    {
        private readonly byte[] records;
        internal int Index {get;}
        internal int DestinationVertex {get;}
        internal int VertexCount {get;}
        internal byte[] Records=>(byte[])records.Clone();
        internal GlassPartialTailDescriptor Descriptor {get;}
        internal GlassPartialTailPacket(GlassPartialCacheEntry entry,int firstVertex,int vertexCount,byte[] ownedRecords)
        {
            Index=entry.Index;DestinationVertex=firstVertex;VertexCount=vertexCount;records=ownedRecords;
            Descriptor=new GlassPartialTailDescriptor((uint)entry.SourceStart,(uint)entry.DestinationStart,(uint)entry.PaddedTriangles,entry.FullMask);
        }
    }
    internal sealed class GlassPartialTailPolicy
    {
        private readonly object gate=new object();
        private readonly Func<bool> isCurrent;
        private readonly int expectedShapeCount;
        private readonly Dictionary<uint,Binding> sources;
        private readonly Dictionary<int,GlassPartialCacheEntry> uploaded=new Dictionary<int,GlassPartialCacheEntry>();
        private int uploadedCount;
        private bool active=true,uploading;
        internal GlassPartialGeometryCache Cache {get;}
        internal int UploadedCount {get{lock(gate)return uploadedCount;}}
        internal int TailTriangleStart=>Cache.TailBaseTriangle;
        internal int DescriptorCapacity=>Cache.MaximumEntries;
        internal int DescriptorBufferCount=>Math.Max(1,DescriptorCapacity);
        internal long DescriptorBytes=>(long)DescriptorBufferCount*16;
        private GlassPartialTailPolicy(GlassPartialGeometryCache cache,int shapeCount,Dictionary<uint,Binding> sources,Func<bool> current)
        {Cache=cache;expectedShapeCount=shapeCount;this.sources=sources;isCurrent=current;}
        internal static bool TryCreate(int shapeCount,int shapeStride,GlassShapeVariantLayout layout,GlassPartialGeometryCache cache,
            IReadOnlyList<GlassPartialTailSource> trustedSources,Func<bool> isCurrent,out GlassPartialTailPolicy policy,out string reason)
        {
            policy=null;reason=null;
            try
            {
                if(isCurrent==null||!isCurrent())return Fail("Resource generation is not current.",out reason);
                if(layout==null||cache==null||trustedSources==null||trustedSources.Count>256||
                    layout.SourceVertices<=0||layout.SourceVertices>4*1024*1024||layout.SourceVertices%3!=0||
                    layout.OwnedVertices<layout.SourceVertices||layout.OwnedVertices%3!=0||
                    cache.MaximumEntries<0||cache.MaximumEntries>256||cache.MaximumTailTriangles<0||cache.MaximumTailTriangles>6144||
                    cache.TailBaseTriangle!=layout.OwnedVertices/3||
                    shapeStride!=124||shapeCount!=(long)layout.OwnedVertices+3L*cache.MaximumTailTriangles||
                    (long)shapeCount*124>int.MaxValue||Marshal.SizeOf(typeof(GlassPartialTailDescriptor))!=16||
                    Marshal.SizeOf(typeof(GlassPartialTailVertexWords))!=124)
                    return Fail("Invalid native prefix, cache allocation bounds or shape ABI.",out reason);
                int sourceTriangles=layout.SourceVertices/3;
                if(layout.TriangleFaceBits==null||layout.TriangleFaceBits.Length!=sourceTriangles||
                    layout.Clusters==null||layout.Clusters.Length!=sourceTriangles||layout.Allocations==null||
                    layout.Allocations.Length<1||layout.Allocations.Length>256||
                    layout.StaticBytes!=(long)layout.OwnedVertices*124+sourceTriangles*20L)
                    return Fail("Invalid native layout table sizes or allocation bytes.",out reason);
                // All mutable layout and caller binding arrays are copied before proof.
                var labels=(uint[])layout.TriangleFaceBits.Clone();
                var clusters=(GlassShapeClusterDescriptor[])layout.Clusters.Clone();
                var allocations=(GlassShapeVariantAllocation[])layout.Allocations.Clone();
                var trusted=new GlassPartialTailSource[trustedSources.Count];for(int i=0;i<trusted.Length;i++)trusted[i]=trustedSources[i];
                var covered=new bool[sourceTriangles];var heads=new bool[sourceTriangles];
                var bySource=new Dictionary<uint,GlassShapeVariantAllocation>();var ids=new HashSet<int>();
                long expectedVariant=sourceTriangles;
                foreach(var a in allocations)
                {
                    if(a.BrickId<0||!ids.Add(a.BrickId)||a.SourceStartTriangle<=0||a.PaddedTriangles<=0||a.PaddedTriangles%12!=0||
                        (long)a.SourceStartTriangle+a.PaddedTriangles>sourceTriangles||a.ValidMask<1||a.ValidMask>255||
                        (a.ValidMask&(a.ValidMask+1))!=0||a.VariantBaseTriangle!=expectedVariant||
                        bySource.ContainsKey((uint)a.SourceStartTriangle))return Fail("Invalid native source/static allocation descriptor.",out reason);
                    uint used=0;
                    for(int offset=0;offset<a.PaddedTriangles;offset++)
                    {
                        int index=a.SourceStartTriangle+offset;uint bit=labels[index];
                        if(covered[index]||(bit&(bit-1))!=0||(bit&~a.ValidMask)!=0)return Fail("Overlapping source ranges or invalid native face labels.",out reason);
                        covered[index]=true;used|=bit;
                        if(offset%12==0)
                        {
                            heads[index]=true;var c=clusters[index];
                            if(c.SourceStartTriangle!=(uint)a.SourceStartTriangle||c.PaddedTriangles!=(uint)a.PaddedTriangles||
                                c.ValidMask!=a.ValidMask||c.VariantBaseTriangle!=(long)a.VariantBaseTriangle+offset)
                                return Fail("Native cluster descriptor disagrees with static source allocation.",out reason);
                        }
                    }
                    if(used!=a.ValidMask)return Fail("Native face labels do not represent complete valid mask.",out reason);
                    expectedVariant+=(long)a.PaddedTriangles*(a.ValidMask+1);
                    if(expectedVariant>layout.OwnedVertices/3)return Fail("Static allocation exceeds native owned prefix.",out reason);
                    bySource.Add((uint)a.SourceStartTriangle,a);
                }
                if(expectedVariant!=layout.OwnedVertices/3)return Fail("Static allocation extent does not equal native owned prefix.",out reason);
                for(int i=0;i<sourceTriangles;i++)
                {
                    if(!covered[i]&&labels[i]!=0)return Fail("Native face table has labels outside source ranges.",out reason);
                    var c=clusters[i];if(!heads[i]&&(c.SourceStartTriangle!=0||c.PaddedTriangles!=0||c.ValidMask!=0||c.VariantBaseTriangle!=0))
                        return Fail("Native cluster table has descriptors outside cluster heads.",out reason);
                }
                var bindings=new Dictionary<uint,Binding>();
                foreach(var source in trusted)
                {
                    if(source.SourceStart<1||source.ActualTriangles<1||source.ActualTriangles>source.PaddedTriangles||
                        (source.PaddedTriangles!=12&&source.PaddedTriangles!=24)||!Hex64(source.SourceHash)||
                        !bySource.TryGetValue(source.SourceStart,out var a)||a.PaddedTriangles!=source.PaddedTriangles||
                        a.ValidMask!=source.ValidMask||bindings.ContainsKey(source.SourceStart))
                        return Fail("Trusted source binding does not match audited native source range/stride/mask/hash.",out reason);
                    var faceBits=new uint[source.PaddedTriangles];Array.Copy(labels,(int)source.SourceStart,faceBits,0,faceBits.Length);
                    for(int i=source.ActualTriangles;i<faceBits.Length;i++)if(faceBits[i]!=0)
                        return Fail("Trusted actual source count contradicts native padding labels.",out reason);
                    bindings.Add(source.SourceStart,new Binding(source,faceBits));
                }
                if(!isCurrent())return Fail("Resource generation changed during layout snapshot.",out reason);
                policy=new GlassPartialTailPolicy(cache,shapeCount,bindings,isCurrent);return true;
            }
            catch(Exception e){return Fail("Tail policy unavailable: "+e.GetType().Name+": "+e.Message,out reason);}
        }
        internal bool TryUpload(GlassPartialCacheEntry entry,int shapeCount,int shapeStride,int descriptorCount,int descriptorStride,
            Action<GlassPartialTailPacket> writeShape,Action<GlassPartialTailPacket> writeDescriptor,out uint encodedMask,out string reason)
        {
            lock(gate)
            {
                encodedMask=0;reason=null;
                try
                {
                    // This MUST precede every read from the abstract entry view.
                    if(!Cache.Owns(entry))return Fail("Entry was not minted by this cache.",out reason);
                    if(uploading)return Fail("Another tail upload is already in progress.",out reason);
                    if(!TryValidateCurrent(out reason)||!TryValidateBuffers(shapeCount,shapeStride,descriptorCount,descriptorStride,out reason))return false;
                    if(entry.Index<0||entry.Index>=DescriptorCapacity)return Fail("Entry index exceeds descriptor capacity.",out reason);
                    if(uploaded.TryGetValue(entry.Index,out var old))
                    {
                        if(!ReferenceEquals(old,entry))return Fail("An uploaded index is already owned by a different immutable entry.",out reason);
                        if(!TryValidateCurrent(out reason))return false;encodedMask=((uint)entry.Index+1)<<8|entry.FullMask;return true;
                    }
                    if(entry.SourceStart<=0||
                        !sources.TryGetValue((uint)entry.SourceStart,out var binding)||
                        entry.ActualTriangles!=binding.Source.ActualTriangles||entry.PaddedTriangles!=binding.Source.PaddedTriangles||
                        entry.FullMask>255||(entry.FullMask&~binding.Source.ValidMask)!=0||
                        !string.Equals(entry.SourceHash,binding.Source.SourceHash,StringComparison.OrdinalIgnoreCase)||
                        !SameLabels(entry.SourceFaceBits,binding.Labels)||
                        !TryValidateDestination(entry.DestinationStart,entry.PaddedTriangles,TailTriangleStart,Cache.MaximumTailTriangles,
                            shapeCount,out int firstVertex,out int vertexCount,out reason))
                        return Fail(reason??"Entry disagrees with trusted source binding or descriptor capacity.",out reason);
                    uint encoding=((uint)entry.Index+1)<<8|entry.FullMask;
                    if(writeShape==null||writeDescriptor==null)return Fail("Both ordered upload writers are required.",out reason);
                    var records=entry.Records;if(records==null||records.Length!=(long)vertexCount*124)return Fail("Raw record byte count disagrees with padded stride.",out reason);
                    var packet=new GlassPartialTailPacket(entry,firstVertex,vertexCount,records);
                    if(!TryValidateCurrent(out reason))return false;
                    uploading=true;
                    try
                    {
                        writeShape(packet);
                        if(!TryValidateCurrent(out reason))return false;
                        writeDescriptor(packet);
                        if(!TryValidateCurrent(out reason))return false;
                        uploaded.Add(entry.Index,entry);uploadedCount=Math.Max(uploadedCount,entry.Index+1);encodedMask=encoding;return true;
                    }
                    finally{uploading=false;}
                }
                catch(Exception e){encodedMask=0;return Fail("Tail upload unavailable: "+e.GetType().Name+": "+e.Message,out reason);}
            }
        }
        internal bool TryValidateBuffers(int shapeCount,int shapeStride,int descriptorCount,int descriptorStride,out string reason)
        {
            reason=null;
            if(shapeStride!=124||shapeCount!=expectedShapeCount||descriptorStride!=16||descriptorCount!=DescriptorBufferCount||
                (long)shapeCount*124>int.MaxValue||Marshal.SizeOf(typeof(GlassPartialTailDescriptor))!=16||Marshal.SizeOf(typeof(GlassPartialTailVertexWords))!=124)
                return Fail("Actual shape/descriptor ABI or allocation count changed.",out reason);
            return true;
        }
        internal bool TryValidateCurrent(out string reason)
        {
            reason=null;
            // The caller predicate may reenter Deactivate/Dispose. Its true
            // result must not authorize a policy it deactivated while running.
            try{if(active&&isCurrent()&&active)return true;return Fail("Resource generation is stale or tail is disposed.",out reason);}
            catch(Exception e){return Fail("Current-generation predicate failed: "+e.GetType().Name+": "+e.Message,out reason);}
        }
        internal void Deactivate(){lock(gate)active=false;}
        internal static bool TryValidateDestination(int destination,int padded,int tailBase,int maxTail,int shapeVertices,out int firstVertex,out int vertexCount,out string reason)
        {
            firstVertex=vertexCount=0;reason=null;
            long relative=(long)destination-tailBase;
            if(destination<=0||tailBase<=0||maxTail<0||maxTail>6144||(padded!=12&&padded!=24)||relative<0||relative%12!=0||
                relative+padded>maxTail||shapeVertices<=0||shapeVertices%3!=0||
                ((long)tailBase+maxTail)*3!=shapeVertices||((long)destination+padded)*372>int.MaxValue||
                ((long)destination+padded)*3>shapeVertices)return Fail("Custom destination is outside aligned reserved tail/upload addressing.",out reason);
            firstVertex=destination*3;vertexCount=padded*3;return true;
        }
        private static bool Hex64(string text)
        {if(text==null||text.Length!=64)return false;foreach(char c in text)if(!((c>='0'&&c<='9')||(c>='a'&&c<='f')||(c>='A'&&c<='F')))return false;return true;}
        private static bool SameLabels(uint[] a,uint[] b)
        {if(a==null||a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        private static bool Fail(string message,out string reason){reason=message;return false;}
        private sealed class Binding
        {
            internal readonly GlassPartialTailSource Source;internal readonly uint[] Labels;
            internal Binding(GlassPartialTailSource source,uint[] labels){Source=source;Labels=labels;}
        }
    }
    // One native 124-byte record per SetData element, preserving every raw bit.
    [StructLayout(LayoutKind.Sequential,Pack=4)]
    internal struct GlassPartialTailVertexWords
    {
#pragma warning disable 0649
        internal uint W00,W01,W02,W03,W04,W05,W06,W07,W08,W09,W10,W11,W12,W13,W14,W15;
        internal uint W16,W17,W18,W19,W20,W21,W22,W23,W24,W25,W26,W27,W28,W29,W30;
#pragma warning restore 0649
    }
#if !GLASS_PARTIAL_TAIL_CPU_ONLY
    // UNCONNECTED prototype. The caller owns generation checks, mask publication,
    // sticky-failure handling, native command detachment and retirement fencing.
    internal sealed class GlassPartialTail : IDisposable
    {
        private readonly ComputeBuffer shape;
        private readonly GlassPartialTailPolicy policy;
        internal ComputeBuffer Descriptors {get;private set;}
        internal int UploadedCount=>policy.UploadedCount;
        internal int TailTriangleStart=>policy.TailTriangleStart;
        internal int DescriptorCapacity=>policy.DescriptorCapacity;
        internal long DescriptorBytes=>policy.DescriptorBytes;
        internal GlassPartialGeometryCache Cache=>policy.Cache;
        private GlassPartialTail(ComputeBuffer shape,ComputeBuffer descriptors,GlassPartialTailPolicy policy)
        {this.shape=shape;Descriptors=descriptors;this.policy=policy;}
        internal static bool TryCreate(ComputeBuffer borrowedOwnedShape,GlassShapeVariantLayout layout,GlassPartialGeometryCache cache,
            IReadOnlyList<GlassPartialTailSource> trustedSources,Func<bool> isCurrent,out GlassPartialTail tail,out string reason)
        {
            tail=null;reason=null;ComputeBuffer unreturned=null;
            try
            {
                if(borrowedOwnedShape==null||!borrowedOwnedShape.IsValid())return Fail("Borrowed owned-shape buffer is unavailable.",out reason);
                if(!GlassPartialTailPolicy.TryCreate(borrowedOwnedShape.count,borrowedOwnedShape.stride,layout,cache,trustedSources,isCurrent,out var policy,out reason))return false;
                unreturned=new ComputeBuffer(policy.DescriptorBufferCount,16);
                if(!policy.TryValidateCurrent(out reason))return false;
                if(!unreturned.IsValid()||!policy.TryValidateBuffers(borrowedOwnedShape.count,borrowedOwnedShape.stride,unreturned.count,unreturned.stride,out reason))return false;
                // Unknown holes are zero rows, forcing V2 static fallback.
                unreturned.SetData(new GlassPartialTailDescriptor[policy.DescriptorBufferCount]);
                if(!policy.TryValidateCurrent(out reason))return false;
                tail=new GlassPartialTail(borrowedOwnedShape,unreturned,policy);unreturned=null;return true;
            }
            catch(Exception e){return Fail("Tail allocation unavailable: "+e.GetType().Name+": "+e.Message,out reason);}
            finally
            {
                // An unreturned descriptor cannot be referenced by a command.
                // Cleanup failure must not turn a fail-closed Try API into a throw.
                try{unreturned?.Dispose();}catch(Exception e){reason=(reason??"Tail construction failed.")+" Cleanup: "+e.GetType().Name+": "+e.Message;}
            }
        }
        internal bool TryUpload(GlassPartialCacheEntry entry,out uint encodedMask,out string reason)
        {
            encodedMask=0;reason=null;
            try
            {
                if(!Cache.Owns(entry))return Fail("Entry was not minted by this cache.",out reason);
                RequireBuffers();
                return policy.TryUpload(entry,shape.count,shape.stride,Descriptors.count,Descriptors.stride,
                    packet=>{RequireBuffers();var bytes=packet.Records;var records=UploadRecords(bytes);if(records.Length!=packet.VertexCount)throw new InvalidOperationException("Raw vertex count changed.");shape.SetData(records,0,packet.DestinationVertex,packet.VertexCount);},
                    packet=>{RequireBuffers();Descriptors.SetData(new[]{packet.Descriptor},0,packet.Index,1);},out encodedMask,out reason);
            }
            catch(Exception e){encodedMask=0;return Fail("Native tail upload unavailable: "+e.GetType().Name+": "+e.Message,out reason);}
        }
        private void RequireBuffers()
        {
            if(shape==null||!shape.IsValid()||Descriptors==null||!Descriptors.IsValid())throw new InvalidOperationException("Actual shape/descriptor buffer is unavailable.");
            if(!policy.TryValidateCurrent(out var reason)||!policy.TryValidateBuffers(shape.count,shape.stride,Descriptors.count,Descriptors.stride,out reason))throw new InvalidOperationException(reason);
        }
        private static GlassPartialTailVertexWords[] UploadRecords(byte[] bytes)
        {
            if(bytes==null||bytes.Length==0||bytes.Length%124!=0||Marshal.SizeOf(typeof(GlassPartialTailVertexWords))!=124)throw new ArgumentException("Invalid raw upload ABI/byte count.");
            var records=new GlassPartialTailVertexWords[bytes.Length/124];var pin=GCHandle.Alloc(records,GCHandleType.Pinned);
            try{Marshal.Copy(bytes,0,pin.AddrOfPinnedObject(),bytes.Length);}finally{pin.Free();}return records;
        }
        // After successful TryCreate, call ONLY after native command clearing and
        // all pending readbacks are fenced. The borrowed shape is never disposed.
        public void Dispose(){policy.Deactivate();var owned=Descriptors;Descriptors=null;owned?.Dispose();}
        private static bool Fail(string message,out string reason){reason=message;return false;}
    }
#endif
}
