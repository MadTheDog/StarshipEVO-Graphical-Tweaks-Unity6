using System;
using System.Collections.Generic;
namespace GraphicalTweaks
{
    // Only watched live candidate IDs are stored; no native-pool-sized snapshot.
    internal sealed class GlassUploadProof<T>
    {
        private const int MaximumWatches=65536;
        private readonly Dictionary<int,Entry> entries=new Dictionary<int,Entry>();
        private object source;
        private long serial;
        internal bool Poisoned {get;private set;}
        internal void Poison(){Poisoned=true;foreach(var entry in entries.Values)entry.Valid=false;}
        internal void ResetTrust(){Clear();Poisoned=false;}
        internal bool IsWatched(int id)=>entries.ContainsKey(id);
        internal IEnumerable<int> PendingIds {get{foreach(var pair in entries)if(!pair.Value.Valid)yield return pair.Key;}}
        internal bool ChangeSource(object current)
        {
            if(ReferenceEquals(source,current))return false;
            source=current;
            foreach(var entry in entries.Values){entry.Ticket=++serial;entry.Valid=false;}
            return true;
        }
        internal bool Watch(int id)
        {
            if(entries.ContainsKey(id))return true;
            if(id<=0||entries.Count>=MaximumWatches)return false;
            entries.Add(id,new Entry{Ticket=++serial});return true;
        }
        internal long BeginWrite(int id)
        {
            if(!entries.TryGetValue(id,out var entry))return -1;
            entry.Valid=false;return entry.Ticket=++serial;
        }
        internal void CompleteWrite(int id,long ticket,T value)
        {
            if(Poisoned||source==null||!entries.TryGetValue(id,out var entry)||ticket!=entry.Ticket)return;
            entry.Value=value;entry.Valid=true;
        }
        // Call only after a successful native PushBufferToGpu and identity check.
        // The audited native method has flushed every deferred write at this point.
        internal void ConfirmSnapshot(int id,T value)
        {if(entries.TryGetValue(id,out var entry))CompleteWrite(id,entry.Ticket,value);}
        internal bool Matches(int id,T value)=>!Poisoned&&source!=null&&entries.TryGetValue(id,out var entry)&&entry.Valid&&EqualityComparer<T>.Default.Equals(entry.Value,value);
        internal void Forget(int id){entries.Remove(id);}
        internal void Clear(){entries.Clear();source=null;serial++;}
        private sealed class Entry {internal long Ticket;internal bool Valid;internal T Value;}
    }
    // No model calls occur in Invalidate. Native callbacks may block old proofs,
    // but geometry removal/recomputation is exclusively a scheduled operation.
    internal sealed class GlassRebuildGate
    {
        private long epoch;
        private bool begun;
        internal bool Blocked {get;private set;}=true;
        // Call only after discarding all geometry and owners from the old epoch.
        internal long Begin(){begun=true;return epoch;}
        internal bool CanPublish(long started)=>begun&&started==epoch;
        internal void Invalidate(){epoch++;begun=false;Blocked=true;}
        internal bool TryComplete(long started,int unresolved)
        {if(!CanPublish(started)||unresolved!=0)return false;Blocked=false;return true;}
    }
}
