using System;
using System.Collections.Generic;
using GraphicalTweaks;

static class Program
{
    static int checks,calls;
    static uint[] cpu=new uint[8];
    sealed class Buffer
    {
        internal readonly uint[] Data;
        internal bool Throw;
        internal Buffer(int size){Data=new uint[size];}
    }
    static void Check(bool value,string text){checks++;if(!value)throw new Exception(text);}
    static bool Publish(GlassMaskUploadJournal j,List<Buffer> buffers)=>j.Publish(buffers,b=>b.Data.Length,(b,s,n)=>
    {
        calls++;
        if(b.Throw)throw new InvalidOperationException("injected GPU upload failure");
        Array.Copy(cpu,s,b.Data,s,n);
    });
    static void Main()
    {
        var j=new GlassMaskUploadJournal();var a=new Buffer(2);var buffers=new List<Buffer>{a};
        cpu[1]=7;
        Check(Publish(j,buffers)&&a.Data[1]==7&&!j.HasPending,"initial complete upload reaches GPU");
        int before=calls;
        Check(Publish(j,buffers)&&calls==before,"clean state does not upload");
        cpu[1]=0;j.MarkDirty(1,1);a.Throw=true;
        try{Publish(j,buffers);Check(false,"injected failure must propagate");}catch(InvalidOperationException){}
        Check(j.HasPending&&a.Data[1]==7,"failed removal retains pending invalidation");
        var b=new Buffer(8);buffers.Add(b);j.RequireFullUpload();a.Throw=false;
        Check(Publish(j,buffers)&&a.Data[1]==0&&b.Data[1]==0,"retry/re-enable clears both retired and active masks");
        cpu[1]=3;cpu[6]=9;j.MarkDirty(1,6);
        Check(Publish(j,buffers)&&a.Data[1]==3&&b.Data[6]==9,"range intersects each generation's capacity");
        // Failure on the second generation must also retry the first. The CPU
        // state can change again before recovery, including same-ID reuse.
        cpu[1]=0;j.MarkDirty(1,1);b.Throw=true;
        try{Publish(j,buffers);}catch(InvalidOperationException){}
        Check(j.HasPending&&a.Data[1]==0&&b.Data[1]==3,"partial success is not committed");
        cpu[1]=5;b.Throw=false;
        Check(Publish(j,buffers)&&a.Data[1]==5&&b.Data[1]==5,"retry uses current CPU state for every generation");
        Array.Clear(cpu);j.RequireFullUpload();
        Check(Publish(j,buffers)&&a.Data[1]==0&&b.Data[6]==0,"full reset independent of drained model changes");
        j.MarkDirty(6,7);buffers.Clear();
        Check(!Publish(j,buffers)&&j.HasPending,"no GPU resources cannot acknowledge dirty state");
        buffers.Add(b);
        Check(Publish(j,buffers)&&!j.HasPending,"first later resource receives pending changes");
        j.MarkDirty(0,0);bool changed=false;
        Check(!j.Publish(buffers,x=>x.Data.Length,(x,s,n)=>{if(!changed){changed=true;j.MarkDirty(5,5);}})&&j.HasPending,
            "publication cannot acknowledge a change added reentrantly");
        cpu[5]=99;
        Check(Publish(j,buffers)&&b.Data[5]==99,"reentrant change survives next publication");
        try{j.MarkDirty(-1,0);Check(false,"negative range rejected");}catch(ArgumentOutOfRangeException){}
        try{j.MarkDirty(2,1);Check(false,"inverted range rejected");}catch(ArgumentOutOfRangeException){}
        j.RequireFullUpload();
        try{j.Publish(buffers,x=>0,(x,s,n)=>{});Check(false,"zero resource capacity rejected");}catch(ArgumentOutOfRangeException){}
        Check(j.HasPending,"invalid resource cannot clear publication state");
        Console.WriteLine($"GlassMaskUpload: {checks} checks passed.");
    }
}
