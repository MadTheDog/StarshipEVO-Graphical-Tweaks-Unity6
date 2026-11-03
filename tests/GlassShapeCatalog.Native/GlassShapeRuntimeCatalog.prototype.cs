using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;
using VoxelEngine;

namespace GraphicalTweaks
{
    internal sealed class GlassShapeRuntimeDescriptor
    {
        internal readonly ushort BrickId;
        internal readonly int SourceStart,ActualTriangles,PaddedTriangles,GroupCount;
        internal readonly BrickMeshResources.ShapeMeshData[] Records;
        internal readonly GlassShapeDeformationMaterial[] Materials;
        internal readonly int[][] GroupTriangles;
        internal readonly uint[] TriangleFaceBits;
        internal readonly GlassNominalProfileAdmission NominalAdmission;
#if SEVO_UNITY6
        internal GlassShapeContactCanonicalizer ContactPolicy;
#endif
        internal GlassShapeRuntimeDescriptor(ushort id,int start,int actual,int padded,
            BrickMeshResources.ShapeMeshData[] records,GlassShapeDeformationMaterial[] materials,GlassShapeGrouping grouping,
            GlassNominalProfileAdmission nominalAdmission=null)
        {
            BrickId=id;SourceStart=start;ActualTriangles=actual;PaddedTriangles=padded;
            Records=records;Materials=materials;GroupTriangles=grouping.GroupTriangles;
            TriangleFaceBits=grouping.TriangleFaceBits;GroupCount=grouping.GroupCount;
            NominalAdmission=nominalAdmission;
        }
    }

