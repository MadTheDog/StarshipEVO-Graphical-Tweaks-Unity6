using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Common;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace GraphicalTweaks
{
    // Optional render-only service. Installation does not enable clipping.
    internal static class GlassFaceCoverage
    {
        private const int MaximumBuildId = 4194303;
        private static readonly GlassFaceCoverageModel model = new GlassFaceCoverageModel();
        private static readonly GlassFaceCoveragePending<EntityBricks> pending = new GlassFaceCoveragePending<EntityBricks>();
        private static readonly Dictionary<EntityBricks, EntityState> entities = new Dictionary<EntityBricks, EntityState>(ReferenceComparer<EntityBricks>.Instance);
        private static readonly Dictionary<int, Tracked> owners = new Dictionary<int, Tracked>();
        private static readonly List<GlassFaceCoverageChange> changes = new List<GlassFaceCoverageChange>();
        private static readonly List<ComputeBuffer> retiredBuffers = new List<ComputeBuffer>();
        private static uint[] upload = new uint[1];
        private static ComputeBuffer buffer;
        private static int mainThread;
        private static bool installed;
        private static volatile bool threadViolation;
        private static long nextEntity;
        internal static bool Enabled { get; private set; }
        internal static int TrackedCubeCount => owners.Count;
        internal static int PendingCubeCount => pending.Count;
        internal static int BufferCount => buffer == null ? 0 : buffer.count;
        internal static uint GetMask(int buildId) => model.GetMask(buildId);

        internal static void Install(Harmony harmony)
        {
            if (installed) return;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.AddBrickAt),nameof(OnBrickChanged),false);
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.Update),nameof(OnBrickChanged),false);
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.UpdateIds),nameof(OnIdsChanged),false);
            Patch(harmony,typeof(EntityBricks),"UpdateBrickHpStatus",nameof(OnHealthChanged),false);
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.Remove),nameof(OnBrickRemoving),true);
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.OnWillUnspawnEntity),nameof(OnEntityRemoving),true);
            Patch(harmony,typeof(EntityBricks),nameof(EntityBricks.OnDestroy),nameof(OnEntityRemoving),true);
            Patch(harmony,typeof(BrickRenderDataResources),nameof(BrickRenderDataResources.FreeIndex),nameof(OnIndexFree),true);
            Patch(harmony,typeof(BrickRenderDataResources),nameof(BrickRenderDataResources.OnGameReload),nameof(OnReset),true);
            Patch(harmony,typeof(BrickRenderDataResources),nameof(BrickRenderDataResources.UnloadResources),nameof(OnReset),true);
            installed=true;
        }
        private static void Patch(Harmony h,Type type,string original,string callback,bool prefix)
        {
            var method=AccessTools.Method(type,original);
            if(method==null)throw new MissingMethodException(type.FullName,original);
            var hook=new HarmonyMethod(typeof(GlassFaceCoverage),callback);
            h.Patch(method,prefix?hook:null,prefix?null:hook);
        }
        internal static void SetEnabled(bool value)
        {
            if(!installed && value)throw new InvalidOperationException("Install glass coverage before enabling it.");
            if(Enabled==value)return;
            Reset(); Enabled=value; threadViolation=false;
            if(!value)return;
            // One initial inventory. Subsequent changes arrive through build lifecycle hooks.
            Guard(()=>
            {
#if SEVO_UNITY6
                var candidates = UnityEngine.Object.FindObjectsByType<VoxelEntity>();
#else
                var candidates = UnityEngine.Object.FindObjectsOfType<VoxelEntity>();
#endif
                foreach(var entity in candidates)
                {
                    if(entity==null || entity.WillBeUnspawned || entity.Bricks==null)continue;
                    var bricks=entity.Bricks;
                    if(!bricks.brickDatas.IsCreated)continue;
                    for(int i=1;i<bricks.brickDatas.Length;i++) Track(bricks,bricks.GetBrickData(i));
                }
            });
        }
        internal static void Bind(MaterialPropertyBlock props)
        {
            if(props==null)return;
            TryGetBuffer(out var masks,out int count);
            props.SetBuffer("_GTGlassFaceMasks",masks);
            props.SetInt("_GTGlassFaceMaskCount",count);
        }
        // The remap compute path consumes exactly the same owned buffer and upload boundary.
        internal static bool TryGetBuffer(out ComputeBuffer masks,out int count)
        {
            if(threadViolation)
            {
                Enabled=false;Reset();threadViolation=false;
                Debug.LogWarning("Graphical Tweaks: glass coverage disabled after off-main-thread lifecycle callback.");
            }
            if(Enabled)Guard(()=>pending.Process(256,(bricks,instance)=>TrackNow(bricks,bricks.GetBrickData(instance))));
            Flush();
            masks=buffer;
            count=Enabled?upload.Length:0;
            return Enabled;
        }
        private static bool CanUpdate()
        {
            if(!Enabled)return false;
            if(Thread.CurrentThread.ManagedThreadId==mainThread)return true;
            threadViolation=true;return false;
        }
        private static void OnBrickChanged(EntityBricks __instance,BrickInstanceData brickData)
        { if(CanUpdate()) Guard(()=>Track(__instance,__instance.GetBrickData(brickData.instanceId))); }
        private static void OnIdsChanged(EntityBricks __instance,int brickInstanceId)
        { if(CanUpdate()) Guard(()=>Track(__instance,__instance.GetBrickData(brickInstanceId))); }
        private static void OnHealthChanged(EntityBricks __instance,BrickInstanceData brickData)
        {
            // The incoming struct has the old HP; native UpdateBrickHpStatus writes the new copy.
            if(CanUpdate())Guard(()=>Track(__instance,__instance.GetBrickData(brickData.instanceId)));
        }
        private static void OnBrickRemoving(EntityBricks __instance,BrickInstanceData brickData)
        {
            if(CanUpdate()) Guard(()=>
            {
                pending.Remove(__instance,brickData.instanceId);
                if(entities.TryGetValue(__instance,out var state) && state.BuildByInstance.TryGetValue(brickData.instanceId,out int id)) Remove(id);
            });
        }
        private static void OnEntityRemoving(EntityBricks __instance)
        {
            if(CanUpdate()) Guard(()=>
            {
                pending.RemoveEntity(__instance);
                if(!entities.TryGetValue(__instance,out var state))return;
                var ids=new List<int>(state.BuildByInstance.Values);foreach(int id in ids)Remove(id);
                entities.Remove(__instance);
            });
        }
        private static void OnIndexFree(int i) { if(CanUpdate())Guard(()=>Remove(i)); }
        private static void OnReset() { if(CanUpdate())Reset(); }
        private static void Guard(Action action)
        {
            try { action(); }
            catch(Exception e)
            {
                Enabled=false;Reset();
                Debug.LogWarning("Graphical Tweaks: glass coverage failed open: "+e);
            }
        }
        private static void Track(EntityBricks bricks,BrickInstanceData data)
        {
            if(TrackNow(bricks,data))pending.Remove(bricks,data.instanceId);
            else pending.Add(bricks,data.instanceId);
        }
        // true = resolved (including deliberately ineligible); false = native render ID not ready.
        private static bool TrackNow(EntityBricks bricks,BrickInstanceData data)
        {
            if(entities.TryGetValue(bricks,out var state) && state.BuildByInstance.TryGetValue(data.instanceId,out int previous)) Remove(previous);
            if(!data.IsValid || data.brickId!=166 || data.healthScore!=byte.MaxValue || bricks.entity==null || bricks.entity.WillBeUnspawned)return true;
            if(GlobalRenderer.renderer==null)return false;
            var ids=bricks.GetBrickIds(data.instanceId);
            if(ids.renderId1==0)return false;
            var render=GlobalRenderer.renderer.GetElement(ids.renderId1);
            if(!render.IsValid)return false;
            if(render.pass!=RenderablePass.transparent || render.transformId!=bricks.entity.transformId)return true;
            int buildId=render.dataId;
            if(buildId<=0)return false;
            if(buildId>MaximumBuildId)return true;
            // Only the observed cuboid with one plain transparent surface is eligible.
            var materials=data.Brick.VoxelMaterials;
            if(materials==null || materials.Length!=1 || materials[0].AlphaMode!=AlphaMode.transparent ||
               materials[0].BorderId!=-1 || materials[0].normalID!=-1 || materials[0].dynDataVert!=DynData_Vert.none)return true;
            Quaternion rotation=data.Quaternion;
            if(!Axis(rotation*Vector3.right) || !Axis(rotation*Vector3.up) || !Axis(rotation*Vector3.forward))return true;
            // Exactly the same geometry bounds used by Brick.SetBrickData for native rendering.
            Bounds bounds=data.GetAabb(useGeoBounds:true).Bounds;
            Vector3 min=bounds.min,max=bounds.max;
            if(!Tick(min.x,out long x0) || !Tick(min.y,out long y0) || !Tick(min.z,out long z0) ||
               !Tick(max.x,out long x1) || !Tick(max.y,out long y1) || !Tick(max.z,out long z1))return true;
            if(state==null) entities.Add(bricks,state=new EntityState(++nextEntity));
            Remove(buildId); // Reused GPU IDs must never inherit a previous owner's mask.
            state.BuildByInstance[data.instanceId]=buildId;
            owners[buildId]=new Tracked(state,data.instanceId);
            model.Upsert(new GlassFaceCoverageBox(state.Key,buildId,x0,y0,z0,x1,y1,z1,data.healthScore));
            return true;
        }
        private static bool Axis(Vector3 value)
        {
            float x=Mathf.Abs(value.x),y=Mathf.Abs(value.y),z=Mathf.Abs(value.z);
            return Mathf.Abs(Mathf.Max(x,Mathf.Max(y,z))-1f)<0.00001f && Mathf.Abs(x+y+z-1f)<0.00001f;
        }
        private static bool Tick(float value,out long tick)
        {
            double scaled=(double)value*32;
            if(double.IsNaN(scaled) || double.IsInfinity(scaled) || Math.Abs(scaled)>(1L<<40)) {tick=0;return false;}
            tick=(long)Math.Round(scaled);
            return Math.Abs(scaled-tick)<0.00001;
        }
        private static void Remove(int id)
        {
            if(owners.TryGetValue(id,out var tracked))
            {tracked.State.BuildByInstance.Remove(tracked.Instance);owners.Remove(id);}
            model.Remove(id);
        }
        private static void Reset()
        {model.Clear();pending.Clear();entities.Clear();owners.Clear();}
        private static void Flush()
        {
            changes.Clear();model.DrainChanges(changes);
            int first=int.MaxValue,last=-1,required=upload.Length;
            foreach(var change in changes) {required=Math.Max(required,change.BuildId+1);first=Math.Min(first,change.BuildId);last=Math.Max(last,change.BuildId);}
            if(required>upload.Length)
            {
                int size=upload.Length;while(size<required)size*=2;
                Array.Resize(ref upload,size);
            }
            foreach(var change in changes)upload[change.BuildId]=change.Mask;
            if(buffer==null || buffer.count<upload.Length)
            {
                // Retain old native resources until Dispose; earlier command buffers may reference them.
                if(buffer!=null)retiredBuffers.Add(buffer);
                buffer=new ComputeBuffer(upload.Length,sizeof(uint));buffer.SetData(upload);
            }
            else if(last>=first)buffer.SetData(upload,first,first,last-first+1);
        }
        internal static void Dispose()
        {
            Enabled=false;Reset();changes.Clear();model.DrainChanges(changes);changes.Clear();
            if(buffer!=null){buffer.Release();buffer=null;}
            foreach(var retired in retiredBuffers)retired.Release();retiredBuffers.Clear();
            upload=new uint[1];
        }
        private sealed class EntityState
        {
            internal readonly long Key;
            internal readonly Dictionary<int,int> BuildByInstance=new Dictionary<int,int>();
            internal EntityState(long key){Key=key;}
        }
        private readonly struct Tracked
        {
            internal readonly EntityState State;internal readonly int Instance;
            internal Tracked(EntityState state,int instance){State=state;Instance=instance;}
        }
        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T:class
        {
            internal static readonly ReferenceComparer<T> Instance=new ReferenceComparer<T>();
            public bool Equals(T x,T y)=>ReferenceEquals(x,y);
            public int GetHashCode(T value)=>RuntimeHelpers.GetHashCode(value);
        }
    }
}
