using System;
using System.Collections.Generic;
using System.Globalization;
using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static GlassPolygonPoint P(double x,double y,double z=0)=>new GlassPolygonPoint(x,y,z);
    static GlassPolygonPoint[] Square(double x=0,double y=0,double z=0)=>new[]{P(x,y,z),P(x+1,y,z),P(x+1,y+1,z),P(x,y+1,z)};
    static GlassNominalCap Cap(int bit=0,int sign=1,int type=118,int family=1,uint compatible=255,GlassPolygonPoint[] boundary=null)
    {
        Check(GlassNominalCap.TryCreate(bit,type,family,compatible,P(0,0,sign),boundary??Square(),out var cap),"valid synthetic cap");
        return cap;
    }
    static Dictionary<int,uint> Drain(GlassNominalCapModel model)
    {
        var list=new List<GlassPolygonCoverageChange>();model.DrainChanges(list);
        var result=new Dictionary<int,uint>();foreach(var change in list){Check(!result.ContainsKey(change.BuildId),"replacement delta unique per ID");result.Add(change.BuildId,change.Mask);}return result;
    }
    static void Main()
    {
        Basic();Separation();Identity();Replacement();Admission();Limits();BoundaryPermutations();MultipleGroups();
        Console.WriteLine("PASS: "+checks+" nominal cap index checks; native factory, integration and visual acceptance are separate.");
    }
    static void Basic()
    {
        var m=new GlassNominalCapModel();
        Check(m.Upsert(1,1,255,new[]{Cap(1)}),"first owner admitted");
        Check(m.GetMask(1)==0&&Drain(m).Count==0,"unpaired cap does not authorize any face");
        Check(m.Upsert(1,2,255,new[]{Cap(2,-1)}),"opposite owner admitted");
        Check(m.GetMask(1)==2&&m.GetMask(2)==4,"opposite equal boundary gives independently expected group bits");
        var changed=Drain(m);Check(changed.Count==2&&changed[1]==2&&changed[2]==4,"both matching sides become dirty");
        Check(m.OwnerCount==2&&m.CapCount==2&&m.KeyPayloadBytes==384,"payload counts 48 bytes per canonical boundary edge, excluding heap overhead");
        Check(m.GetMask(99)==0&&Drain(m).Count==0,"read-only mask queries do not emit changes");
        m.Remove(2);Check(m.GetMask(1)==0&&m.GetMask(2)==0,"remove restores both exposed caps");
        changed=Drain(m);Check(changed.Count==2&&changed[1]==0&&changed[2]==0,"both removed and former neighbour publish zero replacement");
        m.Remove(2);Check(Drain(m).Count==0,"absent remove is idle");
        Check(m.OwnerCount==1&&m.CapCount==1&&m.KeyPayloadBytes==192,"remove releases tracked key payload");
    }
    static void Separation()
    {
        foreach(int mode in new[]{0,1,2,3,4,5,6})
        {
            var m=new GlassNominalCapModel();m.Upsert(1,1,255,new[]{Cap()});
            var b=mode==0?Cap(1):mode==1?Cap(1,-1,317):mode==2?Cap(1,-1,118,2):mode==3?Cap(1,-1,boundary:Square(1d/32)):
                mode==4?Cap(1,-1,boundary:Square(z:1d/32)):mode==5?Cap(1,-1,boundary:Square(Math.Pow(2,-40))):Cap(1,-1);
            m.Upsert(mode==6?2:1,2,255,new[]{b});
            Check(m.GetMask(1)==0&&m.GetMask(2)==0,"same side/type/profile/entity and exact placement differences retain caps; case "+mode);
        }
        var self=new GlassNominalCapModel();self.Upsert(1,1,255,new[]{Cap(0,1),Cap(1,-1)});
        Check(self.GetMask(1)==0,"two caps of one build ID cannot cover each other");
        var c=new GlassNominalCapModel();c.Upsert(1,1,255,new[]{Cap(0,1,209,3,2)});c.Upsert(1,2,255,new[]{Cap(1,-1,209,3,1)});
        Check(c.GetMask(1)==1&&c.GetMask(2)==2,"cross-cap 209 compatible group relation");
        c.Upsert(1,2,255,new[]{Cap(0,-1,209,3,2)});Check(c.GetMask(1)==0&&c.GetMask(2)==0,"same-cap 209 relation explicitly excluded");
        c.Upsert(1,2,255,new[]{Cap(1,-1,209,3,4)});Check(c.GetMask(1)==0&&c.GetMask(2)==0,"compatibility must be mutual");
    }
    static void Identity()
    {
        var input=Square();var a=Cap(0,1,boundary:input);input[0]=P(123,456,789);
        var m=new GlassNominalCapModel();m.Upsert(1,1,255,new[]{a});m.Upsert(1,2,255,new[]{Cap(1,-1)});
        Check(m.GetMask(1)==1&&m.GetMask(2)==2,"caller mutation cannot alter admitted boundary identity");
        bool readOnly=false;try{((IList<GlassPolygonPoint>)a.Boundary)[0]=P(1,2,3);}catch(NotSupportedException){readOnly=true;}
        Check(readOnly&&a.Boundary[0].X==0,"boundary accessor cannot mutate the private snapshot");
        double negativeZero=BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL));
        var zeros=Square();zeros[0]=P(negativeZero,negativeZero,negativeZero);var normalized=Cap(boundary:zeros);
        Check(BitConverter.DoubleToInt64Bits(normalized.Boundary[0].X)==0&&BitConverter.DoubleToInt64Bits(normalized.Boundary[0].Z)==0,"signed-zero coordinates are stored canonically");
        var old=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("da-DK");var b=Square(.125,.25);b[0]=P(.125,.25,-0d);
            var first=Cap(0,1,boundary:b);CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("en-US");
            var n=new GlassNominalCapModel();n.Upsert(1,1,255,new[]{first});n.Upsert(1,2,255,new[]{Cap(1,-1,boundary:Square(.125,.25))});
            Check(n.GetMask(1)==1&&n.GetMask(2)==2,"typed key is culture-independent and normalizes signed zero");
        }
        finally{CultureInfo.CurrentCulture=old;}
        var far=new GlassNominalCapModel();far.Upsert(1,1,255,new[]{Cap(0,boundary:Square(1048575,1048575))});
        far.Upsert(1,2,255,new[]{Cap(1,-1,boundary:Square(1048575,1048575))});
        Check(far.GetMask(1)==1&&far.GetMask(2)==2,"large exact translations preserve boundary identity");
    }
    static void Replacement()
    {
        var m=new GlassNominalCapModel();m.Upsert(1,1,255,new[]{Cap()});m.Upsert(1,2,255,new[]{Cap(1,-1)});Drain(m);
        Check(m.Upsert(1,2,254,new[]{Cap(1,-1)}),"unhealthy replacement conservatively removes nominal ownership");
        Check(m.GetMask(1)==0&&m.GetMask(2)==0&&m.OwnerCount==1,"health change revokes both sides");
        var d=Drain(m);Check(d[1]==0&&d[2]==0,"health changes emit zero replacements");
        m.Upsert(1,2,255,new[]{Cap(1,-1)});Drain(m);
        Check(!m.Upsert(1,2,255,new[]{Cap(0,-1),Cap(0,1)}),"duplicate group bits reject replacement");
        Check(m.GetMask(1)==0&&m.GetMask(2)==0&&!m.Faulted,"bad replacement removes previous identity without poisoning index");
        m.Upsert(1,2,255,new[]{Cap(1,-1)});Drain(m);
        Check(!m.Upsert(1,2,255,null)&&m.GetMask(1)==0&&m.GetMask(2)==0,"null caps cannot preserve old positive mask");
        m.Upsert(1,2,255,new[]{Cap(1,-1)});Drain(m);
        Check(m.Upsert(2,2,255,new[]{Cap(1,-1)})&&m.GetMask(1)==0&&m.GetMask(2)==0,"ID reused in another entity revokes former nominal relation");
        m.Upsert(1,2,255,new[]{Cap(1,-1)});Drain(m);
        Check(m.Upsert(1,2,255,Array.Empty<GlassNominalCap>())&&m.GetMask(1)==0&&m.OwnerCount==1,"no-profile replacement removes old nominal contribution");
        m.Upsert(1,2,255,new[]{Cap(1,-1)});Drain(m);m.Upsert(1,3,255,new[]{Cap(2,-1)});Drain(m);
        m.Remove(2);Check(m.GetMask(1)==1&&m.GetMask(3)==4,"removing one of two opposite neighbours preserves independently supported cap");
        Check(Drain(m)[2]==0,"removed owner still emits zero while supported neighbour remains");
        m.Clear();Check(m.OwnerCount==0&&m.CapCount==0&&m.KeyPayloadBytes==0,"Clear releases owned index state");
        d=Drain(m);Check(d[1]==0&&d[3]==0,"Clear queues zero for every remaining positive owner");
        Check(Drain(m).Count==0,"drain consumes replacement changes");
    }
    static void Admission()
    {
        bool Valid(int bit=0,int type=118,int family=1,uint compatibility=255,GlassPolygonPoint? normal=null,GlassPolygonPoint[] points=null)=>
            GlassNominalCap.TryCreate(bit,type,family,compatibility,normal??P(0,0,1),points??Square(),out _);
        Check(!Valid(bit:-1)&&!Valid(bit:8),"group bits bounded");
        Check(!Valid(type:-1)&&!Valid(family:-1),"negative type/profile rejected");
        Check(!Valid(compatibility:0)&&!Valid(compatibility:256),"compatibility mask bounded and nonzero");
        foreach(var n in new[]{P(0,0,0),P(0,0,2),P(1,1,0),P(double.NaN,0,1),P(0,double.PositiveInfinity,1)})Check(!Valid(normal:n),"normal must be a finite signed unit axis");
        Check(!GlassNominalCap.TryCreate(0,118,1,255,P(0,0,1),null,out _),"null boundary rejected");
        Check(!Valid(points:new[]{P(0,0),P(1,0)}),"fewer than three boundary vertices rejected");
        Check(!Valid(points:new[]{P(0,0),P(1,0),P(2,0)}),"collinear boundary rejected");
        Check(!Valid(points:new[]{P(0,0),P(1,0),P(1,1,1),P(0,1)}),"noncoplanar boundary rejected exactly");
        Check(!Valid(points:new[]{P(0,0),P(1,1),P(0,1),P(1,0)}),"self-crossing boundary rejected");
        Check(!Valid(points:new[]{P(0,0),P(3,0),P(0,3),P(3,3),P(0,1)}),"nonzero signed area does not admit a self crossing");
        Check(!Valid(points:new[]{P(0,0),P(2,0),P(1,0),P(1,1),P(0,1)}),"adjacent reversal/overlap rejected");
        Check(!Valid(points:new[]{P(0,0),P(1,0),P(1,1),P(0,0)}),"duplicate boundary vertex rejected");
        Check(!Valid(points:Square(1048576))&&!Valid(points:new[]{P(0,0),P(double.NaN,1),P(1,0)}),"finite coordinate bound enforced");
        var many=new GlassPolygonPoint[17];for(int i=0;i<many.Length;i++)many[i]=P(Math.Cos(i),Math.Sin(i));Check(!Valid(points:many),"boundary vertex count bounded");
        var l=new[]{P(0,0),P(2,0),P(2,2),P(1,2),P(1,1),P(0,1)};Check(Valid(points:l),"simple concave L cap remains admissible");
        var m=new GlassNominalCapModel();Check(!m.Upsert(1,0,255,new[]{Cap()}),"zero build ID rejected");
        Check(!m.Upsert(1,-1,255,new[]{Cap()}),"negative build ID rejected");
        Check(!m.Upsert(1,1,255,new GlassNominalCap[]{null}),"null cap rejected");
        var caps=new GlassNominalCap[9];for(int i=0;i<caps.Length;i++)caps[i]=Cap(i%8);Check(!m.Upsert(1,1,255,caps),"per-owner cap count bounded");
        m.Upsert(1,1,255,new[]{Cap()});m.Upsert(1,2,255,new[]{Cap(1,-1)});Drain(m);
        Check(!m.Upsert(1,3,255,new GlassNominalCap[]{null})&&m.GetMask(1)==1&&m.GetMask(2)==2,"malformed unrelated owner leaves valid nominal relation intact");
        bool nullDrain=false;try{m.DrainChanges(null);}catch(ArgumentNullException){nullDrain=true;}Check(nullDrain,"null change destination rejected");
    }
    static void Limits()
    {
        foreach(int kind in new[]{0,1,2,3})
        {
            var limit=kind==0?new GlassNominalCapLimits(maximumOwners:2):kind==1?new GlassNominalCapLimits(maximumCaps:2):
                kind==2?new GlassNominalCapLimits(maximumKeyPayloadBytes:384):new GlassNominalCapLimits(maximumCandidatesPerKey:2);
            var m=new GlassNominalCapModel(limit);m.Upsert(1,1,255,new[]{Cap()});m.Upsert(1,2,255,new[]{Cap(1,-1)});Drain(m);
            Check(m.GetMask(1)==1&&m.GetMask(2)==2,"budget fixture reaches positive state "+kind);
            Check(!m.Upsert(1,3,255,new[]{Cap(2,-1)}),"limit failure reported "+kind);
            Check(m.Faulted&&!string.IsNullOrEmpty(m.FailureReason),"limit failure sticks with reason "+kind);
            Check(m.GetMask(1)==0&&m.GetMask(2)==0&&m.OwnerCount==0&&m.CapCount==0&&m.KeyPayloadBytes==0,"budget failure revokes all nominal masks/state "+kind);
            var d=Drain(m);Check(d[1]==0&&d[2]==0,"budget failure publishes both previous positives as zero "+kind);
            Check(!m.Upsert(1,1,255,new[]{Cap()})&&m.OwnerCount==0,"fault cannot silently recover before Clear "+kind);
            m.Clear();Check(!m.Faulted&&m.FailureReason==null,"Clear resets fault "+kind);
            Check(m.Upsert(1,1,255,new[]{Cap()})&&m.Upsert(1,2,255,new[]{Cap(1,-1)})&&m.GetMask(1)==1,"Clear permits fresh nominal relation "+kind);
        }
        foreach(int kind in new[]{0,1,2,3})
        {
            bool rejected=false;try{_ = kind==0?new GlassNominalCapLimits(maximumOwners:0):kind==1?new GlassNominalCapLimits(maximumCaps:0):kind==2?new GlassNominalCapLimits(maximumKeyPayloadBytes:0):new GlassNominalCapLimits(maximumCandidatesPerKey:0);}catch(ArgumentOutOfRangeException){rejected=true;}
            Check(rejected,"nonpositive limits rejected "+kind);
        }
    }
    static void BoundaryPermutations()
    {
        foreach(int axis in new[]{0,1,2})foreach(int start in new[]{0,1,2,3})foreach(int reverse in new[]{0,1})
        {
            var p=Square();for(int i=0;i<p.Length;i++)p[i]=axis==0?P(0,p[i].X,p[i].Y):axis==1?P(p[i].Y,0,p[i].X):p[i];
            var q=new GlassPolygonPoint[4];for(int i=0;i<4;i++)q[i]=p[(start+(reverse==0?i:4-i))%4];
            var n=axis==0?P(1,0,0):axis==1?P(0,1,0):P(0,0,1);var opposite=P(-n.X,-n.Y,-n.Z);
            bool madeA=GlassNominalCap.TryCreate(0,118,1,255,n,p,out var a);
            bool madeB=GlassNominalCap.TryCreate(1,118,1,255,opposite,q,out var b);
            Check(madeA&&madeB,"axis cap admitted");
            var m=new GlassNominalCapModel();m.Upsert(1,1,255,new[]{a});m.Upsert(1,2,255,new[]{b});Check(m.GetMask(1)==1&&m.GetMask(2)==2,"cyclic/reversed edge ordering and axis permutation preserve key");
        }
    }
    static void MultipleGroups()
    {
        var m=new GlassNominalCapModel();m.Upsert(1,1,255,new[]{Cap(0),Cap(4,boundary:Square(z:2))});
        m.Upsert(1,2,255,new[]{Cap(2,-1)});m.Upsert(1,3,255,new[]{Cap(5,-1,boundary:Square(z:2))});
        Check(m.GetMask(1)==17&&m.GetMask(2)==4&&m.GetMask(3)==32,"independent groups compose their own bits");Drain(m);
        m.Remove(2);var d=Drain(m);Check(m.GetMask(1)==16&&d[1]==16&&d[2]==0&&!d.ContainsKey(3),"removing one group replaces mask without disturbing another key");
        var coalesced=new GlassNominalCapModel();coalesced.Upsert(1,1,255,new[]{Cap()});coalesced.Upsert(1,2,255,new[]{Cap(1,-1)});coalesced.Remove(2);
        d=Drain(coalesced);Check(d[1]==0&&d[2]==0,"unpublished positive then removal coalesces to current zero, never accumulated OR");
        var bounded=new GlassNominalCapModel(new GlassNominalCapLimits(maximumCandidatesPerKey:2));
        bounded.Upsert(1,1,255,new[]{Cap(0),Cap(1,-1)});Check(!bounded.Faulted&&bounded.GetMask(1)==0,"same-owner caps consume bounded key candidates without self-matching");
        Check(!bounded.Upsert(1,2,255,new[]{Cap(2,-1)})&&bounded.Faulted,"candidate budget includes every cap, not only distinct owners");
    }
}
