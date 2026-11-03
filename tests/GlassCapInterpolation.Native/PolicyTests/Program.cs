using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void F(byte[] b,int p,float f)=>BitConverter.GetBytes(f).CopyTo(b,p);
    static byte[] Triangle(float translation=0)
    {
        var b=new byte[372];
        for(int v=0;v<3;v++)
        {
            int p=v*124;F(b,p,translation+(v==1?3:0));F(b,p+4,v==2?3:0);
            F(b,p+20,1);F(b,p+24,v==2?-1:1);F(b,p+36,1);F(b,p+44,v==2?-1:1);
            F(b,p+52,v==1?6:0);F(b,p+56,v==2?6:0);BitConverter.GetBytes((uint)271).CopyTo(b,p+60);
            for(int a=64;a<112;a+=4)F(b,p+a,a/4);F(b,p+112,v);
        }
        return b;
    }
    static GlassCapInterpolationCandidate Cap(uint build=7,uint cluster=24,int order=5,float shift=0)=>new(build,cluster,order,Triangle(shift));
    static bool Plan(uint[] mapping,uint[] clip,uint[] args,int start,int offset,GlassCapInterpolationCandidate[] caps,out GlassCapInterpolationPlan plan)=>
        GlassCapInterpolationBuffers.TryPlan(mapping,clip,args,start,offset,caps,out plan,out _);
    static void Reject(uint[] map=null,uint[] clip=null,uint[] args=null,int start=0,int offset=0,GlassCapInterpolationCandidate[] caps=null)
    {
        Check(!Plan(map??new uint[]{24,7},clip??new uint[]{0},args??new uint[]{36,1,0,0},start,offset,caps??new[]{Cap()},out var plan)&&plan==null,
            "invalid plan must fail without returning geometry");
    }
    static void NativeOrderAndBits()
    {
        var caps=new[]{Cap(7,24,8,8),Cap(9,48,2,20),Cap(7,24,1,1)};
        var map=new uint[]{99,99,48,9,72,17,24,7};var clips=new uint[]{8,0x80000000,0xffffffff,0x7fc01234};
        var args=new uint[]{9,9,9,9,36,3,0,0};
        Check(Plan(map,clips,args,1,16,caps,out var plan),"nonzero native offsets select bounded visible slices");
        Check(plan.CaseCount==3&&plan.MappingWords.SequenceEqual(new uint[]{12,9,24,7,36,7}),"native cluster order then original triangle order");
        Check(plan.ClipBits.SequenceEqual(new uint[]{0x80000000,0x7fc01234,0x7fc01234}),"raw clip bits repeat for each cap in a native cluster");
        Check(plan.ArgsWords.SequenceEqual(new uint[]{36,3,0,0})&&plan.ByteCount==35764,"owned indirect draw and independent payload budget calculation including reserved prefix");
        var baseline=plan.BaseShapeRecords;
        Check(BitConverter.ToSingle(baseline,4464)==20&&BitConverter.ToSingle(baseline,8928)==1&&BitConverter.ToSingle(baseline,13392)==8,
            "packed cases preserve selected source order");
        for(int c=0;c<3;c++)for(int t=1;t<12;t++)for(int v=0;v<3;v++)
            Check(baseline.AsSpan((c+1)*4464+t*372+v*124,124).SequenceEqual(baseline.AsSpan((c+1)*4464,124)),"baseline padding repeats the complete finite source record");
        var subdivision=plan.SubdivShapeRecords;
        for(int c=0;c<3;c++)for(int t=3;t<12;t++)for(int v=0;v<3;v++)
            Check(subdivision.AsSpan((c+1)*4464+t*372+v*124,124).SequenceEqual(baseline.AsSpan((c+1)*4464,124)),"subdivision padding repeats the complete original source record");
        Check(map.SequenceEqual(new uint[]{99,99,48,9,72,17,24,7})&&clips[1]==0x80000000&&args[4]==36,"native snapshot input arrays stay unchanged");
    }
    static void OwnershipAndAttributes()
    {
        var bytes=Triangle();var cap=new GlassCapInterpolationCandidate(7,24,5,bytes);
        bytes[0]=255;var leaked=cap.OriginalTriangleRecords;leaked[0]=254;
        Check(Plan(new uint[]{24,7},new uint[]{0},new uint[]{36,1,0,0},0,0,new[]{cap},out var plan)&&plan.BaseShapeRecords[4464]==0,
            "candidate snapshots caller bytes and does not expose them mutably");
        var baseline=plan.BaseShapeRecords;baseline[4464]=255;var changedMap=plan.MappingWords;changedMap[0]=999;
        var changedClip=plan.ClipBits;changedClip[0]=999;var changedArgs=plan.ArgsWords;changedArgs[0]=999;
        Check(plan.BaseShapeRecords[4464]==0&&plan.MappingWords[0]==12&&plan.ClipBits[0]==0&&plan.ArgsWords[0]==36,
            "result arrays cannot mutate a prepared diagnostic plan");
        var b=plan.SubdivShapeRecords;
        var expected=new[]{(0f,0f),(3f,0f),(1f,1f),(3f,0f),(0f,3f),(1f,1f),(0f,3f),(0f,0f),(1f,1f)};
        for(int v=0;v<9;v++)
        {
            int at=4464+v*124;
            Check(BitConverter.ToSingle(b,at)==expected[v].Item1&&BitConverter.ToSingle(b,at+4)==expected[v].Item2,"three fan triangles keep original source winding and cover the uncut triangle");
            Check(BitConverter.ToSingle(b,at+52)==2*expected[v].Item1&&BitConverter.ToSingle(b,at+56)==2*expected[v].Item2,"UV interpolation agrees with the hand-derived fan coordinates");
            Check(BitConverter.ToUInt32(b,at+60)==271&&BitConverter.ToSingle(b,at+20)==1,"native material and flat normal stay unchanged");
            Check(BitConverter.ToSingle(b,at+24)==1&&BitConverter.ToSingle(b,at+44)==1,"varying source bases do not interpolate to zero");
        }
        bool rejected=false;try{_ = new GlassCapInterpolationCandidate(1,2,-1,Triangle());}catch(ArgumentException){rejected=true;}Check(rejected,"negative source order rejected");
        rejected=false;try{_ = new GlassCapInterpolationCandidate(1,2,0,new byte[371]);}catch(ArgumentException){rejected=true;}Check(rejected,"partial source record rejected");
        rejected=false;try{_ = new GlassCapInterpolationCandidate(1,uint.MaxValue,0,Triangle());}catch(ArgumentException){rejected=true;}Check(rejected,"native cluster index wrap rejected");
        foreach(uint build in new uint[]{0,4194304,uint.MaxValue})
        {
            rejected=false;try{_ = new GlassCapInterpolationCandidate(build,24,0,Triangle());}catch(ArgumentException){rejected=true;}
            Check(rejected,"malformed native build ID must be rejected before any GPU draw");
        }
    }
    static void InvalidRangesAndBudgets()
    {
        Reject(map:Array.Empty<uint>());Reject(map:new uint[]{24});Reject(clip:Array.Empty<uint>());Reject(args:Array.Empty<uint>());
        Reject(start:-1);Reject(start:1);Reject(start:int.MaxValue);Reject(offset:-1);Reject(offset:1);Reject(offset:4);Reject(offset:int.MaxValue);
        Reject(args:new uint[]{35,1,0,0});Reject(args:new uint[]{36,0,0,0});Reject(args:new uint[]{36,8193,0,0});
        Reject(args:new uint[]{36,uint.MaxValue,0,0});Reject(args:new uint[]{36,1,1,0});Reject(args:new uint[]{36,1,0,1});
        Reject(map:new uint[]{24,7,36,8},args:new uint[]{36,2,0,0});
        Reject(map:new uint[]{24,7,24,7},clip:new uint[]{1,2},args:new uint[]{36,2,0,0});
        Reject(caps:Array.Empty<GlassCapInterpolationCandidate>());Reject(caps:new GlassCapInterpolationCandidate[]{null});Reject(caps:new[]{Cap(),Cap()});
        Reject(caps:new[]{Cap(8)});Reject(caps:new[]{Cap(7,36)});
        Reject(caps:Enumerable.Range(0,129).Select(i=>Cap((uint)i+1,24,i)).ToArray());
        var nan=Triangle();F(nan,0,float.NaN);Reject(caps:new[]{new GlassCapInterpolationCandidate(7,24,1,nan)});
        var flat=Triangle();Buffer.BlockCopy(flat,0,flat,124,124);Reject(caps:new[]{new GlassCapInterpolationCandidate(7,24,1,flat)});
        foreach(int stride in new[]{0,4,16})Check(!GlassCapInterpolationBuffers.TryValidateLayout(1,stride,1,4,4,4,0,0,out _),"mapping stride mismatch rejected before readback");
        foreach(int stride in new[]{0,8})Check(!GlassCapInterpolationBuffers.TryValidateLayout(1,8,1,stride,4,4,0,0,out _),"clip stride mismatch rejected before readback");
        foreach(int stride in new[]{0,8})Check(!GlassCapInterpolationBuffers.TryValidateLayout(1,8,1,4,4,stride,0,0,out _),"args stride mismatch rejected before readback");
        Check(!GlassCapInterpolationBuffers.TryValidateLayout(1,8,1,4,3,4,0,0,out _),"argument capacity rejected before reading four words");
        var mapping=new uint[8192*2];for(int i=0;i<8192;i++){mapping[i*2]=(uint)i*12;mapping[i*2+1]=(uint)i+1;}
        var caps=Enumerable.Range(0,128).Select(i=>Cap((uint)i+1,(uint)i*12,i)).ToArray();
        Check(Plan(mapping,new uint[8192],new uint[]{36,8192,0,0},0,0,caps,out var plan)&&plan.CaseCount==128&&plan.RecordsCount==4644&&plan.ByteCount==1153264,
            "maximum accepted visible range and candidate count stay within the owned byte budget");
        var ownedMapping=plan.MappingWords;
        Check(Enumerable.Range(0,plan.CaseCount).All(i=>ownedMapping[i*2]>0&&((long)ownedMapping[i*2]+12)*3<=plan.RecordsCount),
            "every cap in the maximum case batch bypasses the zero sentinel and stays inside the shape allocation");
        Reject(map:new uint[8193*2],clip:new uint[8193],args:new uint[]{36,8193,0,0},caps:caps);
        Check(!GlassCapInterpolationBuffers.TryPlan(null,new uint[]{0},new uint[]{36,1,0,0},0,0,new[]{Cap()},out _,out _),"null mapping rejected");
        Check(!GlassCapInterpolationBuffers.TryPlan(new uint[]{24,7},null,new uint[]{36,1,0,0},0,0,new[]{Cap()},out _,out _),"null clip rejected");
        Check(!GlassCapInterpolationBuffers.TryPlan(new uint[]{24,7},new uint[]{0},null,0,0,new[]{Cap()},out _,out _),"null args rejected");
        Check(!GlassCapInterpolationBuffers.TryPlan(new uint[]{24,7},new uint[]{0},new uint[]{36,1,0,0},0,0,null,out _,out _),"null candidates rejected");
    }
    static void NativeZeroSentinelMustNotHideAnyRealCase()
    {
        // Native VS and GTSurfaceVertex discard mapping.x==0. Forgetting the
        // prefix silently omits an otherwise valid first case in BOTH images.
        var first=Triangle(20);var second=Triangle(1);
        Check(Plan(new uint[]{48,9,24,7},new uint[]{0x80000000,0x7fc01234},new uint[]{36,2,0,0},0,0,
            new[]{new GlassCapInterpolationCandidate(7,24,2,second),new GlassCapInterpolationCandidate(9,48,4,first)},out var plan),
            "two real native caps should be admitted");
        var mapping=plan.MappingWords;var baseline=plan.BaseShapeRecords;var subdiv=plan.SubdivShapeRecords;
        for(int i=0;i<plan.CaseCount;i++)
        {
            uint shape=mapping[i*2];
            Check(shape>0&&((long)shape+12)*3<=plan.RecordsCount,
                "every real cap must bypass the native zero-shape sentinel and address a complete allocated cluster");
            int readbackOffset=checked((int)shape*3*124);
            Check(baseline.AsSpan(readbackOffset,372).SequenceEqual(i==0?first:second),
                "mapped GPU readback offset identifies the exact original triangle in native order");
            Check(BitConverter.ToSingle(subdiv,readbackOffset+2*124)==(i==0?21:2)&&BitConverter.ToSingle(subdiv,readbackOffset+2*124+4)==1,
                "mapped first fan center belongs to the same source cap");
        }
        Check(mapping.SequenceEqual(new uint[]{12,9,24,7})&&plan.ArgsWords.SequenceEqual(new uint[]{36,2,0,0}),
            "dummy cluster occupies storage only and never becomes a drawn case");
        Check(baseline.AsSpan(0,4464).ToArray().All(x=>x==0)&&subdiv.AsSpan(0,4464).ToArray().All(x=>x==0),
            "undrawn reserved cluster is entirely zero initialized");
    }
    static void Main()
    {
        NativeZeroSentinelMustNotHideAnyRealCase();
        var source=Triangle();var candidate=new GlassCapInterpolationCandidate(7,24,5,source);
        Check(GlassCapInterpolationBuffers.TryPlan(new uint[]{24,7},new uint[]{0x7fc01234},new uint[]{36,1,0,0},0,0,
            new[]{candidate},out var plan,out var reason),"eligible cap should produce an uncut subdivision plan: "+reason);
        Check(plan.CaseCount==1&&plan.RecordsCount==72,"one cap uses one native cluster plus the undrawn reserved cluster per variant");
        Check(plan.MappingWords.SequenceEqual(new uint[]{12,7})&&plan.ClipBits[0]==0x7fc01234,"preserve original build ID and raw clip bits");
        Check(plan.BaseShapeRecords.AsSpan(4464,372).SequenceEqual(source),"baseline triangle is byte exact after the reserved cluster");
        Check(BitConverter.ToSingle(plan.SubdivShapeRecords,4464+2*124)==1&&BitConverter.ToSingle(plan.SubdivShapeRecords,4464+2*124+4)==1,
            "fan center independently expected at 1,1");
        NativeOrderAndBits();OwnershipAndAttributes();InvalidRangesAndBudgets();
        Console.WriteLine("PASS "+checks+" cap interpolation planner checks");
    }
}
