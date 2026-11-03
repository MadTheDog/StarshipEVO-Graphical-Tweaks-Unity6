using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GraphicalTweaks;

static class Program
{
    const int R=124,T=372;
    static int checks,failures;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void F(byte[] b,int p,float v)=>BitConverter.GetBytes(v).CopyTo(b,p);
    static byte[] Source(int actual=2,int padded=12)
    {
        var b=new byte[padded*T];for(int t=0;t<actual;t++)for(int v=0;v<3;v++)
        {int p=t*T+v*R;F(b,p,t*4+(v==1?2:0));F(b,p+4,v==2?2:0);F(b,p+20,1);F(b,p+24,1);F(b,p+36,1);F(b,p+44,1);BitConverter.GetBytes(271u).CopyTo(b,p+60);}
        return b;
    }
    static uint[] Bits(int actual=2,int padded=12){var a=new uint[padded];for(int i=0;i<actual;i++)a[i]=i==1?2u:1u;return a;}
    static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
    static GlassNativeTriangleEdit Edit()=>new(0,new[]{new GlassBarycentricTriangle(new(1,0,0,1),new(1,1,0,2),new(1,0,1,2))});
    sealed class Fixture
    {
        internal readonly GlassShapeVariantLayout Layout;
        internal readonly GlassPartialGeometryCache Cache;
        internal readonly int ShapeCount;
        internal readonly int Actual,Padded;
        internal GlassPartialTailSource[] Sources;
        internal bool Current=true;
        internal Fixture(int capacity=4,int tail=48,int actual=2,int padded=12)
        {
            Actual=actual;Padded=padded;
            Check(GlassShapeVariantLayout.TryCreate(303,new[]{new GlassShapeVariantSource(1,5,actual,padded,2,Bits(actual,padded))},1024*1024,out Layout),"synthetic native layout admitted");
            Cache=new(Layout.OwnedVertices/3,capacity,tail);ShapeCount=Layout.OwnedVertices+tail*3;
            Sources=new[]{new GlassPartialTailSource(5,actual,padded,3,Hash(Source(actual,padded)))};
        }
        internal GlassPartialCacheEntry Entry(int sourceStart=5,int? actual=null,int? padded=null,uint mask=0,uint[] bits=null)
        {int a=actual??Actual,p=padded??Padded;Check(Cache.TryGetOrAdd(Source(a,p),sourceStart,a,p,mask,bits??Bits(a,p),new[]{Edit()},true,out var e,out var reason),"synthetic cache entry: "+reason);return e;}
        internal GlassPartialTailPolicy Policy(Func<bool> current=null)
        {Check(GlassPartialTailPolicy.TryCreate(ShapeCount,124,Layout,Cache,Sources,current??(()=>Current),out var p,out var reason),"tail policy creation: "+reason);return p;}
    }
    sealed class Sink
    {
        internal readonly List<string> Events=new();
        internal byte[] Records;
        internal GlassPartialTailDescriptor Descriptor;
        internal int Offset,Count;
        internal void Shape(GlassPartialTailPacket packet){Events.Add("shape");Records=packet.Records;Offset=packet.DestinationVertex;Count=packet.VertexCount;}
        internal void Descriptors(GlassPartialTailPacket packet){Events.Add("descriptor");Descriptor=packet.Descriptor;}
    }
    static bool Upload(Fixture f,GlassPartialTailPolicy p,GlassPartialCacheEntry e,Sink sink,out uint encoded,int? shapeCount=null,int shapeStride=124,int? descCount=null,int descStride=16)
        =>p.TryUpload(e,shapeCount??f.ShapeCount,shapeStride,descCount??p.DescriptorBufferCount,descStride,sink.Shape,sink.Descriptors,out encoded,out _);
    static void ExactPacketAndReuse()
    {
        var f=new Fixture();var e=f.Entry(mask:2);var p=f.Policy();var sink=new Sink();
        Check(p.Cache==f.Cache&&p.TailTriangleStart==149&&p.DescriptorCapacity==4&&p.DescriptorBufferCount==4&&p.DescriptorBytes==64,"frozen native allocation API");
        Check(Upload(f,p,e,sink,out var encoded)&&encoded==258&&p.UploadedCount==1,"authorization encodes high index+1 and low fullmask after both writes");
        Check(sink.Events.SequenceEqual(new[]{"shape","descriptor"})&&sink.Offset==447&&sink.Count==36&&sink.Records.Length==4464,"exact raw tail offset/count and write ordering");
        Check(sink.Records.SequenceEqual(e.Records)&&sink.Descriptor.SourceStart==5&&sink.Descriptor.DestinationStart==149&&sink.Descriptor.PaddedTriangles==12&&sink.Descriptor.FullMask==2,"exact records and uint4 descriptor packet");
        Check(Marshal.SizeOf<GlassPartialTailDescriptor>()==16,"descriptor upload struct has 16-byte ABI");
        Check(Upload(f,p,e,sink,out encoded)&&encoded==258&&sink.Events.Count==2&&p.UploadedCount==1,"same immutable entry reuses authorization without writes/copies");
    }
    static void HolesAndMaximumSlot()
    {
        var f=new Fixture(256,3072);var entries=new List<GlassPartialCacheEntry>();
        // Unique source byte patterns are all bound to the same source identity only when
        // their exact hash matches; vary partial barycentric pattern instead.
        for(int i=1;i<=256;i++)
        {
            var triangle=new GlassBarycentricTriangle(new(1,0,0,1),new(i,1,0,i+1),new(i,0,1,i+1));
            Check(f.Cache.TryGetOrAdd(Source(),5,2,12,0,Bits(),new[]{new GlassNativeTriangleEdit(0,new[]{triangle})},true,out var e,out _),"unique exact partial pattern admits");entries.Add(e);
        }
        var p=f.Policy();var sink=new Sink();
        Check(Upload(f,p,entries[255],sink,out var code)&&code==65536&&p.UploadedCount==256,"slot 256 encoding and highest+1 count tolerate unuploaded holes");
        Check(sink.Offset==9627&&sink.Descriptor.DestinationStart==3209,"last descriptor redirects within reserved custom tail");
        Check(Upload(f,p,entries[0],sink,out code)&&code==256&&p.UploadedCount==256,"later filling a hole cannot shrink uploaded descriptor range");
        Check(Upload(f,p,entries[255],sink,out _)&&sink.Events.Count==4,"existing slot remains immutable after holes fill");
    }
    static void TwentyFourTrianglePacket()
    {
        var f=new Fixture(actual:13,padded:24);var e=f.Entry();var p=f.Policy();var sink=new Sink();
        Check(Upload(f,p,e,sink,out var encoded)&&encoded==256&&sink.Offset==591&&sink.Count==72&&sink.Records.Length==8928,"24-triangle source uploads two whole original clusters in its own tail range");
        Check(sink.Descriptor.SourceStart==5&&sink.Descriptor.DestinationStart==197&&sink.Descriptor.PaddedTriangles==24,"24-triangle descriptor retains original native identity/stride");
        Check(sink.Records.AsSpan(12*T,T).SequenceEqual(Source(13,24).AsSpan(12*T,T)),"second-cluster native raw triangle is preserved exactly");
    }
    sealed class Forged : GlassPartialCacheEntry
    {
        readonly GlassPartialCacheEntry e;internal Forged(GlassPartialCacheEntry e){this.e=e;}
        internal override int Index=>e.Index;internal override int SourceStart=>e.SourceStart;internal override int ActualTriangles=>e.ActualTriangles;
        internal override int PaddedTriangles=>e.PaddedTriangles;internal override int DestinationStart=>e.DestinationStart;internal override uint FullMask=>e.FullMask;
        internal override string SourceHash=>e.SourceHash;internal override byte[] Records=>throw new Exception("forged records must never be read");
        internal override byte[] SourceRecords=>throw new Exception("forged source must never be read");
        internal override uint[] SourceFaceBits=>throw new Exception("forged labels must never be read");
        internal override IReadOnlyList<GlassNativeTriangleEdit> Edits=>e.Edits;
    }
    static void ProvenanceAndBindings()
    {
        var f=new Fixture(16,256);var e=f.Entry();var p=f.Policy();var sink=new Sink();var foreign=new Fixture().Entry();
        foreach(var bad in new GlassPartialCacheEntry[]{null,foreign,new Forged(e)})Check(!Upload(f,p,bad,sink,out var code)&&code==0&&p.UploadedCount==0&&sink.Events.Count==0,"foreign/forged/null cannot produce a packet");
        var other=f.Entry(sourceStart:6);Check(!Upload(f,p,other,sink,out _)&&sink.Events.Count==0,"unbound native source range rejects");
        var wrongCount=f.Entry(actual:3);Check(!Upload(f,p,wrongCount,sink,out _),"actual triangle count matches trusted source");
        var wrongStride=f.Entry(padded:24);Check(!Upload(f,p,wrongStride,sink,out _),"padded stride matches binding");
        var labels=Bits();labels[0]=2;labels[1]=1;var swapped=f.Entry(bits:labels);Check(!Upload(f,p,swapped,sink,out _),"native face-label snapshot matches cache source labels");
        var badMask=f.Entry(mask:4,bits:new uint[]{1,4,0,0,0,0,0,0,0,0,0,0});Check(!Upload(f,p,badMask,sink,out _),"fullmask subset must match trusted valid mask");
        var h=new Fixture();h.Sources[0]=new(5,2,12,3,new string('A',64));var hp=h.Policy();Check(!Upload(h,hp,h.Entry(),sink,out _)&&sink.Events.Count==0,"cache source digest must equal caller-trusted catalog binding");
        Check(Upload(f,p,e,sink,out _)&&p.UploadedCount==1,"rejections retain valid source path");
    }
    static void AbiAndDestinationBounds()
    {
        var f=new Fixture();var p=f.Policy();var e=f.Entry();var sink=new Sink();
        Check(!Upload(f,p,e,sink,out _,shapeStride:4)&&!Upload(f,p,e,sink,out _,shapeCount:f.ShapeCount-1)&&!Upload(f,p,e,sink,out _,descStride:4)&&!Upload(f,p,e,sink,out _,descCount:3)&&sink.Events.Count==0,"every actual shape/descriptor stride/count checked before writes");
        Check(GlassPartialTailPolicy.TryValidateDestination(149,12,149,48,591,out int start,out int count,out _)&&start==447&&count==36,"tail alignment is relative; global start need not be divisible by 12");
        foreach(var b in new[]{(148,12,149,48,591),(150,12,149,48,591),(149,13,149,48,591),(185,24,149,48,591),(149,12,149,48,480),(int.MaxValue,24,149,48,591),(-1,12,149,48,591)})
            Check(!GlassPartialTailPolicy.TryValidateDestination(b.Item1,b.Item2,b.Item3,b.Item4,b.Item5,out start,out count,out _)&&start==0&&count==0,"invalid relative alignment/range/overflow never returns accepted offsets");
        Check(p.UploadedCount==0,"invalid ABI/range cannot advance uploaded count");
    }
    static void StaleAndFailureOrdering()
    {
        var f=new Fixture();var e=f.Entry();var p=f.Policy();var sink=new Sink();f.Current=false;
        Check(!Upload(f,p,e,sink,out var code)&&code==0&&sink.Events.Count==0,"stale before planning cannot write/authorize");f.Current=true;
        Check(!p.TryUpload(e,f.ShapeCount,124,4,16,packet=>{sink.Shape(packet);f.Current=false;},sink.Descriptors,out code,out _)&&code==0&&sink.Events.SequenceEqual(new[]{"shape"})&&p.UploadedCount==0,"stale after raw write blocks descriptor and authorization");
        f.Current=true;sink=new Sink();
        Check(!p.TryUpload(e,f.ShapeCount,124,4,16,sink.Shape,packet=>{sink.Descriptors(packet);f.Current=false;},out code,out _)&&code==0&&sink.Events.Count==2&&p.UploadedCount==0,"stale after descriptor write leaves unreferenced immutable bytes without authorization");
        f.Current=true;Check(Upload(f,p,e,new Sink(),out code)&&code==256&&p.UploadedCount==1,"only completed current upload can authorize");
        f.Current=false;Check(!Upload(f,p,e,new Sink(),out code)&&code==0&&p.UploadedCount==1,"cache hit also requires current generation");
        var g=new Fixture();var gp=g.Policy();var ge=g.Entry();var failed=new Sink();
        Check(!gp.TryUpload(ge,g.ShapeCount,124,4,16,packet=>throw new InvalidOperationException("raw failed"),failed.Descriptors,out code,out _)&&code==0&&gp.UploadedCount==0&&failed.Events.Count==0,"raw exception prevents descriptor authorization");
        Check(!gp.TryUpload(ge,g.ShapeCount,124,4,16,failed.Shape,packet=>throw new InvalidOperationException("descriptor failed"),out code,out _)&&code==0&&gp.UploadedCount==0,"descriptor exception prevents authorization without disposal");
        foreach(int limit in new[]{2,3})
        {
            int calls=0;var transient=new Fixture();var tp=transient.Policy(()=>++calls<=limit);var untouched=new Sink();
            Check(!Upload(transient,tp,transient.Entry(),untouched,out code)&&code==0&&untouched.Events.Count==0&&tp.UploadedCount==0,"predicate can revoke generation before/after planning without a packet being accepted");
        }
        var retired=new Fixture();var rp=retired.Policy();var re=retired.Entry();rp.Deactivate();
        Check(!Upload(retired,rp,re,new Sink(),out code)&&code==0,"deactivated policy rejects all further authorization");
    }
    static void CreationValidationAndSnapshots()
    {
        var f=new Fixture();
        bool Create(GlassShapeVariantLayout l,GlassPartialGeometryCache c,GlassPartialTailSource[] s,int? count=null,int stride=124,Func<bool> current=null)
            =>GlassPartialTailPolicy.TryCreate(count??f.ShapeCount,stride,l,c,s,current??(()=>true),out _,out _);
        Check(!Create(f.Layout,f.Cache,f.Sources,stride:4)&&!Create(f.Layout,f.Cache,f.Sources,count:int.MaxValue),"creation rejects borrowed ABI/overflow");
        Check(!Create(f.Layout,new GlassPartialGeometryCache(150,4,48),f.Sources),"cache tail must begin after entire native/static layout");
        foreach(var s in new[]{new GlassPartialTailSource(6,2,12,3,Hash(Source())),new(5,2,24,3,Hash(Source())),new(5,2,12,1,Hash(Source())),new(5,0,12,3,Hash(Source())),new(5,2,12,3,"bad"),new(5,2,12,3,new string('Z',64))})
            Check(!Create(f.Layout,f.Cache,new[]{s}),"trusted binding must be numerically compatible with native layout and 64hex digest");
        Check(!Create(f.Layout,f.Cache,new[]{f.Sources[0],f.Sources[0]}),"duplicate source binding rejected");
        Check(!Create(f.Layout,f.Cache,f.Sources,current:()=>false)&&!Create(f.Layout,f.Cache,f.Sources,current:()=>throw new Exception("epoch failed")),"stale/throwing predicate rejects construction");
        int checksCurrent=0;Check(!Create(f.Layout,f.Cache,f.Sources,current:()=>++checksCurrent==1),"stale after creation snapshot rejects policy");
        var p=f.Policy();var e=f.Entry();f.Sources[0]=new(99,1,24,0,"invalid");Array.Clear(f.Layout.Clusters);Array.Clear(f.Layout.TriangleFaceBits);Array.Clear(f.Layout.Allocations);
        Check(Upload(f,p,e,new Sink(),out _),"trusted binding/layout snapshots survive caller array mutation");
        Check(!Create(f.Layout,f.Cache,new[]{new GlassPartialTailSource(5,2,12,3,Hash(Source()))}),"new policy rejects malformed current layout tables");
        var badCluster=new Fixture();badCluster.Layout.Clusters[5]=new(101,12,3,6);
        Check(!GlassPartialTailPolicy.TryCreate(badCluster.ShapeCount,124,badCluster.Layout,badCluster.Cache,badCluster.Sources,()=>true,out _,out _),"cluster source identity must match the static allocation source");
        var badExtent=new Fixture();badExtent.Layout.Allocations[0]=new(new GlassShapeVariantSource(1,5,2,12,2,Bits()),102);
        Check(!GlassPartialTailPolicy.TryCreate(badExtent.ShapeCount,124,badExtent.Layout,badExtent.Cache,badExtent.Sources,()=>true,out _,out _),"static variant extent must begin at the native prefix and fill the owned static layout");
        var outside=new Fixture();outside.Layout.TriangleFaceBits[0]=1;
        Check(!GlassPartialTailPolicy.TryCreate(outside.ShapeCount,124,outside.Layout,outside.Cache,outside.Sources,()=>true,out _,out _),"unexpected labels outside native source allocations reject creation");
    }
    static void ZeroCapacity()
    {
        var f=new Fixture(0,0);var p=f.Policy();Check(p.DescriptorCapacity==0&&p.DescriptorBufferCount==1&&p.DescriptorBytes==16&&p.UploadedCount==0,"disabled cache owns one zero descriptor dummy and advertises zero slots");
        Check(p.TryValidateBuffers(f.ShapeCount,124,1,16,out _)&&!p.TryValidateBuffers(f.ShapeCount,124,0,16,out _),"dummy descriptor ABI requires actual one-row allocation");
    }
    static void ReentrantDeactivation()
    {
        var f=new Fixture();var entry=f.Entry();GlassPartialTailPolicy policy=null;int calls=0;
        policy=f.Policy(()=>{calls++;if(calls==6)policy.Deactivate();return true;});
        var sink=new Sink();bool accepted=Upload(f,policy,entry,sink,out var encoded);
        Check(calls==6&&sink.Events.SequenceEqual(new[]{"shape","descriptor"}),"repro deactivates inside final predicate after both writes");
        Check(!accepted&&encoded==0&&policy.UploadedCount==0,"reentrant final-predicate deactivation must block authorization/count advance; actual accepted="+accepted+", encoded="+encoded+", count="+policy.UploadedCount);
        Check(!policy.TryValidateCurrent(out _)&&!Upload(f,policy,entry,new Sink(),out encoded)&&encoded==0,"reentrant deactivation is permanent and prevents retries");

        var hitFixture=new Fixture();var hitEntry=hitFixture.Entry();GlassPartialTailPolicy hitPolicy=null;calls=0;
        hitPolicy=hitFixture.Policy(()=>{calls++;if(calls==8)hitPolicy.Deactivate();return true;});var hitSink=new Sink();
        Check(Upload(hitFixture,hitPolicy,hitEntry,hitSink,out encoded)&&encoded==256,"cache-hit repro first completes a current immutable upload");
        Check(!Upload(hitFixture,hitPolicy,hitEntry,hitSink,out encoded)&&encoded==0&&hitPolicy.UploadedCount==1&&hitSink.Events.Count==2,"reentrant final cache-hit predicate cannot reauthorize disposed policy or rewrite accepted slots");
    }
    static int Main()
    {
        foreach(var test in new Action[]{ExactPacketAndReuse,HolesAndMaximumSlot,TwentyFourTrianglePacket,ProvenanceAndBindings,AbiAndDestinationBounds,StaleAndFailureOrdering,CreationValidationAndSnapshots,ZeroCapacity,ReentrantDeactivation})
        {try{test();Console.WriteLine("PASS "+test.Method.Name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+test.Method.Name+": "+e.Message);}}
        Console.WriteLine($"{(failures==0?"PASS":"FAIL")} {checks} partial tail policy checks; {failures} failing groups; UNCONNECTED, actual Unity/GPU/lifetime unproven.");return failures==0?0:1;
    }
}
