using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using VoxelEngine;

namespace GraphicalTweaks
{
    // Native boundary for the reviewed, separate nominal whole-cap universe.
    // Called only during catalog construction and scheduled geometry work.
    internal sealed class GlassMixedCapCoverage:IGlassWholeMaskProvider
    {
        private readonly GlassMixedCapScene scene=new GlassMixedCapScene(4096);
        private readonly Dictionary<ushort,GlassMixedCapSource> sources=new Dictionary<ushort,GlassMixedCapSource>();
        private readonly Dictionary<int,VoxelMaterial> materialProof=new Dictionary<int,VoxelMaterial>();
        private readonly List<int> discardedTouches=new List<int>();
        private GlassShapeRuntimeCatalog boundCatalog;
        private VoxelMaterial[] boundUploadedMaterials;
        private long generation=-1;
        private GlassShapeRotation[] rotations;
        internal int ProfilesCount=>sources.Count;
        internal int OwnerCount=>scene.OwnerCount;
        internal bool Faulted {get;private set;}
        internal string FailureReason {get;private set;}
        public uint GetMask(int id)=>Faulted?0:scene.GetMask(id);
        public void DrainTouched(List<int> output)=>scene.DrainTouched(output);
        internal IReadOnlyList<ushort> AdmittedSourceIds(GlassShapeRuntimeCatalog catalog,long catalogGeneration)
        {
            if(Faulted||!ReferenceEquals(boundCatalog,catalog)||generation!=catalogGeneration)return Array.Empty<ushort>();
            var ids=new List<ushort>(sources.Keys);ids.Sort();return Array.AsReadOnly(ids.ToArray());
        }
        internal bool TryGetSource(GlassShapeRuntimeCatalog catalog,long catalogGeneration,ushort id,out GlassMixedCapSource source)
        {source=null;return !Faulted&&ReferenceEquals(boundCatalog,catalog)&&generation==catalogGeneration&&sources.TryGetValue(id,out source);}
        internal bool TryGetInstance(GlassShapeRuntimeCatalog catalog,long catalogGeneration,int id,out GlassMixedCapInstance instance)
        {
            instance=null;return !Faulted&&ReferenceEquals(boundCatalog,catalog)&&generation==catalogGeneration&&
                scene.TryGetInstance(id,out instance)&&sources.ContainsKey((ushort)instance.BrickId);
        }
        internal bool TryGetCapRemainder(GlassShapeRuntimeCatalog catalog,long catalogGeneration,int id,int bit,out GlassCapClipResult result)
        {result=null;return TryGetInstance(catalog,catalogGeneration,id,out _)&&scene.TryGetCapRemainder(id,bit,out result);}

