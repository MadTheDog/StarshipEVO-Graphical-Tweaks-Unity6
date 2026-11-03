using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using UnityEngine;
using VoxelEngine;

namespace GraphicalTweaks
{
    // Actual native fields, never inferred from a capture CSV. The diagnostic
    // comparison still requires a successful-upload snapshot of this material.
    internal static class GlassCapInterpolationStyle
    {
        internal static bool Eligible(VoxelMaterial m,int id)
        {
            return m.Id==id&&(int)m.AlphaMode==2&&(int)m.dynDataVert==0&&m.TextureID==183&&m.BorderId==-1&&
                m.normalID==-1&&m.specularID==154&&(int)m.recomputeNormal==0&&(int)m.buildingOffset==0&&
                (int)m.dynDataPixel==0&&(int)m.scalingDeformLinkMode==0&&m.sizeUnit==1&&
                m.thresholdGeoLow==-.001f&&m.thresholdGeoHigh==.001f&&
                Finite(m.U_threshold_low)&&Finite(m.U_threshold_up)&&Finite(m.U_threshold_width)&&
                Finite(m.V_threshold_low)&&Finite(m.V_threshold_up)&&Finite(m.V_threshold_width)&&
                Finite(m.UVScale.x)&&Finite(m.UVScale.y)&&Finite(m.translationPath.x)&&Finite(m.translationPath.y)&&Finite(m.translationPath.z);
        }
        private static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
    }

#if !GLASS_CAP_CANDIDATE_STYLE_ONLY
    internal static partial class GlassShapeCoverage
    {
        private const int MaximumInterpolationOwners=4096,MaximumInterpolationCandidates=128;
        private const string Interpolation313Hash="521E1FFA34AB67B0022080BC55A273B450BA0F99D9205AC7D51CDC5B8493E024";

