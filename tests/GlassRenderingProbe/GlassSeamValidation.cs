using System;
using System.Collections;
using System.Globalization;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace SEVO.GlassRenderingProbe
{
    // Development-only same-frame native draw comparison. The caller restores
    // the native MRT/viewport and retires this helper after a later native clear
    // AND Completed. Read never disposes command-buffer referenced targets.
    internal sealed class GlassSeamValidation : IDisposable
    {
        private const long ByteLimit=512L*1024*1024;
        private const string EvidenceRoot=@"D:\Codex\SEVO Modding\Graphical Tweaks\local-evidence";
        private readonly RenderTexture[] targets=new RenderTexture[3];
        private readonly NativeArray<Vector4>[] pixels=new NativeArray<Vector4>[2];
        private readonly AsyncGPUReadbackRequest[] requests=new AsyncGPUReadbackRequest[2];
        private int width,height,requestCount;
        private bool recorded,recordAttempted,reading,disposed,heatmapRequested,completed,abandoned,readbacksFinished;
        internal bool Completed {get=>(completed||abandoned)&&AllDone();private set=>completed=value;}
        internal string Error {get;private set;}
        internal string LastError => Error;
        internal bool WriteHeatmap {get;set;}=true;
        internal bool ExpectIdentical {get;set;}
        internal bool RequireColorCoverage {get;set;}
        internal string HeatmapPath {get;private set;}
        internal long GpuBytes {get;private set;}
        internal long ReadbackBytes {get;private set;}

        // stage0: native original mapping/shape; stage1: native compact mapping/shape.
        // Same original material/pass/transform/args/opaque depth and lighting.
        internal bool Record(CommandBuffer cb,int width,int height,RenderTargetIdentifier opaqueDepth,Action<CommandBuffer,int> draw)
        {
            if(recordAttempted||disposed)return Fail("Seam validation already used or disposed");
            recordAttempted=true;
            heatmapRequested=WriteHeatmap;
            if(cb==null||draw==null)return Fail("Missing seam command buffer or draw callback");
            if(width<=0||height<=0||width>SystemInfo.maxTextureSize||height>SystemInfo.maxTextureSize)
                return Fail("Invalid seam validation dimensions");
            if(!SystemInfo.supportsAsyncGPUReadback||!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                return Fail("ARGBFloat/async readback unsupported");
            long count=(long)width*height;
            GpuBytes=count*16*3;ReadbackBytes=count*16*2;
            // Includes two persistent float4 destinations and a conservative
            // 20 bytes/pixel +1MiB reserve for optional RGBA32/PNG working data.
            long heatmapReserve=heatmapRequested?count*20+1024*1024:0;
            if(count>int.MaxValue||GpuBytes+ReadbackBytes+heatmapReserve>ByteLimit)
                return Fail("Seam validation exceeds its combined 512 MiB payload budget");
            this.width=width;this.height=height;
            try
            {
                for(int i=0;i<targets.Length;i++)
                {
                    targets[i]=new RenderTexture(width,height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear)
                    {name="GT seam validation "+(i==2?"dummy MRT1":"stage "+i),hideFlags=HideFlags.HideAndDontSave,
                     filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp,useMipMap=false,autoGenerateMips=false,antiAliasing=1};
                    if(!targets[i].Create())throw new InvalidOperationException("Seam target allocation failed at "+i);
                }
                for(int stage=0;stage<2;stage++)
                {
                    cb.SetRenderTarget(new[]{new RenderTargetIdentifier(targets[stage]),new RenderTargetIdentifier(targets[2])},opaqueDepth);
                    cb.SetViewport(new Rect(0,0,width,height));
                    cb.ClearRenderTarget(false,true,Color.clear); // Never clear native opaque depth.
                    draw(cb,stage);
                }
                recorded=true;return true;
            }
            catch(Exception error)
            {
                // Partial commands/targets are retained; caller must restore its
                // native targets and retire after native ClearBuffers, even here.
                Error="Seam recording failed: "+error;Completed=true;throw;
            }
        }
        internal IEnumerator Read(Action<string> log,Func<bool> stillValid=null)
        {
            if(log==null)throw new ArgumentNullException(nameof(log));
            if(reading||Completed){log("Seam validation unavailable: already read/completed");yield break;}
            reading=true;
            try
            {
                yield return new WaitForEndOfFrame();
                if(!recorded||disposed||abandoned){log("Seam validation INCONCLUSIVE: "+(Error??"not recorded or canceled/disposed"));yield break;}
                if(stillValid!=null&&!stillValid()){log("Seam validation INCONCLUSIVE: draw generation invalidated before readback");yield break;}
                bool ready=true;
                try
                {
                    for(int i=0;i<2;i++)pixels[i]=new NativeArray<Vector4>(width*height,Allocator.Persistent,NativeArrayOptions.UninitializedMemory);
                    for(int i=0;i<2;i++)
                    {requests[i]=AsyncGPUReadback.RequestIntoNativeArray(ref pixels[i],targets[i],0,TextureFormat.RGBAFloat);requestCount++;}
                }
                catch(Exception error){Error=error.ToString();ready=false;}
                if(!ready){log("Seam validation INCONCLUSIVE: readback setup failed: "+Error);yield break;}
                while(!disposed&&!abandoned&&!AllDone())yield return null;
                if(disposed||abandoned){log("Seam validation canceled during readback");yield break;}
                for(int i=0;i<requestCount;i++)if(requests[i].hasError)
                {log("Seam validation INCONCLUSIVE: GPU readback error at stage "+i);yield break;}
                if(stillValid!=null&&!stillValid()){log("Seam validation INCONCLUSIVE: draw generation invalidated during readback");yield break;}
                Summarize(log);
            }
            finally{Completed=true;}
        }
        private bool AllDone()
        {
            if(readbacksFinished)return true;
            for(int i=0;i<requestCount;i++)if(!requests[i].done)return false;
            readbacksFinished=true;return true;
        }
        private void Summarize(Action<string> log)
        {
            var stats=new GlassSeamPixelStats();
            for(int i=0;i<pixels[0].Length;i++)
            {
                Vector4 full=pixels[0][i],remap=pixels[1][i];
                stats.Add(i%width,i/width,full.x,full.y,full.z,full.w,remap.x,remap.y,remap.z,remap.w);
            }
            log("Seam validation: native original minus native compact; dimensions="+width+"x"+height+
                ", GPU bytes="+GpuBytes+", persistent readback bytes="+ReadbackBytes+", tolerance="+F(GlassSeamPixelStats.Tolerance));
            log("Seam altered pixels: alpha="+stats.AlphaAltered+", RGB="+stats.ColorAltered+", alpha-or-RGB="+stats.Altered+
                ", positive-alpha="+stats.Positive+", negative-alpha="+stats.Negative+", nonfinite="+stats.Nonfinite+", alpha-out-of-range="+stats.AlphaOutOfRange);
            log("Seam alpha difference: signed sum="+F(stats.AlphaSum)+", positive sum="+F(stats.PositiveSum)+
                ", negative magnitude sum="+F(stats.NegativeSum)+", max reduction="+F(stats.MaxPositive)+", max increase="+F(stats.MaxNegative));
            log("Seam RGB difference: total absolute RGB="+F(stats.ColorAbsSum)+", max absolute channel="+F(stats.ColorMaxAbs)+
                "; pixel bounds alpha="+stats.AlphaBounds+", positive="+stats.PositiveBounds+", negative="+stats.NegativeBounds+", RGB="+stats.ColorBounds);
            log("Seam coverage: covered="+stats.Covered+", unchanged covered pixels="+stats.UnchangedCovered+
                ", original-only="+stats.FullOnly+", remap-only="+stats.RemapOnly+". Bounds use GPU readback x/y coordinates; no outer-edge mask is assumed.");
            if(ExpectIdentical)
            {
                var outcome=GlassComparisonOutcome.Identical(stats.Covered,stats.Altered,stats.Nonfinite,stats.AlphaOutOfRange,
                    RequireColorCoverage,stats.Colored);
                log("Uncut interpolation parity "+outcome+": requires at least16 covered pixels and zero RGB/alpha changes above tolerance, nonfinite values or alpha-range failures. This verifies only the captured eligible triangles/frame.");
                if(RequireColorCoverage)log("Sunlight-loss RGB-covered pixels="+stats.Colored+"; at least16 required to rule out an inactive loss capture.");
            }
            else if(stats.Nonfinite!=0||stats.Negative!=0||stats.AlphaOutOfRange!=0)
                log("Seam validation INVALID: nonfinite values, increased opacity or out-of-range alpha; inspect the recorded inputs.");
            else if(stats.Positive==0)
                log("Seam validation INCONCLUSIVE: no positive opacity reduction above tolerance; trivial/unchanged images are not proof.");
            else log("Seam validation MEASURED nontrivial opacity reduction without increased opacity. Unchanged counts alone do not prove preserved pane outer edges.");
            if(heatmapRequested)
            {
                try{SaveHeatmap(stats,log);}
                catch(Exception error){log("Seam heatmap unavailable: "+error);}
            }
        }
        private void SaveHeatmap(GlassSeamPixelStats stats,Action<string> log)
        {
            var colors=new Color32[width*height];
            double scale=Math.Max(GlassSeamPixelStats.Tolerance,Math.Max(stats.MaxPositive,stats.MaxNegative));
            for(int i=0;i<colors.Length;i++)
            {
                Vector4 a=pixels[0][i],b=pixels[1][i];
                if(!GlassSeamPixelStats.Finite(a.x)||!GlassSeamPixelStats.Finite(a.y)||!GlassSeamPixelStats.Finite(a.z)||!GlassSeamPixelStats.Finite(a.w)||
                   !GlassSeamPixelStats.Finite(b.x)||!GlassSeamPixelStats.Finite(b.y)||!GlassSeamPixelStats.Finite(b.z)||!GlassSeamPixelStats.Finite(b.w))
                {colors[i]=new Color32(255,0,255,255);continue;}
                double difference=(double)a.w-b.w;
                byte value=(byte)Math.Min(255,32+223*Math.Sqrt(Math.Abs(difference)/scale));
                if(difference>GlassSeamPixelStats.Tolerance)colors[i]=new Color32(value,0,0,255);
                else if(difference< -GlassSeamPixelStats.Tolerance)colors[i]=new Color32(0,0,value,255);
                else if(Math.Max(Math.Abs(a.x-b.x),Math.Max(Math.Abs(a.y-b.y),Math.Abs(a.z-b.z)))>GlassSeamPixelStats.Tolerance)
                    colors[i]=new Color32(0,80,0,255);
                else colors[i]=new Color32(0,0,0,255);
            }
            Texture2D texture=null;
            try
            {
                texture=new Texture2D(width,height,TextureFormat.RGBA32,false,true){hideFlags=HideFlags.HideAndDontSave};
                texture.SetPixels32(colors);texture.Apply(false,false);
                Directory.CreateDirectory(EvidenceRoot);
                HeatmapPath=Path.Combine(EvidenceRoot,"seam-alpha-difference-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff",CultureInfo.InvariantCulture)+".png");
                File.WriteAllBytes(HeatmapPath,ImageConversion.EncodeToPNG(texture));
                log("Seam technical heatmap: "+HeatmapPath+"; red=opacity reduction, blue=invalid increase, green=RGB-only change, magenta=nonfinite, black=unchanged; sqrt display scale="+F(scale)+". This is owned render-target data, not a UI screenshot.");
            }
            finally{if(texture!=null)UnityEngine.Object.Destroy(texture);}
        }
        private bool Fail(string error){Error=error;Completed=true;return false;}
        // Outer coroutines can be disposed after recording but before Read has
        // started. Mark that path terminal without freeing recorded resources.
        // Retirement still waits for both native clear and any issued readbacks.
        internal void Abandon(){abandoned=true;if(Error==null)Error="comparison abandoned";}
        private static string F(double value)=>value.ToString("G9",CultureInfo.InvariantCulture);
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            for(int i=0;i<requestCount;i++)if(!requests[i].done)requests[i].WaitForCompletion();
            for(int i=0;i<2;i++)if(pixels[i].IsCreated)pixels[i].Dispose();
            foreach(var target in targets)if(target!=null){target.Release();UnityEngine.Object.Destroy(target);}
        }
    }

    // BEGIN PURE STATS
    internal sealed class GlassSeamPixelStats
    {
        internal const double Tolerance=0.00001;
        internal int AlphaAltered,ColorAltered,Altered,Positive,Negative,Nonfinite,AlphaOutOfRange,Covered,Colored,UnchangedCovered,FullOnly,RemapOnly;
        internal double AlphaSum,PositiveSum,NegativeSum,MaxPositive,MaxNegative,ColorAbsSum,ColorMaxAbs;
        internal GlassSeamPixelBounds AlphaBounds,PositiveBounds,NegativeBounds,ColorBounds;
        internal static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
        internal void Add(int x,int y,float ar,float ag,float ab,float aa,float br,float bg,float bb,float ba)
        {
            if(!Finite(ar)||!Finite(ag)||!Finite(ab)||!Finite(aa)||!Finite(br)||!Finite(bg)||!Finite(bb)||!Finite(ba))
            {Nonfinite++;return;}
            double alpha=(double)aa-ba;
            double r=Math.Abs((double)ar-br),g=Math.Abs((double)ag-bg),b=Math.Abs((double)ab-bb);
            double colorMax=Math.Max(r,Math.Max(g,b));
            AlphaSum+=alpha;ColorAbsSum+=r+g+b;ColorMaxAbs=Math.Max(ColorMaxAbs,colorMax);
            if(aa< -Tolerance||ba< -Tolerance||aa>1+Tolerance||ba>1+Tolerance)AlphaOutOfRange++;
            bool alphaChanged=Math.Abs(alpha)>Tolerance,colorChanged=colorMax>Tolerance;
            if(alphaChanged){AlphaAltered++;AlphaBounds.Include(x,y);}
            if(colorChanged){ColorAltered++;ColorBounds.Include(x,y);}
            if(alphaChanged||colorChanged)Altered++;
            if(alpha>Tolerance)
            {Positive++;PositiveSum+=alpha;MaxPositive=Math.Max(MaxPositive,alpha);PositiveBounds.Include(x,y);}
            else if(alpha< -Tolerance)
            {Negative++;NegativeSum-=alpha;MaxNegative=Math.Max(MaxNegative,-alpha);NegativeBounds.Include(x,y);}
            bool full=Math.Max(Math.Abs(aa),Math.Max(Math.Abs(ar),Math.Max(Math.Abs(ag),Math.Abs(ab))))>Tolerance;
            bool remap=Math.Max(Math.Abs(ba),Math.Max(Math.Abs(br),Math.Max(Math.Abs(bg),Math.Abs(bb))))>Tolerance;
            if(Math.Max(Math.Max(Math.Abs(ar),Math.Max(Math.Abs(ag),Math.Abs(ab))),
                Math.Max(Math.Abs(br),Math.Max(Math.Abs(bg),Math.Abs(bb))))>Tolerance)Colored++;
            if(full||remap)
            {
                Covered++;
                if(!alphaChanged&&!colorChanged)UnchangedCovered++;
            }
            if(full&&!remap)FullOnly++;
            if(remap&&!full)RemapOnly++;
        }
    }
    internal struct GlassSeamPixelBounds
    {
        private bool any;private int minX,minY,maxX,maxY;
        internal void Include(int x,int y)
        {if(!any){any=true;minX=maxX=x;minY=maxY=y;}else{minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);}}
        public override string ToString()=>any?"["+minX+","+minY+".."+maxX+","+maxY+"]":"none";
    }
    // END PURE STATS
}
