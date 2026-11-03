using System;
using System.Collections.Generic;
using System.Numerics;

namespace GraphicalTweaks
{
    internal sealed class GlassNativeTriangleEdit
    {
        internal readonly int NativeTriangleIndex;
        internal IReadOnlyList<GlassBarycentricTriangle> Triangles { get; }
        internal GlassNativeTriangleEdit(int nativeTriangleIndex,IReadOnlyList<GlassBarycentricTriangle> triangles)
        {
            if(nativeTriangleIndex<0||triangles==null)throw new ArgumentOutOfRangeException(nameof(nativeTriangleIndex));
            var copy=new GlassBarycentricTriangle[triangles.Count];for(int i=0;i<copy.Length;i++)copy[i]=triangles[i];
            NativeTriangleIndex=nativeTriangleIndex;Triangles=Array.AsReadOnly(copy);
        }
    }
    internal sealed class GlassPartialGeometryResult
    {
        internal readonly byte[] Records;
        internal readonly bool Changed;
        internal IReadOnlyList<int> SourceTriangleIndices {get;}
        internal GlassPartialGeometryResult(byte[] records,bool changed,int[] sourceIndices)
        {Records=records;Changed=changed;SourceTriangleIndices=Array.AsReadOnly(sourceIndices);}
    }
    internal static class GlassPartialGeometry
    {
        private const int RecordBytes=124,TriangleBytes=372,ClusterTriangles=12,MaximumPaddedTriangles=21852;
        internal static bool TryBuild(byte[] sourcePaddedRecords,int actualTriangles,int paddedTriangles,uint fullFaceMask,
            uint[] triangleFaceBits,IReadOnlyList<GlassNativeTriangleEdit> edits,bool verifiedUniformClear,
            out GlassPartialGeometryResult result,out string reason)
        {
            result=null;reason=null;
            if(!verifiedUniformClear)return Fail("Uniform clear-material eligibility was not verified.",out reason);
            if(sourcePaddedRecords==null||actualTriangles<1||paddedTriangles<actualTriangles||paddedTriangles<12||
                paddedTriangles>MaximumPaddedTriangles||paddedTriangles%ClusterTriangles!=0||
                sourcePaddedRecords.Length!=(long)paddedTriangles*TriangleBytes||triangleFaceBits==null||
                triangleFaceBits.Length!=paddedTriangles||fullFaceMask>255||edits==null||edits.Count<0||edits.Count>actualTriangles)
                return Fail("Invalid bounded source range, mask or edit list.",out reason);
            uint used=0;
            for(int i=0;i<paddedTriangles;i++)
            {
                uint bit=triangleFaceBits[i];
                if(bit>128||(bit&(bit-1))!=0||(i>=actualTriangles&&bit!=0))return Fail("Invalid source face label.",out reason);
                used|=bit;
            }
            if((fullFaceMask&~used)!=0)return Fail("Hidden mask contains an unrepresented group.",out reason);
            for(int i=actualTriangles*TriangleBytes;i<sourcePaddedRecords.Length;i++)
                if(sourcePaddedRecords[i]!=0)return Fail("Native source padding is not zero.",out reason);
            var byTriangle=new Dictionary<int,GlassNativeTriangleEdit>();
            foreach(var edit in edits)
            {
                if(edit==null||edit.NativeTriangleIndex<0||edit.NativeTriangleIndex>=actualTriangles||
                    edit.Triangles==null||edit.Triangles.Count>ClusterTriangles||byTriangle.ContainsKey(edit.NativeTriangleIndex)||
                    triangleFaceBits[edit.NativeTriangleIndex]==0)
                    return Fail("Invalid, duplicate or oversized native triangle edit.",out reason);
                foreach(var triangle in edit.Triangles)if(!Positive(triangle))return Fail("Invalid or reversed barycentric triangle.",out reason);
                for(int i=0;i<edit.Triangles.Count;i++)for(int j=0;j<i;j++)
                    if(OverlapsInterior(edit.Triangles[i],edit.Triangles[j]))return Fail("Retained triangles overlap in source parameter space.",out reason);
                byTriangle.Add(edit.NativeTriangleIndex,edit);
            }
            var output=new byte[sourcePaddedRecords.Length];var origins=new int[paddedTriangles];
            for(int i=0;i<origins.Length;i++)origins[i]=-1;
            for(int cluster=0;cluster<paddedTriangles;cluster+=ClusterTriangles)
            {
                int destination=cluster,end=cluster+ClusterTriangles;
                for(int source=cluster;source<Math.Min(end,actualTriangles);source++)
                {
                    if((triangleFaceBits[source]&fullFaceMask)!=0)continue;
                    if(!byTriangle.TryGetValue(source,out var edit))
                    {
                        if(destination>=end)return Fail("An original cluster exceeds twelve output triangles.",out reason);
                        Buffer.BlockCopy(sourcePaddedRecords,source*TriangleBytes,output,destination*TriangleBytes,TriangleBytes);
                        origins[destination++]=source;continue;
                    }
                    if(!SourceBasis(sourcePaddedRecords,source,out int basis,out reason))return false;
                    foreach(var triangle in edit.Triangles)
                    {
                        if(destination>=end)return Fail("An original cluster exceeds twelve output triangles.",out reason);
                        int write=destination*TriangleBytes;
                        if(Identity(triangle))Buffer.BlockCopy(sourcePaddedRecords,source*TriangleBytes,output,write,TriangleBytes);
                        else if(!Interpolate(sourcePaddedRecords,source,basis,triangle,output,write,out reason))return false;
                        origins[destination++]=source;
                    }
                }
                // A whole repeated record is degenerate under the same native
                // vertex deformation. Use original padding when available, so
                // an unchanged native input can remain byte-for-byte unchanged.
                int repeatVertex=(actualTriangles>cluster&&actualTriangles<end?actualTriangles:cluster)*3;
                for(;destination<end;destination++)for(int v=0;v<3;v++)
                    Buffer.BlockCopy(sourcePaddedRecords,repeatVertex*RecordBytes,output,destination*TriangleBytes+v*RecordBytes,RecordBytes);
            }
            bool changed=false;for(int i=0;i<output.Length&&!changed;i++)changed=output[i]!=sourcePaddedRecords[i];
            result=new GlassPartialGeometryResult(output,changed,origins);return true;
        }
        private static bool SourceBasis(byte[] source,int triangle,out int basis,out string reason)
        {
            basis=-1;reason=null;int start=triangle*TriangleBytes;
            for(int v=0;v<3;v++)for(int word=0;word<31;word++)
                if(word!=15&&!Finite(Read(source,start+v*RecordBytes+word*4)))return Fail("Nonfinite edited source attribute.",out reason);
            uint material=BitConverter.ToUInt32(source,start+60);
            for(int v=1;v<3;v++)
            {
                int at=start+v*RecordBytes;
                if(BitConverter.ToUInt32(source,at+60)!=material)return Fail("Edited source triangle has mixed materials.",out reason);
                for(int offset=12;offset<24;offset+=4)if(Read(source,start+offset)!=Read(source,at+offset))
                    return Fail("Edited cap does not have a constant flat normal.",out reason);
                for(int offset=64;offset<112;offset+=4)if(Read(source,start+offset)!=Read(source,at+offset))
                    return Fail("Edited cap bounds metadata varies.",out reason);
                if(Read(source,start+36)!=Read(source,at+36))return Fail("Edited cap tangent handedness varies.",out reason);
            }
            if(!UsableVector(source,start+12))return Fail("Edited cap normal is zero or unsafe.",out reason);
            if(!Nondegenerate(source,start))return Fail("Edited source triangle is degenerate.",out reason);
            for(int v=0;v<3;v++)
            {
                int at=start+v*RecordBytes;
                if(UsableVector(source,at+24)&&UsableVector(source,at+40)&&BasisDeterminant(source,at)!=0)
                {basis=at;return true;}
            }
            return Fail("No finite nonzero source tangent basis.",out reason);
        }
        private static bool Interpolate(byte[] source,int triangle,int basis,GlassBarycentricTriangle weights,
            byte[] output,int write,out string reason)
        {
            reason=null;int start=triangle*TriangleBytes;
            var vertices=new[]{weights.A,weights.B,weights.C};
            for(int v=0;v<3;v++)
            {
                int at=write+v*RecordBytes;
                Buffer.BlockCopy(source,start,output,at,RecordBytes);
                Buffer.BlockCopy(source,basis+24,output,at+24,28); // coherent tangent + binormal
                Buffer.BlockCopy(source,basis+112,output,at+112,12); // verified ignored offset
                foreach(int offset in new[]{0,4,8,52,56})
                {
                    double b=(double)vertices[v].B/(double)vertices[v].D,c=(double)vertices[v].C/(double)vertices[v].D;
                    double a=Read(source,start+offset),x=Read(source,start+RecordBytes+offset),y=Read(source,start+2*RecordBytes+offset);
                    float value=(float)(a+(x-a)*b+(y-a)*c);
                    if(!Finite(value))return Fail("Interpolated source value is nonfinite.",out reason);
                    BitConverter.GetBytes(value).CopyTo(output,at+offset);
                }
            }
            if(!Nondegenerate(output,write))return Fail("Positive remainder collapses after float conversion.",out reason);
            var original=Cross(source,start);var retained=Cross(output,write);
            double dot=original[0]*retained[0]+original[1]*retained[1]+original[2]*retained[2];
            if(!(dot>0)||double.IsInfinity(dot))return Fail("Float conversion changed the source winding.",out reason);
            return true;
        }
        private static bool Positive(GlassBarycentricTriangle t)
        {
            if(!t.A.IsValid||!t.B.IsValid||!t.C.IsValid)return false;
            return CrossSign(t.A,t.B,t.C)>0;
        }
        private static int CrossSign(GlassBarycentric p,GlassBarycentric q,GlassBarycentric r)
        {
            BigInteger x1=q.B*p.D-p.B*q.D,y1=q.C*p.D-p.C*q.D;
            BigInteger x2=r.B*p.D-p.B*r.D,y2=r.C*p.D-p.C*r.D;
            return (x1*y2-y1*x2).Sign;
        }
        private static bool OverlapsInterior(GlassBarycentricTriangle a,GlassBarycentricTriangle b)
        {
            var first=new[]{a.A,a.B,a.C};var second=new[]{b.A,b.B,b.C};
            // Positive triangles are convex. A separating directed edge with
            // all other vertices outside/on it permits only an edge/point touch.
            for(int side=0;side<2;side++)
            {
                var edges=side==0?first:second;var points=side==0?second:first;
                for(int e=0;e<3;e++)
                {
                    bool outside=true;
                    foreach(var p in points)if(CrossSign(edges[e],edges[(e+1)%3],p)>0){outside=false;break;}
                    if(outside)return false;
                }
            }
            return true;
        }
        private static bool Identity(GlassBarycentricTriangle t)=>t.A.A==t.A.D&&t.B.B==t.B.D&&t.C.C==t.C.D;
        private static bool Nondegenerate(byte[] source,int start)
        {var n=Cross(source,start);return (n[0]!=0||n[1]!=0||n[2]!=0)&&!double.IsNaN(n[0])&&!double.IsNaN(n[1])&&!double.IsNaN(n[2]);}
        private static double[] Cross(byte[] source,int start)
        {
            double ax=(double)Read(source,start+RecordBytes)-Read(source,start),ay=(double)Read(source,start+RecordBytes+4)-Read(source,start+4),az=(double)Read(source,start+RecordBytes+8)-Read(source,start+8);
            double bx=(double)Read(source,start+2*RecordBytes)-Read(source,start),by=(double)Read(source,start+2*RecordBytes+4)-Read(source,start+4),bz=(double)Read(source,start+2*RecordBytes+8)-Read(source,start+8);
            return new[]{ay*bz-az*by,az*bx-ax*bz,ax*by-ay*bx};
        }
        private static bool UsableVector(byte[] source,int at)
        {
            float x=Read(source,at),y=Read(source,at+4),z=Read(source,at+8);
            float squared=x*x+y*y+z*z;return Finite(squared)&&squared>0;
        }
        private static double BasisDeterminant(byte[] source,int at)
        {
            double nx=Read(source,at+12),ny=Read(source,at+16),nz=Read(source,at+20);
            double tx=Read(source,at+24),ty=Read(source,at+28),tz=Read(source,at+32);
            double bx=Read(source,at+40),by=Read(source,at+44),bz=Read(source,at+48);
            return nx*(ty*bz-tz*by)+ny*(tz*bx-tx*bz)+nz*(tx*by-ty*bx);
        }
        private static float Read(byte[] bytes,int at)=>BitConverter.ToSingle(bytes,at);
        private static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
        private static bool Fail(string message,out string reason){reason=message;return false;}
    }
}