        // Diagnostic only. No ordinary-frame caller, native buffer mutation,
        // discovery, Watch/Confirm operation or resource publication occurs here.
        internal static bool TryGetCapInterpolationCandidates(out IReadOnlyList<GlassCapInterpolationCandidate> candidates,out string reason)
        {
            candidates=null;reason=null;
            try
            {
                if(Thread.CurrentThread.ManagedThreadId!=mainThread)return InterpolationFail("candidate capture requires the installed Unity main thread",out reason);
                if(!SnapshotCurrent||catalog==null)return InterpolationFail("current glass coverage/catalog snapshot unavailable",out reason);
                if(owners.Count==0||owners.Count>MaximumInterpolationOwners||entities.Count>MaximumInterpolationOwners)
                    return InterpolationFail("tracked owner/entity count outside diagnostic bound",out reason);
                if(Marshal.SizeOf(typeof(BrickMeshResources.ShapeMeshData))!=124||Marshal.SizeOf(typeof(VoxelMaterial))!=132||Marshal.SizeOf(typeof(BrickRenderData))!=44)
                    return InterpolationFail("native candidate ABI changed",out reason);

                var capturedCatalog=catalog;long epoch=SnapshotEpoch,generation=CatalogGeneration;
                var shapeSource=capturedCatalog.SourceBuffer;var buildSource=(ComputeBuffer)nativeBuildBuffer.GetValue(null);
                var materialsSource=uploadedMaterialBuffer;var materialSnapshot=uploadedMaterials;
                var currentMaterials=VoxelMaterialResources.materialParamDatas;var renderer=GlobalRenderer.renderer;
                if(renderer==null||!InterpolationResourcesCurrent(capturedCatalog,epoch,generation,shapeSource,buildSource,materialsSource,materialSnapshot,currentMaterials)||
                   !CatalogInputsReady(shapeSource)||!ValidateUsedMaterials(true))
                    return InterpolationFail("native source/material upload proof unavailable",out reason);
                if(!capturedCatalog.TryGetShape(313,out var descriptor)||!Interpolation313Groups(descriptor))
                    return InterpolationFail("frozen313 cap descriptor mismatch",out reason);
                if(!InterpolationNativeRangeCurrent(descriptor,capturedCatalog.SourceCount))
                    return InterpolationFail("native313 source range/records differ from catalog",out reason);
                var sourceBytes=InterpolationRecordBytes(descriptor.Records);
                string hash;using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(sourceBytes)).Replace("-","");
                if(!string.Equals(hash,Interpolation313Hash,StringComparison.Ordinal))
                    return InterpolationFail("frozen313 full source hash mismatch",out reason);

                var result=new List<GlassCapInterpolationCandidate>();var seen=new HashSet<int>();
                var selectedMaterials=new Dictionary<int,VoxelMaterial>();
                foreach(var pair in entities)
                {
                    var bricks=pair.Key;var state=pair.Value;
                    if(bricks==null||bricks.entity==null||bricks.entity.WillBeUnspawned||!bricks.brickDatas.IsCreated)
                        return InterpolationFail("tracked native entity is no longer current",out reason);
                    foreach(var item in state.BuildByInstance)
                    {
                        if(seen.Count>=MaximumInterpolationOwners||item.Key<1||item.Key>=bricks.brickDatas.Length||!seen.Add(item.Value))
                            return InterpolationFail("tracked instance/build correspondence exceeds bound or is duplicate",out reason);
                        if(!owners.TryGetValue(item.Value,out var owner)||!ReferenceEquals(owner.State,state)||owner.Instance!=item.Key)
                            return InterpolationFail("tracked owner/state correspondence changed",out reason);
                        var data=bricks.GetBrickData(item.Key);
                        if(!data.IsValid||data.instanceId!=item.Key||data.healthScore!=255||!Eligible(data.brickId)||
                           !capturedCatalog.TryGetShape(data.brickId,out var currentDescriptor))
                            return InterpolationFail("tracked healthy shape no longer matches catalog",out reason);
                        var ids=bricks.GetBrickIds(item.Key);
                        if(ids.renderId1==0)return InterpolationFail("tracked native renderer ID is missing",out reason);
                        var render=renderer.GetElement(ids.renderId1);
                        if(!render.IsValid||render.dataId!=item.Value||render.dataId<1||render.dataId>MaximumBuildId||render.dataId>=buildSource.count||
                           render.pass!=RenderablePass.transparent||render.transformId!=bricks.entity.transformId||
                           render.clusterStart!=currentDescriptor.SourceStart||owner.SourceStart!=(uint)currentDescriptor.SourceStart||
                           (long)render.clusterCount*12!=currentDescriptor.PaddedTriangles||render.clusterStartLOD!=0||render.clusterCountLOD!=0)
                            return InterpolationFail("tracked native transparent range/transform/BuildID changed",out reason);
                        var expected=new BrickRenderData((uint)ids.pixelDataId,(uint)ids.vertexDataId,data,bricks.entity.transformId);
                        if(!proofs.Matches(render.dataId,expected)||!TryReadRecord(render.dataId,out var actual)||!actual.Equals(expected))
                            return InterpolationFail("current native Build record has no matching successful upload proof",out reason);
                        if(!capturedCatalog.TryBuildGroups(data,out _,out string geometryReason))
                            return InterpolationFail("current native contact geometry rejected: "+geometryReason,out reason);
                        var scale=data.Scale;
                        if(data.brickId!=313||scale.x!=0||scale.y!=0||scale.z!=0)continue;
                        foreach(var group in descriptor.GroupTriangles)foreach(int tri in group)
                        {
                            if(result.Count>=MaximumInterpolationCandidates)return InterpolationFail("eligible cap candidates exceed128 bound",out reason);
                            uint material=descriptor.Records[tri*3].materialID;
                            if(material>=currentMaterials.Length||material>=materialSnapshot.Length||material>=VoxelMaterialResources.MaterialCount||material>=materialsSource.count||
                               descriptor.Records[tri*3+1].materialID!=material||descriptor.Records[tri*3+2].materialID!=material)
                                return InterpolationFail("actual cap material ID is invalid or mixed",out reason);
                            int id=checked((int)material);var current=currentMaterials[id];
                            if(!GlassCapInterpolationStyle.Eligible(current,id)||!current.Equals(materialSnapshot[id]))
                                return InterpolationFail("actual cap clear-style fields differ from eligible successful native upload",out reason);
                            selectedMaterials[id]=current;
                            var triangle=new byte[372];Buffer.BlockCopy(sourceBytes,tri*372,triangle,0,372);
                            int absolute=checked(descriptor.SourceStart+tri);
                            result.Add(new GlassCapInterpolationCandidate((uint)render.dataId,checked((uint)(descriptor.SourceStart+(tri/12)*12)),absolute,triangle));
                        }
                        // Re-read the record after copying its candidate bytes.
                        if(!proofs.Matches(render.dataId,expected)||!TryReadRecord(render.dataId,out actual)||!actual.Equals(expected)||
                           !bricks.GetBrickData(item.Key).Equals(data)||!renderer.GetElement(ids.renderId1).Equals(render))
                            return InterpolationFail("native owner changed during candidate capture",out reason);
                    }
                }
                if(seen.Count!=owners.Count)return InterpolationFail("tracked native owner inventory is incomplete",out reason);
                if(result.Count==0)return InterpolationFail("no healthy zero-count313 cap candidates in current tracked snapshot",out reason);
                foreach(var pair in selectedMaterials)
                    if(!currentMaterials[pair.Key].Equals(pair.Value)||!materialSnapshot[pair.Key].Equals(pair.Value))
                        return InterpolationFail("actual cap material changed during candidate capture",out reason);
                if(!ReferenceEquals(renderer,GlobalRenderer.renderer)||!InterpolationResourcesCurrent(capturedCatalog,epoch,generation,shapeSource,buildSource,materialsSource,materialSnapshot,currentMaterials)||
                   !ValidateUsedMaterials(true)||!InterpolationNativeRangeCurrent(descriptor,capturedCatalog.SourceCount))
                    return InterpolationFail("native epoch/catalog/resource proof changed during candidate capture",out reason);
                result.Sort((a,b)=>{int order=a.BuildId.CompareTo(b.BuildId);return order!=0?order:a.SourceTriangleId.CompareTo(b.SourceTriangleId);});
                candidates=Array.AsReadOnly(result.ToArray());return true;
            }
            catch(Exception e){return InterpolationFail("candidate capture unavailable: "+e.GetType().Name+": "+e.Message,out reason);}
        }
        private static bool InterpolationResourcesCurrent(GlassShapeRuntimeCatalog captured,long epoch,long generation,ComputeBuffer shape,
            ComputeBuffer build,ComputeBuffer materials,VoxelMaterial[] uploaded,VoxelMaterial[] current)
        {
            return SnapshotCurrent&&SnapshotEpoch==epoch&&CatalogGeneration==generation&&ReferenceEquals(catalog,captured)&&
                shape!=null&&shape.IsValid()&&shape.stride==124&&shape.count==captured.SourceCount&&
                BrickMeshResources.shapeMeshDatas.Count==captured.SourceCount&&ReferenceEquals(shape,uploadedShapeBuffer)&&ReferenceEquals(shape,nativeShapeBuffer.GetValue(null))&&
                build!=null&&build.IsValid()&&build.stride==44&&ReferenceEquals(build,nativeBuildBuffer.GetValue(null))&&
                materials!=null&&materials.IsValid()&&materials.stride==132&&ReferenceEquals(materials,uploadedMaterialBuffer)&&ReferenceEquals(materials,nativeMaterialBuffer.GetValue(null))&&
                uploaded!=null&&current!=null&&ReferenceEquals(uploaded,uploadedMaterials)&&ReferenceEquals(current,VoxelMaterialResources.materialParamDatas)&&
                VoxelMaterialResources.MaterialCount>0&&VoxelMaterialResources.MaterialCount<=current.Length&&VoxelMaterialResources.MaterialCount<=uploaded.Length&&VoxelMaterialResources.MaterialCount<=materials.count;
        }
        private static bool Interpolation313Groups(GlassShapeRuntimeDescriptor d)
        {
            int[][] expected={new[]{0},new[]{8,9},new[]{10,11},new[]{12,13},new[]{14,15},new[]{16,17}};
            if(d==null||d.BrickId!=313||d.SourceStart<=0||d.ActualTriangles!=18||d.PaddedTriangles!=24||d.GroupCount!=6||
               d.Records==null||d.Records.Length!=72||d.GroupTriangles==null||d.GroupTriangles.Length!=expected.Length)return false;
            for(int g=0;g<expected.Length;g++)
            {var actual=d.GroupTriangles[g];if(actual==null||actual.Length!=expected[g].Length)return false;for(int t=0;t<actual.Length;t++)if(actual[t]!=expected[g][t])return false;}
            return true;
        }
        private static bool InterpolationNativeRangeCurrent(GlassShapeRuntimeDescriptor d,int sourceCount)
        {
            if(Brick.BRICK_INDEX==null||Brick.BRICK_INDEX.Length<=313||Brick.BRICK_INDEX[313]==null||Brick.BRICK_INDEX[313].VoxelMesh==null)return false;
            var brick=Brick.BRICK_INDEX[313];var span=brick.VoxelMesh.transparentData;
            if(brick.brickID!=313||span.startTriangle!=d.SourceStart||span.count!=d.ActualTriangles*3||
               (long)span.ClusterCount*12!=d.PaddedTriangles||brick.VoxelMesh.opaqueData.count!=0||brick.VoxelMesh.cutoutData.count!=0||
               (long)d.SourceStart*3+d.Records.Length>sourceCount||BrickMeshResources.shapeMeshDatas.Count!=sourceCount)return false;
            for(int v=0;v<d.Records.Length;v++)if(!BrickMeshResources.shapeMeshDatas[d.SourceStart*3+v].Equals(d.Records[v]))return false;
            return true;
        }
        private static byte[] InterpolationRecordBytes(BrickMeshResources.ShapeMeshData[] records)
        {
            var result=new byte[checked(records.Length*124)];var pin=GCHandle.Alloc(records,GCHandleType.Pinned);
            try{Marshal.Copy(pin.AddrOfPinnedObject(),result,0,result.Length);return result;}finally{pin.Free();}
        }
        private static bool InterpolationFail(string value,out string reason){reason=value;return false;}
    }
#endif
}
