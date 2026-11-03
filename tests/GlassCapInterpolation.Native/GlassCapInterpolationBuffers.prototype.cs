using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
#if !GLASS_CAP_CPU_ONLY
using UnityEngine;
#endif

namespace GraphicalTweaks
{
    internal sealed class GlassCapInterpolationCandidate
    {
        internal readonly uint BuildId,NativeClusterStart;
        internal readonly int SourceTriangleId;
        private readonly byte[] triangle;
        internal byte[] OriginalTriangleRecords=>(byte[])triangle.Clone();
        internal GlassCapInterpolationCandidate(uint buildId,uint nativeClusterStart,int sourceTriangleId,byte[] originalTriangleRecords)
        {
            if(buildId<1||buildId>=4194304||sourceTriangleId<0||nativeClusterStart>uint.MaxValue-11||originalTriangleRecords==null||originalTriangleRecords.Length!=372)
                throw new ArgumentException("A candidate requires a bounded cluster/order key and exactly three 124-byte records.");
            BuildId=buildId;NativeClusterStart=nativeClusterStart;SourceTriangleId=sourceTriangleId;triangle=(byte[])originalTriangleRecords.Clone();
        }
    }
    internal sealed class GlassCapInterpolationPlan
    {
        private readonly byte[] baseline,subdivision;
        private readonly uint[] mapping,clip;
        internal int CaseCount=>clip.Length;
        internal int RecordsCount=>baseline.Length/124;
        internal long ByteCount=>(long)baseline.Length+subdivision.Length+mapping.Length*4L+clip.Length*4L+16;
        internal readonly string Summary;
        internal uint[] MappingWords=>(uint[])mapping.Clone();
        internal uint[] ClipBits=>(uint[])clip.Clone();
        internal uint[] ArgsWords=>new uint[]{36,(uint)CaseCount,0,0};
        internal byte[] BaseShapeRecords=>(byte[])baseline.Clone();
        internal byte[] SubdivShapeRecords=>(byte[])subdivision.Clone();
        // All arguments are freshly created by TryPlan and are never exposed mutably.
        internal GlassCapInterpolationPlan(byte[] baseline,byte[] subdivision,uint[] mapping,uint[] clip,int visibleCount)
        {
            this.baseline=baseline;this.subdivision=subdivision;this.mapping=mapping;this.clip=clip;
            Summary="uncut cap subdivision: cases="+CaseCount+", visible native clusters="+visibleCount+
                ", records per shape="+RecordsCount+", owned GPU bytes="+ByteCount+"; reserved zero cluster=1; duplicate visible mappings rejected";
        }
    }
    // Diagnostic only. Material/zero-resize/native-epoch eligibility is the caller's
    // obligation. Never use this helper to change the scene's actual glass draw.
    internal sealed class GlassCapInterpolationBuffers : IDisposable
    {
        internal const int MaximumVisibleClusters=8192,MaximumCandidates=128;
        internal const long MaximumOwnedBytes=8L*1024*1024;
        private const int RecordBytes=124,TriangleBytes=372,ClusterBytes=12*TriangleBytes;
        internal const int MappingStart=0,ArgsOffsetBytes=0;
#if !GLASS_CAP_CPU_ONLY
        internal ComputeBuffer BaseShape {get;private set;}
        internal ComputeBuffer SubdivShape {get;private set;}
        internal ComputeBuffer Mapping {get;private set;}
        internal ComputeBuffer Clip {get;private set;}
        internal ComputeBuffer Args {get;private set;}
        internal int CaseCount {get;private set;}
        internal int RecordsCount {get;private set;}
        internal long ByteCount {get;private set;}
        internal string Summary {get;private set;}
        private GlassCapInterpolationBuffers(){}

