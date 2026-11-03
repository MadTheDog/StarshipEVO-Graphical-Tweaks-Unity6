using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static GlassPolygonPoint P(double x,double y,double z=0)=>new GlassPolygonPoint(x,y,z);
    static GlassPolygonTriangle T(double ax,double ay,double bx,double by,double cx,double cy,double z=0)
        =>new GlassPolygonTriangle(P(ax,ay,z),P(bx,by,z),P(cx,cy,z));
    static GlassPolygonTriangle Reverse(GlassPolygonTriangle t)=>new GlassPolygonTriangle(t.A,t.C,t.B);
    static GlassPolygonTriangle[] Rect(double x0,double y0,double x1,double y1,bool reverse=false,double z=0,bool alternate=false)
    {
        var a=alternate?T(x0,y0,x1,y0,x0,y1,z):T(x0,y0,x1,y0,x1,y1,z);
        var b=alternate?T(x1,y0,x1,y1,x0,y1,z):T(x0,y0,x1,y1,x0,y1,z);
        return reverse?new[]{Reverse(a),Reverse(b)}:new[]{a,b};
    }
    static GlassPolygonCoverageBrick B(int id,GlassPolygonTriangle[] triangles,long entity=1,byte health=255,int bit=0)
        =>new GlassPolygonCoverageBrick(entity,id,health,new GlassPolygonFaceGroup(bit,triangles));
    static GlassPolygonTriangle[] Reversed(GlassPolygonTriangle[] triangles)=>Array.ConvertAll(triangles,Reverse);
    static GlassPolygonTriangle[] Map(GlassPolygonTriangle[] triangles,Func<GlassPolygonPoint,GlassPolygonPoint> map)
        =>Array.ConvertAll(triangles,t=>new GlassPolygonTriangle(map(t.A),map(t.B),map(t.C)));
    static void Main(string[] args)
    {
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        var m=new GlassPolygonCoverageModel();m.Upsert(B(1,Rect(0,0,1,1)));
        Check(m.GetMask(1)==0,"isolated surface remains visible");
        m.Upsert(B(2,Rect(0,0,1,1,true)));
        Check(m.GetMask(1)==1&&m.GetMask(2)==1,"opposite coplanar faces cover each other");
        m.Remove(2);Check(m.GetMask(1)==0&&m.GetMask(2)==0,"removal restores target and clears old mask");
        m.Upsert(B(2,Rect(0,0,1,1)));Check(m.GetMask(1)==0,"same winding cannot hide a face");
        m.Upsert(B(2,Rect(0,0,1,1,true),entity:2));Check(m.GetMask(1)==0,"different entities cannot hide each other");
        m.Upsert(B(2,Rect(0,0,1,1,true),health:254));Check(m.GetMask(1)==0&&m.GetMask(2)==0,"damaged neighbor is neither hidden nor an occluder");
        m.Upsert(B(2,Rect(0,0,1,1,true)));Check(m.GetMask(1)==1,"repair restores coverage");
        m.Upsert(B(2,Rect(8,0,9,1,true)));Check(m.GetMask(1)==0&&m.GetMask(2)==0,"reused build ID loses previous geometry");
        m.Clear();m.Upsert(B(1,Rect(0,0,1,1)));m.Upsert(B(2,Rect(0,0,1,1,true,alternate:true)));
        Check(m.GetMask(1)==1&&m.GetMask(2)==1,"different triangle diagonals cover the same rectangle");
        m.Clear();m.Upsert(B(1,new[]{T(0,0,2,0,0,2)}));m.Upsert(B(2,Rect(0,0,1,2,true)));
        Check(m.GetMask(1)==0,"cube half alone does not cover triangular target");
        m.Upsert(B(3,new[]{Reverse(T(1,0,2,0,1,1))}));
        Check(m.GetMask(1)==1,"cube polygon plus wedge polygon jointly cover a target triangle");
        m.Remove(2);Check(m.GetMask(1)==0,"removing rectangular member reopens mixed union");
        m.Clear();m.Upsert(B(1,Rect(0,0,2,1)));m.Upsert(B(2,Rect(0,0,1,1,true)));
        m.Upsert(B(3,new[]{Reverse(T(1,0,2,0,2,1))}));m.Upsert(B(4,new[]{Reverse(T(1,0,2,1,1,1))}));
        Check(m.GetMask(1)==1,"rectangle and two independently owned triangles form one union");
        m.Upsert(B(4,new[]{Reverse(T(1,0,2,1,1,.999999999999))}));
        Check(m.GetMask(1)==0,"tiny uncovered in-plane sliver is never discarded by area tolerance");
        foreach(double gap in new[]{1e-4,1e-10,1e-14})
        {
            m.Clear();m.Upsert(B(1,Rect(0,0,1,1)));m.Upsert(B(2,Rect(0,0,1,1,true,z:gap)));
            Check(m.GetMask(1)==0,"parallel near-plane gap stays visible "+gap);
            m.Clear();m.Upsert(B(1,Rect(0,0,1,1)));m.Upsert(B(2,Rect(gap,0,1,1,true)));
            Check(m.GetMask(1)==0,"near-edge gap stays visible "+gap);
        }
        m.Clear();m.Upsert(B(1,Rect(0,0,1,1)));m.Upsert(B(2,Rect(1,0,2,1,true)));
        Check(m.GetMask(1)==0,"edge-only contact cannot cover area");
        var ring=new List<GlassPolygonTriangle>();ring.AddRange(Rect(0,0,3,1));ring.AddRange(Rect(0,2,3,3));ring.AddRange(Rect(0,1,1,2));ring.AddRange(Rect(2,1,3,2));
        m.Clear();m.Upsert(B(1,ring.ToArray()));m.Upsert(B(2,Reversed(ring.ToArray())));
        Check(m.GetMask(1)==1,"nonconvex cap triangle union with a hole covers its matching cap");
        m.Upsert(B(3,Rect(0,0,3,3)));Check(m.GetMask(3)==0,"opposing ring does not cover an exposed hole");
        var arch=new List<GlassPolygonTriangle>();
        for(int i=0;i<5;i++)
        {
            double a=i*Math.PI/10,b=(i+1)*Math.PI/10;
            var innerA=P(Math.Cos(a),Math.Sin(a));var outerA=P(2*Math.Cos(a),2*Math.Sin(a));
            var innerB=P(Math.Cos(b),Math.Sin(b));var outerB=P(2*Math.Cos(b),2*Math.Sin(b));
            arch.Add(new GlassPolygonTriangle(innerA,outerA,outerB));arch.Add(new GlassPolygonTriangle(innerA,outerB,innerB));
        }
        m.Clear();m.Upsert(B(1,arch.ToArray()));m.Upsert(B(2,Reversed(arch.ToArray())));
        Check(m.GetMask(1)==1,"rounded nonrectangular cap retains its actual triangle tessellation");
        m.Upsert(B(3,new[]{T(0,0,2,0,0,2)}));Check(m.GetMask(3)==0,"curved band does not fill its bounding triangle");
        foreach(Func<GlassPolygonPoint,GlassPolygonPoint> transform in new Func<GlassPolygonPoint,GlassPolygonPoint>[] {
            p=>P(p.Z,p.X,p.Y),p=>P(-p.X,p.Z,p.Y),p=>P(2*p.X+7,3*p.Y-11,p.X+2*p.Y+13)})
        {
            var shape=Map(Rect(-1,-2,3,4),transform);m.Clear();m.Upsert(B(1,shape));m.Upsert(B(2,Reversed(shape)));
            Check(m.GetMask(1)==1,"rotated or nonuniform diagonal plane coverage is geometric");
        }
        m.Clear();m.Upsert(new GlassPolygonCoverageBrick(1,1,255,new GlassPolygonFaceGroup(0,Rect(0,0,1,1)),new GlassPolygonFaceGroup(7,Rect(2,0,3,1))));
        m.Upsert(B(2,Rect(2,0,3,1,true)));Check(m.GetMask(1)==128,"source-local bit7 is independent of group0");
        m.Upsert(B(3,Rect(0,0,1,1,true)));Check(m.GetMask(1)==129,"every group is evaluated independently");
        m.RemoveEntity(1);Check(m.GetMask(1)==0&&m.GetMask(2)==0,"entity removal clears all owned masks");
        m.Clear();m.Upsert(B(1,Rect(0,0,1,1)));m.Upsert(B(2,new[]{new GlassPolygonTriangle(P(double.NaN,0),P(1,0),P(0,1))}));
        Check(m.GetMask(1)==0&&m.GetMask(2)==0,"nonfinite geometry remains visible and cannot occlude");
        m.Upsert(B(2,Rect(0,0,1,1,true),bit:8));Check(m.GetMask(1)==0,"unknown group bit cannot occlude");
        m.Upsert(new GlassPolygonCoverageBrick(1,2,255,new GlassPolygonFaceGroup(0,Rect(0,0,1,1,true)),new GlassPolygonFaceGroup(0,Rect(0,0,1,1,true))));
        Check(m.GetMask(1)==0,"duplicate source bits are ambiguous and fail open");
        m.Upsert(B(2,new[]{T(0,0,0,0,0,0)}));Check(m.GetMask(1)==0&&m.GetMask(2)==0,"degenerate-only group cannot hide itself or others");
        var padded=new List<GlassPolygonTriangle>(Rect(0,0,1,1,true)){T(0,0,0,0,0,0)};m.Upsert(B(2,padded.ToArray()));
        Check(m.GetMask(1)==1&&m.GetMask(2)==1,"complete zero triangle padding does not prevent real coverage");
        var changes=new List<GlassPolygonCoverageChange>();m.DrainChanges(changes);Check(changes.Count>0,"incremental mask changes can be drained");
        changes.Clear();m.DrainChanges(changes);Check(changes.Count==0,"drain consumes queued changes");m.Clear();m.DrainChanges(changes);
        Check(changes.Exists(c=>c.BuildId==1&&c.Mask==0),"clear publishes mask restoration");
        foreach(var limit in new[]{new GlassPolygonCoverageLimits(maximumCandidates:1),new GlassPolygonCoverageLimits(maximumFragments:1),new GlassPolygonCoverageLimits(maximumOperations:1)})
        {
            var bounded=new GlassPolygonCoverageModel(limit);bounded.Upsert(B(1,Rect(0,0,3,1)));bounded.Upsert(B(2,Rect(1,0,2,1,true)));
            bounded.Upsert(B(3,Rect(0,0,1,1,true)));bounded.Upsert(B(4,Rect(2,0,3,1,true)));
            Check(bounded.GetMask(1)==0&&bounded.BudgetFailures>0,"candidate/fragment/operation budget fails open");
        }
        m.Clear();m.Upsert(B(1,Rect(0,0,1,1)));m.Upsert(B(2,Rect(0,0,2000000,1,true)));
        Check(m.GetMask(1)==0,"out-of-range coordinates cannot occlude");
        var small=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(maximumCellsPerTriangle:1));
        small.Upsert(B(1,Rect(0,0,1,1)));small.Upsert(B(2,Rect(0,0,16,16,true)));Check(small.GetMask(1)==0,"oversized spatial footprint fails open");
        var few=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(maximumTrianglesPerBrick:1));
        few.Upsert(B(1,new[]{T(0,0,1,0,0,1)}));few.Upsert(B(2,Rect(0,0,1,1,true)));Check(few.GetMask(1)==0,"excess triangle input fails open");
        var totalLimit=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(maximumTrianglesTotal:1));
        totalLimit.Upsert(B(1,new[]{T(0,0,1,0,0,1)}));totalLimit.Upsert(B(2,new[]{Reverse(T(0,0,1,0,0,1))}));
        Check(totalLimit.GetMask(1)==0&&totalLimit.BudgetFailures>0,"global triangle capacity fails open");
        var cellLimit=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(maximumTrianglesPerCell:2));
        cellLimit.Upsert(B(1,Rect(0,0,1,1)));cellLimit.Upsert(B(2,Rect(0,0,1,1,true)));
        Check(cellLimit.GetMask(1)==0&&cellLimit.BudgetFailures>0,"dense plane-cell capacity fails open");
        var brickCells=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(maximumCellsPerBrick:1));
        brickCells.Upsert(B(1,new[]{T(0,0,1,0,0,1)}));brickCells.Upsert(B(2,Rect(0,0,1,1,true)));
        Check(brickCells.GetMask(1)==0&&brickCells.BudgetFailures>0,"per-brick cell capacity fails open");
        m.Clear();var tiny=new[]{T(0,0,1,0,0,1e-14)};m.Upsert(B(1,tiny));m.Upsert(B(2,Reversed(tiny)));
        Check(m.GetMask(1)==1,"tiny nondegenerate triangles are tested rather than discarded");
        m.Clear();m.Upsert(B(1,Rect(0,0,1,1)));m.Upsert(B(2,new[]{Reverse(T(0,0,1,0,1,2))}));
        m.Upsert(B(3,new[]{Reverse(T(0,0,1,2,0,1))}));Check(m.GetMask(1)==1,"non-dyadic intersection coordinates are handled exactly");
        m.Clear();m.Upsert(new GlassPolygonCoverageBrick(1,1,255,new GlassPolygonFaceGroup(0,Rect(0,0,1,1)),new GlassPolygonFaceGroup(1,Rect(0,0,1,1,true))));
        Check(m.GetMask(1)==0,"opposite groups from the same brick cannot hide each other");
        m.Clear();var mutable=Rect(0,0,1,1,true);m.Upsert(B(1,Rect(0,0,1,1)));m.Upsert(B(2,mutable));
        mutable[0]=T(50,50,51,50,50,51);Check(m.GetMask(1)==1,"input array mutation cannot alter an indexed snapshot");
        m.Upsert(B(2,mutable));Check(m.GetMask(1)==0,"explicit upsert publishes changed geometry");
        m.Clear();m.Upsert(B(1,Rect(0,0,1,1)));m.Upsert(B(2,Rect(0,0,1,1,true,z:1e-7)));
        Check(m.GetMask(1)==0,"native-scale rotation noncoincidence is retained without canonicalization");
        m.Upsert(B(2,Rect(0,0,1,1,true,z:.0025)));Check(m.GetMask(1)==0,"real arched-cap-sized gap is retained");
        m.Upsert(B(2,new[]{new GlassPolygonTriangle(P(double.PositiveInfinity,0),P(1,0),P(0,1))}));
        Check(m.GetMask(1)==0,"infinite input cannot occlude");
        m.Upsert(B(2,new[]{T(0,0,1,0,0,1e-200)}));Check(m.GetMask(1)==0,"excess rational precision fails open");
        foreach(double cellSize in new[]{.5,2,8,17})
        {
            var indexed=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(cellSize:cellSize));
            indexed.Upsert(B(1,Rect(-4,-3,2,1)));indexed.Upsert(B(2,Rect(-4,-3,-1,1,true)));indexed.Upsert(B(3,Rect(-1,-3,2,1,true)));
            Check(indexed.GetMask(1)==1,"negative coordinates and cell-boundary union at cell size "+cellSize);
        }
        var reference=new GlassFaceCoverageModel();var generic=new GlassPolygonCoverageModel();var random=new Random(771);
        for(int step=0;step<128;step++)
        {
            int id=random.Next(1,17);
            if(step%4==0){reference.Remove(id);generic.Remove(id);}
            else
            {
                int x=random.Next(-2,3),y=random.Next(-2,3);byte health=(byte)(step%11==0?254:255);
                reference.Upsert(new GlassFaceCoverageBox(1,id,x*32,y*32,0,(x+1)*32,(y+1)*32,32,health));
                generic.Upsert(Cube(id,x,y,health));
            }
            for(int i=1;i<=16;i++)Check(reference.GetMask(i)==generic.GetMask(i),"legacy cube differential step="+step+" id="+i);
        }
        var stacked=new GlassPolygonCoverageModel();
        for(int z=0;z<3;z++)for(int y=0;y<18;y++)for(int x=0;x<18;x++)stacked.Upsert(Cube(1+x+18*y+324*z,x,y,z:z));
        Check(stacked.GetMask(1+12+18*12+324)==63,"fully surrounded cube in a stacked grid hides six faces");
        Check(stacked.BudgetFailures==0,"ordinary stacked grid does not exhaust plane cell capacity");
        m.Clear();for(int i=0;i<2000;i++)m.Upsert(B(i+1,Rect(i*64,0,i*64+1,1)));
        Check(m.CandidateChecks<100000,"plane/spatial index avoids an all-pairs scan for distant coplanar shapes");
        Console.WriteLine("PASS: "+checks+" generic polygon coverage checks; distant candidates="+m.CandidateChecks);
        if(Array.IndexOf(args,"--benchmark")>=0)Benchmark();
    }
    static GlassPolygonCoverageBrick Cube(int id,int x,int y,byte health=255,int z=0)
    {
        var flat=Rect(0,0,1,1);
        return new GlassPolygonCoverageBrick(1,id,health,
            new GlassPolygonFaceGroup(0,Map(flat,p=>P(x+1,y+p.X,z+p.Y))),
            new GlassPolygonFaceGroup(1,Reversed(Map(flat,p=>P(x,y+p.X,z+p.Y)))),
            new GlassPolygonFaceGroup(2,Map(flat,p=>P(x+p.Y,y+1,z+p.X))),
            new GlassPolygonFaceGroup(3,Reversed(Map(flat,p=>P(x+p.Y,y,z+p.X)))),
            new GlassPolygonFaceGroup(4,Map(flat,p=>P(x+p.X,y+p.Y,z+1))),
            new GlassPolygonFaceGroup(5,Reversed(Map(flat,p=>P(x+p.X,y+p.Y,z)))));
    }
    static void Benchmark()
    {
        Console.WriteLine("BENCH environment="+RuntimeInformation.FrameworkDescription+", arch="+RuntimeInformation.ProcessArchitecture+
            ", TieredCompilation="+(Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")??"default"));
        int beforeChecks=checks;
        const int width=25,height=20,mixed=500;
        var warm=new GlassPolygonCoverageModel();for(int i=0;i<20;i++)warm.Upsert(Cube(i+1,i%5,i/5));
        var cubeInputs=new GlassPolygonCoverageBrick[width*height];
        for(int i=0;i<cubeInputs.Length;i++)cubeInputs[i]=Cube(i+1,i%width,i/width);
        var legacy=new GlassFaceCoverageModel();var legacyInputs=new GlassFaceCoverageBox[width*height];
        for(int i=0;i<legacyInputs.Length;i++)
        {int x=i%width,y=i/width;legacyInputs[i]=new GlassFaceCoverageBox(1,i+1,x*32,y*32,0,(x+1)*32,(y+1)*32,32);}
        var legacyWarm=new GlassFaceCoverageModel();for(int i=0;i<20;i++)legacyWarm.Upsert(legacyInputs[i]);
        MeasureLegacy("legacy 500 adjacent full cubes (exact tick rectangles only)",legacy,legacyInputs);
        Check(legacy.GetMask(width+2)==15,"legacy benchmark reference has the same internal cube mask");
        var legacyChanges=new GlassFaceCoverageBox[100];
        for(int i=0;i<legacyChanges.Length;i++)legacyChanges[i]=new GlassFaceCoverageBox(1,width+2,32,32,0,64,64,32,(byte)(i%2==0?254:255));
        MeasureLegacy("legacy100 health changes within adjacent cubes",legacy,legacyChanges);
        var model=new GlassPolygonCoverageModel();var samples=new List<double>();
        Measure("500 adjacent full cubes",model,cubeInputs,samples);
        Check(model.GetMask(width+2)==15&&model.BudgetFailures==0,"adjacent benchmark proves all four internal cube groups covered without budget fallback");
        var changed=new GlassPolygonCoverageBrick[100];for(int i=0;i<changed.Length;i++)changed[i]=Cube(width+2,1,1,(byte)(i%2==0?254:255));
        Measure("100 health changes within adjacent cubes",model,changed,new List<double>());
        var panels=new GlassPolygonCoverageBrick[mixed*3];
        for(int i=0;i<mixed;i++)
        {
            int id=10000+i*3;double x=i*4;
            panels[i*3]=B(id,new[]{T(x,0,x+2,0,x,2)},entity:2);
            panels[i*3+1]=B(id+1,Rect(x,0,x+1,2,true),entity:2);
            panels[i*3+2]=B(id+2,new[]{Reverse(T(x+1,0,x+2,0,x+1,1))},entity:2);
        }
        var mixedModel=new GlassPolygonCoverageModel();Measure("500 mixed cube/wedge unions (1500 panels)",mixedModel,panels,new List<double>());
        for(int i=0;i<mixed;i++)Check(mixedModel.GetMask(10000+i*3)==1,"mixed benchmark target "+i+" covered");
        Check(mixedModel.BudgetFailures==0,"mixed benchmark does not obtain speed by failing open");
        foreach(double size in new[]{4.0,2.0})
        {
            var tuned=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(cellSize:size));
            Measure("500 adjacent cubes cellSize="+size,tuned,cubeInputs,new List<double>());
            Check(tuned.GetMask(width+2)==15&&tuned.BudgetFailures==0,"tuned spatial benchmark preserves results");
        }
        var stretched=new GlassPolygonCoverageBrick[64];
        for(int i=0;i<stretched.Length;i++)
        {
            var source=Cube(i+1,i%8,i/8);var groups=new GlassPolygonFaceGroup[source.Groups.Length];
            for(int g=0;g<groups.Length;g++)groups[g]=new GlassPolygonFaceGroup(source.Groups[g].Bit,
                Map(source.Groups[g].Triangles,p=>P(16*p.X,16*p.Y,16*p.Z)));
            stretched[i]=new GlassPolygonCoverageBrick(1,i+1,255,groups);
        }
        foreach(double size in new[]{8.0,2.0})
        {
            var large=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(cellSize:size));
            Measure("64 adjacent cubes stretched16x cellSize="+size,large,stretched,new List<double>());
            Check(large.GetMask(10)==15&&large.BudgetFailures==0,"stretched benchmark remains exact without fallback");
        }
        Console.WriteLine("PASS: "+(checks-beforeChecks)+" benchmark correctness checks; total checks="+checks);
    }
    static void Measure(string name,GlassPolygonCoverageModel model,GlassPolygonCoverageBrick[] inputs,List<double> samples)
    {
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long bytes=GC.GetAllocatedBytesForCurrentThread();
        long candidates=model.CandidateChecks,budget=model.BudgetFailures;var total=Stopwatch.StartNew();
        foreach(var input in inputs)
        {
            long start=Stopwatch.GetTimestamp();model.Upsert(input);
            samples.Add((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency);
        }
        total.Stop();bytes=GC.GetAllocatedBytesForCurrentThread()-bytes;samples.Sort();
        Console.WriteLine("BENCH "+name+": totalMs="+total.Elapsed.TotalMilliseconds.ToString("F2")+
            ", medianMs="+samples[samples.Count/2].ToString("F3")+", p95Ms="+samples[(samples.Count-1)*95/100].ToString("F3")+
            ", maxMs="+samples[samples.Count-1].ToString("F3")+", allocatedMiB="+(bytes/1048576.0).ToString("F2")+
            ", candidateChecks="+(model.CandidateChecks-candidates)+", budgetFailures="+(model.BudgetFailures-budget));
    }
    static void MeasureLegacy(string name,GlassFaceCoverageModel model,GlassFaceCoverageBox[] inputs)
    {
        var samples=new List<double>();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long bytes=GC.GetAllocatedBytesForCurrentThread();
        long candidates=model.CandidateChecks;var total=Stopwatch.StartNew();
        foreach(var input in inputs)
        {
            long start=Stopwatch.GetTimestamp();model.Upsert(input);
            samples.Add((Stopwatch.GetTimestamp()-start)*1000.0/Stopwatch.Frequency);
        }
        total.Stop();bytes=GC.GetAllocatedBytesForCurrentThread()-bytes;samples.Sort();
        Console.WriteLine("BENCH "+name+": totalMs="+total.Elapsed.TotalMilliseconds.ToString("F2")+
            ", medianMs="+samples[samples.Count/2].ToString("F3")+", p95Ms="+samples[(samples.Count-1)*95/100].ToString("F3")+
            ", maxMs="+samples[samples.Count-1].ToString("F3")+", allocatedMiB="+(bytes/1048576.0).ToString("F2")+
            ", candidateChecks="+(model.CandidateChecks-candidates));
    }
}
