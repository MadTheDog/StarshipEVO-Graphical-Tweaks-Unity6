using System;
using System.Collections.Generic;

namespace SEVO.RenderingPerformanceProbe
{
    internal readonly struct GlassPartialReferenceDescriptor
    {
        internal readonly uint Source,Mask;
        internal GlassPartialReferenceDescriptor(uint source,uint mask){Source=source;Mask=mask;}
    }
    internal static class GlassPartialReferenceChecks
    {
        internal static int Inspect(int[] words,int descriptorCount,Func<int,bool> isOwner,
            IReadOnlyDictionary<uint,GlassPartialReferenceDescriptor> authorized,out int references)
        {
            if(words==null||words.Length%2!=0||words.Length>8388608||descriptorCount<0||descriptorCount>256||isOwner==null||authorized==null)
                throw new ArgumentException("Invalid bounded partial-reference snapshot");
            references=0;int bad=0;
            for(int id=0;id<words.Length/2;id++)
            {
                uint encoded=unchecked((uint)words[id*2]),slot=encoded>>8,source=unchecked((uint)words[id*2+1]);
                if(slot==0)continue;references++;
                if(!isOwner(id)||slot>256||slot>descriptorCount||!authorized.TryGetValue(slot,out var d)||source==0||d.Source!=source||d.Mask!=(encoded&255))bad++;
            }
            return bad;
        }
    }
}