        // READS borrowed native buffers. It never writes or disposes them. GetData
        // is an explicit one-shot diagnostic stall, never a per-frame operation.
        internal static bool TryCreate(ComputeBuffer nativeMapping,ComputeBuffer nativeClip,ComputeBuffer nativeArgs,
            int mappingStart,int argsOffsetBytes,IReadOnlyList<GlassCapInterpolationCandidate> candidates,
            out GlassCapInterpolationBuffers buffers,out string reason)
        {
            buffers=null;reason=null;GlassCapInterpolationBuffers owned=null;
            try
            {
                if(nativeMapping==null||nativeClip==null||nativeArgs==null||!nativeMapping.IsValid()||!nativeClip.IsValid()||!nativeArgs.IsValid())
                    return Fail("Native mapping, clip or argument buffer is unavailable.",out reason);
                if(!TryValidateLayout(nativeMapping.count,nativeMapping.stride,nativeClip.count,nativeClip.stride,
                    nativeArgs.count,nativeArgs.stride,mappingStart,argsOffsetBytes,out reason))return false;
                if(candidates==null||candidates.Count<1||candidates.Count>MaximumCandidates)
                    return Fail("Candidate count is outside the diagnostic budget.",out reason);
                var args=new uint[4];nativeArgs.GetData(args,0,argsOffsetBytes/4,4);
                if(!TryValidateArguments(args,0,nativeMapping.count,nativeClip.count,mappingStart,out int visible,out reason))return false;
                if(Marshal.SizeOf(typeof(NativeMappingRecord))!=8||Marshal.SizeOf(typeof(VertexWords))!=RecordBytes)
                    return Fail("Blittable diagnostic upload ABI changed.",out reason);
                var nativeMap=new NativeMappingRecord[visible];var clip=new uint[visible];
                nativeMapping.GetData(nativeMap,0,mappingStart,visible);nativeClip.GetData(clip,0,mappingStart,visible);
                var mapWords=new uint[visible*2];for(int i=0;i<visible;i++){mapWords[i*2]=nativeMap[i].Shape;mapWords[i*2+1]=nativeMap[i].Build;}
                if(!TryPlan(mapWords,clip,args,0,0,candidates,out var plan,out reason))return false;
                // Finish and validate all CPU uploads before allocating GPU resources.
                var baseline=UploadRecords(plan.BaseShapeRecords);var subdivision=UploadRecords(plan.SubdivShapeRecords);
                var words=plan.MappingWords;var mapping=new NativeMappingRecord[plan.CaseCount];
                for(int i=0;i<mapping.Length;i++)mapping[i]=new NativeMappingRecord{Shape=words[i*2],Build=words[i*2+1]};
                owned=new GlassCapInterpolationBuffers{CaseCount=plan.CaseCount,RecordsCount=plan.RecordsCount,ByteCount=plan.ByteCount,Summary=plan.Summary};
                owned.BaseShape=new ComputeBuffer(plan.RecordsCount,RecordBytes);owned.BaseShape.SetData(baseline);
                owned.SubdivShape=new ComputeBuffer(plan.RecordsCount,RecordBytes);owned.SubdivShape.SetData(subdivision);
                owned.Mapping=new ComputeBuffer(plan.CaseCount,8);owned.Mapping.SetData(mapping);
                owned.Clip=new ComputeBuffer(plan.CaseCount,4);owned.Clip.SetData(plan.ClipBits);
                owned.Args=new ComputeBuffer(4,4,ComputeBufferType.IndirectArguments);owned.Args.SetData(plan.ArgsWords);
                buffers=owned;owned=null;return true;
            }
            catch(Exception exception){return Fail("Cap interpolation diagnostic unavailable: "+exception.GetType().Name+": "+exception.Message,out reason);}
            finally
            {
                // No command buffer can reference a failed, unreturned allocation.
                if(owned!=null)owned.Dispose();
            }
        }
        private static VertexWords[] UploadRecords(byte[] bytes)
        {
            if(bytes==null||bytes.Length==0||bytes.Length%RecordBytes!=0)throw new ArgumentException("Invalid shape upload byte range.");
            var records=new VertexWords[bytes.Length/RecordBytes];var pin=GCHandle.Alloc(records,GCHandleType.Pinned);
            try{Marshal.Copy(bytes,0,pin.AddrOfPinnedObject(),bytes.Length);}finally{pin.Free();}
            return records;
        }
        [StructLayout(LayoutKind.Sequential,Pack=4)]
        private struct NativeMappingRecord {internal uint Shape,Build;}
        // Thirty-one uint words preserve every float's raw bits, including the
        // material word. SetData sees one 124-byte element per original record.
        [StructLayout(LayoutKind.Sequential,Pack=4)]
        private struct VertexWords
        {
#pragma warning disable 0649
            internal uint W00,W01,W02,W03,W04,W05,W06,W07,W08,W09,W10,W11,W12,W13,W14,W15;
            internal uint W16,W17,W18,W19,W20,W21,W22,W23,W24,W25,W26,W27,W28,W29,W30;
#pragma warning restore 0649
        }
#endif
        // After a successful return, the caller MUST retain these owned buffers
        // until native command clearing AND all diagnostic readbacks complete.
        // Disposal must not race a recorded or submitted draw.
        public void Dispose()
        {
#if !GLASS_CAP_CPU_ONLY
            var a=BaseShape;BaseShape=null;var b=SubdivShape;SubdivShape=null;var c=Mapping;Mapping=null;var d=Clip;Clip=null;var e=Args;Args=null;
            try{a?.Dispose();}finally{try{b?.Dispose();}finally{try{c?.Dispose();}finally{try{d?.Dispose();}finally{e?.Dispose();}}}}
#endif
        }

