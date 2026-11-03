using System;
using GraphicalTweaks;

static partial class Program
{
    static int checks;
    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static GlassShapeFloat3 V(float x,float y,float z)=>new GlassShapeFloat3(x,y,z);
    static void Main(string[] args)
    {
        Check(GlassShapeContactCanonicalizer.TryCreate(new[]{V(.5f,0,-.50000006f)},out var policy),"bounded captured source policy admitted");
        Check(policy.TryCanonicalize(V(.5f,0,-.50000006f),out var p)&&p.X==.5f&&p.Z==-.5f,"source magnitude noise joins exact half integer");
        Synthetic();
        if(args.Length>0)Live(args[0],args.Length>1&&args[1]=="--joins-only");
        Console.WriteLine($"PASS {checks} contact checks");
    }
}
