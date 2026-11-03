using System.Numerics;
using GraphicalTweaks;

static class Program
{
    static int checks;
    const int Record=124,Triangle=372;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void F(byte[] bytes,int offset,float value)=>BitConverter.GetBytes(value).CopyTo(bytes,offset);
    static float F(byte[] bytes,int offset)=>BitConverter.ToSingle(bytes,offset);
    static void U(byte[] bytes,int offset,uint value)=>BitConverter.GetBytes(value).CopyTo(bytes,offset);
    static uint U(byte[] bytes,int offset)=>BitConverter.ToUInt32(bytes,offset);
    static GlassBarycentric W(int a,int b,int c,int d=1)=>new(a,b,c,d);
    static readonly GlassBarycentric A=W(1,0,0),B=W(0,1,0),C=W(0,0,1);
    static GlassBarycentricTriangle Quarter()=>new(A,W(1,1,0,2),W(1,0,1,2));
    static byte[] Source(int actual=1,int padded=12)
    {
        var bytes=new byte[padded*Triangle];
        for(int t=0;t<actual;t++)for(int v=0;v<3;v++)
        {
            int p=t*Triangle+v*Record;float x=v==1?2:0,y=v==2?2:0;
            F(bytes,p,x+t*4);F(bytes,p+4,y);F(bytes,p+8,0);
            F(bytes,p+12,0);F(bytes,p+16,0);F(bytes,p+20,1);
            F(bytes,p+24,v==0?1:v==2?-1:0);F(bytes,p+28,v==1?1:0);F(bytes,p+32,0);F(bytes,p+36,1);
            F(bytes,p+40,v==1?-1:0);F(bytes,p+44,v==0?1:v==2?-1:0);F(bytes,p+48,0);
            F(bytes,p+52,x);F(bytes,p+56,y);U(bytes,p+60,(uint)(271+t));
            for(int i=0;i<3;i++){F(bytes,p+64+i*4,.5f);F(bytes,p+76+i*4,i==2?1:2);F(bytes,p+88+i*4,0);F(bytes,p+100+i*4,i==2?0:2);F(bytes,p+112+i*4,3+i+v*3);}
        }
        return bytes;
    }
    static uint[] Bits(int actual,int padded=12){var bits=new uint[padded];for(int i=0;i<Math.Min(actual,padded);i++)bits[i]=1;return bits;}
    static void QuarterTriangle()
    {
        var source=Source();var edit=new GlassNativeTriangleEdit(0,new[]{Quarter()});
        Check(GlassPartialGeometry.TryBuild(source,1,12,0,Bits(1),new[]{edit},true,out var result,out var reason),"quarter triangle should build: "+reason);
        Check(result.Changed&&result.Records.Length==source.Length,"same padded size and changed source geometry");
        var expected=new[]{(0f,0f),(1f,0f),(0f,1f)};
        for(int v=0;v<3;v++)
        {
            int at=v*Record;Check(F(result.Records,at)==expected[v].Item1&&F(result.Records,at+4)==expected[v].Item2,"independent quarter-position expectation");
            Check(F(result.Records,at+52)==expected[v].Item1&&F(result.Records,at+56)==expected[v].Item2,"independent quarter-UV expectation");
            Check(U(result.Records,at+60)==271,"material integer preserved");
            Check(F(result.Records,at+24)==1&&F(result.Records,at+28)==0&&F(result.Records,at+44)==1,"finite source basis copied instead of interpolating cancellation");
        }
    }
    static bool Build(byte[] source,int actual,int padded,uint mask,uint[] bits,GlassNativeTriangleEdit[] edits,
        out GlassPartialGeometryResult result,bool eligibility=true)=>
        GlassPartialGeometry.TryBuild(source,actual,padded,mask,bits,edits,eligibility,out result,out _);
    static void Reject(byte[] source,int actual=1,int padded=12,uint mask=0,uint[] bits=null,
        GlassNativeTriangleEdit[] edits=null,bool eligibility=true)
    {
        Check(!Build(source,actual,padded,mask,bits??Bits(actual,padded),edits??new[]{new GlassNativeTriangleEdit(0,new[]{Quarter()})},out var result,eligibility)&&result==null,
            "invalid or unsafe input must fail the entire owner without output");
    }
    static GlassBarycentricTriangle[] Halves()
    {var middle=W(0,1,1,2);return new[]{new GlassBarycentricTriangle(A,B,middle),new GlassBarycentricTriangle(A,middle,C)};}
    static void UnchangedAndOwnership()
    {
        var source=Source(3);var before=(byte[])source.Clone();
        Check(Build(source,3,12,0,Bits(3),Array.Empty<GlassNativeTriangleEdit>(),out var unchanged)&&!unchanged.Changed,"unedited input remains unchanged");
        Check(unchanged.Records.SequenceEqual(source)&&!ReferenceEquals(unchanged.Records,source),"unchanged result is a byte-exact owned copy");
        var identity=new GlassNativeTriangleEdit(1,new[]{new GlassBarycentricTriangle(A,B,C)});
        Check(Build(source,3,12,0,Bits(3),new[]{identity},out var same)&&!same.Changed&&same.Records.SequenceEqual(source),"identity edit preserves original varying tangent records exactly");
        var weights=new[]{Quarter()};var immutable=new GlassNativeTriangleEdit(0,weights);weights[0]=new(A,B,C);
        Check(Build(source,3,12,0,Bits(3),new[]{immutable},out var edited)&&F(edited.Records,Record)==1,"edit snapshots caller triangle arrays");
        Check(source.SequenceEqual(before),"builder leaves source bytes unchanged");
        F(source,0,100);Check(F(unchanged.Records,0)==0&&F(edited.Records,0)==0,"later source mutation cannot change result records");
        bool readOnly=false;try{((IList<GlassBarycentricTriangle>)immutable.Triangles)[0]=new(A,B,C);}catch(NotSupportedException){readOnly=true;}
        Check(readOnly,"edit exposes a read-only retained-triangle view");
    }
    static void MaskAndClusterOrdering()
    {
        var source=Source(3);var bits=Bits(3);bits[1]=2;
        Check(Build(source,3,12,2,bits,Array.Empty<GlassNativeTriangleEdit>(),out var masked),"full mask omits complete source triangles");
        Check(masked.SourceTriangleIndices.Take(2).SequenceEqual(new[]{0,2})&&masked.SourceTriangleIndices.Skip(2).All(x=>x==-1),"survivors keep original order and filler has no source tag");
        Check(masked.Records.AsSpan(0,Triangle).SequenceEqual(source.AsSpan(0,Triangle))&&
            masked.Records.AsSpan(Triangle,Triangle).SequenceEqual(source.AsSpan(2*Triangle,Triangle)),"untouched surviving triangle chunks are byte-exact");
        for(int t=2;t<12;t++)Check(masked.Records.AsSpan(t*Triangle,Record).SequenceEqual(masked.Records.AsSpan(t*Triangle+Record,Record))&&
            masked.Records.AsSpan(t*Triangle,Record).SequenceEqual(masked.Records.AsSpan(t*Triangle+2*Record,Record)),"padding repeats complete records");
        Check(Build(Source(1),1,12,1,Bits(1),Array.Empty<GlassNativeTriangleEdit>(),out var hidden)&&hidden.SourceTriangleIndices.All(x=>x==-1),"fully hidden owner emits only degenerate filler");
        var twoClusters=Source(13,24);var edits=new[]{new GlassNativeTriangleEdit(0,Halves())};
        Reject(twoClusters,13,24,0,Bits(13,24),edits);
        var labels=Bits(13,24);labels[1]=2;
        Check(Build(twoClusters,13,24,2,labels,edits,out var packed),"space freed inside the same cluster permits subdivision");
        Check(packed.SourceTriangleIndices.Take(12).SequenceEqual(new[]{0,0,2,3,4,5,6,7,8,9,10,11})&&
            packed.SourceTriangleIndices[12]==12&&packed.SourceTriangleIndices.Skip(13).All(x=>x==-1),"subdivision cannot move source triangles between native clusters");
        Check(packed.Records.AsSpan(12*Triangle,Triangle).SequenceEqual(twoClusters.AsSpan(12*Triangle,Triangle)),"second-cluster native triangle remains untouched");
        Check(Build(Source(2),2,12,0,Bits(2),new[]{new GlassNativeTriangleEdit(0,Array.Empty<GlassBarycentricTriangle>())},out var empty)&&
            empty.SourceTriangleIndices[0]==1,"empty retained set removes only its source triangle");
        var full=Source(12);var fullLabels=Bits(12);fullLabels[11]=2;
        Check(Build(full,12,12,2,fullLabels,Array.Empty<GlassNativeTriangleEdit>(),out var last),"full native cluster can lose its final triangle");
        for(int v=0;v<3;v++)Check(last.Records.AsSpan(11*Triangle+v*Record,Record).SequenceEqual(full.AsSpan(0,Record)),
            "new filler in a formerly full cluster preserves every source word, not only position");
    }
    static void InputGuards()
    {
        Reject(null);Reject(new byte[10]);Reject(Source(),actual:0);Reject(Source(),actual:13);Reject(Source(),padded:13);
        Reject(Source(),mask:256);Reject(Source(),mask:2);Reject(Source(),eligibility:false);
        var badBits=Bits(1);badBits[0]=3;Reject(Source(),bits:badBits);badBits=Bits(1);badBits[0]=256;Reject(Source(),bits:badBits);
        badBits=Bits(1);badBits[11]=1;Reject(Source(),bits:badBits);
        Reject(Source(),edits:new[]{new GlassNativeTriangleEdit(1,new[]{Quarter()})});
        var edit=new GlassNativeTriangleEdit(0,new[]{Quarter()});Reject(Source(2),actual:2,edits:new[]{edit,edit});Reject(Source(),edits:new GlassNativeTriangleEdit[]{null});
        Reject(Source(),edits:new[]{new GlassNativeTriangleEdit(0,new[]{default(GlassBarycentricTriangle)})});
        Reject(Source(),edits:new[]{new GlassNativeTriangleEdit(0,new[]{new GlassBarycentricTriangle(A,C,B)})});
        Reject(Source(),edits:new[]{new GlassNativeTriangleEdit(0,new[]{new GlassBarycentricTriangle(A,A,B)})});
        Reject(Source(),edits:new[]{new GlassNativeTriangleEdit(0,Enumerable.Repeat(Quarter(),13).ToArray())});
        var padding=Source();padding[Triangle+1]=1;Reject(padding);
        var unlabelled=Bits(1);unlabelled[0]=0;Reject(Source(),bits:unlabelled);
        // These are positive and inside the source triangle, but overlap.
        Reject(Source(),edits:new[]{new GlassNativeTriangleEdit(0,new[]{Quarter(),Quarter()})});
        var nested=new GlassBarycentricTriangle(A,W(3,1,0,4),W(3,0,1,4));
        Reject(Source(),edits:new[]{new GlassNativeTriangleEdit(0,new[]{Quarter(),nested})});
        var up=new GlassBarycentricTriangle(W(26,12,2,40),W(1,22,17,40),W(21,2,17,40));
        var down=new GlassBarycentricTriangle(W(6,12,22,40),W(31,2,7,40),W(11,22,7,40));
        Reject(Source(),edits:new[]{new GlassNativeTriangleEdit(0,new[]{up,down})});
    }
    static void AttributeAndFloatGuards()
    {
        void Mutate(Action<byte[]> mutation){var input=Source();mutation(input);Reject(input);}
        Mutate(b=>U(b,Record+60,272));Mutate(b=>F(b,Record+12,.25f));Mutate(b=>F(b,2*Record+64,.75f));
        Mutate(b=>F(b,Record+100,float.NaN));Mutate(b=>F(b,Record+36,-1));Mutate(b=>F(b,Record+52,float.PositiveInfinity));
        Mutate(b=>{for(int v=0;v<3;v++)F(b,v*Record+20,0);});
        Mutate(b=>{for(int v=0;v<3;v++)for(int offset=24;offset<36;offset+=4)F(b,v*Record+offset,0);});
        Mutate(b=>{for(int v=0;v<3;v++)F(b,v*Record+8,0);F(b,2*Record+4,0);});
        var alternate=Source();for(int offset=24;offset<36;offset+=4)F(alternate,offset,0);
        Check(Build(alternate,1,12,0,Bits(1),new[]{new GlassNativeTriangleEdit(0,new[]{Quarter()})},out var chosen)&&
            F(chosen.Records,24)==0&&F(chosen.Records,28)==1&&F(chosen.Records,112)==6,"one coherent usable source basis replaces unusable first tangent");
        var collapse=Source();float next=BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(1)+1);
        for(int v=0;v<3;v++){F(collapse,v*Record,v==1?next:1);F(collapse,v*Record+4,v==2?next:1);}
        Reject(collapse);
        var denominator=BigInteger.One<<500;
        var tiny=new GlassBarycentricTriangle(A,new GlassBarycentric(denominator-1,1,0,denominator),new GlassBarycentric(denominator-1,0,1,denominator));
        Reject(Source(),edits:new[]{new GlassNativeTriangleEdit(0,new[]{tiny})});
        var signedZero=Source();F(signedZero,Record+12,-0f);
        Check(Build(signedZero,1,12,0,Bits(1),new[]{new GlassNativeTriangleEdit(0,new[]{Quarter()})},out _),"normal signed zero is numerically constant");
    }
    static void GridSubdivision()
    {
        foreach(int n in new[]{2,3})
        {
            GlassBarycentric P(int b,int c)=>W(n-b-c,b,c,n);
            var triangles=new List<GlassBarycentricTriangle>();
            for(int i=0;i<n;i++)for(int j=0;j<n-i;j++)
            {
                triangles.Add(new(P(i,j),P(i+1,j),P(i,j+1)));
                if(i+j+2<=n)triangles.Add(new(P(i+1,j),P(i+1,j+1),P(i,j+1)));
            }
            Check(triangles.Count==n*n,"independently constructed triangular lattice has n-squared cells");
            Check(Build(Source(),1,12,0,Bits(1),new[]{new GlassNativeTriangleEdit(0,triangles)},out var result),"nonoverlapping edge-sharing subdivision builds");
            for(int t=0;t<triangles.Count;t++)
            {
                var weights=new[]{triangles[t].A,triangles[t].B,triangles[t].C};
                for(int v=0;v<3;v++)
                {
                    float x=(float)(2d*(double)weights[v].B/(double)weights[v].D),y=(float)(2d*(double)weights[v].C/(double)weights[v].D);
                    int at=t*Triangle+v*Record;
                    Check(F(result.Records,at)==x&&F(result.Records,at+4)==y,"lattice position equals its independently known axis coordinate");
                    Check(F(result.Records,at+52)==x&&F(result.Records,at+56)==y&&U(result.Records,at+60)==271,"lattice UV/material expectation");
                }
            }
            Check(result.SourceTriangleIndices.Take(n*n).All(x=>x==0)&&result.SourceTriangleIndices.Skip(n*n).All(x=>x==-1),"lattice packing preserves source ownership and filler boundaries");
        }
    }
    static void NativeCaptureSmoke(string capture)
    {
        var bytes=File.ReadAllBytes(Path.Combine(capture,"313-cpu.bin"));int padded=bytes.Length/Triangle;
        var row=File.ReadLines(Path.Combine(capture,"catalog.csv")).Skip(1).Select(s=>s.Split(',')).Single(s=>s[0]=="313");
        int actual=int.Parse(row[3])/3;
        foreach(int target in new[]{10,11})
        {
            var bits=new uint[padded];bits[target]=1;
            Check(Build(bytes,actual,padded,0,bits,new[]{new GlassNativeTriangleEdit(target,new[]{Quarter()})},out var result),"captured hepta cap accepts its varying finite source tangent basis");
            Check(result.Records.Length==bytes.Length&&result.Changed,"captured clipped result keeps padded byte span");
            for(int t=0;t<actual;t++)if(t!=target)Check(result.Records.AsSpan(t*Triangle,Triangle).SequenceEqual(bytes.AsSpan(t*Triangle,Triangle)),"captured untouched native triangles remain byte exact");
            int at=target*Triangle;uint material=U(bytes,at+60);
            for(int v=0;v<3;v++)Check(U(result.Records,at+v*Record+60)==material,"captured material index stays original");
        }
    }
    static void Main(string[] args){QuarterTriangle();UnchangedAndOwnership();MaskAndClusterOrdering();InputGuards();AttributeAndFloatGuards();GridSubdivision();if(args.Length>0)NativeCaptureSmoke(args[0]);Console.WriteLine($"PASS {checks} raw partial geometry checks; native rendering acceptance unproven.");}
}