        internal void Bind(GlassShapeRuntimeCatalog catalog,long catalogGeneration,VoxelMaterial[] current,VoxelMaterial[] uploaded,int materialCount)
        {
            Reset();boundCatalog=catalog;generation=catalogGeneration;boundUploadedMaterials=uploaded;
            var matrices=Shader.GetGlobalMatrixArray("VoxelRotations");
            if(catalog==null||matrices==null||matrices.Length<32||Marshal.SizeOf(typeof(BrickMeshResources.ShapeMeshData))!=124||Marshal.SizeOf(typeof(VoxelMaterial))!=132)
            {FailureReason="mixed native catalog/rotation/ABI proof unavailable";return;}
            rotations=new GlassShapeRotation[32];
            for(int i=0;i<32;i++)
            {
                var m=matrices[i];var r=new GlassShapeRotation(new[]{m.m00,m.m01,m.m02,m.m03,m.m10,m.m11,m.m12,m.m13,m.m20,m.m21,m.m22,m.m23,m.m30,m.m31,m.m32,m.m33});
                if(!GlassShapeContactModel.TryCanonicalRotation(r,out rotations[i])){rotations=null;FailureReason="mixed native rotation outside quarter-turn proof";return;}
            }
            var failures=new List<string>();
            foreach(ushort id in new ushort[]{118,313,314})
            {
                try
                {
                    if(!catalog.TryGetShape(id,out var descriptor)||descriptor.Records==null||descriptor.GroupTriangles==null)
                    {failures.Add(id+": descriptor unavailable");continue;}
                    var used=new Dictionary<int,GlassMixedCapMaterial>();var proof=new Dictionary<int,VoxelMaterial>();bool valid=true;
                    foreach(var group in descriptor.GroupTriangles)foreach(int t in group)for(int v=0;v<3;v++)
                    {
                        if(t<0||t>=descriptor.ActualTriangles||t*3+v>=descriptor.Records.Length){valid=false;break;}
                        uint rawId=descriptor.Records[t*3+v].materialID;
                        if(rawId>=materialCount||current==null||uploaded==null||rawId>=current.Length||rawId>=uploaded.Length)
                        {valid=false;break;}
                        int material=(int)rawId;
                        if(!TryMaterial(current[material],material,out var style)||!SameMaterial(current[material],uploaded[material]))
                        {valid=false;break;}
                        used[material]=style;proof[material]=current[material];
                    }
                    if(!valid){failures.Add(id+": actual used-cap style/upload mismatch");continue;}
                    var records=(BrickMeshResources.ShapeMeshData[])descriptor.Records.Clone();var bytes=new byte[checked(records.Length*124)];
                    var pin=GCHandle.Alloc(records,GCHandleType.Pinned);
                    try{Marshal.Copy(pin.AddrOfPinnedObject(),bytes,0,bytes.Length);}finally{pin.Free();}
                    if(!GlassMixedCapCatalog.TryCreate(id,bytes,descriptor.ActualTriangles,descriptor.PaddedTriangles,descriptor.GroupTriangles,used,out var admitted))
                    {failures.Add(id+": frozen source hash/group/material correspondence rejected");continue;}
                    sources.Add(id,admitted);foreach(var pair in proof)materialProof[pair.Key]=pair.Value;
                }
                catch(Exception e){failures.Add(id+": "+e.GetType().Name+": "+e.Message);}
            }
            FailureReason=failures.Count==0?null:string.Join("; ",failures.ToArray());
        }
        internal bool MaterialsCurrent(GlassShapeRuntimeCatalog catalog,long catalogGeneration,VoxelMaterial[] current,VoxelMaterial[] uploaded,int count)
        {
            if(!ReferenceEquals(boundCatalog,catalog)||generation!=catalogGeneration||!ReferenceEquals(boundUploadedMaterials,uploaded))return false;
            foreach(var pair in materialProof)
            {
                int id=pair.Key;
                if(current==null||uploaded==null||id<0||id>=count||id>=current.Length||id>=uploaded.Length||
                   !SameMaterial(current[id],pair.Value)||!SameMaterial(uploaded[id],pair.Value))return false;
            }
            return true;
        }
        internal void BeginScene()
        {scene.Clear();scene.DrainTouched(discardedTouches);discardedTouches.Clear();Faulted=false;}
        internal bool Track(GlassShapeRuntimeCatalog catalog,long catalogGeneration,long entity,int buildId,BrickInstanceData data)
        {
            if(!ReferenceEquals(boundCatalog,catalog)||generation!=catalogGeneration)return Fail("mixed source generation does not match current native catalog");
            var scale=data.Scale;
            if(Faulted)return false;
            if(!sources.TryGetValue(data.brickId,out var source)||scale.x!=0||scale.y!=0||scale.z!=0||rotations==null)
            {scene.Remove(buildId);return true;}
            var placement=new GlassPolygonPoint(data.gridPosition.x/32d,data.gridPosition.y/32d,data.gridPosition.z/32d);
            if(!GlassMixedCapCatalog.TryBuild(source,entity,buildId,data.healthScore,scale.x,scale.y,scale.z,data.GridSize,
                data.rotation<32?rotations[data.rotation]:default,placement,out var instance))
            {scene.Remove(buildId);return true;}
            return scene.Upsert(instance)||Fail("mixed scene owner/polygon work bound exceeded");
        }
        private bool Fail(string reason)
        {Faulted=true;FailureReason=reason;scene.Clear();return false;}
        internal void Reset()
        {
            scene.Clear();scene.DrainTouched(discardedTouches);discardedTouches.Clear();sources.Clear();materialProof.Clear();
            boundCatalog=null;boundUploadedMaterials=null;generation=-1;rotations=null;Faulted=false;FailureReason=null;
        }
        internal static bool TryMaterial(VoxelMaterial m,int id,out GlassMixedCapMaterial material)
        {
            material=default;
            if(m.Id!=id||(int)m.AlphaMode!=2||(int)m.dynDataVert!=0||m.TextureID!=183||m.BorderId!=-1||m.normalID!=-1||m.specularID!=154||
               (int)m.recomputeNormal!=0||(int)m.buildingOffset!=0||(int)m.dynDataPixel!=0||(int)m.scalingDeformLinkMode!=0||
               m.sizeUnit!=1||m.thresholdGeoLow!=-.001f||m.thresholdGeoHigh!=.001f||
               !Finite(m.U_threshold_low)||!Finite(m.U_threshold_up)||!Finite(m.U_threshold_width)||!Finite(m.V_threshold_low)||!Finite(m.V_threshold_up)||!Finite(m.V_threshold_width)||
               !Finite(m.UVScale.x)||!Finite(m.UVScale.y)||!Finite(m.translationPath.x)||!Finite(m.translationPath.y)||!Finite(m.translationPath.z))return false;
            material=new GlassMixedCapMaterial(id,new GlassShapeDeformationMaterial((int)m.scalingDeformLinkMode,(int)m.dynDataVert,(int)m.AlphaMode,
                m.sizeUnit,m.thresholdGeoLow,m.thresholdGeoHigh,m.BorderId,(int)m.buildingOffset),m.TextureID,m.specularID,m.normalID,(int)m.recomputeNormal,(int)m.dynDataPixel);return true;
        }
        // Complete native struct value comparison, without boxing/allocating a
        // VoxelMaterial for each per-frame/later-upload safety check.
        internal static bool SameMaterial(VoxelMaterial a,VoxelMaterial b)=>
            a.Id==b.Id&&a.AlphaMode==b.AlphaMode&&a.dynDataVert==b.dynDataVert&&a.TextureID==b.TextureID&&a.BorderId==b.BorderId&&a.Tiling==b.Tiling&&a.sizeUnit==b.sizeUnit&&
            a.U_threshold_low==b.U_threshold_low&&a.U_threshold_up==b.U_threshold_up&&a.U_threshold_width==b.U_threshold_width&&
            a.V_threshold_low==b.V_threshold_low&&a.V_threshold_up==b.V_threshold_up&&a.V_threshold_width==b.V_threshold_width&&
            a.thresholdGeoHigh==b.thresholdGeoHigh&&a.thresholdGeoLow==b.thresholdGeoLow&&a.BorderMode==b.BorderMode&&a.normalID==b.normalID&&a.specularID==b.specularID&&
            a.borderNormalID==b.borderNormalID&&a.borderSpecularID==b.borderSpecularID&&a.uvProj==b.uvProj&&a.paintMode==b.paintMode&&a.blendMode==b.blendMode&&a.recomputeNormal==b.recomputeNormal&&
            a.UVScale.x==b.UVScale.x&&a.UVScale.y==b.UVScale.y&&a.buildingOffset==b.buildingOffset&&a.dynDataPixel==b.dynDataPixel&&a.scalingDeformLinkMode==b.scalingDeformLinkMode&&
            a.translationPath.x==b.translationPath.x&&a.translationPath.y==b.translationPath.y&&a.translationPath.z==b.translationPath.z&&a.fuelOverlay==b.fuelOverlay;
        private static bool Finite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
    }
}
