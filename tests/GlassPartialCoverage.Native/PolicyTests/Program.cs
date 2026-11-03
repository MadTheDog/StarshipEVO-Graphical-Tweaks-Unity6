using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool okay,string why){checks++;if(!okay)throw new Exception(why);}
    static void Main()
    {
        var pending=new GlassPartialPendingWork();Check(pending.Retry(1)&&pending.Count==1,"legitimate current owner waiting for resources must be queued");
        Check(pending.Retry(1)&&pending.Count==1,"same waiting owner has one bounded retry node");var ids=new List<int>();pending.Take(8,ids);
        Check(ids.SequenceEqual(new[]{1})&&pending.Count==0,"retry is consumed once per scheduled capture");
        Check(pending.Retry(1),"legitimate initial resource wait can defer again");pending.Cancel(1);Check(pending.Count==0,"permanent unsupported result stops idle retries");
        for(int i=1;i<=4096;i++)Check(pending.Retry(i),"bounded pending owner admits");
        Check(!pending.Retry(4097)&&pending.Count==4096,"pending hard bound is4096");ids.Clear();pending.Take(100,ids);Check(ids.Count==8&&pending.Count==4088,"one capture takes at most8 pending owners");
        pending.Clear();for(int cycle=0;cycle<30;cycle++){for(int j=0;j<8;j++)pending.Retry(cycle*8+j+1);ids.Clear();pending.Take(8,ids);Check(pending.Count==0&&ids.Count==8,"fresh owner lifetimes retain no retired retry nodes");}
        var budget=new GlassPartialCaptureBudget(1000000);Check(!budget.TryEnter(0),"unbegun capture cannot perform queries");budget.Begin(100);
        for(int i=0;i<8;i++)Check(budget.TryEnter(100+i),"first8 eligible owners fit owner budget");Check(!budget.TryEnter(109)&&budget.Entered==8,"ninth eligible owner defers");
        budget.Begin(100);Check(budget.TryEnter(2099)&&!budget.TryEnter(2100)&&!budget.TryEnter(99),"soft2ms boundary and monotonic clock gate query starts");
        budget.Begin(5000);Check(budget.Entered==0&&budget.TryEnter(5000),"next capture resets owner and time budget");
        var cache=new GlassPartialGeometryCache(1000);var entry=Seed(cache);var ledger=new GlassPartialSelectionLedger();
        Check(!ledger.TryRead(1,1,cache,120,0,out _),"nothing selected by default");
        Check(ledger.Record(1,1,cache,120,0,256,entry)&&ledger.TryRead(1,1,cache,120,0,out uint code)&&code==256,"only exact owned cache entry selection can be read");
        var other=new GlassPartialGeometryCache(1000);var otherEntry=Seed(other);
        Check(!ledger.TryRead(1,2,cache,120,0,out _)&&!ledger.TryRead(1,1,other,120,0,out _)&&!ledger.TryRead(1,1,cache,121,0,out _)&&!ledger.TryRead(1,1,cache,120,1,out _),
            "epoch/cache/native source/whole-mask changes cannot revive selected high bits");
        Check(ledger.Count==1,"read-only stale diagnostic lookup does not mutate selection state");
        Check(!ledger.Record(1,1,cache,120,0,256,otherEntry)&&!ledger.Record(1,1,cache,120,0,512,entry)&&!ledger.Record(1,1,cache,120,1,257,entry),"foreign entry, bad slot and changed low mask reject");
        ledger.Remove(1);pending.Retry(1);Check(!ledger.TryRead(1,1,cache,120,0,out _)&&pending.Count==1,"actual geometry touch revokes old high bits while bounded work waits");
        ids.Clear();pending.Take(8,ids);Check(ledger.Record(1,1,cache,120,0,256,entry),"current successful retry can authorize the current entry again");
        ledger.Clear();pending.Clear();Check(ledger.Count==0&&pending.Count==0&&!ledger.TryRead(1,1,cache,120,0,out _),"mode/catalog/seamless invalidation clears all logical authorization and retry work");
        for(int i=1;i<=4096;i++)Check(ledger.Record(i,1,cache,120,0,256,entry),"bounded selection owner admits");Check(!ledger.Record(4097,1,cache,120,0,256,entry)&&ledger.Count==4096,"selected proof hard bound is4096");
        Console.WriteLine($"PASS {checks} partial publication policy checks");
    }
    static GlassPartialCacheEntry Seed(GlassPartialGeometryCache cache)
    {
        var raw=new byte[12*372];void F(int p,float f)=>BitConverter.GetBytes(f).CopyTo(raw,p);
        for(int v=0;v<3;v++)
        {
            int p=v*124;F(p,v==1?2:0);F(p+4,v==2?2:0);F(p+20,1);F(p+24,1);F(p+36,1);F(p+44,1);
            F(p+52,v==1?2:0);F(p+56,v==2?2:0);BitConverter.GetBytes((uint)271).CopyTo(raw,p+60);F(p+64,.5f);F(p+76,2);F(p+100,2);F(p+112,3);
        }
        var labels=new uint[12];labels[0]=1;
        var tri=new GlassBarycentricTriangle(new(1,0,0,1),new(1,1,0,2),new(1,0,1,2));
        Check(cache.TryGetOrAdd(raw,120,1,12,0,labels,new[]{new GlassNativeTriangleEdit(0,new[]{tri})},true,out var entry,out var reason),"real raw writer/cache synthetic partial admitted: "+reason);return entry;
    }
}
