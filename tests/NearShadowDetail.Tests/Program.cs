using System;
using GraphicalTweaks;
static class Program
{
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
    static bool Close(float a,float b)=>Math.Abs(a-b)<.000001f;
    static void Main()
    {
        var m=new NearShadowDetailModel();var baseline=new NearShadowDetailSplits(.1f,.067f,.2f,.467f);
        Check(m.Choice==0&&m.RequestedNearRange==null,"game default has no requested range");
        Check(!m.TryCalculate(2,1500,baseline,out _),"game default never requests an override");
        Check(m.TrySet(1)&&m.RequestedNearRange==15,"15-unit choice accepted");
        Check(m.TryCalculate(2,1500,baseline,out var target)&&Close(target.Two,.01f),"two cascades place first split at15 of1500");
        Check(target.FourX==baseline.FourX&&target.FourY==baseline.FourY&&target.FourZ==baseline.FourZ,"two mode preserves four-cascade state");
        Check(m.TryCalculate(2,100000,baseline,out target)&&target.Two==.005f,"very long range clamps to minimum fraction");
        Check(m.TryCalculate(2,1,baseline,out target)&&target.Two==.95f,"short total range clamps to upper fraction");
        Check(m.TrySet(4)&&m.RequestedNearRange==100,"100-unit choice accepted");
        Check(m.TryCalculate(4,100,baseline,out target)&&Close(target.FourX,.195f),"four mode leaves spacing before native second split");
        Check(target.Two==baseline.Two&&target.FourY==baseline.FourY&&target.FourZ==baseline.FourZ,"four mode preserves other fractions exactly");
        Check(!m.TryCalculate(1,1500,baseline,out _)&&!m.TryCalculate(0,1500,baseline,out _)&&!m.TryCalculate(3,1500,baseline,out _),"unsupported cascade counts retain native");
        Check(!m.TryCalculate(2,0,baseline,out _)&&!m.TryCalculate(2,-1,baseline,out _)&&!m.TryCalculate(2,float.NaN,baseline,out _)&&!m.TryCalculate(2,float.PositiveInfinity,baseline,out _),"invalid total ranges retain native");
        Check(!m.TryCalculate(4,1500,new NearShadowDetailSplits(.1f,.1f,.009f,.4f),out _),"impossible four-split spacing retains native");
        Check(!m.TryCalculate(4,1500,new NearShadowDetailSplits(.1f,.1f,.5f,.4f),out _)&&!m.TryCalculate(4,1500,new NearShadowDetailSplits(.1f,.1f,float.NaN,.9f),out _),"invalid four-split ordering retains native");
        Check(!m.TrySet(-1)&&!m.TrySet(5)&&m.Choice==4,"invalid choice preserves current selection");
        Check(m.TrySet(2)&&m.RequestedNearRange==30&&m.TrySet(3)&&m.RequestedNearRange==60,"middle choices map to requested distances");
        Check(NearShadowDetailModel.NormalizeSaved(99)==0&&NearShadowDetailModel.NormalizeSaved(3)==3,"saved choices normalize safely");
        m.Reset();Check(m.Choice==0&&m.RequestedNearRange==null,"reset returns game default");
        m.TrySet(1);var owner=new NearShadowDetailOwnership();
        Check(owner.Evaluate(m,2,1500,baseline,out var written)&&Close(written.Two,.01f)&&owner.HasOverride,"override captures and writes first fraction");
        Check(owner.Baseline.Same(baseline),"both native split fields captured exactly");
        Check(!owner.Evaluate(m,2,1500,written,out var repeated)&&repeated.Same(written),"own writes do not replace baseline or repeat setters");
        Check(owner.Evaluate(m,2,3000,written,out written)&&Close(written.Two,.005f),"distance changes recalculate without changing total range");
        var external=new NearShadowDetailSplits(.23f,written.FourX,written.FourY,written.FourZ);
        Check(owner.Evaluate(m,2,1500,external,out written)&&Close(written.Two,.01f)&&owner.Baseline.Two==.23f,"external two-cascade write becomes restoration baseline");
        m.Reset();Check(owner.Evaluate(m,2,1500,written,out written)&&written.Two==.23f&&!owner.HasOverride,"default restores latest external baseline");
        var zero=new NearShadowDetailSplits(0,0,.3f,.6f);m.TrySet(2);
        owner.Evaluate(m,2,1500,zero,out written);owner.Restore(written,out written);
        Check(written.Same(zero),"disable restores exact zero baseline");
        var negativeZero=new NearShadowDetailSplits(BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),0,.3f,.6f);
        owner.Evaluate(m,2,1500,negativeZero,out written);owner.Restore(written,out written);
        Check(written.Same(negativeZero),"signed zero baseline restored bit-exactly");
        m.TrySet(1);owner.Evaluate(m,4,1500,baseline,out written);
        external=new NearShadowDetailSplits(.22f,written.FourX,.4f,.8f);
        owner.Evaluate(m,4,1500,external,out written);owner.Restore(written,out written);
        Check(written.Two==.22f&&written.FourX==baseline.FourX&&written.FourY==.4f&&written.FourZ==.8f,"external Y/Z changes preserve original owned X baseline");
        owner.Evaluate(m,4,1500,baseline,out written);
        external=new NearShadowDetailSplits(written.Two,.12f,written.FourY,written.FourZ);
        owner.Evaluate(m,4,1500,external,out written);owner.Restore(written,out written);
        Check(written.FourX==.12f,"external four-cascade X becomes restoration baseline");
        owner.Evaluate(m,2,1500,baseline,out written);owner.Evaluate(m,4,1500,written,out written);
        Check(written.Two==baseline.Two&&Close(written.FourX,.01f),"cascade switch restores inactive owned fraction");
        owner.Evaluate(m,1,1500,written,out written);
        Check(written.Same(baseline)&&!owner.HasOverride,"single cascade restores override and retains native splits");
        owner.Evaluate(m,2,1500,baseline,out written);owner.Evaluate(m,2,float.NaN,written,out written);
        Check(written.Same(baseline)&&!owner.HasOverride,"invalid distance restores active override");
        Check(!owner.Restore(baseline,out written)&&written.Same(baseline),"restore without ownership leaves native fields untouched");
        owner.Evaluate(m,4,1500,baseline,out written);
        external=new NearShadowDetailSplits(written.Two,written.FourX,.04f,.8f);
        owner.Evaluate(m,4,1500,external,out written);owner.Restore(written,out written);
        Check(written.Same(external),"external ordering change adopts compatible native vector instead of restoring X past Y");
        Check(!m.TryCalculate(4,1500,new NearShadowDetailSplits(.1f,float.NaN,.2f,.5f),out _)&&
              !m.TryCalculate(2,1500,new NearShadowDetailSplits(float.NaN,.1f,.2f,.5f),out _),"nonfinite active native split fails open");
        UnityEngine.QualitySettings.shadowCascades=2;UnityEngine.QualitySettings.shadowDistance=1500;
        UnityEngine.QualitySettings.shadowCascade2Split=.1f;
        UnityEngine.QualitySettings.shadowCascade4Split=new UnityEngine.Vector3(.067f,.2f,.467f);
        UnityEngine.QualitySettings.TwoWrites=UnityEngine.QualitySettings.FourWrites=0;
        var controller=new NearShadowDetailController();
        Check(!controller.Apply()&&UnityEngine.QualitySettings.TwoWrites==0&&UnityEngine.QualitySettings.FourWrites==0,"default controller makes no native writes");
        Check(controller.SetChoice(1)&&UnityEngine.QualitySettings.TwoWrites==1&&UnityEngine.QualitySettings.FourWrites==0&&
            UnityEngine.QualitySettings.shadowDistance==1500&&UnityEngine.QualitySettings.shadowCascades==2,"controller changes only two-cascade first split");
        Check(!controller.Apply()&&!controller.SetChoice(5)&&controller.Choice==1,"unchanged apply and invalid choice make no setter calls");
        controller.Dispose();
        Check(UnityEngine.QualitySettings.shadowCascade2Split==.1f&&!controller.HasOverride&&!controller.Apply()&&!controller.SetChoice(1),"dispose restores and prevents reactivation");
        UnityEngine.QualitySettings.shadowCascades=4;
        UnityEngine.QualitySettings.TwoWrites=UnityEngine.QualitySettings.FourWrites=0;
        controller=new NearShadowDetailController();controller.SetChoice(2);
        Check(UnityEngine.QualitySettings.TwoWrites==0&&UnityEngine.QualitySettings.FourWrites==1&&
            UnityEngine.QualitySettings.shadowCascade4Split.y==.2f&&UnityEngine.QualitySettings.shadowCascade4Split.z==.467f,
            "four-cascade controller preserves Y/Z and inactive two split");
        controller.Restore();controller.Apply();controller.SetChoice(0);
        Check(UnityEngine.QualitySettings.shadowCascade4Split.x==.067f&&controller.Choice==0,"temporary disable and reset both restore native first split");
        controller.Dispose();
        Console.WriteLine("PASS: "+checks+" near-shadow detail checks");
    }
}
