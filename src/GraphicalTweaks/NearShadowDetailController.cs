using System;
using System.Globalization;
using UnityEngine;

namespace GraphicalTweaks
{
    // Changes only an existing cascade's first distance fraction. Never changes
    // total shadow distance, resolution, cascade count, lights or cameras.
    internal sealed class NearShadowDetailController : IDisposable
    {
        private readonly NearShadowDetailModel model;
        private readonly NearShadowDetailOwnership ownership=new NearShadowDetailOwnership();
        private bool disposed;
        internal NearShadowDetailController():this(new NearShadowDetailModel()) { }
        internal NearShadowDetailController(NearShadowDetailModel model)
        {this.model=model??throw new ArgumentNullException(nameof(model));}
        internal int Choice => model.Choice;
        internal bool HasOverride => ownership.HasOverride;
        internal NearShadowDetailSplits Baseline => ownership.Baseline;

        // Returns acceptance of the index; Apply reports whether native fields changed.
        internal bool SetChoice(int index)
        {
            if(disposed||!model.TrySet(index))return false;
            Apply();return true;
        }
        internal bool Apply()
        {
            if(disposed)return false;
            var current=Read();
            if(!ownership.Evaluate(model,QualitySettings.shadowCascades,QualitySettings.shadowDistance,current,out var desired))return false;
            Write(current,desired);return true;
        }
        internal bool Restore()
        {
            var current=Read();
            if(!ownership.Restore(current,out var desired))return false;
            Write(current,desired);return true;
        }
        internal string DescribeCurrent()
        {
            var current=Read();int cascades=QualitySettings.shadowCascades;float distance=QualitySettings.shadowDistance;
            string effective=cascades==2?F(current.Two*distance):cascades==4?F(current.FourX*distance):"unsupported";
            return "choice="+Choice+", requestedNear="+(model.RequestedNearRange?.ToString(CultureInfo.InvariantCulture)??"game default")+
                ", cascades="+cascades+", totalDistance="+F(distance)+", firstRange="+effective+
                ", twoSplit="+F(current.Two)+", fourSplits=("+F(current.FourX)+","+F(current.FourY)+","+F(current.FourZ)+"), override="+HasOverride;
        }
        private static NearShadowDetailSplits Read()
        {
            Vector3 four=QualitySettings.shadowCascade4Split;
            return new NearShadowDetailSplits(QualitySettings.shadowCascade2Split,four.x,four.y,four.z);
        }
        private static void Write(NearShadowDetailSplits current,NearShadowDetailSplits desired)
        {
            if(!NearShadowDetailSplits.Same(current.Two,desired.Two))QualitySettings.shadowCascade2Split=desired.Two;
            if(!NearShadowDetailSplits.Same(current.FourX,desired.FourX))
                QualitySettings.shadowCascade4Split=new Vector3(desired.FourX,current.FourY,current.FourZ);
        }
        private static string F(float value)=>value.ToString("G9",CultureInfo.InvariantCulture);
        public void Dispose()
        {if(disposed)return;Restore();disposed=true;}
    }
}
