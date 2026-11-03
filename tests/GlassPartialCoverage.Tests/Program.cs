using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    static GlassPolygonPoint P(double x,double y,double z=0)=>new GlassPolygonPoint(x,y,z);
    static GlassPolygonTriangle T(GlassPolygonPoint a,GlassPolygonPoint b,GlassPolygonPoint c,bool reverse=false)=>reverse?new GlassPolygonTriangle(a,c,b):new GlassPolygonTriangle(a,b,c);
    static GlassPolygonTriangle[] Rect(double x0,double y0,double x1,double y1,bool reverse=false,double z=0)=>new[]{T(P(x0,y0,z),P(x1,y0,z),P(x1,y1,z),reverse),T(P(x0,y0,z),P(x1,y1,z),P(x0,y1,z),reverse)};
    static GlassPolygonCoverageBrick Brick(int id,GlassPolygonTriangle[] triangles,long entity=1,byte health=255,int bit=0)=>new GlassPolygonCoverageBrick(entity,id,health,new GlassPolygonFaceGroup(bit,triangles));
    static GlassPolygonPoint Lift(GlassBarycentric b,GlassPolygonTriangle t)
    {
        Check(b.IsValid,"valid exact weights");double a=(double)b.A/(double)b.D,c=(double)b.C/(double)b.D,d=(double)b.B/(double)b.D;
        return P(a*t.A.X+d*t.B.X+c*t.C.X,a*t.A.Y+d*t.B.Y+c*t.C.Y,a*t.A.Z+d*t.B.Z+c*t.C.Z);
    }
    static double SignedArea(GlassPolygonPoint a,GlassPolygonPoint b,GlassPolygonPoint c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
    static double Area(GlassCapClipResult r,GlassPolygonTriangle[] source)
    {
        double area=0;foreach(var s in r.Triangles)foreach(var t in s.Triangles)
        {var original=source[s.TriangleIndex];area+=Math.Abs(SignedArea(Lift(t.A,original),Lift(t.B,original),Lift(t.C,original)))*.5;}return area;
    }
    static void HalfSquare()
    {
        var source=Rect(0,0,1,1);var m=new GlassPolygonCoverageModel();m.Upsert(Brick(1,source));m.Upsert(Brick(2,Rect(0,0,.5,1,true)));
        Check(m.TryGetCapRemainder(1,0,out var r),"partial cap query admitted");
        Check(Area(r,source)==.5,"partial square must retain exactly the exposed half");
        Check(r.Changed&&m.GetMask(1)==0,"partial geometry changes without claiming the entire face covered");
    }
    readonly struct Q : IEquatable<Q>
    {
        internal readonly BigInteger N,D;
        internal Q(BigInteger n,BigInteger d)
        {if(d.Sign<0){n=-n;d=-d;}var g=BigInteger.GreatestCommonDivisor(BigInteger.Abs(n),d);N=n/g;D=d/g;}
        public static implicit operator Q(int n)=>new Q(n,1);
        public static Q operator +(Q a,Q b)=>new Q(a.N*b.D+b.N*a.D,a.D*b.D);
        public static Q operator -(Q a,Q b)=>new Q(a.N*b.D-b.N*a.D,a.D*b.D);
        public static Q operator *(Q a,Q b)=>new Q(a.N*b.N,a.D*b.D);
        internal Q Abs()=>new Q(BigInteger.Abs(N),D);
        public bool Equals(Q q)=>N==q.N&&D==q.D;
        public override string ToString()=>N+"/"+D;
    }
    static Q WeightB(GlassBarycentric w)=>new Q(w.B,w.D);
    static Q WeightC(GlassBarycentric w)=>new Q(w.C,w.D);
    static Q Fraction(GlassBarycentricTriangle t)=>(WeightB(t.B)-WeightB(t.A))*(WeightC(t.C)-WeightC(t.A))-
        (WeightC(t.B)-WeightC(t.A))*(WeightB(t.C)-WeightB(t.A));
    static Q ExactArea(GlassCapClipResult result,params Q[] sourceAreas)
    {
        Q area=0;
        for(int i=0;i<result.Triangles.Count;i++)
        {
            var source=result.Triangles[i];Check(source.TriangleIndex==i,"nondegenerate source ordinals are stable");
            foreach(var t in source.Triangles)
            {
                Check(t.A.IsValid&&t.B.IsValid&&t.C.IsValid,"every emitted barycentric is valid");
                Q fraction=Fraction(t);Check(fraction.N.Sign>0,"retained triangle preserves source winding and positive area");
                area+=fraction*sourceAreas[source.TriangleIndex];
            }
        }
        return area;
    }
    static string Key(GlassCapClipResult result)=>string.Join("|",result.Triangles.Select(s=>s.TriangleIndex+":"+s.Changed+":"+
        string.Join(";",s.Triangles.Select(t=>t.A+" "+t.B+" "+t.C))));
    static GlassCapClipResult Query(GlassPolygonCoverageModel model,int id=1,int bit=0)
    {Check(model.TryGetCapRemainder(id,bit,out var r),"query succeeds");return r;}
    static void QuarterAndBand()
    {
        var m=new GlassPolygonCoverageModel();m.Upsert(Brick(1,Rect(0,0,1,1)));m.Upsert(Brick(2,Rect(0,0,.75,1,true)));
        Check(ExactArea(Query(m),new Q(1,2),new Q(1,2)).Equals(new Q(1,4)),"quarter square remains by exact weights");
        var band=new[]{T(P(0,2),P(0,1),P(1,0)),T(P(0,2),P(1,0),P(2,0))};
        var b=new GlassPolygonCoverageModel();b.Upsert(Brick(1,band));b.Upsert(Brick(2,Rect(-1,-1,1,3,true)));
        var r=Query(b);Check(r.Changed&&ExactArea(r,new Q(1,2),1).Equals(new Q(1,2)),"band area1.5 minus shared1 retains exposed triangle0.5");
        foreach(var tri in r.Triangles)foreach(var fragment in tri.Triangles)foreach(var weight in new[]{fragment.A,fragment.B,fragment.C})
        {var p=Lift(weight,band[tri.TriangleIndex]);Check(p.X>=1&&p.Y>=0&&p.X+p.Y<=2,"exposed band remainder stays in its independently known right triangle");}
    }
    static void CompleteEmptyAndEligibility()
    {
        var source=Rect(0,0,1,1);
        foreach(var other in new[]{Brick(2,source),Brick(2,Rect(0,0,1,1,true),entity:2),Brick(2,Rect(0,0,1,1,true,z:1d/32)),Brick(2,Rect(1,0,2,1,true)),Brick(2,Rect(0,0,1,1,true),health:254)})
        {
            var m=new GlassPolygonCoverageModel();m.Upsert(Brick(1,source));m.Upsert(other);var r=Query(m);
            Check(!r.Changed&&ExactArea(r,new Q(1,2),new Q(1,2)).Equals(new Q(1,1)),"same side/entity gap/plane gap/edge-only/unhealthy cannot remove area");
        }
        var self=new GlassPolygonCoverageModel();self.Upsert(Brick(1,source.Concat(Rect(0,0,1,1,true)).ToArray()));
        var sr=Query(self);Check(!sr.Changed&&ExactArea(sr,new Q(1,2),new Q(1,2),new Q(1,2),new Q(1,2)).Equals(new Q(2,1)),"owner cannot cover itself with opposite triangles");
        var full=new GlassPolygonCoverageModel();full.Upsert(Brick(1,source));full.Upsert(Brick(2,Rect(0,0,.5,1,true)));full.Upsert(Brick(3,Rect(.5,0,1,1,true)));
        var fr=Query(full);Check(fr.Changed&&fr.Triangles.All(t=>t.Changed&&t.Triangles.Count==0)&&full.GetMask(1)==1,"union of two neighbours fully covers the original cap");
        var triangle=new[]{T(P(0,0),P(1,0),P(0,1))};var touch=new GlassPolygonCoverageModel();touch.Upsert(Brick(1,triangle));touch.Upsert(Brick(2,new[]{T(P(1,0),P(1,1),P(0,1),true)}));
        var tr=Query(touch);Check(!tr.Changed&&tr.Triangles[0].Triangles.Count==1&&tr.Triangles[0].Triangles[0].A.Equals(new GlassBarycentric(1,0,0,1)),"diagonal line-only contact retains original identity triangle");
    }
    static void HoleSliverAndOffset()
    {
        var m=new GlassPolygonCoverageModel();m.Upsert(Brick(1,Rect(0,0,2,2)));
        m.Upsert(Brick(2,Rect(0,0,2,.75,true)));m.Upsert(Brick(3,Rect(0,1.25,2,2,true)));
        m.Upsert(Brick(4,Rect(0,.75,.75,1.25,true)));m.Upsert(Brick(5,Rect(1.25,.75,2,1.25,true)));
        var hole=Query(m);Check(ExactArea(hole,2,2).Equals(new Q(1,4)),"four neighbours leave exact central square hole area0.25");
        var inputs=Rect(0,0,2,2);
        foreach(var tri in hole.Triangles)foreach(var fragment in tri.Triangles)foreach(var weight in new[]{fragment.A,fragment.B,fragment.C})
        {var p=Lift(weight,inputs[tri.TriangleIndex]);Check(p.X>=.75&&p.X<=1.25&&p.Y>=.75&&p.Y<=1.25,"all retained hole vertices lie in the known aperture");}
        foreach(int power in new[]{5,20,40})
        {
            double gap=Math.Pow(2,-power);var s=new GlassPolygonCoverageModel();s.Upsert(Brick(1,Rect(0,0,1,1)));s.Upsert(Brick(2,Rect(0,0,1-gap,1,true)));
            Check(ExactArea(Query(s),new Q(1,2),new Q(1,2)).Equals(new Q(1,BigInteger.One<<power)),"a nonzero exact sliver is never discarded by an epsilon");
        }
        var offset=new GlassPolygonCoverageModel();offset.Upsert(Brick(1,Rect(0,0,1,1)));offset.Upsert(Brick(2,Rect(1d/32,0,1+1d/32,1,true)));
        Check(ExactArea(Query(offset),new Q(1,2),new Q(1,2)).Equals(new Q(1,32)),"one-grid-step in-plane offset retains exposed strip");
    }
    static void AxesAndClockwise()
    {
        for(int plane=0;plane<4;plane++)foreach(bool reverse in new[]{false,true})
        {
            GlassPolygonPoint Map(GlassPolygonPoint p)=>plane==0?p:plane==1?P(3,p.X,p.Y):plane==2?P(p.Y,4,p.X):P(p.X,p.Y,p.X+p.Y);
            GlassPolygonTriangle[] MapAll(GlassPolygonTriangle[] source)=>source.Select(t=>new GlassPolygonTriangle(Map(t.A),Map(t.B),Map(t.C))).ToArray();
            var m=new GlassPolygonCoverageModel();m.Upsert(Brick(1,MapAll(Rect(0,0,1,1,reverse))));m.Upsert(Brick(2,MapAll(Rect(0,0,.5,1,!reverse))));
            Check(ExactArea(Query(m),new Q(1,2),new Q(1,2)).Equals(new Q(1,2)),"all axis projections and a sloped plane preserve exact source barycentric area/winding");
        }
    }
    static void LifecycleAndDeterminism()
    {
        var targets=Rect(0,0,2,2);var candidates=new[]{Brick(9,Rect(0,0,.5,2,true)),Brick(3,Rect(.25,0,1,1,true)),Brick(7,Rect(0,.5,1.5,1.5,true))};
        var a=new GlassPolygonCoverageModel();var b=new GlassPolygonCoverageModel();a.Upsert(Brick(1,targets));b.Upsert(Brick(1,targets));
        foreach(var other in candidates)a.Upsert(other);foreach(var other in candidates.Reverse())b.Upsert(other);
        Check(Key(Query(a))==Key(Query(b)),"candidate insertion order cannot alter canonical query output");
        string before=Key(Query(a));a.Remove(7);var removed=Query(a);Check(Key(removed)!=before,"neighbour removal exposes additional geometry");a.Upsert(candidates[2]);Check(Key(Query(a))==before,"re-adding neighbour restores exact pattern");
        var deltasBefore=new List<GlassPolygonCoverageChange>();b.DrainChanges(deltasBefore);long candidatesBefore=a.CandidateChecks,failuresBefore=a.BudgetFailures;
        var immutable=Query(a);string oldKey=Key(immutable);for(int i=0;i<5;i++)Query(a);
        Check(a.CandidateChecks==candidatesBefore&&a.BudgetFailures==failuresBefore,"queries leave original model work and failure counters unchanged");
        var deltasAfter=new List<GlassPolygonCoverageChange>();a.DrainChanges(deltasAfter);
        Check(deltasAfter.Count==deltasBefore.Count&&deltasBefore.All(x=>deltasAfter.Any(y=>x.BuildId==y.BuildId&&x.Mask==y.Mask)),"queries leave mask delta queue unchanged");
        a.Clear();Check(Key(immutable)==oldKey,"returned result has no mutable model references");
        bool readOnly=false;try{((IList<GlassTriangleRemainder>)immutable.Triangles)[0]=null;}catch(NotSupportedException){readOnly=true;}Check(readOnly,"cap result collection is read-only");
        readOnly=false;var populated=immutable.Triangles.First(x=>x.Triangles.Count>0);try{((IList<GlassBarycentricTriangle>)populated.Triangles)[0]=default;}catch(NotSupportedException){readOnly=true;}Check(readOnly,"fragment collection is read-only");
        var degenerate=T(P(0,0),P(0,0),P(0,0));var ordinal=new GlassPolygonCoverageModel();ordinal.Upsert(Brick(1,new[]{degenerate,targets[0],degenerate,targets[1]},bit:5));
        var ord=Query(ordinal,1,5);Check(ord.Triangles.Count==2&&ord.Triangles[0].TriangleIndex==0&&ord.Triangles[1].TriangleIndex==1,"query ordinal explicitly excludes degenerate input triangles");
    }
    static void ContractsAndFailures()
    {
        Check(new GlassBarycentric(2,2,0,4).Equals(new GlassBarycentric(1,1,0,2)),"weights reduce a common denominator canonically");
        BigInteger huge=BigInteger.One<<600;Check(new GlassBarycentric(huge,0,0,huge).Equals(new GlassBarycentric(1,0,0,1)),"reduced storage rather than raw common scale controls precision");
        foreach(var args in new[]{new BigInteger[]{1,0,0,0},new BigInteger[]{-1,1,1,1},new BigInteger[]{1,1,0,1},new[]{(BigInteger.One<<513)-1,BigInteger.One,BigInteger.Zero,BigInteger.One<<513}})
        {bool rejected=false;try{new GlassBarycentric(args[0],args[1],args[2],args[3]);}catch(ArgumentOutOfRangeException){rejected=true;}Check(rejected,"malformed or excessive-precision weights reject");}
        Check(!default(GlassBarycentric).IsValid,"default barycentric cannot authorize geometry");
        var unknown=new GlassPolygonCoverageModel();Check(!unknown.TryGetCapRemainder(1,0,out var missing)&&missing==null,"unknown owner returns no result");unknown.Upsert(Brick(1,Rect(0,0,1,1)));
        Check(!unknown.TryGetCapRemainder(1,7,out missing)&&missing==null&&!unknown.TryGetCapRemainder(1,-1,out missing),"unknown group returns no result");
        foreach(var limits in new[]{new GlassPolygonCoverageLimits(maximumOperations:1),new GlassPolygonCoverageLimits(maximumCandidates:1),new GlassPolygonCoverageLimits(maximumFragments:1)})
        {
            var m=new GlassPolygonCoverageModel(limits);m.Upsert(Brick(1,new[]{T(P(0,0),P(4,0),P(0,4))}));m.Upsert(Brick(2,new[]{T(P(1,1),P(1.5,1),P(1,1.5),true)}));
            m.Upsert(Brick(3,new[]{T(P(2,0),P(2.5,0),P(2,.5),true)}));long oldFailures=m.BudgetFailures;var delta=new List<GlassPolygonCoverageChange>();m.DrainChanges(delta);delta.Clear();uint mask=m.GetMask(1);
            Check(!m.TryGetCapRemainder(1,0,out var r)&&r==null&&m.CapQueryBudgetFailures==1,"operation/candidate/fragment limit falls back without partial output");
            Check(m.BudgetFailures==oldFailures&&m.GetMask(1)==mask,"query exhaustion cannot corrupt original mask state or budget counter");m.DrainChanges(delta);Check(delta.Count==0,"query exhaustion cannot emit mask deltas");
        }
    }
    static void PrecisionAndPartitions()
    {
        bool exhausted=false;
        foreach(int power in new[]{180,220,250})
        {
            double e=Math.Pow(2,-power);var m=new GlassPolygonCoverageModel(new GlassPolygonCoverageLimits(maximumOperations:100000,maximumFragments:4096));
            m.Upsert(Brick(1,new[]{T(P(0,0),P(1,e),P(e,1))}));
            m.Upsert(Brick(2,new[]{T(P(e,0),P(1,0),P(0,1),true)}));
            if(!m.TryGetCapRemainder(1,0,out var r))
            {Check(r==null&&m.CapQueryBudgetFailures==1,"finite input causing excessive rational precision falls back with no geometry");exhausted=true;}
            else Check(r!=null,"supported precision remains explicit");
        }
        Check(exhausted,"adversarial dyadic intersections exercise the query precision limit");
        var target=Rect(0,0,1,1);var covers=new[]{Brick(2,Rect(0,0,.25,1,true)),Brick(3,Rect(.25,0,.5,1,true)),Brick(4,Rect(.5,0,.75,1,true))};
        string final=null;
        for(int partition=1;partition<=3;partition++)
        {
            var m=new GlassPolygonCoverageModel();m.Upsert(Brick(1,target));
            for(int i=0;i<covers.Length;i++){m.Upsert(covers[i]);if((i+1)%partition==0)Query(m);}
            var r=Query(m);Check(ExactArea(r,new Q(1,2),new Q(1,2)).Equals(new Q(1,4)),"partitioned discovery leaves the expected quarter");
            if(final==null)final=Key(r);else Check(Key(r)==final,"intermediate pure queries cannot change final pattern");
        }
    }
    static void Main(){HalfSquare();QuarterAndBand();CompleteEmptyAndEligibility();HoleSliverAndOffset();AxesAndClockwise();LifecycleAndDeterminism();ContractsAndFailures();PrecisionAndPartitions();Console.WriteLine($"PASS {checks} exact partial cap checks; native acceptance unproven.");}
}
