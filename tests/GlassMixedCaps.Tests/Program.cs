using System.Globalization;
using System.Text.Json;
using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
    const string Hash118="06AD79D6C5B28D3151AA37B81CBE8965D5B0B04F7FEB92D67DFA331E941FF6AB";
    const string Hash313="521E1FFA34AB67B0022080BC55A273B450BA0F99D9205AC7D51CDC5B8493E024";
    const string Hash314="10B78DE3CCB39A1EA78FC9CB7F89DE47C4F1EAE1C5BA9F462CDDF0151351176D";
    static readonly GlassShapeRotation Identity=new(new float[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1});
    static string Hash(int id)=>id==118?Hash118:id==313?Hash313:Hash314;
    static int Actual(int id)=>id==118?8:id==313?18:4;
    static int Padded(int id)=>id==313?24:12;
    static int[][] Groups(int id)=>id switch{
        118=>new[]{new[]{2,3},new[]{4,5},new[]{6,7}},
        313=>new[]{new[]{0},new[]{8,9},new[]{10,11},new[]{12,13},new[]{14,15},new[]{16,17}},
        _=>new[]{new[]{0},new[]{1},new[]{2},new[]{3}}};
    static GlassMixedCapMaterial Style(int id)=>new(id,new GlassShapeDeformationMaterial(0,0,2,1,-.001f,.001f));
    static Dictionary<int,GlassMixedCapMaterial> Materials(int id)=>id switch{
        118=>new(){{271,Style(271)}},313=>new(){{695,Style(695)},{696,Style(696)}},_=>new(){{698,Style(698)}}};
    static GlassMixedCapSource Source(int id)
    {
        Check(GlassMixedCapCatalog.TryAdmit(id,Hash(id),Actual(id),Padded(id),Groups(id),Materials(id),out var source),$"source {id} admitted");return source;
    }
    static GlassMixedCapInstance Build(GlassMixedCapSource source,int id=17,long entity=1,float unit=1,
        GlassShapeRotation? rotation=null,GlassPolygonPoint placement=default)
    {
        Check(GlassMixedCapCatalog.TryBuild(source,entity,id,255,0,0,0,unit,rotation??Identity,placement,out var instance),$"zero-count build {source.BrickId}/{id}");return instance;
    }
    static void Main(string[] args)
    {
        CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
        AdmissionControls();PoseControls();FactorySemantics();ExactCoverageControls();SceneTracking();RetiredDeltaLifetime();
        if(args.Length!=0)
        {
            if(args.Length!=4||args[0]!="--capture"||args[2]!="--scene")throw new Exception("Usage: --capture private-native-capture-directory --scene private-entity.json");
            CaptureLane(args[1],args[3]);
        }
        Console.WriteLine($"PASS {checks} mixed zero-count cap checks; native/GPU acceptance unproven.");
    }
    static void AdmissionControls()
    {
        foreach(int id in new[]{118,313,314})
        {
            var groups=Groups(id);var materials=Materials(id);var source=Source(id);
            Check(source.BrickId==id&&source.ActualTriangles==Actual(id)&&source.PaddedTriangles==Padded(id),"source identity/counts");
            bool Admit(string hash,int actual,int padded,int[][] g,Dictionary<int,GlassMixedCapMaterial> m)=>GlassMixedCapCatalog.TryAdmit(id,hash,actual,padded,g,m,out _);
            Check(!Admit((Hash(id)[0]=='0'?"1":"0")+Hash(id)[1..],Actual(id),Padded(id),groups,materials),"source hash mismatch rejected");
            Check(!Admit(Hash(id),Actual(id)+1,Padded(id),groups,materials),"actual count mismatch rejected");
            Check(!Admit(Hash(id),Actual(id),Padded(id)+12,groups,materials),"padded count mismatch rejected");
            var bad=Groups(id);bad[0][0]++;Check(!Admit(Hash(id),Actual(id),Padded(id),bad,materials),"group membership mismatch rejected");
            Check(!Admit(Hash(id),Actual(id),Padded(id),null,materials),"null groups rejected");
            int material=materials.Keys.First();var broken=new Dictionary<int,GlassMixedCapMaterial>(materials);broken.Remove(material);
            Check(!Admit(Hash(id),Actual(id),Padded(id),groups,broken),"missing actual used cap material rejected");
            broken[999]=Style(999);Check(!Admit(Hash(id),Actual(id),Padded(id),groups,broken),"unused declaration cannot replace actual used material");
            var b=Style(material);var d=b.Deformation;
            var invalid=new[]{new GlassMixedCapMaterial(material+1,d),
                new GlassMixedCapMaterial(material,d,182),new GlassMixedCapMaterial(material,d,specularIndex:153),
                new GlassMixedCapMaterial(material,d,normalIndex:0),new GlassMixedCapMaterial(material,d,normalMode:1),new GlassMixedCapMaterial(material,d,pixelMode:1),
                new GlassMixedCapMaterial(material,new(1,0,2,1,-.001f,.001f)),new GlassMixedCapMaterial(material,new(0,1,2,1,-.001f,.001f)),
                new GlassMixedCapMaterial(material,new(0,0,1,1,-.001f,.001f)),new GlassMixedCapMaterial(material,new(0,0,2,2,-.001f,.001f)),
                new GlassMixedCapMaterial(material,new(0,0,2,1,-.002f,.001f)),new GlassMixedCapMaterial(material,new(0,0,2,1,-.001f,.002f)),
                new GlassMixedCapMaterial(material,new(0,0,2,1,-.001f,.001f,0)),new GlassMixedCapMaterial(material,new(0,0,2,1,-.001f,.001f,buildingOffset:1)),
                new GlassMixedCapMaterial(material,new(0,0,2,float.NaN,-.001f,.001f))};
            foreach(var invalidStyle in invalid){broken=new(materials);broken[material]=invalidStyle;Check(!Admit(Hash(id),Actual(id),Padded(id),groups,broken),"exact material guard rejected alteration");}
            Check(!GlassMixedCapCatalog.TryCreate(id,new byte[Padded(id)*3*124],Actual(id),Padded(id),groups,materials,out _),"synthetic bytes do not pass native source SHA");
            Check(!GlassMixedCapCatalog.TryCreate(id,new byte[1],Actual(id),Padded(id),groups,materials,out _),"wrong source byte length rejected");
            groups[0][0]=123;materials.Clear();Check(Build(source).Groups.Count==(id==313?6:3),"source token owns controls after caller mutation");
        }
        Check(!GlassMixedCapCatalog.TryAdmit(166,Hash118,8,12,Groups(118),Materials(118),out _),"unsupported source rejected");
    }
    static void PoseControls()
    {
        foreach(int type in new[]{118,313,314})
        {
            var source=Source(type);
            bool Pose(byte health=255,int x=0,int y=0,int z=0,float unit=1,GlassShapeRotation? r=null,GlassPolygonPoint p=default,int id=1)
                =>GlassMixedCapCatalog.TryBuild(source,1,id,health,x,y,z,unit,r??Identity,p,out _);
            Check(!Pose(health:254),"damaged owner rejected");Check(!Pose(health:0),"dead owner rejected");
            foreach(int count in new[]{-1,1,2,15,16}){Check(!Pose(x:count),"any X resize rejected");Check(!Pose(y:count),"any Y resize rejected");Check(!Pose(z:count),"any Z resize rejected");}
            foreach(float bad in new[]{0,-1,.1f,.3f,32,float.NaN,float.PositiveInfinity})Check(!Pose(unit:bad),"unsupported/invalid unit rejected");
            foreach(float u in new[]{.125f,.25f,.5f,1,2,4,8,16})Check(Pose(unit:u),"bounded exact power-of-two unit admitted");
            Check(!Pose(id:0)&&!Pose(id:-1)&&!Pose(id:4194304),"native build-id domain guarded");
            Check(!Pose(p:new(.01,0,0))&&!Pose(p:new(0,double.NaN,0))&&!Pose(p:new(0,0,double.PositiveInfinity)),"non-grid/nonfinite placement rejected");
            Check(!Pose(p:new(1048576,0,0)),"transformed vertex coordinate limit guarded");
            Check(Pose(p:new(-1048570,1048570,1d/32)),"finite bounded exact placement admitted");
            Check(!Pose(r:new GlassShapeRotation(new float[]{-1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1})),"mirror matrix rejected; source rotation must be proper");
            Check(!Pose(r:new GlassShapeRotation(new float[]{.999f,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1})),"non-quarter rotation rejected");
            Check(!Pose(r:new GlassShapeRotation(new float[]{float.NaN,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1})),"nonfinite rotation rejected");
        }
    }
    static void FactorySemantics()
    {
        foreach(int type in new[]{118,313,314})
        {
            var source=Source(type);var native=Build(source);double expected=type==118?4.5:type==313?6:1.5;
            Check(native.Brick.Groups.Sum(g=>g.Triangles.Sum(Area))==expected,$"{type} semantic area");
            Check(native.Brick.Groups.Select(g=>g.Bit).SequenceEqual(type==313?Enumerable.Range(0,6):type==118?Enumerable.Range(0,3):Enumerable.Range(1,3)),"group-bit correspondence");
            foreach(var group in native.Brick.Groups){Check(group.Triangles.All(t=>Area(t)>0),"no collapsed proxy reaches exact model");Check(group.Triangles.Sum(Area)==(type==313&&group.Bit<3||type==314?.5:1.5),"unit triangle or 2-minus-1 band area");}
            var mutate=native.Brick;mutate.Groups[0].Triangles[0]=default;mutate.Groups[0]=null;Check(native.Brick.Groups.All(g=>g!=null)&&native.Brick.Groups.Sum(g=>g.Triangles.Sum(Area))==expected,"output brick returns defensive snapshot");
            foreach(var r in Rotations())foreach(float u in new[]{.125f,1,16})
            {
                var moved=Build(source,rotation:r,unit:u,placement:new(7d/32,-13d/32,23d/32));
                Check(moved.Brick.Groups.Sum(g=>g.Triangles.Sum(Area))==expected*u*u,"all24 proper rotations preserve winding/area for exact units");
            }
        }
        var hepta=Build(Source(313));var g1=hepta.Groups.Single(g=>g.Bit==1);var g2=hepta.Groups.Single(g=>g.Bit==2);
        Check(g1.HasCollapsedInputs&&g1.NativeTriangleIndices.SequenceEqual(new[]{8})&&g1.WholeGroupNativeTriangleIndices.SequenceEqual(new[]{8,9}),"313cap1 source9 collapse explicitly mapped");
        Check(g2.HasCollapsedInputs&&g2.NativeTriangleIndices.SequenceEqual(new[]{11})&&g2.WholeGroupNativeTriangleIndices.SequenceEqual(new[]{10,11}),"313cap2 source10 collapse explicitly mapped");
        Check(hepta.Groups.Where(g=>g.Bit!=1&&g.Bit!=2).All(g=>!g.HasCollapsedInputs),"collapse flags limited to audited small cap inputs");
        bool throws=false;try{((IList<int>)g1.NativeTriangleIndices)[0]=999;}catch(NotSupportedException){throws=true;}Check(throws,"query-native correspondence is read-only");
    }
    static void ExactCoverageControls()
    {
        // Hand-authored math controls, not admitted asset bytes.
        var a=new GlassPolygonTriangle(new(0,0,0),new(2,0,0),new(0,2,0));
        GlassPolygonTriangle Opp(double x,double y,double z,double size)=>new(new(x,y,z),new(x,y+size,z),new(x+size,y,z));
        GlassPolygonCoverageBrick Brick(int id,long entity,params GlassPolygonTriangle[] ts)=>new(entity,id,255,new GlassPolygonFaceGroup(0,ts));
        var model=new GlassPolygonCoverageModel();model.Upsert(Brick(1,1,a));model.Upsert(Brick(2,1,Opp(0,0,0,2)));Check(model.GetMask(1)==1,"coincident opposite whole face hides");
        model.Upsert(Brick(2,2,Opp(0,0,0,2)));Check(model.GetMask(1)==0,"other entity never hides");
        model.Upsert(Brick(2,1,Opp(0,0,1d/32,2)));Check(model.GetMask(1)==0,"parallel shifted plane never hides");
        model.Upsert(Brick(2,1,Opp(1d/32,0,0,2)));Check(model.GetMask(1)==0,"intentional in-plane offset keeps exposed strip");
        Check(model.TryGetCapRemainder(1,0,out var strip)&&strip.Changed&&RemainderArea(new GlassPolygonFaceGroup(0,a),strip)>0,"strip remainder stays positive");
        model.Upsert(Brick(2,1,Opp(0,0,0,1)));Check(model.GetMask(1)==0,"larger partial face remains");
        Check(model.TryGetCapRemainder(1,0,out var part)&&RemainderArea(new GlassPolygonFaceGroup(0,a),part)==1.5,"triangle subtraction retains hand-derived1.5");
        model.Remove(2);model.Upsert(Brick(2,1,Opp(0,0,0,1)));model.Upsert(Brick(3,1,Opp(1,0,0,1)));model.Upsert(Brick(4,1,Opp(0,1,0,1)));
        Check(model.GetMask(1)==0&&model.TryGetCapRemainder(1,0,out var hole)&&RemainderArea(new GlassPolygonFaceGroup(0,a),hole)==.5,"three corner covers retain central triangular hole");
        model.Upsert(Brick(5,1,new GlassPolygonTriangle(new(1,0,0),new(0,1,0),new(1,1,0))));Check(model.GetMask(1)==1,"four-piece exact union covers target");
        model.Remove(5);Check(model.GetMask(1)==0,"remove union member reopens hole");Check(model.BudgetFailures==0,"math controls stay inside work bounds");
    }
    static void SceneTracking()
    {
        var source=Source(118);var a=Build(source);var scene=new GlassMixedCapScene();var touched=new List<int>();
        Check(scene.Upsert(a),"scene admits factory output");scene.DrainTouched(touched);Check(touched.SequenceEqual(new[]{17})&&scene.GetMask(17)==0,"geometry touch emitted with zero full mask");
        long checksBefore=scene.CandidateChecks;Check(scene.Upsert(a),"identical immutable owner accepted");touched.Clear();scene.DrainTouched(touched);Check(touched.Count==0&&scene.CandidateChecks==checksBefore,"idle instance causes no polygon work");
        Check(scene.Upsert(Build(source)),"equivalent new snapshot accepted");touched.Clear();scene.DrainTouched(touched);Check(touched.Count==0&&scene.CandidateChecks==checksBefore,"equivalent geometry snapshot does no polygon work");
        scene.Upsert(Build(source,18,placement:new(1d/32,0,0)));touched.Clear();scene.DrainTouched(touched);Check(touched.SequenceEqual(new[]{17,18}),"new partial neighbour touches existing zero-mask owner");
        scene.Upsert(Build(source,18,placement:new(100,0,0)));touched.Clear();scene.DrainTouched(touched);Check(touched.SequenceEqual(new[]{17,18}),"replacement touches former and current positions");
        scene.Upsert(Build(source,19,entity:2));touched.Clear();scene.DrainTouched(touched);Check(touched.SequenceEqual(new[]{19}),"separate entity does not invalidate first entity");
        Check(!scene.TryUpsert(source,1,17,254,0,0,0,1,Identity,default),"invalid replacement removes prior admitted owner");touched.Clear();scene.DrainTouched(touched);Check(touched.Contains(17)&&scene.OwnerCount==2&&scene.GetMask(17)==0,"failed replacement retains no stale geometry");
        scene.RemoveEntity(1);Check(scene.OwnerCount==1,"entity disposal scoped");scene.Clear();Check(scene.OwnerCount==0,"scene clear drops all owners");
        var bounded=new GlassMixedCapScene(1);Check(bounded.Upsert(a)&&!bounded.Upsert(Build(source,18))&&bounded.OwnerCount==1,"bounded scene retains faces on owner overflow");
        var hepta=Build(Source(313));scene.Upsert(hepta);Check(!scene.TryGetCapRemainder(17,1,out _),"partial clip of collapsed proxy group explicitly unsupported");
    }
    static void CaptureLane(string capture,string scenePath)
    {
        var catalog=File.ReadLines(Path.Combine(capture,"catalog.csv")).Skip(1).Select(s=>s.Split(',')).ToDictionary(c=>int.Parse(c[0]));
        var materialRows=File.ReadLines(Path.Combine(capture,"materials.csv")).Skip(1).Select(s=>s.Split(',')).ToArray();
        var bytes=new Dictionary<int,byte[]>();var sourcePositions=new List<GlassShapeFloat3>();
        foreach(int type in new[]{118,313,314})
        {
            var b=File.ReadAllBytes(Path.Combine(capture,type+"-cpu.bin"));bytes[type]=b;
            int actualVertices=int.Parse(catalog[type][3]);for(int i=0;i<actualVertices;i++)sourcePositions.Add(Read3(b,i*124));
        }
        Check(GlassShapeContactCanonicalizer.TryCreate(sourcePositions,out var policy),"private source-only grouping policy");
        var sources=new Dictionary<int,GlassMixedCapSource>();
        foreach(int type in new[]{118,313,314})
        {
            var b=bytes[type];int actualVertices=int.Parse(catalog[type][3]),paddedVertices=int.Parse(catalog[type][4]);
            Check(GlassShapeGrouping.TryCreate(Enumerable.Range(0,actualVertices).Select(i=>Read3(b,i*124)).ToArray(),actualVertices/3,paddedVertices/3,type!=314,policy,out var grouping,out var reason),"private actual source grouping: "+reason);
            var actualMaterials=new Dictionary<int,GlassMixedCapMaterial>();
            foreach(int material in grouping.GroupTriangles.SelectMany(g=>g).SelectMany(t=>Enumerable.Range(t*3,3)).Select(v=>BitConverter.ToInt32(b,v*124+60)).Distinct())
            {
                var rows=materialRows.Where(c=>int.Parse(c[0])==type&&int.Parse(c[2])==material).ToArray();Check(rows.Length==1,"private actual material has one captured definition");var c=rows[0];
                Check(c[3]=="transparent"&&c[4]=="none"&&c[5]=="-1"&&c[6]=="-1"&&c[7]=="183"&&c[9]=="free"&&c[12]=="off"&&c[13]=="off","private captured actual style fields");
                // CSV omits specularIndex/pixelMode. These are expected guard
                // inputs, not verification of actual native material fields.
                actualMaterials[material]=new GlassMixedCapMaterial(material,new(0,0,2,float.Parse(c[8]),float.Parse(c[10]),float.Parse(c[11])),183,154,-1,0,0);
            }
            Check(GlassMixedCapCatalog.TryCreate(type,b,actualVertices/3,paddedVertices/3,grouping.GroupTriangles,actualMaterials,out var source),$"private self-hash admission {type}");sources[type]=source;
            var nominal=Build(source);
            foreach(var map in nominal.Groups)foreach(int nativeIndex in map.NativeTriangleIndices)
            {
                int ordinal=map.NativeTriangleIndices.ToList().IndexOf(nativeIndex);var proxy=nominal.Brick.Groups.Single(g=>g.Bit==map.Bit).Triangles[ordinal];
                var raw=new GlassPolygonTriangle(ToPoint(Read3(b,nativeIndex*3*124)),ToPoint(Read3(b,(nativeIndex*3+1)*124)),ToPoint(Read3(b,(nativeIndex*3+2)*124)));
                var n=Normal(raw);var q=Normal(proxy);Check(n.X*q.X+n.Y*q.Y+n.Z*q.Z>0,"frozen native/proxy source winding correspondence");
            }
            var mutated=(byte[])b.Clone();mutated[0]^=1;Check(!GlassMixedCapCatalog.TryCreate(type,mutated,actualVertices/3,paddedVertices/3,grouping.GroupTriangles,actualMaterials,out _),"changed actual byte revokes source admission");
        }
        var rows32=Enumerable.Range(0,64).Select(_=>new float[16]).ToArray();
        foreach(var c in File.ReadLines(Path.Combine(capture,"rotations.csv")).Skip(1).Select(s=>s.Split(',')))rows32[int.Parse(c[0])][int.Parse(c[1])*4+int.Parse(c[2])]=float.Parse(c[3]);
        var rotations=rows32.Take(32).Select(r=>new GlassShapeRotation(r)).ToArray();foreach(var r in rotations)
        {
            Check(GlassShapeContactModel.TryCanonicalRotation(r,out _),"all32 captured rotations are proper quarter turns");
            foreach(var source in sources.Values)foreach(float unit in new[]{.125f,.25f,.5f,1,2,4,8,16})
                Build(source,unit:unit,rotation:r,placement:new(13d/32,-7d/32,23d/32));
        }
        using var json=JsonDocument.Parse(File.ReadAllText(scenePath));var scene=new GlassMixedCapScene();var instances=new Dictionary<int,GlassMixedCapInstance>();int rejectedResize=0;
        foreach(var bd in json.RootElement[8][0].EnumerateArray())
        {
            int type=bd[0].GetInt32(),id=bd[7].GetInt32();if(!sources.TryGetValue(type,out var source)||id==0)continue;
            int scale=bd[2].GetInt32();float unit=(float)Math.Pow(2,bd[8].GetInt32())*.125f;
            var position=new GlassPolygonPoint(bd[1][0].GetInt32()/32d,bd[1][1].GetInt32()/32d,bd[1][2].GetInt32()/32d);
            bool admitted=GlassMixedCapCatalog.TryBuild(source,1,id,(byte)bd[6].GetInt32(),scale&15,(scale>>4)&15,(scale>>8)&15,unit,rotations[bd[3].GetInt32()&31],position,out var instance);
            if(scale!=0){Check(!admitted,"current resized owner excluded by zero-count domain");rejectedResize++;continue;}
            Check(admitted&&scene.Upsert(instance),"private scene admitted zero-count instance");instances[id]=instance;
        }
        var expected=new Dictionary<int,uint>{{17,11},{18,14},{19,16},{21,10},{23,36},{26,20},{27,7}};
        foreach(var p in expected){Check(scene.GetMask(p.Key)==p.Value,$"actual scene full mask {p.Key}: expected{p.Value} got{scene.GetMask(p.Key)}");Console.WriteLine($"CAPTURE mask build={p.Key} type={instances[p.Key].BrickId} mask={scene.GetMask(p.Key)}");}
        var target=instances[17].Brick.Groups.Single(g=>g.Bit==5);Check(target.Triangles.Sum(Area)==1.5&&(scene.GetMask(17)&32)==0,"actual partial face area1.5 not wholly hidden");
        Check(scene.TryGetCapRemainder(17,5,out var remainder)&&remainder.Changed&&RemainderArea(target,remainder)==.5,"actual17bit5 exposed area0.5 after exact union subtraction");
        var touched=new List<int>();scene.DrainTouched(touched);scene.Remove(19);touched.Clear();scene.DrainTouched(touched);
        Check(touched.Contains(17)&&(scene.GetMask(17)&32)==0&&scene.TryGetCapRemainder(17,5,out var reopened)&&!reopened.Changed&&RemainderArea(target,reopened)==1.5,"actual zero-mask partial pattern invalidates and reopens on neighbour removal");
        scene.Upsert(instances[19]);touched.Clear();scene.DrainTouched(touched);
        Check(touched.Contains(17)&&(scene.GetMask(17)&32)==0&&scene.TryGetCapRemainder(17,5,out var restored)&&restored.Changed&&RemainderArea(target,restored)==.5,"actual partial pattern restored on neighbour re-add while whole-face bit remains0");
        Check(scene.BudgetFailures==0,"actual scene within exact polygon work bounds");
        Console.WriteLine($"CAPTURE zero-count owners={instances.Count} rejected resized={rejectedResize} candidateChecks={scene.CandidateChecks};17bit5 area1.5, shared1, exposed0.5");
        Console.WriteLine("CAPTURE STYLE LIMIT: specularIndex154 and pixelMode0 are expected guard values; CSV omits them. Native style binding proof is required.");
    }
    static void RetiredDeltaLifetime()
    {
        // Full-mask deltas are not the scene's publication contract. Its touched
        // queue is drained, so hidden retired IDs must not survive in the model.
        var scene=new GlassMixedCapScene(3);var source=Source(314);var touched=new List<int>();
        var opposite=new GlassShapeRotation(new float[]{0,0,1,0,0,-1,0,0,1,0,0,0,0,0,0,1});
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var model=(GlassPolygonCoverageModel)typeof(GlassMixedCapScene).GetField("model",flags)!.GetValue(scene)!;
        var changes=(System.Collections.IDictionary)typeof(GlassPolygonCoverageModel).GetField("changes",flags)!.GetValue(model)!;
        for(int cycle=0;cycle<25;cycle++)
        {
            int first=1000+cycle*3;
            Check(scene.Upsert(Build(source,first))&&scene.Upsert(Build(source,first+1,rotation:opposite))&&scene.Upsert(Build(source,first+2)),"bounded three-owner lifetime inserted");
            Check(scene.GetMask(first)==2&&scene.GetMask(first+1)==2&&scene.GetMask(first+2)==2,"lifetime control has actual nonzero full masks");
            scene.DrainTouched(touched);touched.Clear();
            scene.Remove(first);scene.Remove(first+1);scene.Remove(first+2);scene.DrainTouched(touched);touched.Clear();
            Check(scene.OwnerCount==0&&changes.Count==0,$"retired native build IDs must not accumulate hidden model deltas: cycle{cycle} retained{changes.Count}");
        }
    }
    static GlassShapeFloat3 Read3(byte[] b,int p)=>new(BitConverter.ToSingle(b,p),BitConverter.ToSingle(b,p+4),BitConverter.ToSingle(b,p+8));
    static GlassPolygonPoint ToPoint(GlassShapeFloat3 p)=>new(p.X,p.Y,p.Z);
    static GlassPolygonPoint Normal(GlassPolygonTriangle t)
    {double x=t.B.X-t.A.X,y=t.B.Y-t.A.Y,z=t.B.Z-t.A.Z,u=t.C.X-t.A.X,v=t.C.Y-t.A.Y,w=t.C.Z-t.A.Z;return new(y*w-z*v,z*u-x*w,x*v-y*u);}
    static double RemainderArea(GlassPolygonFaceGroup group,GlassCapClipResult result)
    {
        double sum=0;foreach(var r in result.Triangles){var src=group.Triangles[r.TriangleIndex];foreach(var t in r.Triangles)sum+=Area(new(Weighted(src,t.A),Weighted(src,t.B),Weighted(src,t.C)));}return sum;
    }
    static GlassPolygonPoint Weighted(GlassPolygonTriangle t,GlassBarycentric b)
    {
        double a=(double)b.A/(double)b.D,c=(double)b.B/(double)b.D,d=(double)b.C/(double)b.D;
        return new(a*t.A.X+c*t.B.X+d*t.C.X,a*t.A.Y+c*t.B.Y+d*t.C.Y,a*t.A.Z+c*t.B.Z+d*t.C.Z);
    }
    static double Area(GlassPolygonTriangle t){double x=t.B.X-t.A.X,y=t.B.Y-t.A.Y,z=t.B.Z-t.A.Z,u=t.C.X-t.A.X,v=t.C.Y-t.A.Y,w=t.C.Z-t.A.Z;return .5*Math.Sqrt(Math.Pow(y*w-z*v,2)+Math.Pow(z*u-x*w,2)+Math.Pow(x*v-y*u,2));}
    static IEnumerable<GlassShapeRotation> Rotations()
    {
        int[][] permutations={new[]{0,1,2},new[]{0,2,1},new[]{1,0,2},new[]{1,2,0},new[]{2,0,1},new[]{2,1,0}};
        foreach(var p in permutations)foreach(int x in new[]{-1,1})foreach(int y in new[]{-1,1})foreach(int z in new[]{-1,1})
        {
            var m=new float[16];m[p[0]]=x;m[4+p[1]]=y;m[8+p[2]]=z;m[15]=1;var r=new GlassShapeRotation(m);
            if(GlassShapeContactModel.TryCanonicalRotation(r,out _))yield return r;
        }
    }
}