    // UNCONNECTED prototype. The lifecycle owner must invalidate on source/material/
    // rotation-table resource replacement before exposing any masks. Construction
    // reads the current native CPU pool once; neither this class nor its instance
    // geometry path reads back a GPU buffer or rewrites any source record.
    internal sealed class GlassShapeRuntimeCatalog
    {
        static readonly ushort[] Ids={166,168,314,315,316,167,118,313,317,207,209};
        static readonly int[] ExpectedGroups={6,5,4,7,5,4,3,6,3,4,3};
#if SEVO_UNITY6
        static readonly Guid ExpectedMvid=new Guid("00859162-dd57-4798-be35-54252142455d");
#else
        static readonly Guid ExpectedMvid=new Guid("f4666bd3-9a44-486c-b149-36d0a9c82e72");
#endif
        static readonly GlassShapeRotation Identity=new GlassShapeRotation(new float[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1});
        internal GlassShapeVariantSource[] Sources {get;private set;}
        internal GlassShapeContactCanonicalizer Policy {get;private set;}
        internal ComputeBuffer SourceBuffer {get;private set;}
        internal int SourceCount {get;private set;}
        internal int NominalProfilesCount {get;private set;}
#if SEVO_UNITY6
        internal string PipeAdmissionReason {get;private set;}
#endif
        readonly Dictionary<ushort,GlassShapeRuntimeDescriptor> shapes=new Dictionary<ushort,GlassShapeRuntimeDescriptor>();
        readonly GlassShapeRotation[] rotations=new GlassShapeRotation[32];
        private GlassShapeRuntimeCatalog(){}
        internal bool TryGetShape(ushort id,out GlassShapeRuntimeDescriptor descriptor)=>shapes.TryGetValue(id,out descriptor);
        internal static bool TryCreate(ComputeBuffer source,out GlassShapeRuntimeCatalog catalog)=>TryCreate(source,out catalog,out _);
        internal static bool TryCreate(ComputeBuffer source,out GlassShapeRuntimeCatalog catalog,out string reason)
        {
            catalog=null;reason=null;
            try
            {
                var native=BrickMeshResources.shapeMeshDatas;var materials=VoxelMaterialResources.materialParamDatas;
                if(typeof(BrickMeshResources).Module.ModuleVersionId!=ExpectedMvid||Marshal.SizeOf(typeof(BrickMeshResources.ShapeMeshData))!=124)
                    return Fail("native assembly MVID or 124-byte shape ABI changed",out reason);
                if(source==null||!source.IsValid()||source.stride!=124||source.count<=0||source.count>4*1024*1024||source.count%3!=0||native.Count!=source.count)
                    return Fail("native CPU/source-buffer identity count or stride invalid",out reason);
                if(materials==null||VoxelMaterialResources.MaterialCount<1||VoxelMaterialResources.MaterialCount>materials.Length)
                    return Fail("native material catalog unavailable",out reason);
                var result=new GlassShapeRuntimeCatalog{SourceBuffer=source,SourceCount=source.count,Sources=new GlassShapeVariantSource[Ids.Length]};
                var rawRecords=new BrickMeshResources.ShapeMeshData[Ids.Length][];
                var perTriangleMaterials=new GlassShapeDeformationMaterial[Ids.Length][];
                var starts=new int[Ids.Length];var actuals=new int[Ids.Length];
                var positions=new List<GlassShapeFloat3>();
                for(int i=0;i<Ids.Length;i++)
                {
                    ushort id=Ids[i];
                    if(Brick.BRICK_INDEX==null||id>=Brick.BRICK_INDEX.Length||Brick.BRICK_INDEX[id]==null||Brick.BRICK_INDEX[id].VoxelMesh==null)
                        return Fail("missing explicit clear shape "+id,out reason);
                    var brick=Brick.BRICK_INDEX[id];var span=brick.VoxelMesh.transparentData;
                    long padded=(long)span.ClusterCount*36,first=(long)span.startTriangle*3;
                    if(brick.brickID!=id||span.startTriangle<=0||span.count<=0||span.count%3!=0||span.ClusterCount<=0||
                        padded<span.count||padded-span.count>=36||padded>65536||first+padded>native.Count||brick.VoxelMesh.opaqueData.count!=0||brick.VoxelMesh.cutoutData.count!=0)
                        return Fail("invalid clear transparent-only range "+id,out reason);
                    for(int j=0;j<i;j++)if(span.startTriangle<starts[j]+rawRecords[j].Length/3&&starts[j]<span.startTriangle+padded/3)
                        return Fail("overlapping clear source ranges",out reason);
                    starts[i]=span.startTriangle;actuals[i]=span.count/3;
                    var records=rawRecords[i]=new BrickMeshResources.ShapeMeshData[(int)padded];native.CopyTo((int)first,records,0,records.Length);
                    if(!ZeroPadding(records,span.count))return Fail("nonzero full-record native padding "+id,out reason);
                    var used=perTriangleMaterials[i]=new GlassShapeDeformationMaterial[actuals[i]];
                    for(int t=0;t<actuals[i];t++)
                    {
                        uint mat=records[t*3].materialID;
                        if(records[t*3+1].materialID!=mat||records[t*3+2].materialID!=mat||mat>=VoxelMaterialResources.MaterialCount||mat>=materials.Length)
                            return Fail("triangle has mixed or invalid used material "+id,out reason);
                        bool declared=false;
                        if(brick.VoxelMaterials!=null)foreach(var m in brick.VoxelMaterials)if(m.Id==(int)mat){declared=true;break;}
                        if(!declared||!TryMaterial(materials[(int)mat],(int)mat,out used[t]))return Fail("ineligible actual triangle material "+id+"/"+mat,out reason);
                        for(int v=0;v<3;v++)positions.Add(F(records[t*3+v].position));
                    }
                    if(positions.Count>65536)return Fail("clear source-position budget exceeded",out reason);
                }
                if(!GlassShapeContactCanonicalizer.TryCreate(positions,out var policy))return Fail("source contact policy rejected",out reason);
                result.Policy=policy;
                for(int i=0;i<Ids.Length;i++)
                {
                    var records=rawRecords[i];var ps=new GlassShapeFloat3[actuals[i]*3];
                    for(int v=0;v<ps.Length;v++)
                    {
                        ps[v]=F(records[v].position);var m=perTriangleMaterials[i][v/3];
                        var input=new GlassShapeDeformationInput(ps[v],default,F(records[v].boundsCenter),F(records[v].boundsSize),m,0,0,0,1,Identity,default);
                        if(!GlassShapeContactModel.TryDeform(input,policy,default,out _))return Fail("invalid source deformation fields "+Ids[i],out reason);
                        if(m.ResizeMode==7||m.ResizeMode==8)
                        {
                            var c=records[v].boundsCenter;var s=records[v].boundsSize;
                            if(!policy.TryCanonicalizeRadialAnchor(new GlassShapeFloat3(c.x-.5f*s.x,c.y-.5f*s.y,c.z-.5f*s.z),out _))
                                return Fail("derived radial anchor outside contact classes "+Ids[i],out reason);
                        }
                    }
                    if(!GlassShapeGrouping.TryCreate(ps,actuals[i],records.Length/3,i>=5,policy,out var grouped,out string why)||grouped.GroupCount!=ExpectedGroups[i])
                        return Fail("source plane grouping rejected "+Ids[i]+": "+why,out reason);
                    var nominal=AdmitNominalProfile(Ids[i],records,perTriangleMaterials[i],grouped.GroupTriangles);
                    if(nominal!=null)result.NominalProfilesCount++;
                    var d=new GlassShapeRuntimeDescriptor(Ids[i],starts[i],actuals[i],records.Length/3,records,perTriangleMaterials[i],grouped,nominal);
                    result.shapes.Add(Ids[i],d);
                    result.Sources[i]=new GlassShapeVariantSource(d.BrickId,d.SourceStart,d.ActualTriangles,d.PaddedTriangles,d.GroupCount,d.TriangleFaceBits);
                }
                var matrices=Shader.GetGlobalMatrixArray("VoxelRotations");
                if(matrices==null||matrices.Length<32)return Fail("native VoxelRotations table unavailable",out reason);
                for(int i=0;i<32;i++)
                {
                    var m=matrices[i];var r=new GlassShapeRotation(new[]{m.m00,m.m01,m.m02,m.m03,m.m10,m.m11,m.m12,m.m13,m.m20,m.m21,m.m22,m.m23,m.m30,m.m31,m.m32,m.m33});
                    if(!GlassShapeContactModel.TryCanonicalRotation(r,out _))return Fail("native rotation outside 2^-23 quarter-turn gate: "+i,out reason);
                    result.rotations[i]=r;
                }
#if SEVO_UNITY6
                // Optional and independent: a changed/unsupported bordered pipe
                // must never reject or alter the existing eleven-shape policy.
                result.TryAddPipe(native,materials);
#endif
                if(native.Count!=result.SourceCount||source.count!=result.SourceCount||!source.IsValid())return Fail("source resources changed during catalog construction",out reason);
                catalog=result;return true;
            }
            catch(Exception e){return Fail("catalog unavailable: "+e.GetType().Name+": "+e.Message,out reason);}
        }
        internal bool TryBuildGroups(BrickInstanceData bd,out GlassPolygonFaceGroup[] groups)=>TryBuildGroups(bd,out groups,out _);
        internal bool TryBuildGroups(BrickInstanceData bd,out GlassPolygonFaceGroup[] groups,out string reason)
        {
            groups=null;reason=null;
            if(bd.healthScore!=255||bd.rotation>31||!shapes.TryGetValue(bd.brickId,out var descriptor))return Fail("instance health, rotation or explicit shape unsupported",out reason);
            try
            {
                var scale=bd.Scale;float unit=bd.GridSize;
                var exact=new GlassPolygonPoint(bd.gridPosition.x/32d,bd.gridPosition.y/32d,bd.gridPosition.z/32d);
                var placement=new GlassShapeFloat3((float)exact.X,(float)exact.Y,(float)exact.Z);
                var contactPolicy=Policy;
#if SEVO_UNITY6
                if(descriptor.ContactPolicy!=null)contactPolicy=descriptor.ContactPolicy;
#endif
                var result=new GlassPolygonFaceGroup[descriptor.GroupCount];
                for(int bit=0;bit<result.Length;bit++)
                {
                    var local=descriptor.GroupTriangles[bit];var triangles=new GlassPolygonTriangle[local.Length];
                    for(int n=0;n<local.Length;n++)
                    {
                        int t=local[n];var vertices=new GlassPolygonPoint[3];
                        for(int v=0;v<3;v++)
                        {
                            var r=descriptor.Records[t*3+v];
                            var input=new GlassShapeDeformationInput(F(r.position),default,F(r.boundsCenter),F(r.boundsSize),descriptor.Materials[t],
                                scale.x,scale.y,scale.z,unit,rotations[bd.rotation&31],placement);
                            if(!GlassShapeContactModel.TryDeform(input,contactPolicy,exact,out vertices[v]))return Fail("contact deformation rejected "+bd.brickId+" triangle "+t,out reason);
                        }
                        if(!Nondegenerate(vertices[0],vertices[1],vertices[2]))return Fail("deformed contact triangle degenerate "+bd.brickId+" triangle "+t,out reason);
                        triangles[n]=new GlassPolygonTriangle(vertices[0],vertices[1],vertices[2]);
                    }
                    result[bit]=new GlassPolygonFaceGroup(bit,triangles);
                }
                groups=result;return true;
            }
            catch(Exception e){return Fail("instance geometry unavailable: "+e.GetType().Name+": "+e.Message,out reason);}
        }
        internal bool TryBuildNominalCaps(BrickInstanceData bd,out GlassNominalCap[] caps)
        {
            caps=null;
            if(bd.healthScore!=255||bd.rotation>31||!shapes.TryGetValue(bd.brickId,out var descriptor)||descriptor.NominalAdmission==null)return false;
            var scale=bd.Scale;
            var placement=new GlassPolygonPoint(bd.gridPosition.x/32d,bd.gridPosition.y/32d,bd.gridPosition.z/32d);
            return GlassNominalCapProfiles.TryBuild(descriptor.NominalAdmission,scale.x,scale.y,scale.z,
                bd.GridSize,rotations[bd.rotation&31],placement,out caps);
        }
        private static GlassNominalProfileAdmission AdmitNominalProfile(ushort id,BrickMeshResources.ShapeMeshData[] records,
            GlassShapeDeformationMaterial[] materials,int[][] groups)
        {
            if(id!=118&&id!=317&&id!=209)return null;
            // Optional appearance policy: failure here never rejects the exact
            // geometry catalog. Hash once per catalog generation, not per frame.
            try
            {
                var used=new Dictionary<int,GlassShapeDeformationMaterial>();
                foreach(var group in groups)foreach(int triangle in group)
                    used[(int)records[triangle*3].materialID]=materials[triangle];
                var bytes=new byte[checked(records.Length*124)];var scratch=new byte[124];
                IntPtr pointer=Marshal.AllocHGlobal(124);
                try
                {
                    for(int i=0;i<records.Length;i++)
                    {
                        Marshal.StructureToPtr(records[i],pointer,false);Marshal.Copy(pointer,scratch,0,124);
                        Buffer.BlockCopy(scratch,0,bytes,i*124,124);
                    }
                }
                finally{Marshal.FreeHGlobal(pointer);}
                string hash;
                using(var sha=SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","");
                return GlassNominalCapProfiles.TryAdmit(id,hash,groups,used,out var admission)?admission:null;
            }
            catch{return null;}
        }
#if SEVO_UNITY6
        private void TryAddPipe(List<BrickMeshResources.ShapeMeshData> native,VoxelMaterial[] materials)
        {
            PipeAdmissionReason="pipe source/material contract is unavailable";
            try
            {
                const ushort id=GlassPipeEndCapPolicy.BrickId;
                if(Brick.BRICK_INDEX==null||id>=Brick.BRICK_INDEX.Length||Brick.BRICK_INDEX[id]?.VoxelMesh==null||
                    materials==null||materials.Length<=489||VoxelMaterialResources.MaterialCount<=489||Marshal.SizeOf(typeof(VoxelMaterial))!=132)return;
                var brick=Brick.BRICK_INDEX[id];var mesh=brick.VoxelMesh;var span=mesh.transparentData;
                long first=(long)span.startTriangle*3;
                if(brick.brickID!=id||span.startTriangle<=0||span.count!=144||span.ClusterCount!=4||first+144>native.Count||
                    mesh.opaqueData.count!=0||mesh.cutoutData.count!=0)return;
                foreach(var other in shapes.Values)
                    if(span.startTriangle<other.SourceStart+other.PaddedTriangles&&other.SourceStart<span.startTriangle+48)return;
                bool sideDeclared=false,capDeclared=false;
                if(brick.VoxelMaterials!=null)foreach(var material in brick.VoxelMaterials)
                {if(material.Id==488)sideDeclared=true;if(material.Id==489)capDeclared=true;}
                if(!sideDeclared||!capDeclared)return;
                var records=new BrickMeshResources.ShapeMeshData[144];native.CopyTo((int)first,records,0,144);
                var actualMaterials=new Dictionary<int,byte[]>{{488,RecordBytes(new[]{materials[488]})},{489,RecordBytes(new[]{materials[489]})}};
                if(!GlassPipeEndCapPolicy.TryAdmit(RecordBytes(records),actualMaterials,out var pipe))return;
                var descriptor=new GlassShapeRuntimeDescriptor(id,span.startTriangle,48,48,records,pipe.Materials,pipe.Grouping)
                    {ContactPolicy=pipe.Policy};
                var sources=new GlassShapeVariantSource[Sources.Length+1];Array.Copy(Sources,sources,Sources.Length);
                sources[sources.Length-1]=new GlassShapeVariantSource(id,span.startTriangle,48,48,2,descriptor.TriangleFaceBits);
                shapes.Add(id,descriptor);Sources=sources;PipeAdmissionReason=null;
            }
            catch(Exception error){PipeAdmissionReason="pipe admission unavailable: "+error.GetType().Name+": "+error.Message;}
        }
        private static byte[] RecordBytes<T>(T[] values) where T:struct
        {
            var bytes=new byte[checked(Marshal.SizeOf(typeof(T))*values.Length)];
            var handle=GCHandle.Alloc(values,GCHandleType.Pinned);
            try{Marshal.Copy(handle.AddrOfPinnedObject(),bytes,0,bytes.Length);}
            finally{handle.Free();}
            return bytes;
        }
#endif
        static bool TryMaterial(VoxelMaterial m,int id,out GlassShapeDeformationMaterial result)
        {
            result=default;int mode=(int)m.scalingDeformLinkMode;
            if(m.Id!=id||(int)m.AlphaMode!=2||m.BorderId!=-1||m.normalID!=-1||(int)m.dynDataVert!=0||
                (int)m.buildingOffset!=0||(mode!=0&&mode!=7&&mode!=8)||!Finite(m.sizeUnit)||m.sizeUnit<=0||
                !Finite(m.thresholdGeoLow)||!Finite(m.thresholdGeoHigh)||m.thresholdGeoLow>m.thresholdGeoHigh)return false;
            result=new GlassShapeDeformationMaterial(mode,0,2,m.sizeUnit,m.thresholdGeoLow,m.thresholdGeoHigh);return true;
        }
        static bool ZeroPadding(BrickMeshResources.ShapeMeshData[] records,int actualVertices)
        {
            IntPtr pointer=Marshal.AllocHGlobal(124);var bytes=new byte[124];
            try{for(int i=actualVertices;i<records.Length;i++){Marshal.StructureToPtr(records[i],pointer,false);Marshal.Copy(pointer,bytes,0,124);foreach(byte b in bytes)if(b!=0)return false;}return true;}
            finally{Marshal.FreeHGlobal(pointer);}
        }
        static GlassShapeFloat3 F(Vector3 p)=>new GlassShapeFloat3(p.x,p.y,p.z);
        static bool Nondegenerate(GlassPolygonPoint a,GlassPolygonPoint b,GlassPolygonPoint c)
        {
            double x=b.X-a.X,y=b.Y-a.Y,z=b.Z-a.Z,u=c.X-a.X,v=c.Y-a.Y,w=c.Z-a.Z;
            return y*w-z*v!=0||z*u-x*w!=0||x*v-y*u!=0;
        }
        static bool Finite(float f)=>!float.IsNaN(f)&&!float.IsInfinity(f);
        static bool Fail(string value,out string reason){reason=value;return false;}
    }
}
