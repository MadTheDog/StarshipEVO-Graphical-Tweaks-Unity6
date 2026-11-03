using System;
using System.Collections.Generic;

namespace GraphicalTweaks
{
    internal static class GlassCoverageWorkBudget
    {
        internal static bool CanDiscover(int inspected,long elapsedTicks,long frequency)=>inspected<8192&&elapsedTicks<frequency/2000;
        internal static bool CanProcess(long elapsedTicks,long frequency)=>elapsedTicks<frequency/500;
    }
    internal readonly struct GlassMaskRecord
    {
        internal readonly uint Mask,SourceStart;
        internal GlassMaskRecord(uint mask,uint source){Mask=mask;SourceStart=source;}
    }

    // Pure scheduled capture; native callbacks only invalidate and publish zeros.
    internal interface IGlassWholeMaskProvider
    {
        uint GetMask(int buildId);
        void DrainTouched(List<int> output);
    }
    internal static class GlassMaskSnapshot
    {
        internal static bool Capture(GlassRebuildGate gate,long epoch,GlassPolygonCoverageModel model,
            List<GlassPolygonCoverageChange> changes,List<int> newOwners,Func<int,uint?> sourceStart,
            ref GlassMaskRecord[] records,GlassMaskUploadJournal journal,int maximumBuildId,GlassNominalCapModel nominal=null,
            IGlassWholeMaskProvider mixed=null,List<int> mixedTouched=null,Func<int,uint,uint,uint> transform=null)
        {
            if(!gate.CanPublish(epoch))return false;
            try
            {
                changes.Clear();model.DrainChanges(changes);nominal?.DrainChanges(changes);
                if(mixed!=null)
                {
                    if(mixedTouched==null)mixedTouched=new List<int>();
                    mixedTouched.Clear();mixed.DrainTouched(mixedTouched);
                }
                if(!gate.CanPublish(epoch))return Revoke(ref records,journal);
                foreach(var change in changes)
                    if(!TryAssignCurrent(change.BuildId,gate,epoch,model,nominal,mixed,sourceStart,ref records,journal,maximumBuildId,transform))return Revoke(ref records,journal);
                // A new owner or a changed nominal contact may have no mask-only
                // delta at all. Publish its current source and all current masks.
                foreach(int id in newOwners)
                    if(!TryAssignCurrent(id,gate,epoch,model,nominal,mixed,sourceStart,ref records,journal,maximumBuildId,transform))return Revoke(ref records,journal);
                if(mixed!=null)foreach(int id in mixedTouched)
                    if(!TryAssignCurrent(id,gate,epoch,model,nominal,mixed,sourceStart,ref records,journal,maximumBuildId,transform))return Revoke(ref records,journal);
                changes.Clear();newOwners.Clear();mixedTouched?.Clear();return true;
            }
            // A new provider can fail after earlier records were assigned. Never
            // expose a partial positive snapshot; the native caller rebuilds.
            catch(Exception) when(mixed!=null||transform!=null){return Revoke(ref records,journal);}
        }
        private static bool TryAssignCurrent(int id,GlassRebuildGate gate,long epoch,GlassPolygonCoverageModel model,
            GlassNominalCapModel nominal,IGlassWholeMaskProvider mixed,Func<int,uint?> sourceStart,
            ref GlassMaskRecord[] records,GlassMaskUploadJournal journal,int maximumBuildId,Func<int,uint,uint,uint> transform)
        {
            uint? source=sourceStart(id);if(!gate.CanPublish(epoch))return false;
            uint extra=mixed?.GetMask(id)??0;
            if(!gate.CanPublish(epoch)||(extra&~255u)!=0)return false;
            uint whole=model.GetMask(id)|(nominal?.GetMask(id)??0)|extra,encoded=whole;
            if(source.HasValue&&transform!=null)
            {
                encoded=transform(id,whole,source.Value);
                if(!gate.CanPublish(epoch)||(encoded&255u)!=whole||(encoded>>8)>256)return false;
            }
            Assign(id,encoded,source,ref records,journal,maximumBuildId);return true;
        }
        private static bool Revoke(ref GlassMaskRecord[] records,GlassMaskUploadJournal journal)
        {Array.Clear(records,0,records.Length);journal.RequireFullUpload();return false;}
        private static void Assign(int id,uint mask,uint? source,ref GlassMaskRecord[] records,
            GlassMaskUploadJournal journal,int maximumBuildId)
        {
            if(id<0||id>maximumBuildId)throw new InvalidOperationException("General glass mask build ID exceeds limit.");
            if(id>=records.Length)
            {
                int size=records.Length;
                while(size<=id)size=Math.Min(checked(size*2),checked(maximumBuildId+1));
                Array.Resize(ref records,size);journal.RequireFullUpload();
            }
            // Unknown/freed owners cannot authorize a proof. Complete replacement
            // also applies 1 -> 0 changes caused by conservative work limits.
            var value=source.HasValue?new GlassMaskRecord(mask,source.Value):default;
            if(records[id].Mask==value.Mask&&records[id].SourceStart==value.SourceStart)return;
            records[id]=value;journal.MarkDirty(id,id);
        }
    }
}
