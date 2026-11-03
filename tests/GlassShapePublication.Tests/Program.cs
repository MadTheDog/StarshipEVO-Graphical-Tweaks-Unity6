using System;
using System.Collections.Generic;
using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static GlassPolygonTriangle Triangle(bool reverse=false,double size=1,double z=0)
    {
        var a=new GlassPolygonPoint(0,0,z);var b=new GlassPolygonPoint(size,0,z);var c=new GlassPolygonPoint(0,size,z);
        return reverse?new GlassPolygonTriangle(a,c,b):new GlassPolygonTriangle(a,b,c);
    }
    static GlassPolygonCoverageBrick Brick(int id,bool reverse=false,long entity=1,double size=1,double z=0)=>
        new GlassPolygonCoverageBrick(entity,id,255,new GlassPolygonFaceGroup(0,Triangle(reverse,size,z)));
    sealed class Buffer
    {
        internal readonly GlassMaskRecord[] Data;
        internal bool Throw;
        internal Buffer(int count){Data=new GlassMaskRecord[count];}
    }
    sealed class ThirdProvider:IGlassWholeMaskProvider
    {
        internal readonly Dictionary<int,uint> Masks=new Dictionary<int,uint>();
        internal readonly List<int> Touched=new List<int>();
        internal Action OnRead,OnDrain;
        public uint GetMask(int id){OnRead?.Invoke();return Masks.TryGetValue(id,out uint value)?value:0;}
        public void DrainTouched(List<int> output){OnDrain?.Invoke();output.AddRange(Touched);Touched.Clear();}
        internal void Set(int id,uint mask){Masks[id]=mask;Touched.Add(id);}
    }
    sealed class State
    {
        internal readonly GlassRebuildGate Gate=new GlassRebuildGate();
        internal readonly GlassPolygonCoverageModel Model;
        internal readonly GlassNominalCapModel Nominal;
        internal readonly GlassMaskUploadJournal Journal=new GlassMaskUploadJournal();
        internal readonly Dictionary<int,uint> Owners=new Dictionary<int,uint>();
        internal readonly List<int> NewOwners=new List<int>();
        internal readonly List<GlassPolygonCoverageChange> Changes=new List<GlassPolygonCoverageChange>();
        internal readonly List<Buffer> Buffers=new List<Buffer>();
        internal GlassMaskRecord[] Records=new GlassMaskRecord[1];
        internal long Epoch;
        internal int UploadCalls;
        internal State(GlassPolygonCoverageLimits limits=null,GlassNominalCapLimits nominalLimits=null)
        {Model=new GlassPolygonCoverageModel(limits);Nominal=new GlassNominalCapModel(nominalLimits);}
        internal void Begin()
        {
            Model.Clear();Nominal.Clear();Owners.Clear();NewOwners.Clear();Changes.Clear();Model.DrainChanges(Changes);Nominal.DrainChanges(Changes);Changes.Clear();
            Epoch=Gate.Begin();
        }
        internal void Insert(GlassPolygonCoverageBrick brick,uint source=100)
        {Owners[brick.BuildId]=source;NewOwners.Add(brick.BuildId);Model.Remove(brick.BuildId);Model.Upsert(brick);}
        internal bool Capture()=>GlassMaskSnapshot.Capture(Gate,Epoch,Model,Changes,NewOwners,
            id=>Owners.TryGetValue(id,out uint source)?source:(uint?)null,ref Records,Journal,4194303,Nominal);
        internal void Allocate()
        {
            if(Buffers.Count>0&&Buffers[Buffers.Count-1].Data.Length==Records.Length)return;
            Buffers.Add(new Buffer(Records.Length));Journal.RequireFullUpload();
        }
        internal bool Publish()=>Journal.Publish(Buffers,b=>b.Data.Length,(b,s,n)=>
        {UploadCalls++;if(b.Throw)throw new InvalidOperationException("injected upload failure");Array.Copy(Records,s,b.Data,s,n);});
        internal void Invalidate(){Gate.Invalidate();Array.Clear(Records);Journal.RequireFullUpload();}
    }
    static void PartialAndInvalidation()
    {
        var s=new State();Check(!s.Capture(),"default token must not capture an unbegun model");
        s.Begin();s.Insert(Brick(1));
        Check(s.Capture(),"fresh partial model must capture before inventory and retry completion");
        s.Allocate();Check(s.Publish()&&s.Buffers[0].Data[1].Mask==0&&s.Buffers[0].Data[1].SourceStart==100,
            "new owner with no covered group still publishes its source identity");
        s.Insert(Brick(2,true));Check(s.Capture()&&s.Gate.Blocked,"incomplete inventory does not block a current partial proof");
        s.Allocate();Check(s.Publish()&&s.Buffers[0].Data[1].Mask==1&&s.Buffers[s.Buffers.Count-1].Data[2].Mask==1,
            "exact current same-entity opposite triangles publish positive masks while another entry is unresolved");
        int calls=s.UploadCalls;s.Capture();s.Publish();Check(s.UploadCalls==calls,"empty scheduled delta performs no upload");
        long old=s.Epoch;s.Invalidate();
        Check(!s.Capture()&&!s.Gate.TryComplete(old,0),"mutation revokes capture and completion before stale model can drain");
        Check(s.Publish()&&s.Buffers[0].Data[1].Mask==0,"late zero publication revokes already recorded partial proof");
        s.Begin();s.Insert(Brick(1));s.Capture();s.Publish();
        Check(s.Records[1].Mask==0&&!s.Gate.TryComplete(old,0),"surviving target remains visible and obsolete completion cannot restore proof");
        s.Insert(Brick(2,true,entity:2));s.Capture();s.Allocate();s.Publish();
        Check(s.Records[1].Mask==0,"cross-entity geometry cannot prove a partial mask");
        s.Insert(Brick(2,true,z:.0025));s.Capture();s.Publish();Check(s.Records[1].Mask==0,"real gap stays visible");
    }
    static void ConservativeReplacement()
    {
        var s=new State(new GlassPolygonCoverageLimits(maximumCandidates:1));s.Begin();
        s.Insert(Brick(1));s.Insert(Brick(2,true,size:2));s.Capture();s.Allocate();s.Publish();
        Check(s.Records[1].Mask==1,"initial exact subtraction proof is positive before budget fallback");
        s.Insert(Brick(3,true,size:2));s.Capture();s.Allocate();s.Publish();
        Check(s.Model.BudgetFailures>0&&s.Records[1].Mask==0&&s.Buffers[0].Data[1].Mask==0,
            "new candidates can force budget fallback and must replace positive mask with zero rather than OR");
        s.Owners.Remove(2);s.NewOwners.Add(2);s.Model.Remove(2);s.Capture();s.Publish();
        Check(s.Records[2].Mask==0&&s.Records[2].SourceStart==0,"freed owner publishes an all-zero record");
        var unknown=new State();unknown.Begin();unknown.Model.Upsert(Brick(1));unknown.Model.Upsert(Brick(2,true));
        unknown.Capture();Check(unknown.Model.GetMask(1)==1&&unknown.Records[1].Mask==0&&unknown.Records[1].SourceStart==0,
            "a positive model delta without a current owner cannot authorize suppression");
    }
    static void ReuseAndToggle()
    {
        var s=new State();s.Begin();s.Insert(Brick(1));s.Insert(Brick(2,true));s.Capture();s.Allocate();s.Publish();
        long obsolete=s.Epoch;s.Invalidate();s.Publish();
        Check(!s.Capture()&&s.Buffers[0].Data[1].Mask==0,"disable immediately revokes old partial proof and zeros GPU mirror");
        s.Begin();s.Insert(Brick(1,z:5));s.Capture();s.Publish();
        Check(s.Records[1].Mask==0&&s.Records[1].SourceStart==100&&!s.Gate.CanPublish(obsolete),
            "same source and build ID at moved geometry cannot inherit an old epoch mask after re-enable");
        s.Invalidate();s.Publish();s.Begin();s.Insert(Brick(1,z:5),999);s.Capture();s.Publish();
        Check(s.Records[1].Mask==0&&s.Records[1].SourceStart==999,"different source reuse replaces uint2 identity while retaining visible geometry");
    }
    static void GrowthAndJournal()
    {
        var s=new State();s.Begin();s.Insert(Brick(1));s.Insert(Brick(2,true));s.Capture();s.Allocate();s.Publish();
        var retired=s.Buffers[0];s.Insert(Brick(17,entity:2),777);s.Capture();s.Allocate();
        Check(s.Publish()&&retired.Data[1].Mask==1&&s.Buffers[1].Data[1].Mask==1,
            "partial growth refreshes overlapping prefix in retained and new allocations");
        Check(s.Buffers[1].Data[17].Mask==0&&s.Buffers[1].Data[17].SourceStart==777&&
            s.Buffers[1].Data[16].SourceStart==0&&s.Buffers[1].Data[31].Mask==0,
            "growth preserves zero-mask owner identity and initializes unknown capacity to zero");
        s.Invalidate();s.Buffers[1].Throw=true;
        try{s.Publish();Check(false,"injected second-generation zero upload must fail");}catch(InvalidOperationException){}
        Check(s.Journal.HasPending&&retired.Data[1].Mask==0&&s.Buffers[1].Data[1].Mask==1,
            "failed late upload cannot acknowledge partially revoked retained buffers");
        s.Buffers[1].Throw=false;Check(s.Publish()&&s.Buffers[1].Data[1].Mask==0&&retired.Data[1].Mask==0,
            "retry zeros every retained allocation independent of geometry deltas");
        s.Buffers.Clear();s.Journal.RequireFullUpload();Check(!s.Publish()&&s.Journal.HasPending,"no-resource full obligation survives empty deltas");
        s.Begin();s.Capture();s.Allocate();Check(s.Publish()&&!s.Journal.HasPending,"first resource consumes retained full obligation with empty model delta");
        s.Journal.MarkDirty(0,0);bool changed=false;
        Check(!s.Journal.Publish(s.Buffers,b=>b.Data.Length,(b,i,n)=>{if(!changed){changed=true;s.Invalidate();}})&&s.Journal.HasPending,
            "reentrant invalidation cannot commit an older journal publication");
    }
    static void PartitionEquivalence()
    {
        var inputs=new[]{Brick(1),Brick(2,true),Brick(3,entity:2),Brick(4,true,entity:2),Brick(5,true,z:.0025)};
        var whole=new GlassPolygonCoverageModel();foreach(var b in inputs)whole.Upsert(b);
        for(int partition=1;partition<=3;partition++)
        {
            var s=new State();s.Begin();
            for(int i=0;i<inputs.Length;i++)
            {s.Insert(inputs[i],(uint)(100+i));if((i+1)%partition==0){s.Capture();s.Allocate();s.Publish();}}
            s.Capture();s.Allocate();s.Publish();
            foreach(var b in inputs)Check(s.Records[b.BuildId].Mask==whole.GetMask(b.BuildId),"partitioned final masks equal unpartitioned exact model");
            Check(s.Records[1].Mask==1&&s.Records[3].Mask==1&&s.Records[5].Mask==0,"partition comparison includes hand-derived covered and uncovered controls");
        }
    }
    static void DeadlineAndRetries()
    {
        var pending=new GlassFaceCoveragePending<object>();var entity=new object();
        pending.Add(entity,1);pending.Add(entity,2);int attempts=0;bool timeRemaining=true;
        pending.Process(8,(e,id)=>{attempts++;timeRemaining=false;return true;},()=>timeRemaining);
        Check(attempts==1&&pending.Count==1,"deadline stops a snapshotted batch before attempting the next entry");
        for(int i=0;i<65;i++)pending.Process(8,(e,id)=>{attempts++;return false;},()=>false);
        Check(attempts==1&&pending.Count==1,"deadline-skipped work is neither attempted nor charged against retry lifetime");
        pending.Add(entity,3);var seen=new HashSet<int>();
        pending.Process(8,(e,id)=>{Check(seen.Add(id),"each candidate receives at most one attempt per frame");return id==3;},()=>true);
        Check(seen.Count==2&&pending.Count==1,"permanently unresolved candidate cannot prevent unrelated exact work");
        pending.Clear();for(int i=1;i<=20;i++)pending.Add(entity,i);attempts=0;
        pending.Process(8,(e,id)=>{attempts++;return false;},()=>true);
        Check(attempts==8&&pending.Count==20,"pending exact work retains eight-attempt frame ceiling");
    }
    static void DiscoveryFairness()
    {
        const long frequency=1000000;long ticks=0;int inspected=0;
        while(GlassCoverageWorkBudget.CanDiscover(inspected,ticks,frequency)){inspected++;ticks++;}
        var pending=new GlassFaceCoveragePending<object>();var entity=new object();
        for(int i=1;i<=20;i++)pending.Add(entity,i);int exact=0;
        pending.Process(8,(e,id)=>{exact++;ticks+=100;return true;},()=>GlassCoverageWorkBudget.CanProcess(ticks,frequency));
        Check(inspected==500&&exact==8&&pending.Count==12,
            "large sparse inventory uses its discovery subbudget and leaves time for eight pending exact attempts");
        inspected=0;while(GlassCoverageWorkBudget.CanDiscover(inspected,0,frequency))inspected++;
        Check(inspected==8192,"inventory absolute ceiling bounds scans even when clock has not advanced");
        Check(!GlassCoverageWorkBudget.CanDiscover(0,500,frequency)&&GlassCoverageWorkBudget.CanProcess(500,frequency)&&
            !GlassCoverageWorkBudget.CanProcess(2000,frequency),"discovery and exact work obey separate boundaries within shared deadline");
    }
    static GlassNominalCap Cap(bool opposite=false,double z=0)
    {
        var boundary=new[]{new GlassPolygonPoint(0,0,z),new GlassPolygonPoint(1,0,z),
            new GlassPolygonPoint(1,1,z),new GlassPolygonPoint(0,1,z)};
        if(opposite)Array.Reverse(boundary);
        Check(GlassNominalCap.TryCreate(0,118,1,1,new GlassPolygonPoint(0,0,opposite?-1:1),boundary,out var cap),"nominal cap fixture admitted");
        return cap;
    }
    static void CombinedNominalPublication()
    {
        var s=new State();s.Begin();s.Insert(Brick(1));s.Insert(Brick(2,true,z:1));
        s.Nominal.Upsert(1,1,255,new[]{Cap()});s.Capture();s.Allocate();s.Publish();
        Check(s.Records[1].Mask==0&&s.Model.GetMask(1)==0,"unpaired nominal and noncoplanar exact faces start visible");
        s.Nominal.Upsert(1,2,255,new[]{Cap(true)});
        Check(s.Nominal.GetMask(1)==1&&s.Nominal.GetMask(2)==1,"combined publication requires a working positive nominal index");
        s.Capture();s.Publish();
        Check(s.Model.GetMask(1)==0&&s.Model.GetMask(2)==0&&s.Records[1].Mask==1&&s.Records[2].Mask==1,
            "nominal-only delta publishes both current masks without an exact-model change");
        s.Insert(Brick(17,entity:2),777);s.Capture();s.Allocate();s.Publish();
        s.Nominal.Remove(2);s.Capture();s.Publish();
        Check(s.Records[1].Mask==0&&s.Records[2].Mask==0&&s.Buffers[0].Data[1].Mask==0&&s.Buffers[1].Data[1].Mask==0,
            "nominal removal replaces positive masks in every retained generation");
        s.Nominal.Upsert(1,2,255,new[]{Cap(true)});s.Capture();s.Publish();
        s.Insert(Brick(2,true));s.Capture();s.Publish();
        Check(s.Model.GetMask(1)==1&&s.Records[1].Mask==1,"strict and nominal masks combine without losing either proof");
        int uploads=s.UploadCalls;
        s.Model.Remove(2);s.Capture();s.Publish();
        Check(s.Model.GetMask(1)==0&&s.Nominal.GetMask(1)==1&&s.Records[1].Mask==1&&s.UploadCalls==uploads,
            "strict-positive to nominal-positive transition preserves the current combined mask without a redundant upload");
        s.Nominal.Upsert(1,2,255,new[]{Cap(true,z:2)});s.Insert(Brick(2,true,z:2),999);s.Capture();s.Publish();
        Check(s.Records[1].Mask==0&&s.Records[2].Mask==0&&s.Records[2].SourceStart==999,
            "replacement geometry and source identity cannot inherit a previous nominal match");
        s.Nominal.Upsert(1,2,255,new[]{Cap(true)});s.Invalidate();
        Check(!s.Capture()&&s.Records[1].Mask==0,"invalid epoch cannot recapture pending nominal positives");
        s.Publish();s.Begin();s.Insert(Brick(1,z:5));s.Capture();s.Publish();
        Check(s.Nominal.GetMask(1)==0&&s.Records[1].Mask==0&&s.Buffers[0].Data[1].Mask==0,
            "fresh epoch discards both old models and their pending changes");
    }
    static void NominalBudgetAndReentrancy()
    {
        var s=new State(nominalLimits:new GlassNominalCapLimits(maximumCandidatesPerKey:2));s.Begin();
        s.Insert(Brick(1));s.Insert(Brick(2,true,z:1));s.Insert(Brick(17,entity:2),777);
        s.Nominal.Upsert(1,1,255,new[]{Cap()});s.Nominal.Upsert(1,2,255,new[]{Cap(true)});
        s.Capture();s.Allocate();s.Publish();
        s.Insert(Brick(65,entity:3),888);s.Capture();s.Allocate();s.Publish();
        Check(s.Records[1].Mask==1&&s.Buffers.Count==2,"budget fixture starts with positive masks in two generations");
        s.Nominal.Upsert(1,3,255,new[]{Cap()});s.Insert(Brick(3,z:3));s.Insert(Brick(4,true,z:3));
        s.Capture();s.Buffers[1].Throw=true;
        try{s.Publish();Check(false,"combined zero retry injects an upload failure");}catch(InvalidOperationException){}
        Check(s.Nominal.Faulted&&s.Records[1].Mask==0&&s.Records[2].Mask==0&&s.Journal.HasPending,
            "nominal budget failure revokes old positives and retains the failed GPU upload obligation");
        s.Buffers[1].Throw=false;s.Publish();
        Check(s.Buffers[0].Data[1].Mask==0&&s.Buffers[1].Data[1].Mask==0&&s.Records[3].Mask==1&&s.Records[4].Mask==1,
            "nominal-only failure zeros every retained GPU prefix while exact coverage stays usable");
        var t=new State();t.Begin();t.Insert(Brick(1));t.Insert(Brick(2,true,z:1));
        t.Nominal.Upsert(1,1,255,new[]{Cap()});t.Nominal.Upsert(1,2,255,new[]{Cap(true)});
        bool captured=GlassMaskSnapshot.Capture(t.Gate,t.Epoch,t.Model,t.Changes,t.NewOwners,id=>
            {t.Gate.Invalidate();return (uint?)100;},ref t.Records,t.Journal,4194303,t.Nominal);
        Check(!captured&&(t.Records.Length==1||t.Records[1].Mask==0)&&t.Journal.HasPending,
            "reentrant epoch invalidation during a combined capture revokes the mirror instead of installing a stale positive");
    }
    static void MixedWholePublication()
    {
        var s=new State();s.Begin();s.Insert(Brick(1));s.Capture();s.Allocate();s.Publish();var mixed=new ThirdProvider();var touched=new List<int>();
        mixed.Set(1,4);
        bool Capture()=>GlassMaskSnapshot.Capture(s.Gate,s.Epoch,s.Model,s.Changes,s.NewOwners,id=>s.Owners.TryGetValue(id,out uint source)?source:(uint?)null,
            ref s.Records,s.Journal,4194303,s.Nominal,mixed,touched);
        Check(Capture()&&s.Records[1].Mask==4,"third-provider-only neighbour touch publishes a fresh whole mask without strict or nominal delta");
        s.Publish();mixed.Set(1,0);Check(Capture()&&s.Records[1].Mask==0,"third-provider1to0 replaces previous mirror bits");
        mixed.Set(1,4);s.Model.Upsert(Brick(2,true));s.Owners[2]=100;s.NewOwners.Add(2);Check(Capture()&&s.Records[1].Mask==5,"current strict and third masks merge");
        s.Model.Remove(2);mixed.Set(1,8);Check(Capture()&&s.Records[1].Mask==8,"fresh provider mask replaces old strict and mixed bits");
        s.Owners[1]=999;mixed.Set(1,0);Check(Capture()&&s.Records[1].Mask==0&&s.Records[1].SourceStart==999,"zero-mask touch still replaces source identity");
        mixed.Set(1,8);s.Owners.Remove(1);Check(Capture()&&s.Records[1].Mask==0&&s.Records[1].SourceStart==0,"unknown source suppresses stale third-provider proof");
        s.Owners[1]=100;mixed.Set(1,4);s.Nominal.Upsert(1,1,255,new[]{Cap()});s.Nominal.Upsert(1,2,255,new[]{Cap(true)});
        Check(Capture()&&s.Records[1].Mask==5,"current nominal and mixed whole proofs merge");
        mixed.Set(1,0);s.Nominal.Remove(2);Check(Capture()&&s.Records[1].Mask==0,"nominal and mixed removal cannot inherit either old mask");
        mixed.Set(1,4);mixed.OnRead=()=>s.Gate.Invalidate();Check(!Capture()&&s.Records[1].Mask==0&&s.Journal.HasPending,"reentrant third-mask lookup invalidates complete mirror and requires full upload");
        var t=new State();t.Begin();t.Insert(Brick(1));t.Capture();var provider=new ThirdProvider();provider.Set(1,4);provider.OnDrain=()=>t.Gate.Invalidate();
        Check(!GlassMaskSnapshot.Capture(t.Gate,t.Epoch,t.Model,t.Changes,t.NewOwners,id=>100,ref t.Records,t.Journal,4194303,t.Nominal,provider,new List<int>())&&t.Records[1].Mask==0&&t.Journal.HasPending,
            "reentrant provider touch drain revokes complete snapshot");
        var u=new State();u.Begin();u.Insert(Brick(1));u.Capture();var throwing=new ThirdProvider();throwing.Set(1,4);throwing.OnRead=()=>throw new InvalidOperationException("provider failure");
        Check(!GlassMaskSnapshot.Capture(u.Gate,u.Epoch,u.Model,u.Changes,u.NewOwners,id=>100,ref u.Records,u.Journal,4194303,u.Nominal,throwing,new List<int>())&&u.Records[1].Mask==0&&u.Journal.HasPending,
            "third-provider exception never leaves a partial positive snapshot");
        throwing.OnRead=null;throwing.Set(1,256);Check(!GlassMaskSnapshot.Capture(u.Gate,u.Epoch,u.Model,u.Changes,u.NewOwners,id=>100,ref u.Records,u.Journal,4194303,u.Nominal,throwing,new List<int>()),"whole-only provider cannot publish high bits/custom slots");
    }
    static void PartialEncodingPublication()
    {
        var s=new State();s.Begin();s.Insert(Brick(1),123);uint code=256u<<8;
        bool Capture(Func<int,uint,uint,uint> transform)=>GlassMaskSnapshot.Capture(s.Gate,s.Epoch,s.Model,s.Changes,s.NewOwners,
            id=>s.Owners.TryGetValue(id,out uint source)?source:(uint?)null,ref s.Records,s.Journal,4194303,s.Nominal,null,null,transform);
        Check(Capture((id,whole,source)=>{Check(id==1&&whole==0&&source==123,"transform sees fresh whole mask and native source");return code;})&&s.Records[1].Mask==code,
            "zero-low mask with maximum slot256 can publish only after optional transformer authorizes it");
        s.NewOwners.Add(1);Check(Capture((id,whole,source)=>whole)&&s.Records[1].Mask==0,"fresh high-to-low replacement cannot inherit a selected slot");
        s.NewOwners.Add(1);Check(Capture((id,whole,source)=>whole|256)&&s.Records[1].Mask==256,"fresh low-to-high replacement publishes only the new slot");
        s.Owners[1]=999;s.NewOwners.Add(1);Check(Capture((id,whole,source)=>{Check(source==999,"transform source identity is fresh");return whole|512;})&&s.Records[1].SourceStart==999&&s.Records[1].Mask==512,
            "source replacement updates both high slot and native source identity");
        s.Owners.Remove(1);s.NewOwners.Add(1);int calls=0;Check(Capture((id,whole,source)=>{calls++;return whole|256;})&&calls==0&&s.Records[1].Mask==0&&s.Records[1].SourceStart==0,"unknown source never calls partial transform and publishes all-zero record");
        foreach(uint invalid in new[]{257u<<8,uint.MaxValue,1u})
        {
            var bad=new State();bad.Begin();bad.Insert(Brick(1));
            Check(!GlassMaskSnapshot.Capture(bad.Gate,bad.Epoch,bad.Model,bad.Changes,bad.NewOwners,id=>100,ref bad.Records,bad.Journal,4194303,null,null,null,(id,whole,source)=>invalid)&&
                (bad.Records.Length<2||bad.Records[1].Mask==0)&&bad.Journal.HasPending,"invalid slot or changed low bits revoke the entire snapshot");
        }
        foreach(bool throws in new[]{false,true})
        {
            var bad=new State();bad.Begin();bad.Insert(Brick(1));bad.Insert(Brick(2,z:2));int transforms=0;
            bool okay=GlassMaskSnapshot.Capture(bad.Gate,bad.Epoch,bad.Model,bad.Changes,bad.NewOwners,id=>100,ref bad.Records,bad.Journal,4194303,null,null,null,(id,whole,source)=>
            {
                transforms++;if(id==2){if(throws)throw new InvalidOperationException("partial callback");bad.Gate.Invalidate();}return whole|256;
            });
            Check(!okay&&transforms==2&&Array.TrueForAll(bad.Records,r=>r.Mask==0&&r.SourceStart==0)&&bad.Journal.HasPending,
                "exception or epoch invalidation after an earlier partial assignment revokes every mask/source and requires full upload");
        }
        var merge=new State();merge.Begin();merge.Insert(Brick(1));merge.Insert(Brick(2,true));
        Check(GlassMaskSnapshot.Capture(merge.Gate,merge.Epoch,merge.Model,merge.Changes,merge.NewOwners,id=>100,ref merge.Records,merge.Journal,4194303,null,null,null,(id,whole,source)=>whole|256)&&
            merge.Records[1].Mask==257&&merge.Records[2].Mask==257,"partial transform preserves freshly merged positive whole low bits exactly");
    }
    static void Main(){PartialAndInvalidation();ConservativeReplacement();ReuseAndToggle();GrowthAndJournal();PartitionEquivalence();DeadlineAndRetries();DiscoveryFairness();CombinedNominalPublication();NominalBudgetAndReentrancy();MixedWholePublication();PartialEncodingPublication();Console.WriteLine($"PASS {checks} progressive publication checks");}
}
