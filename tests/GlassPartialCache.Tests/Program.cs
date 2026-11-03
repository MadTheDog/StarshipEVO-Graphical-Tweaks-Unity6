using System.Globalization;
using System.Numerics;
using GraphicalTweaks;

static class Program
{
    const int Record=124,Triangle=372,Span12=4464;
    static int checks,failures;
    static void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
    static void F(byte[] b,int p,float value)=>BitConverter.GetBytes(value).CopyTo(b,p);
    static float F(byte[] b,int p)=>BitConverter.ToSingle(b,p);
    static void U(byte[] b,int p,uint value)=>BitConverter.GetBytes(value).CopyTo(b,p);
    static readonly GlassBarycentric A=new(1,0,0,1),B=new(0,1,0,1),C=new(0,0,1,1);
    static GlassBarycentricTriangle Quarter()=>new(A,new(1,1,0,2),new(1,0,1,2));
    static GlassNativeTriangleEdit Edit(int triangle=0)=>new(triangle,new[]{Quarter()});
    static byte[] Source(int actual=2,int padded=12)
    {
        var b=new byte[padded*Triangle];
        for(int t=0;t<actual;t++)for(int v=0;v<3;v++)
        {
            int p=t*Triangle+v*Record;float x=v==1?2:0,y=v==2?2:0;
            F(b,p,t*4+x);F(b,p+4,y);F(b,p+20,1);F(b,p+24,1);F(b,p+36,1);F(b,p+44,1);
            F(b,p+52,x);F(b,p+56,y);U(b,p+60,271);
            F(b,p+64,.5f);F(b,p+76,2);F(b,p+100,2);F(b,p+112,3);
        }
        return b;
    }
    static uint[] Bits(int actual=2,int padded=12){var b=new uint[padded];for(int i=0;i<actual;i++)b[i]=i==1?2u:1u;return b;}
    static bool Add(GlassPartialGeometryCache cache,out GlassPartialCacheEntry entry,byte[] source=null,int start=120,int actual=2,int padded=12,
        uint mask=0,uint[] bits=null,IReadOnlyList<GlassNativeTriangleEdit> edits=null,bool verified=true)
        =>cache.TryGetOrAdd(source??Source(actual,padded),start,actual,padded,mask,bits??Bits(actual,padded),edits??new[]{Edit()},verified,out entry,out _);
    static GlassPartialCacheEntry Seed(GlassPartialGeometryCache cache)
    {Check(Add(cache,out var e),"valid quarter cap must enter cache");return e;}
    static void RejectUnchanged(GlassPartialGeometryCache cache,Func<bool> action,string message)
    {
        var state=(cache.Count,cache.AllocatedTriangles,cache.RawBytes);
        Check(!action(),message);Check(state==(cache.Count,cache.AllocatedTriangles,cache.RawBytes),"rejection must preserve counters: "+message);
    }
    static void ReuseAndDescriptor()
    {
        var cache=new GlassPartialGeometryCache(1001);var e=Seed(cache);
        Check(e.Index==0&&e.SourceStart==120&&e.ActualTriangles==2&&e.PaddedTriangles==12&&e.FullMask==0,"entry keeps native identity/counts");
        Check(e.DestinationStart==1001&&cache.Count==1&&cache.AllocatedTriangles==12&&cache.RawBytes==8928,"first immutable append accounts source plus output bytes");
        Check(e.Descriptor.SourceStart==120&&e.Descriptor.DestinationStart==1001&&e.Descriptor.PaddedTriangles==12&&e.Descriptor.FullMask==0,"native descriptor fields are explicit uint triangle units");
        Check(e.Records.Length==Span12&&F(e.Records,Record)==1&&F(e.Records,Record+52)==1&&BitConverter.ToUInt32(e.Records,60)==271,"real writer interpolates position/UV and keeps material");
        Check(e.Records.AsSpan(Triangle,Triangle).SequenceEqual(Source().AsSpan(Triangle,Triangle)),"unedited native triangle remains byte exact");
        var calls=cache.WriterCallCount;Check(Add(cache,out var same)&&ReferenceEquals(e,same),"same exact pattern reuses immutable entry");
        Check(cache.WriterCallCount==calls&&calls==1&&cache.Count==1,"cache hit performs no writer work or append");
        Check(e.SourceHash==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Source())),"source hash is computed from all actual/padded owned bytes");
    }
    static void CanonicalOrderAndCulture()
    {
        var cache=new GlassPartialGeometryCache(1001);var edits=new[]{Edit(1),Edit(0)};
        Check(Add(cache,out var e,edits:edits),"two partial edits admit");var calls=cache.WriterCallCount;
        var culture=CultureInfo.CurrentCulture;
        try{CultureInfo.CurrentCulture=new CultureInfo("ar-SA");Check(Add(cache,out var same,edits:edits.Reverse().ToArray())&&ReferenceEquals(e,same),"edit-list order and culture do not alter key");}
        finally{CultureInfo.CurrentCulture=culture;}
        Check(cache.WriterCallCount==calls,"canonical hit avoids writer");
        var equivalent=new GlassBarycentricTriangle(A,new(2,2,0,4),new(2,0,2,4));
        Check(Add(cache,out var normalized,edits:new[]{new GlassNativeTriangleEdit(0,new[]{equivalent}),Edit(1)})&&ReferenceEquals(e,normalized),"canonical rational values reuse");
    }
    static void ExactIdentitySeparation()
    {
        var cache=new GlassPartialGeometryCache(1001);var first=Seed(cache);
        Check(Add(cache,out var mask,mask:2)&&mask.Index==1&&mask.FullMask==2,"complete mask separates keys");
        var bytes=Source();F(bytes,Triangle+112,4);
        Check(Add(cache,out var source,source:bytes)&&source.Index==2,"even unedited raw attributes separate source identity");
        Check(Add(cache,out var start,start:121)&&start.Index==3,"native sourceStart separates identity");
        var tiny=new GlassBarycentricTriangle(A,new(3,1,0,4),new(3,0,1,4));
        Check(Add(cache,out var edit,edits:new[]{new GlassNativeTriangleEdit(0,new[]{tiny})})&&edit.Index==4,"exact partial weights separate identity");
        Check(Add(cache,out var labels,bits:new uint[]{1,4,0,0,0,0,0,0,0,0,0,0})&&labels.Index==5,"source face labels separate identity");
        Check(Add(cache,out var count,actual:1)&&count.Index==6,"actual source count separates identity");
        var rotated=Quarter();var differentOrder=new GlassBarycentricTriangle(rotated.B,rotated.C,rotated.A);
        Check(Add(cache,out var vertices,edits:new[]{new GlassNativeTriangleEdit(0,new[]{differentOrder})})&&vertices.Index==7,"retained vertex order is preserved in key/output");
        Check(first.DestinationStart==1001&&mask.DestinationStart==1013&&source.DestinationStart==1025&&vertices.DestinationStart==1085,"append ranges are stable nonoverlapping and aligned relative to arbitrary tail base");
        Check(Add(cache,out var twoClusters,actual:13,padded:24)&&twoClusters.DestinationStart==1097&&twoClusters.PaddedTriangles==24&&cache.AllocatedTriangles==120,"12/24 strides append without crossing or reallocating existing slots");
        var sameBytes=Source();
        Check(Add(cache,out var differentActual,source:sameBytes,actual:3,bits:Bits())&&differentActual.Index==9,"actual count is keyed even when source bytes and labels are identical");
        var padded24=new byte[8928];Source().CopyTo(padded24,0);
        Check(Add(cache,out var differentPadded,source:padded24,actual:2,padded:24)&&differentPadded.Index==10,"padded stride is part of native source identity");
    }
    static void RetainedTriangleOrder()
    {
        var cache=new GlassPartialGeometryCache(200);
        var q=Quarter();var right=new GlassBarycentricTriangle(new(1,1,0,2),B,new(0,1,1,2));
        Check(Add(cache,out var e,actual:1,edits:new[]{new GlassNativeTriangleEdit(0,new[]{q,right})}),"two disjoint retained triangles admit");
        Check(Add(cache,out var reverse,actual:1,edits:new[]{new GlassNativeTriangleEdit(0,new[]{right,q})})&&reverse.Index==1,"retained triangle ordering remains exact");
        Check(F(e.Records,0)==0&&F(reverse.Records,0)==1,"writer respects retained triangle order");
    }
    static void Ownership()
    {
        var cache=new GlassPartialGeometryCache(1001);var bytes=Source();var labels=Bits();var edits=new[]{Edit()};
        Check(Add(cache,out var e,source:bytes,bits:labels,edits:edits),"ownership fixture admits");var expected=e.Records;var hash=e.SourceHash;
        F(bytes,0,99);labels[0]=4;edits[0]=Edit(1);
        var records=e.Records;Array.Fill(records,(byte)255);var ownedSource=e.SourceRecords;Array.Clear(ownedSource);var ownedLabels=e.SourceFaceBits;ownedLabels[0]=8;
        Check(e.Records.SequenceEqual(expected)&&F(e.SourceRecords,0)==0&&e.SourceFaceBits[0]==1&&e.SourceHash==hash,"input and getter mutation cannot corrupt owned entry");
        bool immutable=false;try{((IList<GlassNativeTriangleEdit>)e.Edits)[0]=Edit(1);}catch(NotSupportedException){immutable=true;}
        Check(immutable&&e.Edits[0].NativeTriangleIndex==0,"exposed edits are read-only owned snapshot");
        immutable=false;try{((IList<GlassBarycentricTriangle>)e.Edits[0].Triangles)[0]=new(A,B,C);}catch(NotSupportedException){immutable=true;}
        Check(immutable,"nested retained-triangle view cannot mutate entry");
        Check(Add(cache,out var again)&&ReferenceEquals(e,again)&&cache.WriterCallCount==1,"input/getter mutation cannot corrupt future lookup");
    }
    static void BudgetFailures()
    {
        var entries=new GlassPartialGeometryCache(1001,maximumEntries:1);var first=Seed(entries);var calls=entries.WriterCallCount;
        RejectUnchanged(entries,()=>Add(entries,out _,start:121),"entry capacity rejects new pattern");
        Check(Add(entries,out var hit)&&ReferenceEquals(first,hit)&&entries.WriterCallCount==calls,"capacity permits hits and prevents miss writer work");
        var triangles=new GlassPartialGeometryCache(1001,maximumTailTriangles:12);Seed(triangles);
        RejectUnchanged(triangles,()=>Add(triangles,out _,start:121),"tail triangle capacity is transactional");
        var bytes=new GlassPartialGeometryCache(1001,maximumRawBytes:8928);Seed(bytes);
        RejectUnchanged(bytes,()=>Add(bytes,out _,start:121),"owned source/output byte capacity is transactional");
        var insufficient=new GlassPartialGeometryCache(1001,maximumRawBytes:8927);
        RejectUnchanged(insufficient,()=>Add(insufficient,out _),"one-byte-short budget rejects before writer");Check(insufficient.WriterCallCount==0,"preallocation budget gate");
        int limit=int.MaxValue/Triangle;var addressing=new GlassPartialGeometryCache(limit-12);Seed(addressing);
        RejectUnchanged(addressing,()=>Add(addressing,out _,start:121),"destination byte-end overflow rejects append");
    }
    static void AdmissionFailures()
    {
        var cache=new GlassPartialGeometryCache(1001);Seed(cache);
        RejectUnchanged(cache,()=>Add(cache,out _,verified:false),"verified material token remains necessary even on a cached key");
        foreach(int start in new[]{0,-1,int.MaxValue})RejectUnchanged(cache,()=>Add(cache,out _,start:start),"invalid native source range");
        RejectUnchanged(cache,()=>cache.TryGetOrAdd(null,120,2,12,0,Bits(),new[]{Edit()},true,out _,out _),"null raw source");
        RejectUnchanged(cache,()=>Add(cache,out _,source:new byte[2]),"malformed source byte count");
        RejectUnchanged(cache,()=>Add(cache,out _,actual:0),"no actual triangles");
        RejectUnchanged(cache,()=>Add(cache,out _,actual:1,padded:36),"only initially audited padded strides admit");
        RejectUnchanged(cache,()=>Add(cache,out _,mask:256),"mask exceeds low eight bits");
        RejectUnchanged(cache,()=>Add(cache,out _,mask:4),"mask contains unknown face label");
        RejectUnchanged(cache,()=>Add(cache,out _,edits:Array.Empty<GlassNativeTriangleEdit>()),"masks-only uses static variants");
        RejectUnchanged(cache,()=>Add(cache,out _,edits:new[]{new GlassNativeTriangleEdit(0,new[]{new GlassBarycentricTriangle(A,B,C)})}),"identity edit is not partial");
        RejectUnchanged(cache,()=>Add(cache,out _,edits:new[]{new GlassNativeTriangleEdit(0,Array.Empty<GlassBarycentricTriangle>())}),"empty removal alone is not partial");
        RejectUnchanged(cache,()=>Add(cache,out _,mask:1),"partial edits entirely suppressed by full mask are not admitted");
        var halves=new[]{new GlassBarycentricTriangle(A,B,new(1,0,1,2)),new GlassBarycentricTriangle(B,C,new(1,0,1,2))};
        RejectUnchanged(cache,()=>Add(cache,out _,actual:1,edits:new[]{new GlassNativeTriangleEdit(0,halves)}),"uncut retessellation is not an actual partial edit");
        Check(Add(cache,out _,edits:new[]{Edit(),new GlassNativeTriangleEdit(1,Array.Empty<GlassBarycentricTriangle>())}),"partial pattern may also remove another triangle completely");
    }
    static void FrozenWriterFailures()
    {
        var cache=new GlassPartialGeometryCache(1001);Seed(cache);
        void BadSource(Action<byte[]> mutate,string message){var b=Source();mutate(b);long calls=cache.WriterCallCount;RejectUnchanged(cache,()=>Add(cache,out _,source:b),message);Check(cache.WriterCallCount==calls+1,"raw attributes are checked by real frozen writer");}
        BadSource(b=>F(b,Record+52,float.NaN),"nonfinite edited attribute");
        BadSource(b=>U(b,Record+60,272),"mixed material");
        BadSource(b=>F(b,Record+20,.5f),"nonflat normal");
        BadSource(b=>F(b,Record+64,1),"varying raw bounds");
        BadSource(b=>{for(int v=0;v<3;v++)F(b,v*Record+24,0);},"zero tangent basis");
        var labels=Bits();labels[11]=1;RejectUnchanged(cache,()=>Add(cache,out _,bits:labels),"padding face labels must be zero");
        var padding=Source();padding[2*Triangle]=1;RejectUnchanged(cache,()=>Add(cache,out _,source:padding),"source padding must be zero");
        RejectUnchanged(cache,()=>Add(cache,out _,edits:new GlassNativeTriangleEdit[]{null}),"null edit");
        RejectUnchanged(cache,()=>Add(cache,out _,edits:new[]{Edit(),Edit()}),"duplicate native IDs");
        RejectUnchanged(cache,()=>Add(cache,out _,edits:new[]{Edit(2)}),"edit must address actual source");
        RejectUnchanged(cache,()=>Add(cache,out _,edits:new[]{new GlassNativeTriangleEdit(0,new[]{default(GlassBarycentricTriangle)})}),"default malformed weights");
        RejectUnchanged(cache,()=>Add(cache,out _,edits:new[]{new GlassNativeTriangleEdit(0,new[]{new GlassBarycentricTriangle(A,C,B)})}),"reversed weights");
        RejectUnchanged(cache,()=>Add(cache,out _,edits:new[]{new GlassNativeTriangleEdit(0,new[]{Quarter(),Quarter()})}),"overlap rejected by frozen writer");
        var half=new GlassBarycentricTriangle(A,new(1,1,0,2),C);
        var disjoint=new GlassBarycentricTriangle(new(1,1,0,2),B,C);
        long before=cache.WriterCallCount;
        RejectUnchanged(cache,()=>Add(cache,out _,actual:12,edits:new[]{new GlassNativeTriangleEdit(0,new[]{Quarter(),new GlassBarycentricTriangle(new(1,1,0,2),B,new(0,1,1,2))})}),"12-triangle cluster overflow fails entire owner");
        Check(cache.WriterCallCount==before+1,"cluster overflow goes through frozen writer");
        before=cache.WriterCallCount;
        RejectUnchanged(cache,()=>Add(cache,out _,edits:new[]{new GlassNativeTriangleEdit(0,new[]{default(GlassBarycentricTriangle)})}),"malformed default weights use real writer validation");
        Check(cache.WriterCallCount==before+1,"malformed weights reached frozen writer");
        RejectUnchanged(cache,()=>Add(cache,out _,edits:new[]{new GlassNativeTriangleEdit(0,Enumerable.Repeat(Quarter(),13).ToArray())}),"fragment count bounded before key/writer work");
        GlassPartialCacheEntry failedEntry;string failureReason;
        Check(!cache.TryGetOrAdd(Source(),120,2,12,0,Bits(),new[]{Edit()},false,out failedEntry,out failureReason)&&failedEntry==null&&!string.IsNullOrWhiteSpace(failureReason),"rejection exposes no entry and explains failure");
        Check(Add(cache,out var still)&&still.Index==0,"writer failures leave prior immutable entry reusable");
    }
    static void CompleteCeilingsAndReset()
    {
        var cache=new GlassPartialGeometryCache(1001);
        for(int i=0;i<256;i++)
        {
            Check(Add(cache,out var entry,start:120+i,actual:13,padded:24)&&entry.Index==i&&entry.DestinationStart==1001+i*24,"every maximum-capacity append has stable descriptor and nonoverlapping aligned range");
        }
        Check(cache.Count==256&&cache.AllocatedTriangles==6144&&cache.RawBytes==4571136,"hard ceilings account all retained source/output bytes");
        RejectUnchanged(cache,()=>Add(cache,out _,start:1000,actual:13,padded:24),"257th unique pattern cannot append");
        Check(Add(cache,out var old,start:120,actual:13,padded:24)&&old.Index==0&&old.DestinationStart==1001,"maximum-capacity cache retains earliest immutable entry");
        var replacement=new GlassPartialGeometryCache(2001);var reset=Seed(replacement);
        Check(reset.Index==0&&reset.DestinationStart==2001&&cache.Count==256,"new resource generation uses a fresh cache without mutating old entries");
    }
    static void ConstructorBounds()
    {
        void Reject(Func<GlassPartialGeometryCache> make){bool failed=false;try{make();}catch(ArgumentOutOfRangeException){failed=true;}Check(failed,"constructor rejects unsafe limits");}
        Reject(()=>new(0));Reject(()=>new(-1));Reject(()=>new(int.MaxValue));Reject(()=>new(1,257));Reject(()=>new(1,-1));
        Reject(()=>new(1,maximumTailTriangles:6145));Reject(()=>new(1,maximumTailTriangles:-1));Reject(()=>new(1,maximumRawBytes:8388609));Reject(()=>new(1,maximumRawBytes:-1));
        var disabled=new GlassPartialGeometryCache(1,0,0,0);RejectUnchanged(disabled,()=>Add(disabled,out _),"zero budgets disable admission");
    }
    // A caller can supply a view whose visible fields match a real entry.
    // Provenance must reject it rather than authorize upload from its fields.
    sealed class ForgedEntry : GlassPartialCacheEntry
    {
        readonly GlassPartialCacheEntry copied;
        internal ForgedEntry(GlassPartialCacheEntry copied){this.copied=copied;}
        internal override int Index=>copied.Index;
        internal override int SourceStart=>copied.SourceStart;
        internal override int ActualTriangles=>copied.ActualTriangles;
        internal override int PaddedTriangles=>copied.PaddedTriangles;
        internal override int DestinationStart=>copied.DestinationStart;
        internal override uint FullMask=>copied.FullMask;
        internal override string SourceHash=>copied.SourceHash;
        internal override byte[] Records=>copied.Records;
        internal override byte[] SourceRecords=>copied.SourceRecords;
        internal override uint[] SourceFaceBits=>copied.SourceFaceBits;
        internal override IReadOnlyList<GlassNativeTriangleEdit> Edits=>copied.Edits;
    }
    static void CacheProvenance()
    {
        var cache=new GlassPartialGeometryCache(1001);var real=Seed(cache);
        Check(cache.Owns(real),"cache authorizes its own minted concrete entry");
        Check(!cache.Owns(null),"null has no cache provenance");
        var replacement=new GlassPartialGeometryCache(1001);var sameDescriptor=Seed(replacement);
        Check(real.Descriptor.SourceStart==sameDescriptor.Descriptor.SourceStart&&real.Descriptor.DestinationStart==sameDescriptor.Descriptor.DestinationStart&&real.SourceHash==sameDescriptor.SourceHash,"cross-cache fixture has identical visible source/descriptor identity");
        Check(!cache.Owns(sameDescriptor)&&!replacement.Owns(real)&&replacement.Owns(sameDescriptor),"entry provenance belongs to exactly its minting cache");
        var forged=new ForgedEntry(real);
        Check(forged.Index==real.Index&&forged.Records.SequenceEqual(real.Records)&&forged.Descriptor.SourceStart==real.Descriptor.SourceStart,"forged fixture reproduces genuine entry fields and output");
        Check(!cache.Owns(forged)&&!replacement.Owns(forged),"matching abstract entry view does not authorize native upload");
        RejectUnchanged(cache,()=>Add(cache,out _,verified:false),"failed admission leaves minted provenance valid");
        Check(cache.Owns(real)&&Add(cache,out var hit)&&ReferenceEquals(real,hit)&&cache.Owns(hit),"immutable provenance survives rejection and exact-key reuse");
    }
    static void AllocationBoundsView()
    {
        var cache=new GlassPartialGeometryCache(1001,maximumEntries:2,maximumTailTriangles:24);
        Check(cache.TailBaseTriangle==1001&&cache.MaximumEntries==2&&cache.MaximumTailTriangles==24,"caller sees exact configured native allocation bounds");
        var first=Seed(cache);Check(Add(cache,out var second,start:121),"configured bounds permit two 12-triangle entries");
        Check(first.DestinationStart==cache.TailBaseTriangle&&second.DestinationStart+second.PaddedTriangles==cache.TailBaseTriangle+cache.MaximumTailTriangles&&cache.Count==cache.MaximumEntries,"exposed bounds match actual appended tail range and descriptor capacity");
        RejectUnchanged(cache,()=>Add(cache,out _,start:122),"configured allocation view matches exhausted cache");
        Check(cache.TailBaseTriangle==1001&&cache.MaximumEntries==2&&cache.MaximumTailTriangles==24,"readonly configured bounds remain stable after appends and rejection");
    }
    static int Main()
    {
        foreach(var test in new Action[]{ReuseAndDescriptor,CanonicalOrderAndCulture,ExactIdentitySeparation,RetainedTriangleOrder,Ownership,BudgetFailures,AdmissionFailures,FrozenWriterFailures,CompleteCeilingsAndReset,ConstructorBounds,CacheProvenance,AllocationBoundsView})
        {try{test();Console.WriteLine("PASS "+test.Method.Name);}catch(Exception ex){failures++;Console.WriteLine("FAIL "+test.Method.Name+": "+ex.Message);}}
        Console.WriteLine($"{(failures==0?"PASS":"FAIL")} {checks} immutable partial cache checks; {failures} failing groups; UNCONNECTED CPU prototype, native acceptance unproven.");return failures==0?0:1;
    }
}
