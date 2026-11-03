using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace GraphicalTweaks
{
    // Keep pending retries reachable even before the first cube has a render ID.
    internal static class GlassFaceCoverageRemapGate
    {
        internal static bool ShouldPoll(int trackedCount,int pendingCount) => trackedCount>0 || pendingCount>0;
        internal static bool IsReady(int trackedCount,int maskCount) => trackedCount>0 && maskCount>0;
    }

    internal sealed class GlassFaceCoveragePending<TEntity> where TEntity : class
    {
        private const int MaximumAttempts=60;
        private readonly Dictionary<TEntity,Dictionary<int,Entry>> entities=new Dictionary<TEntity,Dictionary<int,Entry>>(ReferenceComparer.Instance);
        private readonly LinkedList<Entry> queue=new LinkedList<Entry>();
        internal int Count => queue.Count;
        internal void Add(TEntity entity,int instance)
        {
            if(entity==null || instance<=0)return;
            if(entities.TryGetValue(entity,out var items) && items.ContainsKey(instance))return;
            Add(new Entry(entity,instance));
        }
        private void Add(Entry entry)
        {
            if(!entities.TryGetValue(entry.Entity,out var items))entities.Add(entry.Entity,items=new Dictionary<int,Entry>());
            if(items.ContainsKey(entry.Instance))return;
            items.Add(entry.Instance,entry);entry.Node=queue.AddLast(entry);
        }
        internal void Remove(TEntity entity,int instance)
        {
            if(entity==null || !entities.TryGetValue(entity,out var items) || !items.TryGetValue(instance,out var entry))return;
            queue.Remove(entry.Node);items.Remove(instance);if(items.Count==0)entities.Remove(entity);
        }
        internal void RemoveEntity(TEntity entity)
        {
            if(entity==null || !entities.TryGetValue(entity,out var items))return;
            foreach(var entry in items.Values)queue.Remove(entry.Node);
            entities.Remove(entity);
        }
        internal void Clear() { entities.Clear();queue.Clear(); }
        internal void Process(int budget,Func<TEntity,int,bool> tryResolve,Func<bool> shouldContinue=null)
        {
            int count=Math.Min(Math.Max(0,budget),queue.Count);
            // Retry each queued item at most once per boundary, including reentrant lifecycle changes.
            var work=new List<Entry>(count);
            var node=queue.First;
            for(int i=0;i<count;i++,node=node.Next)work.Add(node.Value);
            foreach(var entry in work)
            {
                // A skipped item remains queued with its retry lifetime intact.
                if(shouldContinue!=null&&!shouldContinue())break;
                if(entry.Node.List==null)continue;
                Remove(entry.Entity,entry.Instance);
                if(!tryResolve(entry.Entity,entry.Instance) && ++entry.Attempts<MaximumAttempts)Add(entry);
            }
        }
        private sealed class Entry
        {
            internal readonly TEntity Entity;internal readonly int Instance;
            internal int Attempts;internal LinkedListNode<Entry> Node;
            internal Entry(TEntity entity,int instance){Entity=entity;Instance=instance;}
        }
        private sealed class ReferenceComparer:IEqualityComparer<TEntity>
        {
            internal static readonly ReferenceComparer Instance=new ReferenceComparer();
            public bool Equals(TEntity x,TEntity y)=>ReferenceEquals(x,y);
            public int GetHashCode(TEntity value)=>RuntimeHelpers.GetHashCode(value);
        }
    }
}
