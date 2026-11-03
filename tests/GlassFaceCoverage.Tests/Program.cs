using System;
using System.Collections.Generic;
using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static GlassFaceCoverageBox Box(int id, long x, long y=0, long z=0, long sx=32, long sy=32, long sz=32, long entity=1)
        => new GlassFaceCoverageBox(entity,id,x,y,z,x+sx,y+sy,z+sz);
    static void Main()
    {
        var m = new GlassFaceCoverageModel();
        m.Upsert(Box(1,0));
        Check(m.GetMask(1)==0,"isolated cube retains six faces");
        m.Upsert(Box(2,32));
        Check(m.GetMask(1)==1 && m.GetMask(2)==2,"touching cubes hide only opposing X faces");
        m.Upsert(Box(3,0,32));
        Check(m.GetMask(1)==5 && m.GetMask(3)==8,"perpendicular join keeps exposed edges");
        m.Upsert(Box(4,0,0,32));
        Check(m.GetMask(1)==21 && m.GetMask(4)==32,"Z face bits use entity axes");
        m.Remove(2);
        Check(m.GetMask(1)==20 && m.GetMask(2)==0,"removal reopens former join and clears removed ID");
        m.Upsert(Box(2,100));
        Check(m.GetMask(2)==0 && m.GetMask(1)==20,"recycled build ID does not retain previous mask");
        m.Clear();
        m.Upsert(Box(1,0)); m.Upsert(Box(2,33));
        Check(m.GetMask(1)==0 && m.GetMask(2)==0,"one-tick gap stays visible");
        m.Upsert(Box(2,31));
        Check(m.GetMask(1)==0 && m.GetMask(2)==0,"overlapping volumes are not shared boundary planes");
        m.Upsert(Box(2,32,entity:2));
        Check(m.GetMask(1)==0 && m.GetMask(2)==0,"different entities never share masks");
        m.Upsert(Box(2,32,sy:16));
        Check(m.GetMask(1)==0 && m.GetMask(2)==2,"partial neighbor leaves larger face exposed");
        m.Upsert(Box(3,32,16,sy:16));
        Check(m.GetMask(1)==1 && m.GetMask(2)==6 && m.GetMask(3)==10,"union of smaller neighbors fully covers face");
        m.Remove(3);
        Check(m.GetMask(1)==0 && m.GetMask(2)==2,"removing part of union reopens larger face");
        m.Clear();
        m.Upsert(Box(1,-64,-32,-32,64,32,32)); m.Upsert(Box(2,0,-32,-32,32,32,32));
        Check(m.GetMask(1)==1 && m.GetMask(2)==2,"negative coordinates and unequal stretches join exactly");
        m.Upsert(Box(1,-64,-32,-32,32,64,32));
        Check(m.GetMask(1)==0 && m.GetMask(2)==0,"rotated cuboid extents invalidate previous plane");
        var changes = new List<GlassFaceCoverageChange>(); m.DrainChanges(changes);
        Check(changes.Count>0,"mask changes available for incremental upload");
        changes.Clear();m.DrainChanges(changes);Check(changes.Count==0,"draining consumes changes");
        m.Upsert(Box(1,0,sx:0)); Check(m.GetMask(1)==0,"degenerate box fails open");
        m.Clear();m.Upsert(Box(1,0,sy:1000000,sz:1000000));m.Upsert(Box(2,32,sy:1000000,sz:1000000));
        Check(m.GetMask(1)==0 && m.GetMask(2)==0,"oversized face fails open within bounded index cost");
        m.Clear();
        for(int i=0;i<2000;i++) m.Upsert(Box(i+1,i*64));
        Check(m.CandidateChecks<100000,"spatial index avoids global all-pairs scan");
        m.Clear();m.Upsert(Box(1,0));m.Upsert(Box(2,32));
        m.Upsert(new GlassFaceCoverageBox(1,2,32,0,0,64,32,32,254));
        Check(m.GetMask(1)==0 && m.GetMask(2)==0,"damaged glass cannot hide itself or its intact neighbor");
        m.Upsert(Box(2,32));
        Check(m.GetMask(1)==1 && m.GetMask(2)==2,"fully repaired glass restores exact shared coverage");
        var pending=new GlassFaceCoveragePending<object>();var entity=new object();var entity2=new object();
        pending.Add(entity,7);pending.Add(entity,7);
        Check(pending.Count==1,"missing render ID coalesces repeated lifecycle callbacks");
        int attempts=0;pending.Process(256,(e,i)=>{attempts++;return false;});
        Check(attempts==1 && pending.Count==1,"missing ID retries once per binding boundary");
        pending.Process(256,(e,i)=>{attempts++;return true;});
        Check(attempts==2 && pending.Count==0,"render ID becoming ready resolves without another scene scan");
        pending.Add(entity,7);pending.Remove(entity,7);pending.Process(256,(e,i)=>throw new Exception("removed brick retried"));
        Check(pending.Count==0,"deletion cancels pending registration before instance reuse");
        pending.Add(entity,1);pending.Add(entity2,1);pending.RemoveEntity(entity);
        int otherCount=0;pending.Process(256,(e,i)=>{if(ReferenceEquals(e,entity2))otherCount++;return true;});
        Check(otherCount==1 && pending.Count==0,"entity unspawn removes only its own pending IDs");
        for(int i=1;i<=300;i++)pending.Add(entity,i);
        int work=0;pending.Process(256,(e,i)=>{work++;return false;});
        Check(work==256 && pending.Count==300,"large pending set obeys per-boundary work budget");
        pending.Clear();pending.Add(entity,7);
        for(int i=0;i<61;i++)pending.Process(256,(e,id)=>false);
        Check(pending.Count==0,"permanently missing render IDs expire and fail open");
        // Exercise the same pre/post upload gates as TryPrepare. No lifecycle hook
        // runs between these boundaries; only the simulated native ID becomes ready.
        int tracked=0,allocations=0,polls=0;bool renderIdReady=false;
        Action boundary=()=>
        {
            if(!GlassFaceCoverageRemapGate.ShouldPoll(tracked,pending.Count))return;
            polls++;
            pending.Process(256,(e,id)=>
            {
                if(!renderIdReady)return false;
                tracked++;return true;
            });
            if(GlassFaceCoverageRemapGate.IsReady(tracked,1))allocations++;
        };
        pending.Add(entity,7);boundary();
        Check(polls==1 && pending.Count==1 && tracked==0 && allocations==0,"sole pending cube retries without allocating before native ID exists");
        renderIdReady=true;boundary();
        Check(polls==2 && pending.Count==0 && tracked==1 && allocations==1,"sole pending cube reaches remap after native ID appears without another hook");
        tracked=0;allocations=0;polls=0;renderIdReady=false;pending.Add(entity,7);
        for(int i=0;i<65;i++)boundary();
        Check(polls==60 && pending.Count==0 && allocations==0,"pending-only retries expire at the bounded limit without remap allocation");
        Check(!GlassFaceCoverageRemapGate.IsReady(1,0),"tracked cube without uploaded mask storage cannot remap");
        Console.WriteLine($"PASS: {checks} glass face coverage checks");
    }
}
