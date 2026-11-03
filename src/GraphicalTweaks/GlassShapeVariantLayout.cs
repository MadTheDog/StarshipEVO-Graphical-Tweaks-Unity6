using System;
using System.Collections.Generic;

namespace GraphicalTweaks
{
    internal sealed class GlassShapeVariantSource
    {
        internal readonly int BrickId, StartTriangle, ActualTriangles, PaddedTriangles, GroupCount;
        internal readonly uint[] TriangleFaceBits;
        internal GlassShapeVariantSource(int brickId, int startTriangle, int actualTriangles,
            int paddedTriangles, int groupCount, uint[] triangleFaceBits)
        {
            BrickId=brickId; StartTriangle=startTriangle; ActualTriangles=actualTriangles;
            PaddedTriangles=paddedTriangles; GroupCount=groupCount; TriangleFaceBits=triangleFaceBits;
        }
    }

    internal readonly struct GlassShapeClusterDescriptor
    {
        internal readonly uint VariantBaseTriangle, PaddedTriangles, ValidMask, SourceStartTriangle;
        internal GlassShapeClusterDescriptor(uint variantBase, uint padded, uint validMask, uint sourceStart)
        { VariantBaseTriangle=variantBase; PaddedTriangles=padded; ValidMask=validMask; SourceStartTriangle=sourceStart; }
    }

    internal readonly struct GlassShapeVariantAllocation
    {
        internal readonly int BrickId, SourceStartTriangle, PaddedTriangles;
        internal readonly uint VariantBaseTriangle, ValidMask;
        internal GlassShapeVariantAllocation(GlassShapeVariantSource source,uint variantBase)
        {
            BrickId=source.BrickId; SourceStartTriangle=source.StartTriangle;
            PaddedTriangles=source.PaddedTriangles; VariantBaseTriangle=variantBase;
            ValidMask=(1u<<source.GroupCount)-1;
        }
    }

    internal sealed class GlassShapeVariantLayout
    {
        // Admission bounds apply before allocating tables, including when a
        // caller supplies an unrealistically large byte budget.
        private const int MaximumSourceVertices=4*1024*1024;
        internal int SourceVertices {get; private set;}
        internal int OwnedVertices {get; private set;}
        internal long StaticBytes {get; private set;}
        internal uint[] TriangleFaceBits {get; private set;}
        internal GlassShapeClusterDescriptor[] Clusters {get; private set;}
        internal GlassShapeVariantAllocation[] Allocations {get; private set;}
        internal static bool TryCreate(int sourceVertices, IReadOnlyList<GlassShapeVariantSource> sources,
            long maximumStaticBytes, out GlassShapeVariantLayout layout)
        {
            layout=null;
            if(sourceVertices<=0||sourceVertices>MaximumSourceVertices||sourceVertices%3!=0||
               sources==null||sources.Count==0||sources.Count>256||maximumStaticBytes<=0)return false;
            int sourceTriangles=sourceVertices/3;
            var ids=new HashSet<int>();
            long ownedTriangles=sourceTriangles;
            // Validate all metadata and byte arithmetic before any pool-sized
            // allocations. Overlaps are ambiguous even when labels agree.
            for(int i=0;i<sources.Count;i++)
            {
                var source=sources[i];
                if(source==null||source.BrickId<0||!ids.Add(source.BrickId)||source.StartTriangle<=0||
                   source.StartTriangle>=sourceTriangles||source.PaddedTriangles<=0||source.PaddedTriangles%12!=0||
                   source.PaddedTriangles>sourceTriangles-source.StartTriangle||source.ActualTriangles<=0||
                   source.ActualTriangles>source.PaddedTriangles||source.GroupCount<1||source.GroupCount>8||
                   source.TriangleFaceBits==null||source.TriangleFaceBits.Length!=source.PaddedTriangles)return false;
                for(int previous=0;previous<i;previous++)
                {
                    var other=sources[previous];
                    if(source.StartTriangle<other.StartTriangle+other.PaddedTriangles&&
                       other.StartTriangle<source.StartTriangle+source.PaddedTriangles)return false;
                }
                uint validMask=(1u<<source.GroupCount)-1, usedMask=0;
                for(int triangle=0;triangle<source.PaddedTriangles;triangle++)
                {
                    uint bit=source.TriangleFaceBits[triangle];
                    if((bit&(bit-1))!=0||(bit&~validMask)!=0||
                       (triangle>=source.ActualTriangles&&bit!=0))return false;
                    usedMask|=bit;
                }
                if(usedMask!=validMask)return false;
                ownedTriangles+=(long)source.PaddedTriangles*(validMask+1);
            }
            long ownedVertices=ownedTriangles*3;
            long bytes=ownedVertices*124+sourceTriangles*20L;
            if(ownedVertices>int.MaxValue||bytes>maximumStaticBytes)return false;
            var result=new GlassShapeVariantLayout
            {
                SourceVertices=sourceVertices, OwnedVertices=(int)ownedVertices, StaticBytes=bytes,
                TriangleFaceBits=new uint[sourceTriangles],
                Clusters=new GlassShapeClusterDescriptor[sourceTriangles],
                Allocations=new GlassShapeVariantAllocation[sources.Count]
            };
            uint variantBase=(uint)sourceTriangles;
            int allocation=0;
            foreach(var source in sources)
            {
                uint validMask=(1u<<source.GroupCount)-1;
                result.Allocations[allocation++]=new GlassShapeVariantAllocation(source,variantBase);
                Array.Copy(source.TriangleFaceBits,0,result.TriangleFaceBits,source.StartTriangle,source.PaddedTriangles);
                for(int offset=0;offset<source.PaddedTriangles;offset+=12)
                    result.Clusters[source.StartTriangle+offset]=new GlassShapeClusterDescriptor(
                        variantBase+(uint)offset,(uint)source.PaddedTriangles,validMask,(uint)source.StartTriangle);
                variantBase+=(uint)source.PaddedTriangles*(validMask+1);
            }
            layout=result;
            return true;
        }
    }
}