        internal static bool TryValidateLayout(int mappingCount,int mappingStride,int clipCount,int clipStride,
            int argsCount,int argsStride,int mappingStart,int argsOffsetBytes,out string reason)
        {
            reason=null;
            if(mappingStride!=8||clipStride!=4||argsStride!=4||mappingCount<1||clipCount<1||argsCount<4||
                mappingStart<0||mappingStart>=mappingCount||mappingStart>=clipCount||argsOffsetBytes<0||argsOffsetBytes%4!=0||
                (long)argsOffsetBytes+16>(long)argsCount*4)
                return Fail("Native stride, offset or capacity is invalid.",out reason);
            return true;
        }
        private static bool TryValidateArguments(uint[] argsWords,int argsStart,int mappingCount,int clipCount,int mappingStart,
            out int visible,out string reason)
        {
            visible=0;reason=null;
            if(argsWords==null||argsStart<0||(long)argsStart+4>argsWords.Length||argsWords[argsStart]!=36||argsWords[argsStart+1]<1||
                argsWords[argsStart+1]>MaximumVisibleClusters||argsWords[argsStart+2]!=0||argsWords[argsStart+3]!=0)
                return Fail("Expected bounded native arguments [36, visible clusters, 0, 0].",out reason);
            visible=(int)argsWords[argsStart+1];
            if(mappingStart<0||(long)mappingStart+visible>mappingCount||(long)mappingStart+visible>clipCount)
                return Fail("Native visible range exceeds mapping or clip capacity.",out reason);
            return true;
        }
        internal static bool TryPlan(uint[] mappingWords,uint[] clipBits,uint[] argsWords,int mappingStart,int argsOffsetBytes,
            IReadOnlyList<GlassCapInterpolationCandidate> candidates,out GlassCapInterpolationPlan plan,out string reason)
        {
            plan=null;reason=null;
            if(mappingWords==null||clipBits==null||argsWords==null||mappingWords.Length%2!=0||
                !TryValidateLayout(mappingWords.Length/2,8,clipBits.Length,4,argsWords.Length,4,mappingStart,argsOffsetBytes,out reason))
                return Fail(reason??"Invalid native snapshot arrays.",out reason);
            if(!TryValidateArguments(argsWords,argsOffsetBytes/4,mappingWords.Length/2,clipBits.Length,mappingStart,out int visible,out reason))return false;
            if(candidates==null||candidates.Count<1||candidates.Count>MaximumCandidates)
                return Fail("Candidate count is outside the diagnostic budget.",out reason);
            var byCluster=new Dictionary<ulong,List<GlassCapInterpolationCandidate>>();
            foreach(var candidate in candidates)
            {
                if(candidate==null||candidate.BuildId<1||candidate.BuildId>=4194304)
                    return Fail("Null cap candidate or out-of-range native build ID.",out reason);
                ulong key=Key(candidate.NativeClusterStart,candidate.BuildId);
                if(!byCluster.TryGetValue(key,out var values))byCluster.Add(key,values=new List<GlassCapInterpolationCandidate>());
                foreach(var value in values)if(value.SourceTriangleId==candidate.SourceTriangleId)
                    return Fail("Duplicate source-order key in one native cluster.",out reason);
                values.Add(candidate);
            }
            foreach(var values in byCluster.Values)values.Sort((a,b)=>a.SourceTriangleId.CompareTo(b.SourceTriangleId));
            var cases=new List<GlassCapInterpolationCandidate>();var clips=new List<uint>();var seen=new HashSet<ulong>();
            for(int i=0;i<visible;i++)
            {
                int at=mappingStart+i;ulong key=Key(mappingWords[at*2],mappingWords[at*2+1]);
                if(!seen.Add(key))return Fail("Duplicate visible native mapping pair; preserve the original draw.",out reason);
                if(!byCluster.TryGetValue(key,out var values))continue;
                foreach(var value in values){cases.Add(value);clips.Add(clipBits[at]);}
            }
            if(cases.Count<1)return Fail("No eligible cap occurs in the visible native mapping.",out reason);
            // Native vertex programs discard mapping.x==0. Reserve a complete
            // undrawn zero cluster, so every real cap has a nonzero shape start.
            long bytes=(cases.Count+1L)*2*ClusterBytes+cases.Count*12L+16;
            if(cases.Count>MaximumCandidates||bytes>MaximumOwnedBytes||(cases.Count+1L)*36>int.MaxValue)
                return Fail("Owned cap geometry exceeds the diagnostic budget.",out reason);
            var baseline=new byte[(cases.Count+1)*ClusterBytes];var subdivision=new byte[baseline.Length];var mapping=new uint[cases.Count*2];
            var a=new GlassBarycentric(1,0,0,1);var b=new GlassBarycentric(0,1,0,1);var c=new GlassBarycentric(0,0,1,1);
            var center=new GlassBarycentric(1,1,1,3);
            var fan=new[]{new GlassBarycentricTriangle(a,b,center),new GlassBarycentricTriangle(b,c,center),new GlassBarycentricTriangle(c,a,center)};
            var edits=new[]{new GlassNativeTriangleEdit(0,fan)};var faceBits=new uint[12];faceBits[0]=1;
            for(int i=0;i<cases.Count;i++)
            {
                var original=cases[i].OriginalTriangleRecords;var input=new byte[ClusterBytes];Buffer.BlockCopy(original,0,input,0,TriangleBytes);
                // Caller already proved the native free-clear material, zero
                // resize, dynamic-offset and current-source eligibility gates.
                if(!GlassPartialGeometry.TryBuild(input,1,12,0,faceBits,edits,true,out var result,out string geometryReason))
                    return Fail("Uncut source triangle cannot be safely subdivided: "+geometryReason,out reason);
                int write=(i+1)*ClusterBytes;Buffer.BlockCopy(original,0,baseline,write,TriangleBytes);
                Buffer.BlockCopy(result.Records,0,subdivision,write,3*TriangleBytes);
                Fill(baseline,write,1,original);Fill(subdivision,write,3,original);
                mapping[i*2]=(uint)((i+1)*12);mapping[i*2+1]=cases[i].BuildId;
            }
            plan=new GlassCapInterpolationPlan(baseline,subdivision,mapping,clips.ToArray(),visible);return true;
        }
        private static void Fill(byte[] target,int cluster,int firstTriangle,byte[] original)
        {
            for(int triangle=firstTriangle;triangle<12;triangle++)for(int vertex=0;vertex<3;vertex++)
                Buffer.BlockCopy(original,0,target,cluster+triangle*TriangleBytes+vertex*RecordBytes,RecordBytes);
        }
        private static ulong Key(uint cluster,uint build)=>((ulong)cluster<<32)|build;
        private static bool Fail(string message,out string reason){reason=message;return false;}
    }
}
