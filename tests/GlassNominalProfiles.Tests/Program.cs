using GraphicalTweaks;

static class Program
{
    static int checks;
    static readonly Dictionary<int, string> Hashes = new()
    {
        [118] = "06AD79D6C5B28D3151AA37B81CBE8965D5B0B04F7FEB92D67DFA331E941FF6AB",
        [317] = "5625F268A5EDE1F4C0648F77F73B040E207FDD0ED98BE17B109E5242F70D4736",
#if SEVO_UNITY6
        [209] = "C6F42D354DC0445F8904021393FDDE923C0740B39E82A953C1CC32B61086D9F8"
#else
        [209] = "4367C7DE21165DB4395E56C82FEB6A4B805B2D6B9191AA1C95B8D6797AA89D0A"
#endif
    };
    static void Check(bool result, string message) { checks++; if (!result) throw new Exception(message); }
    static int[][] Groups(int id) => id == 118 ? new[] { new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 } } :
        id == 317 ? new[] { new[] { 4, 5, 8, 9 }, new[] { 6, 7 }, new[] { 10, 11 } } :
        new[] { Enumerable.Range(0, 10).ToArray(), Enumerable.Range(10, 10).ToArray(), Enumerable.Range(30, 10).ToArray() };
    static Dictionary<int, GlassShapeDeformationMaterial> Materials(int id)
    {
        var result = new Dictionary<int, GlassShapeDeformationMaterial>();
        foreach (int material in id == 118 ? new[] { 271 } : id == 317 ? new[] { 701 } : new[] { 511 })
            result.Add(material, new(id == 209 ? 8 : 0, 0, 2, 1, id == 209 ? 0 : -.001f, id == 209 ? 0 : .001f));
        return result;
    }
    static bool Admit(int id, string hash, int[][] groups, IReadOnlyDictionary<int, GlassShapeDeformationMaterial> materials,
        out GlassNominalProfileAdmission token) => GlassNominalCapProfiles.TryAdmit(id, hash, groups, materials, out token);
    static readonly GlassShapeRotation Identity = R(1,0,0,0,1,0,0,0,1);
    static readonly GlassShapeRotation Y90 = R(0,0,1,0,1,0,-1,0,0);
    static readonly GlassShapeRotation Y180 = R(-1,0,0,0,1,0,0,0,-1);
    static readonly GlassShapeRotation XMinus90 = R(1,0,0,0,0,1,0,-1,0);
    static GlassShapeRotation R(params float[] m) => new(new[] { m[0],m[1],m[2],0,m[3],m[4],m[5],0,m[6],m[7],m[8],0,0,0,0,1 });
    static GlassNominalProfileAdmission Token(int id)
    { Check(Admit(id, Hashes[id], Groups(id), Materials(id), out var token), "source token"); return token; }
    static GlassNominalCap[] Build(GlassNominalProfileAdmission token, int[] count, float unit, GlassShapeRotation rotation, GlassPolygonPoint position)
    {
        Check(GlassNominalCapProfiles.TryBuild(token,count[0],count[1],count[2],unit,rotation,position,out var caps), "audited factory build");
        return caps;
    }
    static GlassPolygonPoint Point(double x,double y,double z) => new(x,y,z);
    static GlassPolygonPoint Add(GlassPolygonPoint a,GlassPolygonPoint b) => Point(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    static GlassPolygonPoint Sub(GlassPolygonPoint a,GlassPolygonPoint b) => Point(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
    static GlassPolygonPoint Apply(GlassShapeRotation r,GlassPolygonPoint p) => Point(r.M00*p.X+r.M01*p.Y+r.M02*p.Z,r.M10*p.X+r.M11*p.Y+r.M12*p.Z,r.M20*p.X+r.M21*p.Y+r.M22*p.Z);
    static GlassShapeRotation Compose(GlassShapeRotation a,GlassShapeRotation b)
    {
        var x=Apply(a,Point(b.M00,b.M10,b.M20));var y=Apply(a,Point(b.M01,b.M11,b.M21));var z=Apply(a,Point(b.M02,b.M12,b.M22));
        return R((float)x.X,(float)y.X,(float)z.X,(float)x.Y,(float)y.Y,(float)z.Y,(float)x.Z,(float)y.Z,(float)z.Z);
    }
    static bool Same(GlassPolygonPoint a,GlassPolygonPoint b) => a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;
    static void Contains(GlassNominalCap cap,params GlassPolygonPoint[] points)
    {
        Check(cap.Boundary.Count==points.Length,"independent expected boundary cardinality");
        foreach(var p in points)Check(cap.Boundary.Any(q=>Same(p,q)),"independent expected control");
    }
    static double Winding(GlassNominalCap cap)
    {
        double result=0;var o=cap.Boundary[0];
        for(int i=0;i<cap.Boundary.Count;i++)
        {var a=Sub(cap.Boundary[i],o);var b=Sub(cap.Boundary[(i+1)%cap.Boundary.Count],o);result+=(a.Y*b.Z-a.Z*b.Y)*cap.Normal.X+(a.Z*b.X-a.X*b.Z)*cap.Normal.Y+(a.X*b.Y-a.Y*b.X)*cap.Normal.Z;}
        return result;
    }
    static void Admission()
    {
        foreach (int id in Hashes.Keys)
        {
            var groups = Groups(id); var materials = Materials(id);
            Check(Admit(id, Hashes[id], groups, materials, out var token) && token != null && token.BrickId == id, "audited source admits " + id);
            Check(Admit(id, Hashes[id].ToLowerInvariant(), groups, materials, out _), "hex case is not a changed source");
            Check(!Admit(id, (Hashes[id][0] == '0' ? "1" : "0") + Hashes[id].Substring(1), groups, materials, out _), "changed source hash");
            Check(!Admit(id, null, groups, materials, out _), "missing source hash");
            for (int bit = 0; bit < groups.Length; bit++)
            {
                var changed = Groups(id); changed[bit][0]++;
                Check(!Admit(id, Hashes[id], changed, materials, out _), "changed group member");
                changed = Groups(id); Array.Reverse(changed[bit]);
                Check(!Admit(id, Hashes[id], changed, materials, out _), "changed source group correspondence/order");
            }
            Check(!Admit(id, Hashes[id], null, materials, out _), "missing group table");
            Check(!Admit(id, Hashes[id], groups.Take(2).ToArray(), materials, out _), "truncated group table");
            var nullGroup=Groups(id);nullGroup[0]=null;
            Check(!Admit(id, Hashes[id], nullGroup, materials, out _), "missing group row");
            Check(!Admit(id, Hashes[id], groups, null, out _), "missing materials");
            foreach (int material in materials.Keys.ToArray())
            {
                var missing = Materials(id); missing.Remove(material);
                Check(!Admit(id, Hashes[id], groups, missing, out _), "missing used cap material");
                var m = materials[material];
                var mutations = new[]
                {
                    new GlassShapeDeformationMaterial(m.ResizeMode == 0 ? 7 : 7, m.VertexMode, m.AlphaMode, m.ResizeUnit, m.GeometryLow, m.GeometryHigh),
                    new GlassShapeDeformationMaterial(m.ResizeMode, 1, m.AlphaMode, m.ResizeUnit, m.GeometryLow, m.GeometryHigh),
                    new GlassShapeDeformationMaterial(m.ResizeMode, m.VertexMode, 0, m.ResizeUnit, m.GeometryLow, m.GeometryHigh),
                    new GlassShapeDeformationMaterial(m.ResizeMode, m.VertexMode, m.AlphaMode, 2, m.GeometryLow, m.GeometryHigh),
                    new GlassShapeDeformationMaterial(m.ResizeMode, m.VertexMode, m.AlphaMode, m.ResizeUnit, m.GeometryLow - .01f, m.GeometryHigh),
                    new GlassShapeDeformationMaterial(m.ResizeMode, m.VertexMode, m.AlphaMode, m.ResizeUnit, m.GeometryLow, m.GeometryHigh + .01f),
                    new GlassShapeDeformationMaterial(m.ResizeMode, m.VertexMode, m.AlphaMode, m.ResizeUnit, m.GeometryLow, m.GeometryHigh, 0),
                    new GlassShapeDeformationMaterial(m.ResizeMode, m.VertexMode, m.AlphaMode, m.ResizeUnit, m.GeometryLow, m.GeometryHigh, -1, 1),
                    new GlassShapeDeformationMaterial(m.ResizeMode, m.VertexMode, m.AlphaMode, float.NaN, m.GeometryLow, m.GeometryHigh)
                };
                foreach (var change in mutations)
                {
                    var changed = Materials(id); changed[material] = change;
                    Check(!Admit(id, Hashes[id], groups, changed, out _), "changed deformation material field");
                }
            }
            groups[0][0] = -99; materials.Clear();
            Check(token.BrickId == id, "admission retains no caller arrays/dictionaries");
        }
        Check(!Admit(166, Hashes[118], Groups(118), Materials(118), out _), "unreviewed shape");
        var constructors=typeof(GlassNominalProfileAdmission).GetConstructors(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
        Check(constructors.Length==1&&constructors[0].IsPrivate&&typeof(GlassNominalProfileAdmission).IsSealed,"token construction is guarded/private");
    }
    static void Coordinates()
    {
        var a=Build(Token(118),new[]{2,3,4},.5f,Identity,Point(1,2,3));
        Contains(a[2],Point(.25,4.5,2.75),Point(.25,4,2.75),Point(1.75,2,2.75),Point(2.25,2,2.75));
        Check(a[2].CompatibleGroupBits==7&&a[2].ProfileFamily==1&&Same(a[2].Normal,Point(0,0,-1)),"118 band policy");
        var l=Build(Token(317),new[]{2,3,4},1,Identity,default)[0];
        Contains(l,Point(-.5,0,4.5),Point(2.5,0,4.5),Point(2.5,0,-.5),Point(3.5,0,-.5),Point(3.5,0,5.5),Point(-.5,0,5.5));
        Check(l.ProfileFamily==2&&l.CompatibleGroupBits==1,"317 L distinct from band");
        var arc=Build(Token(209),new[]{2,0,15},1,Identity,default);
        Check(arc.Length==2&&arc[0].CompatibleGroupBits==2&&arc[1].CompatibleGroupBits==1,"209 emits cross caps only");
        foreach(var p in new[]{Point(-1.5,4,-.5),Point(2.5,0,-.5),Point(2,0,-.5),Point(-1.5,3.5,-.5)})
            Check(arc[1].Boundary.Any(q=>Same(q,p)),"209 independent radius endpoints");
        var other=Build(Token(209),new[]{2,15,0},1,Identity,default);
        for(int bit=0;bit<2;bit++)Check(arc[bit].Boundary.Count==other[bit].Boundary.Count&&arc[bit].Boundary.All(p=>other[bit].Boundary.Any(q=>Same(p,q))),"radiusXYZ uses CountX, not Y/Z");
        foreach(var cap in a.Concat(new[]{l}).Concat(arc))Check(Winding(cap)>0,"boundary winding follows outward normal");
    }
    static void InvalidInputs()
    {
        var token=Token(118);var zero=new[]{0,0,0};
        Check(!GlassNominalCapProfiles.TryBuild(null,0,0,0,1,Identity,default,out _),"missing token");
        foreach(var count in new[]{new[]{-1,0,0},new[]{16,0,0},new[]{0,-1,0},new[]{0,16,0},new[]{0,0,-1},new[]{0,0,16}})
            Check(!GlassNominalCapProfiles.TryBuild(token,count[0],count[1],count[2],1,Identity,default,out _),"invalid packed count");
        foreach(float unit in new[]{0f,-1,.0625f,2,float.NaN,float.PositiveInfinity})
            Check(!GlassNominalCapProfiles.TryBuild(token,0,0,0,unit,Identity,default,out _),"unsupported unit");
        foreach(var point in new[]{Point(1d/64,0,0),Point(0,1d/64,0),Point(0,0,1d/64),Point(double.NaN,0,0),Point(0,double.PositiveInfinity,0),Point(1048577,0,0),Point(1048576,0,0)})
            Check(!GlassNominalCapProfiles.TryBuild(token,15,15,15,1,Identity,point,out _),"invalid placement or boundary beyond bound");
        foreach(var r in new[]{default(GlassShapeRotation),R(-1,0,0,0,1,0,0,0,1),R(1,.01f,0,0,1,0,0,0,1),R(float.NaN,0,0,0,1,0,0,0,1)})
            Check(!GlassNominalCapProfiles.TryBuild(token,0,0,0,1,r,default,out _),"unsupported rotation");
        var close=R(.99999994f,0,0,0,1,0,0,0,1);
        Check(GlassNominalCapProfiles.TryBuild(token,0,0,0,1,close,default,out var caps),"audited native quarter-turn float noise accepted");
        Check(caps[0].Boundary.All(p=>p.X== -1.5),"canonical rotation leaves exact cap plane");
        var groups=Groups(118);var mats=Materials(118);Check(Admit(118,Hashes[118],groups,mats,out var retained),"immutable token admission");
        groups[0][0]=-100;mats.Clear();var built=Build(retained,zero,1,Identity,default);
        Check(built.Length==3,"caller mutation cannot change an admitted token");
    }
    static List<GlassShapeRotation> Rotations()
    {
        var result=new List<GlassShapeRotation>();
        foreach(var p in new[]{new[]{0,1,2},new[]{0,2,1},new[]{1,0,2},new[]{1,2,0},new[]{2,0,1},new[]{2,1,0}})
        foreach(int a in new[]{-1,1})foreach(int b in new[]{-1,1})foreach(int c in new[]{-1,1})
        {
            var m=new float[9];m[p[0]]=a;m[3+p[1]]=b;m[6+p[2]]=c;
            float det=m[0]*(m[4]*m[8]-m[5]*m[7])-m[1]*(m[3]*m[8]-m[5]*m[6])+m[2]*(m[3]*m[7]-m[4]*m[6]);
            if(det==1)result.Add(R(m));
        }
        Check(result.Count==24,"all proper cardinal orientations");return result;
    }
    static void AssertPair(GlassNominalCap[] a,GlassNominalCap[] b,uint expectedA,uint expectedB)
    {
        var model=new GlassNominalCapModel();
        Check(model.Upsert(1,1,255,a)&&model.Upsert(1,2,255,b),"pair admitted");
        Check(model.GetMask(1)==expectedA&&model.GetMask(2)==expectedB,"independent expected joining masks");
    }
    static void ActualPairs()
    {
        AssertPair(Build(Token(118),new[]{2,2,2},.5f,Identity,Point(-.75,1.5,2.75)),Build(Token(118),new[]{2,2,2},.5f,XMinus90,Point(-.75,1.75,2.5)),4,2);
        AssertPair(Build(Token(317),new[]{0,0,0},1,Y90,Point(-5,1.5,1)),Build(Token(317),new[]{0,0,0},1,Y180,Point(-6,1.5,1)),4,2);
        AssertPair(Build(Token(209),new[]{0,0,0},1,Y90,Point(-12,1.5,0)),Build(Token(209),new[]{0,0,0},1,Y180,Point(-14,1.5,1)),2,1);
    }
    static void Sweep()
    {
        int pairs=0;
        foreach(int id in new[]{118,317,209})
        {
            var token=Token(id);var baseA=id==118?Identity:Y90;var baseB=id==118?XMinus90:Y180;
            var anchor=id==317?Point(-.5,0,-.5):Point(-1.5,0,-.5);
            var delta=Sub(Apply(baseA,anchor),Apply(baseB,anchor));
            foreach(var outer in Rotations())foreach(float unit in new[]{.125f,.25f,.5f,1f})foreach(int n in Enumerable.Range(0,16))
            {
                var count=new[]{n,n,n};var pb=Apply(outer,Point(delta.X*unit,delta.Y*unit,delta.Z*unit));
                var a=Build(token,count,unit,Compose(outer,baseA),default);var b=Build(token,count,unit,Compose(outer,baseB),pb);
                AssertPair(a,b,id==209?2u:4u,id==209?1u:2u);pairs++;
                foreach(var cap in a.Concat(b))Check(Winding(cap)>0,"rotated cap winding");
                if(n!=0&&n!=15)continue;
                for(int axis=0;axis<3;axis++)
                {
                    var p=new[]{pb.X,pb.Y,pb.Z};p[axis]+=1d/32;
                    AssertPair(a,Build(token,count,unit,Compose(outer,baseB),Point(p[0],p[1],p[2])),0,0);
                }
                var changed=new[]{n==15?14:n+1,n==15?14:n+1,n==15?14:n+1};
                AssertPair(a,Build(token,changed,unit,Compose(outer,baseB),pb),0,0);
            }
        }
        Check(pairs==4608,"full rotation/uniform resize/unit sweep");
    }
    static void AsymmetricSweep()
    {
        int pairs=0;
        foreach(int id in new[]{118,317,209})
        {
            var token=Token(id);var baseA=id==118?Identity:Y90;var baseB=id==118?XMinus90:Y180;
            var anchor=id==317?Point(-.5,0,-.5):Point(-1.5,0,-.5);
            var delta=Sub(Apply(baseA,anchor),Apply(baseB,anchor));
            foreach(var outer in Rotations())foreach(float unit in new[]{.125f,.25f,.5f,1f})
            foreach(var uv in new[]{new[]{0,1},new[]{1,0},new[]{1,2},new[]{15,0},new[]{0,15},new[]{7,15},new[]{15,7}})
            {
                int u=uv[0],v=uv[1];
                var ca=id==118?new[]{u,v,15}:id==317?new[]{u,v,0}:new[]{u,0,15};
                var cb=id==118?new[]{u,0,v}:id==317?new[]{15,v,u}:new[]{u,15,0};
                var pb=Apply(outer,Point(delta.X*unit,delta.Y*unit,delta.Z*unit));
                AssertPair(Build(token,ca,unit,Compose(outer,baseA),default),Build(token,cb,unit,Compose(outer,baseB),pb),id==209?2u:4u,id==209?1u:2u);pairs++;
            }
        }
        Check(pairs==2016,"asymmetric count-axis and CountX-only pair sweep");
    }
    static void SameCapAnnulusIsExcluded()
    {
        var token=Token(209);var count=new[]{15,0,0};var a=Build(token,count,1,Identity,default);
        var anchor=Point(-1.5,0,-.5);
        foreach(var setup in new[]{(bit:0,rotation:R(-1,0,0,0,0,1,0,1,0)),(bit:1,rotation:R(0,1,0,1,0,0,0,0,-1))})
        {
            var pb=Sub(anchor,Apply(setup.rotation,anchor));var b=Build(token,count,1,setup.rotation,pb);
            AssertPair(new[]{a[setup.bit]},new[]{b[setup.bit]},0,0);
            // Independent control: equal geometry/opposite normals WOULD match
            // if the explicit cross-cap compatibility restriction were absent.
            Check(GlassNominalCap.TryCreate(setup.bit,209,3,255,a[setup.bit].Normal,a[setup.bit].Boundary.ToArray(),out var broadA),"broad same-cap control A");
            Check(GlassNominalCap.TryCreate(setup.bit,209,3,255,b[setup.bit].Normal,b[setup.bit].Boundary.ToArray(),out var broadB),"broad same-cap control B");
            AssertPair(new[]{broadA},new[]{broadB},1u<<setup.bit,1u<<setup.bit);
        }
    }
    static void UsedCapMaterialAdmission()
    {
        // Actual selected 124-byte cap records use271 for118,701 for317,
        // and511 for209. Other declared asset materials are not cap inputs.
        foreach(var pair in new[]{(type:118,material:271),(type:317,material:701),(type:209,material:511)})
        {
            var actual=new Dictionary<int,GlassShapeDeformationMaterial>
            {
                [pair.material]=new(pair.type==209?8:0,0,2,1,pair.type==209?0:-.001f,pair.type==209?0:.001f)
            };
            Check(Admit(pair.type,Hashes[pair.type],Groups(pair.type),actual,out var token)&&token.BrickId==pair.type,
                "actual used-cap-only material dictionary must admit "+pair.type);
            Check(!Admit(pair.type,Hashes[pair.type],Groups(pair.type),new Dictionary<int,GlassShapeDeformationMaterial>(),out _),
                "missing actual cap material must reject "+pair.type);
            if(pair.type==209)continue;
            int unused=pair.type==118?270:702;
            var unusedOnly=new Dictionary<int,GlassShapeDeformationMaterial>{[unused]=new(0,0,2,1,-.001f,.001f)};
            Check(!Admit(pair.type,Hashes[pair.type],Groups(pair.type),unusedOnly,out _),"declared unused material cannot replace cap material");
            actual[unused]=default;
            Check(Admit(pair.type,Hashes[pair.type],Groups(pair.type),actual,out _),"unused/body material mismatch must not deny valid cap inputs");
        }
    }
    static void Main()
    {
        UsedCapMaterialAdmission();
        Admission();
        foreach (int id in Hashes.Keys)
        {
            Check(Admit(id, Hashes[id], Groups(id), Materials(id), out var token), "build admission");
            var identity = new GlassShapeRotation(new float[] { 1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1 });
            Check(GlassNominalCapProfiles.TryBuild(token, 0,0,0,1,identity,default,out var caps) &&
                caps.Length == (id == 209 ? 2 : 3), "audited caps build " + id);
        }
        Coordinates(); InvalidInputs(); ActualPairs(); Sweep(); AsymmetricSweep(); SameCapAnnulusIsExcluded();
        Console.WriteLine($"PASS {checks} nominal profile factory checks; native acceptance unproven.");
    }
}
