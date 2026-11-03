using System;
using System.Collections.Generic;

namespace GraphicalTweaks
{
    // Single-threaded publication journal. Changes stay dirty until every
    // retained buffer generation has received them. This is necessary because
    // an already-recorded draw can still reference an older mask allocation.
    internal sealed class GlassMaskUploadJournal
    {
        private bool full=true;
        private int firstDirty=int.MaxValue,lastDirty=-1;
        private long revision;
        internal bool HasPending=>full||lastDirty>=firstDirty;
        internal void MarkDirty(int first,int last)
        {
            if(first<0||last<first)throw new ArgumentOutOfRangeException(nameof(first));
            firstDirty=Math.Min(firstDirty,first);lastDirty=Math.Max(lastDirty,last);revision++;
        }
        internal void RequireFullUpload(){full=true;revision++;}
        internal bool Publish<T>(IReadOnlyList<T> resources,Func<T,int> capacity,Action<T,int,int> upload)
        {
            if(resources==null)throw new ArgumentNullException(nameof(resources));
            if(capacity==null)throw new ArgumentNullException(nameof(capacity));
            if(upload==null)throw new ArgumentNullException(nameof(upload));
            if(!HasPending)return true;
            if(resources.Count==0)return false;
            long publishingRevision=revision;
            bool publishFull=full;
            int first=firstDirty,last=lastDirty;
            foreach(var resource in resources)
            {
                int count=capacity(resource);
                if(count<=0)throw new ArgumentOutOfRangeException(nameof(capacity));
                int start=publishFull?0:Math.Min(first,count);
                int end=publishFull?count-1:Math.Min(last,count-1);
                if(end>=start)upload(resource,start,end-start+1);
            }
            // An exception leaves every pending range/full flag intact. A
            // callback that adds work also cannot have that new work committed
            // by this older publication, even if the uploaded data included it.
            if(revision!=publishingRevision)return false;
            full=false;firstDirty=int.MaxValue;lastDirty=-1;return true;
        }
    }
}
