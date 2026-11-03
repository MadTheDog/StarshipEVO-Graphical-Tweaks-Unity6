using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace GraphicalTweaks
{
    internal readonly struct GlassPartialCacheDescriptor
    {
        internal readonly uint SourceStart,DestinationStart,PaddedTriangles,FullMask;
        internal GlassPartialCacheDescriptor(int sourceStart,int destinationStart,int paddedTriangles,uint fullMask)
        {SourceStart=(uint)sourceStart;DestinationStart=(uint)destinationStart;PaddedTriangles=(uint)paddedTriangles;FullMask=fullMask;}
    }
    // Read-only entry view. Only the cache's private implementation owns arrays.
    internal abstract class GlassPartialCacheEntry
    {
        internal abstract int Index {get;}
        internal abstract int SourceStart {get;}
        internal abstract int ActualTriangles {get;}
        internal abstract int PaddedTriangles {get;}
        internal abstract int DestinationStart {get;}
        internal abstract uint FullMask {get;}
        internal abstract string SourceHash {get;}
        internal abstract byte[] Records {get;}
        internal abstract byte[] SourceRecords {get;}
        internal abstract uint[] SourceFaceBits {get;}
        internal abstract IReadOnlyList<GlassNativeTriangleEdit> Edits {get;}
        internal GlassPartialCacheDescriptor Descriptor=>new(SourceStart,DestinationStart,PaddedTriangles,FullMask);
    }
    internal sealed class GlassPartialGeometryCache
    {
        private const int TriangleBytes=372;
        private readonly object gate=new();
        private readonly int tailBase,maximumEntries,maximumTailTriangles;
        private readonly long maximumRawBytes;
        private readonly Dictionary<Key,GlassPartialCacheEntry> entries=new();
        private int allocatedTriangles;
        private long rawBytes,writerCallCount;
        internal GlassPartialGeometryCache(int customTailBaseTriangle,int maximumEntries=256,int maximumTailTriangles=6144,long maximumRawBytes=8L*1024*1024)
        {
            if(customTailBaseTriangle<=0||(long)customTailBaseTriangle*TriangleBytes>int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(customTailBaseTriangle));
            if(maximumEntries<0||maximumEntries>256)throw new ArgumentOutOfRangeException(nameof(maximumEntries));
            if(maximumTailTriangles<0||maximumTailTriangles>6144)throw new ArgumentOutOfRangeException(nameof(maximumTailTriangles));
            if(maximumRawBytes<0||maximumRawBytes>8L*1024*1024)throw new ArgumentOutOfRangeException(nameof(maximumRawBytes));
            tailBase=customTailBaseTriangle;this.maximumEntries=maximumEntries;this.maximumTailTriangles=maximumTailTriangles;this.maximumRawBytes=maximumRawBytes;
        }
        internal int Count {get{lock(gate)return entries.Count;}}
        internal int TailBaseTriangle=>tailBase;
        internal int MaximumTailTriangles=>maximumTailTriangles;
        internal int MaximumEntries=>maximumEntries;
        // Visible fields are never authorization: require the private concrete
        // entry and its immutable reference to this exact cache generation.
        internal bool Owns(GlassPartialCacheEntry entry)=>entry is OwnedEntry owned&&ReferenceEquals(owned.Owner,this);
        internal int AllocatedTriangles {get{lock(gate)return allocatedTriangles;}}
        // Both owned raw source bytes and generated output bytes count toward the cap.
        internal long RawBytes {get{lock(gate)return rawBytes;}}
        internal long WriterCallCount {get{lock(gate)return writerCallCount;}}
        internal bool TryGetOrAdd(byte[] source,int sourceStart,int actualTriangles,int paddedTriangles,uint fullFaceMask,
            uint[] faceBits,IReadOnlyList<GlassNativeTriangleEdit> edits,bool verifiedUniformClear,
            out GlassPartialCacheEntry entry,out string reason)
        {
            lock(gate)
            {
                entry=null;reason=null;
                if(source==null||sourceStart<=0||actualTriangles<1||actualTriangles>paddedTriangles||
                    (paddedTriangles!=12&&paddedTriangles!=24)||source.Length!=(long)paddedTriangles*TriangleBytes||
                    ((long)sourceStart+paddedTriangles)*TriangleBytes>int.MaxValue||
                    faceBits==null||faceBits.Length!=paddedTriangles||edits==null||edits.Count<1||edits.Count>actualTriangles)
                    return Fail("Invalid bounded source range or partial edit list.",out reason);
                // Bound all work before cloning, serialization, exact arithmetic, or writer allocation.
                for(int i=0;i<edits.Count;i++)
                    if(edits[i]==null||edits[i].Triangles==null||edits[i].Triangles.Count>12)
                        return Fail("Invalid or oversized partial edit.",out reason);
                var ownedSource=(byte[])source.Clone();var ownedLabels=(uint[])faceBits.Clone();
                var ownedEdits=new GlassNativeTriangleEdit[edits.Count];
                for(int i=0;i<ownedEdits.Length;i++)ownedEdits[i]=new GlassNativeTriangleEdit(edits[i].NativeTriangleIndex,edits[i].Triangles);
                Array.Sort(ownedEdits,(a,b)=>a.NativeTriangleIndex.CompareTo(b.NativeTriangleIndex));
                string sourceHash=BitConverter.ToString(Digest(ownedSource)).Replace("-","");
                var key=new Key(ownedSource,sourceHash,sourceStart,actualTriangles,paddedTriangles,fullFaceMask,ownedLabels,ownedEdits);
                // Eligibility is checked on every call, including exact-key hits.
                if(verifiedUniformClear&&entries.TryGetValue(key,out entry))return true;
                entry=null;
                long nextTriangles=(long)allocatedTriangles+paddedTriangles;
                long nextRawBytes=rawBytes+2L*ownedSource.Length;
                long destination=(long)tailBase+allocatedTriangles;
                long destinationEnd=(long)tailBase+nextTriangles;
                if(entries.Count>=maximumEntries||nextTriangles>maximumTailTriangles||nextRawBytes>maximumRawBytes||
                    destinationEnd*TriangleBytes>int.MaxValue||destinationEnd>uint.MaxValue)
                    return Fail("Immutable cache budget or upload-address limit exhausted.",out reason);
                writerCallCount++;
                if(!GlassPartialGeometry.TryBuild(ownedSource,actualTriangles,paddedTriangles,fullFaceMask,ownedLabels,ownedEdits,
                    verifiedUniformClear,out var built,out reason))return false;
                if(!HasVisiblePartial(ownedEdits,ownedLabels,fullFaceMask))
                    return Fail("No visible source triangle retains strictly partial positive area; use static full-face variants.",out reason);
                var added=new OwnedEntry(this,entries.Count,sourceStart,actualTriangles,paddedTriangles,(int)destination,
                    fullFaceMask,sourceHash,ownedSource,ownedLabels,ownedEdits,built.Records);
                entries.Add(key,added);allocatedTriangles=(int)nextTriangles;rawBytes=nextRawBytes;entry=added;return true;
            }
        }
        private static bool HasVisiblePartial(GlassNativeTriangleEdit[] edits,uint[] labels,uint mask)
        {
            foreach(var edit in edits)
            {
                if((labels[edit.NativeTriangleIndex]&mask)!=0)continue;
                BigInteger numerator=0,denominator=1;
                foreach(var t in edit.Triangles)
                {
                    // Twice parameter-space area divided by original twice-area (1).
                    // This admission test performs no clipping; writer validation has already passed.
                    var p=t.A;var q=t.B;var r=t.C;
                    BigInteger n=(q.B*p.D-p.B*q.D)*(r.C*p.D-p.C*r.D)-(q.C*p.D-p.C*q.D)*(r.B*p.D-p.B*r.D);
                    BigInteger d=p.D*p.D*q.D*r.D;
                    numerator=numerator*d+n*denominator;denominator*=d;
                    var gcd=BigInteger.GreatestCommonDivisor(numerator,denominator);numerator/=gcd;denominator/=gcd;
                }
                if(numerator.Sign>0&&numerator<denominator)return true;
            }
            return false;
        }
        private static bool Fail(string message,out string reason){reason=message;return false;}
        private static byte[] Digest(byte[] bytes)
        {using(var sha=SHA256.Create())return sha.ComputeHash(bytes);}
        private static bool ExactBytes(byte[] left,byte[] right)
        {
            if(left.Length!=right.Length)return false;
            for(int i=0;i<left.Length;i++)if(left[i]!=right[i])return false;
            return true;
        }
        // The concrete type is private to the cache, so no caller can mint an
        // entry with caller-owned mutable backing arrays. Cache snapshots and
        // the unexposed writer result are transferred without duplicate storage.
        private sealed class OwnedEntry : GlassPartialCacheEntry
        {
            internal readonly GlassPartialGeometryCache Owner;
            private readonly byte[] records,sourceRecords;
            private readonly uint[] sourceFaceBits;
            internal override int Index {get;}
            internal override int SourceStart {get;}
            internal override int ActualTriangles {get;}
            internal override int PaddedTriangles {get;}
            internal override int DestinationStart {get;}
            internal override uint FullMask {get;}
            internal override string SourceHash {get;}
            internal override byte[] Records=>(byte[])records.Clone();
            internal override byte[] SourceRecords=>(byte[])sourceRecords.Clone();
            internal override uint[] SourceFaceBits=>(uint[])sourceFaceBits.Clone();
            internal override IReadOnlyList<GlassNativeTriangleEdit> Edits {get;}
            internal OwnedEntry(GlassPartialGeometryCache owner,int index,int sourceStart,int actual,int padded,int destination,uint mask,
                string hash,byte[] source,uint[] labels,GlassNativeTriangleEdit[] edits,byte[] output)
            {
                Owner=owner;Index=index;SourceStart=sourceStart;ActualTriangles=actual;PaddedTriangles=padded;DestinationStart=destination;
                FullMask=mask;SourceHash=hash;sourceRecords=source;sourceFaceBits=labels;records=output;Edits=Array.AsReadOnly(edits);
            }
        }
        private sealed class Key : IEquatable<Key>
        {
            private readonly byte[] source;
            private readonly string metadata;
            private readonly int hash;
            internal Key(byte[] source,string sourceHash,int start,int actual,int padded,uint mask,uint[] labels,GlassNativeTriangleEdit[] edits)
            {
                this.source=source;var text=new StringBuilder();
                void Number(long value){text.Append(value.ToString(CultureInfo.InvariantCulture));text.Append(';');}
                Number(start);Number(actual);Number(padded);Number(mask);foreach(uint label in labels)Number(label);
                Number(edits.Length);
                foreach(var edit in edits)
                {
                    Number(edit.NativeTriangleIndex);Number(edit.Triangles.Count);
                    foreach(var t in edit.Triangles)
                    {text.Append(t.A.ToString());text.Append(';');text.Append(t.B.ToString());text.Append(';');text.Append(t.C.ToString());text.Append(';');}
                }
                metadata=text.ToString();hash=BitConverter.ToInt32(Digest(Encoding.UTF8.GetBytes(sourceHash+";"+metadata)),0);
            }
            // Digest collisions still require exact metadata AND every raw source byte to match.
            public bool Equals(Key other)=>other!=null&&metadata==other.metadata&&ExactBytes(source,other.source);
            public override bool Equals(object other)=>other is Key key&&Equals(key);
            public override int GetHashCode()=>hash;
        }
    }
}
