using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using Common;
using HarmonyLib;
using UI;
using UI.MainScreen;
using UnityEngine;
using UnityEngine.UI;

namespace GraphicalTweaks
{
    internal sealed class Runtime : IPluginRuntime
    {
        public const string Id = "local.sevo.graphicaltweaks";
        internal static Runtime Instance;
        internal bool Started { get; private set; }
        private readonly Plugin host;
        private BepInEx.Logging.ManualLogSource Logger => host.Log;
        private ConfigFile Config => host.Settings;
        private bool isActiveAndEnabled => host.isActiveAndEnabled;
        private readonly FeatureLifetime lifetime;
        private bool disposed;
        internal Runtime(Plugin host)
        {
            this.host = host;
            lifetime = new FeatureLifetime((name, error) => Logger.LogError(name + " unavailable: " + error));
        }
        internal ShadowDistanceModel Model;
        private ConfigEntry<bool> enabledOverride;
        private ConfigEntry<float> distance;
        private ConfigEntry<int> decalPercent;
        private ConfigEntry<float> decalMultiplier;
        private ConfigEntry<int> sunResolutionChoice;
        private ConfigEntry<bool> optimiseEmptyPasses;
        private ConfigEntry<bool> optimiseAmbientOcclusion;
        private ConfigEntry<bool> skipOffscreenDecalProxyWork;
        private ConfigEntry<bool> glassSunlightOcclusion;
        private ConfigEntry<bool> removeBakedGlassHorizon;
        private ConfigEntry<int> neutralGlassSheen;
        private ConfigEntry<bool> seamlessGlass;
        private ConfigEntry<int> nearShadowChoice;
        private ShadowResolutionModel resolutionModel;
        private SunShadowResolutionController resolutionController;
        private NearShadowDetailModel nearShadowModel;
        private NearShadowDetailController nearShadowController;
        private GlassRenderingController glassController;
        private bool glassInstalled;
        private Harmony harmony;
        private bool synchronizing;
        private float nextCheck;

        public void Initialize()
        {
            Instance = this;
            Logger.LogInfo("Initializing Unity 6 runtime settings.");
            Model = new ShadowDistanceModel(QualitySettings.shadowDistance);
            enabledOverride = Config.Bind("Shadows", "OverrideEnabled", false, "Use the custom shadow distance.");
            distance = Config.Bind("Shadows", "Distance", Model.Baseline, "Shadow range in world units.");
            bool restoreOverride = enabledOverride.Value;
            if (!Model.TrySet(distance.Value)) restoreOverride = false;
            if (!restoreOverride) Model.Reset();
            MenuOptions.Initialize(Model);
            resolutionModel = new ShadowResolutionModel();
            sunResolutionChoice = Config.Bind("Shadows", "SunResolutionMode", 0, "Sun map: 0=default, 1=512, 2=1024, 3=2048, 4=4096.");
            sunResolutionChoice.Value = ShadowResolutionModel.NormalizeSaved(sunResolutionChoice.Value);
            resolutionModel.TrySet(sunResolutionChoice.Value);
            resolutionController = new SunShadowResolutionController(resolutionModel);
            MenuOptions.SunResolution.Value = resolutionModel.Choice;
            MenuOptions.SunResolution.eventVariable.OnValueChanged += OnSunResolutionChanged;
            nearShadowModel = new NearShadowDetailModel();
            nearShadowChoice = Config.Bind("Shadows", "NearShadowDetailChoice", 0, "Sharp range: 0=default, 1=15, 2=30, 3=60, 4=100.");
            nearShadowChoice.Value = NearShadowDetailModel.NormalizeSaved(nearShadowChoice.Value);
            nearShadowModel.TrySet(nearShadowChoice.Value);
            nearShadowController = new NearShadowDetailController(nearShadowModel);
            MenuOptions.NearShadowDetail.Value = nearShadowModel.Choice;
            MenuOptions.NearShadowDetail.eventVariable.OnValueChanged += OnNearShadowDetailChanged;
            glassSunlightOcclusion = Config.Bind("Glass", "SunlightOcclusion", true, "Block direct glass glare using sunlight shadows.");
            removeBakedGlassHorizon = Config.Bind("Glass", "RemoveBakedHorizon", false, "Remove the baked horizon reflection on glass.");
            neutralGlassSheen = Config.Bind("Glass", "NeutralSheen", 0, "0=off, 1=low, 2=medium, 3=high.");
            neutralGlassSheen.Value = BakedGlassHorizonPolicy.NormalizeSheen(neutralGlassSheen.Value);
            seamlessGlass = Config.Bind("Glass", "SeamlessGlass", true, "Remove internal joins between touching borderless glass.");
            MenuOptions.GlassSunlightOcclusion.Value = glassSunlightOcclusion.Value;
            MenuOptions.RemoveBakedGlassHorizon.Value = removeBakedGlassHorizon.Value;
            MenuOptions.NeutralGlassSheen.Value = neutralGlassSheen.Value;
            MenuOptions.SeamlessGlass.Value = seamlessGlass.Value;
            MenuOptions.GlassSunlightOcclusion.eventVariable.OnValueChanged += OnGlassSunlightOcclusionChanged;
            MenuOptions.RemoveBakedGlassHorizon.eventVariable.OnValueChanged += OnRemoveBakedGlassHorizonChanged;
            MenuOptions.NeutralGlassSheen.eventVariable.OnValueChanged += OnNeutralGlassSheenChanged;
            MenuOptions.SeamlessGlass.eventVariable.OnValueChanged += OnSeamlessGlassChanged;
            string bundlePath = Path.Combine(Path.GetDirectoryName(typeof(Runtime).Assembly.Location), "graphicaltweaks-glass.bundle");
            optimiseEmptyPasses = Config.Bind("Optimizations", "SkipEmptyRenderPassDispatches", true, "Skip culling work for empty render passes.");
            RendererOptimization.Enabled = optimiseEmptyPasses.Value;
            MenuOptions.OptimizeRenderer.Value = optimiseEmptyPasses.Value;
            MenuOptions.OptimizeRenderer.eventVariable.OnValueChanged += OnRendererOptimizationChanged;
            optimiseAmbientOcclusion = Config.Bind("Optimizations", "RepairAmbientOcclusionLifecycle", true, "Prevent duplicate or stale AO passes.");
            MenuOptions.OptimizeAmbientOcclusion.Value = optimiseAmbientOcclusion.Value;
            MenuOptions.OptimizeAmbientOcclusion.eventVariable.OnValueChanged += OnAmbientOcclusionOptimizationChanged;
            skipOffscreenDecalProxyWork = Config.Bind("Optimizations", "SkipOffscreenDecalProxyWork", false, "Experimental camera-turn optimisation; off by default.");
            MenuOptions.OptimizeDecalCpu.Value = skipOffscreenDecalProxyWork.Value;
            MenuOptions.OptimizeDecalCpu.eventVariable.OnValueChanged += OnDecalProxyWorkFilteringChanged;
            ApplyDecalProxyWorkFiltering();
            MenuOptions.Slider.eventVariable.OnValueChanged += OnDistanceChanged;
            decalPercent = Config.Bind("Decals", "VisibilityPercent", 100, "Legacy percentage for older configurations.");
            decalMultiplier = Config.Bind("Decals", "VisibilityMultiplier", DecalRangeModel.NormalizeMultiplier(decalPercent.Value / 100f), "Decal visibility, 1.0–10.0x.");
            decalMultiplier.Value = DecalRangeModel.NormalizeMultiplier(decalMultiplier.Value);
            decalPercent.Value = Mathf.RoundToInt(decalMultiplier.Value * 100);
            MenuOptions.DecalSlider.Value = Mathf.RoundToInt(decalMultiplier.Value * 10);
            MenuOptions.DecalNumeric.Value = decalMultiplier.Value;
            MenuOptions.DecalSlider.eventVariable.OnValueChanged += OnDecalSliderChanged;
            MenuOptions.DecalNumeric.eventVariable.OnValueChanged += OnDecalNumericChanged;
            DecalVisibility.Request(decalPercent.Value);
            DecalProjection.Request(decalPercent.Value);
            harmony = new Harmony(Id + ".glass");
            // Native ResourcesLoader.Awake uploads the shape/material/rotation
            // tables before plugin Start. Observe those successful uploads here;
            // asset loading and render interception still start in Start.
            lifetime.TryStartNative("glass-bootstrap", () => NativeFeatureContracts.Require("glass-bootstrap"), () =>
            {
                NativeLayout.Verify(message => Logger.LogInfo(message));
                glassController = new GlassRenderingController(Logger, bundlePath);
                glassController.InstallBootstrapObservers(harmony);
            }, () => { glassController?.Dispose(); harmony.UnpatchSelf(); });
            Apply(false);
            Logger.LogInfo($"Graphical Tweaks loaded. baseline={Model.Baseline}, range=1..{Model.Maximum}, effective={Model.EffectiveDistance}, cascades={QualitySettings.shadowCascades}, resolution={QualitySettings.shadowResolution}, persistentDataPath={Application.persistentDataPath}");
        }

        public void Start()
        {
            InstallPatchGroup("menu", () => new[] { typeof(MenuPatch), typeof(NumericPatch), typeof(MultiplierInputPatch),
                typeof(MultiplierNumberFormatPatch), typeof(MultiplierSliderFormatPatch) });
            InstallPatchGroup("decal-visibility", () => NestedPatches(typeof(DecalVisibility)),
                restore: DecalVisibility.Shutdown, native: true);
            InstallPatchGroup("decal-projection", () => NestedPatches(typeof(DecalProjection)),
                before: owner => DecalProjection.Install(owner), restore: DecalProjection.Shutdown, native: true);
            InstallPatchGroup("renderer", () => NestedPatches(typeof(RendererOptimization)),
                restore: () => RendererOptimization.Enabled = false, native: true);
            lifetime.TryStartNative("ambient-occlusion", () => NativeFeatureContracts.Require("ambient-occlusion"), () =>
            {
                AmbientOcclusionLifecycle.Install(new Harmony(Id + ".ao"), message => Logger.LogInfo(message));
                ApplyAmbientOcclusionOptimization();
            }, AmbientOcclusionLifecycle.Dispose);
            if (glassController != null) lifetime.TryStart("glass", () =>
            {
                glassController.Install(harmony);
                glassInstalled = true;
                ApplyGlassOptions();
            }, () => { glassInstalled = false; glassController?.Dispose(); harmony.UnpatchSelf(); });
            Logger.LogInfo("Unity 6 feature initialization completed.");
            Started = true;
        }

        private static Type[] NestedPatches(Type feature)
            => Array.FindAll(feature.GetNestedTypes(System.Reflection.BindingFlags.NonPublic),
                type => type.IsDefined(typeof(HarmonyPatch), false));

        private void InstallPatchGroup(string name, Func<Type[]> types, Action<Harmony> before = null, Action restore = null, bool native = false)
        {
            var owner = new Harmony(Id + "." + name);
            Action initialize = () =>
            {
                before?.Invoke(owner);
                foreach (var type in types()) owner.CreateClassProcessor(type).Patch();
                Logger.LogInfo("Initialized " + name + ".");
            };
            Action cleanup = () =>
            {
                try { restore?.Invoke(); }
                finally { owner.UnpatchSelf(); }
            };
            if (native) lifetime.TryStartNative(name, () => NativeFeatureContracts.Require(name), initialize, cleanup);
            else lifetime.TryStart(name, initialize, cleanup);
        }

        public void OnEnable()
        {
            Logger.LogInfo("Lifecycle OnEnable");
            nearShadowController?.Apply();
            if (glassInstalled) ApplyGlassOptions();
            ApplyAmbientOcclusionOptimization();
            ApplyDecalProxyWorkFiltering();
        }
        public void OnDisable()
        {
            Logger.LogInfo("Lifecycle OnDisable");
            nearShadowController?.Restore();
            if (glassInstalled)
            {
                glassController.SetBakedHorizonOptions(false, 0);
                glassController.SetOptions(false, false);
            }
            AmbientOcclusionLifecycle.SetEnabled(false);
            DecalVisibility.SetProxyWorkFiltering(false);
        }

        private void OnDistanceChanged()
        {
            if (synchronizing) return;
            if (Model.TrySet(MenuOptions.Slider.Value)) Apply(true);
            else Refresh();
        }

        internal void ResetShadow()
        {
            Model.Reset();
            Apply(true);
        }

        internal void LogDecal(string message) => Logger.LogInfo(message);
        internal int SunShadowResolutionChoice => resolutionModel.Choice;
        internal bool RendererOptimizationEnabled => RendererOptimization.Enabled;
        internal bool AmbientOcclusionOptimizationEnabled => optimiseAmbientOcclusion.Value;
        internal int NearShadowDetailChoice => nearShadowModel.Choice;
        internal bool GlassSunlightOcclusionEnabled => glassSunlightOcclusion.Value;
        internal bool RemoveBakedGlassHorizonEnabled => removeBakedGlassHorizon.Value;
        internal int NeutralGlassSheenChoice => neutralGlassSheen.Value;
        internal bool SeamlessGlassEnabled => seamlessGlass.Value;
        private static Light CurrentSun => PositionManager.instance != null ? PositionManager.instance.sunLight : null;
        private void OnSunResolutionChanged() { if (!synchronizing) SetSunShadowResolutionChoice(MenuOptions.SunResolution.Value); }
        internal void SetSunShadowResolutionChoice(int choice)
        {
            if (!resolutionModel.TrySet(choice)) { RefreshQualityControls(); return; }
            sunResolutionChoice.Value = choice;
            resolutionController.Apply(CurrentSun);
            Config.Save();
            Logger.LogInfo($"Sun shadow resolution applied: choice={choice}, light={CurrentSun?.shadowResolution}, custom={CurrentSun?.shadowCustomResolution}, quality={QualitySettings.shadowResolution}");
            RefreshQualityControls();
        }
        private void OnRendererOptimizationChanged() { if (!synchronizing) SetRendererOptimization(MenuOptions.OptimizeRenderer.Value); }
        internal void SetRendererOptimization(bool enabled)
        {
            optimiseEmptyPasses.Value = enabled;
            RendererOptimization.Enabled = enabled;
            Config.Save();
            Logger.LogInfo("Renderer empty-pass optimisation: " + enabled);
            RefreshQualityControls();
        }
        private void OnNearShadowDetailChanged() { if (!synchronizing) SetNearShadowDetailChoice(MenuOptions.NearShadowDetail.Value); }
        private void OnAmbientOcclusionOptimizationChanged() { if (!synchronizing) SetAmbientOcclusionOptimization(MenuOptions.OptimizeAmbientOcclusion.Value); }
        internal void SetAmbientOcclusionOptimization(bool value)
        {
            optimiseAmbientOcclusion.Value = value;
            Config.Save();
            ApplyAmbientOcclusionOptimization();
            RefreshQualityControls();
        }
        private void ApplyAmbientOcclusionOptimization()
        {
            if (AmbientOcclusionLifecycle.Installed)
                AmbientOcclusionLifecycle.SetEnabled(isActiveAndEnabled && optimiseAmbientOcclusion != null && optimiseAmbientOcclusion.Value);
        }
        private void OnDecalProxyWorkFilteringChanged() { if (!synchronizing) SetDecalProxyWorkFiltering(MenuOptions.OptimizeDecalCpu.Value); }
        internal void SetDecalProxyWorkFiltering(bool value)
        {
            skipOffscreenDecalProxyWork.Value = value;
            Config.Save();
            ApplyDecalProxyWorkFiltering();
            RefreshQualityControls();
        }
        private void ApplyDecalProxyWorkFiltering()
        {
            DecalVisibility.SetProxyWorkFiltering(isActiveAndEnabled && skipOffscreenDecalProxyWork != null && skipOffscreenDecalProxyWork.Value);
        }
        internal void SetNearShadowDetailChoice(int choice)
        {
            if (!nearShadowModel.TrySet(choice)) { RefreshQualityControls(); return; }
            if (isActiveAndEnabled) nearShadowController.Apply();
            else nearShadowController.Restore();
            nearShadowChoice.Value = choice;
            Config.Save();
            Logger.LogInfo("Near shadow detail applied: " + nearShadowController.DescribeCurrent());
            RefreshQualityControls();
        }
        private void OnGlassSunlightOcclusionChanged() { if (!synchronizing) SetGlassSunlightOcclusion(MenuOptions.GlassSunlightOcclusion.Value); }
        private void OnRemoveBakedGlassHorizonChanged() { if (!synchronizing) SetRemoveBakedGlassHorizon(MenuOptions.RemoveBakedGlassHorizon.Value); }
        private void OnNeutralGlassSheenChanged() { if (!synchronizing) SetNeutralGlassSheen(MenuOptions.NeutralGlassSheen.Value); }
        private void OnSeamlessGlassChanged() { if (!synchronizing) SetSeamlessGlass(MenuOptions.SeamlessGlass.Value); }
        internal void SetGlassSunlightOcclusion(bool enabled)
        {
            glassSunlightOcclusion.Value = enabled;
            Config.Save();ApplyGlassOptions();RefreshQualityControls();
            Logger.LogInfo("Glass sunlight occlusion requested: " + enabled);
        }
        internal void SetSeamlessGlass(bool enabled)
        {
            seamlessGlass.Value = enabled;
            Config.Save();ApplyGlassOptions();RefreshQualityControls();
            Logger.LogInfo("Seamless glass requested: " + enabled);
        }
        internal void SetRemoveBakedGlassHorizon(bool enabled)
        {
            removeBakedGlassHorizon.Value = enabled;
            Config.Save();ApplyGlassOptions();RefreshQualityControls();
            Logger.LogInfo("Remove baked glass horizon requested: " + enabled);
        }
        internal void SetNeutralGlassSheen(int choice)
        {
            neutralGlassSheen.Value = BakedGlassHorizonPolicy.NormalizeSheen(choice);
            Config.Save();ApplyGlassOptions();RefreshQualityControls();
            Logger.LogInfo("Neutral glass sheen requested: " + neutralGlassSheen.Value);
        }
        private void ApplyGlassOptions()
        {
            if (!glassInstalled) return;
            glassController.SetBakedHorizonOptions(isActiveAndEnabled && removeBakedGlassHorizon.Value, neutralGlassSheen.Value);
            if (!isActiveAndEnabled) { glassController.SetOptions(false, false); return; }
            glassController.SetOptions(glassSunlightOcclusion.Value, seamlessGlass.Value);
            glassController.Apply();
        }
        private void RefreshQualityControls()
        {
            synchronizing = true;
            try
            {
                MenuOptions.SunResolution.Value = resolutionModel.Choice;
                MenuOptions.OptimizeRenderer.Value = RendererOptimization.Enabled;
                MenuOptions.OptimizeAmbientOcclusion.Value = optimiseAmbientOcclusion.Value;
                MenuOptions.OptimizeDecalCpu.Value = skipOffscreenDecalProxyWork.Value;
                MenuOptions.NearShadowDetail.Value = nearShadowModel.Choice;
                MenuOptions.GlassSunlightOcclusion.Value = glassSunlightOcclusion.Value;
                MenuOptions.RemoveBakedGlassHorizon.Value = removeBakedGlassHorizon.Value;
                MenuOptions.NeutralGlassSheen.Value = neutralGlassSheen.Value;
                MenuOptions.SeamlessGlass.Value = seamlessGlass.Value;
                // Native checkbox UI does not refresh its image in OnValueChanged.
                if (MenuOptions.OptimizeRenderer.uiInstance != null) MenuOptions.OptimizeRenderer.uiInstance.SetGenericOption(MenuOptions.OptimizeRenderer);
                if (MenuOptions.OptimizeAmbientOcclusion.uiInstance != null) MenuOptions.OptimizeAmbientOcclusion.uiInstance.SetGenericOption(MenuOptions.OptimizeAmbientOcclusion);
                if (MenuOptions.OptimizeDecalCpu.uiInstance != null) MenuOptions.OptimizeDecalCpu.uiInstance.SetGenericOption(MenuOptions.OptimizeDecalCpu);
                if (MenuOptions.GlassSunlightOcclusion.uiInstance != null) MenuOptions.GlassSunlightOcclusion.uiInstance.SetGenericOption(MenuOptions.GlassSunlightOcclusion);
                if (MenuOptions.RemoveBakedGlassHorizon.uiInstance != null) MenuOptions.RemoveBakedGlassHorizon.uiInstance.SetGenericOption(MenuOptions.RemoveBakedGlassHorizon);
                if (MenuOptions.SeamlessGlass.uiInstance != null) MenuOptions.SeamlessGlass.uiInstance.SetGenericOption(MenuOptions.SeamlessGlass);
            }
            finally { synchronizing = false; }
        }

        private void OnDecalSliderChanged() { if (!synchronizing) SetDecalMultiplier(MenuOptions.DecalSlider.Value / 10f); }
        private void OnDecalNumericChanged() { if (!synchronizing) SetDecalMultiplier(MenuOptions.DecalNumeric.Value); }
        internal void ResetDecals() => SetDecalMultiplier(1f);
        private void SetDecalMultiplier(float value)
        {
            var model = new DecalRangeModel();
            if (!model.TrySetMultiplier(value)) { RefreshDecals(); return; }
            int percent = model.Percent;
            decalMultiplier.Value = model.Multiplier;
            decalPercent.Value = percent;
            Config.Save();
            DecalVisibility.Request(percent);
            DecalProjection.Request(percent);
            Logger.LogInfo($"Decal visibility requested: {model.Multiplier:F1}x (queued for native upload phase)");
            RefreshDecals();
        }

        internal void CommitDecalInput(string text)
        {
            if (float.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && !float.IsNaN(value) && !float.IsInfinity(value) && value >= 1 && value <= 10)
                SetDecalMultiplier(value);
            else Logger.LogWarning("Decal visibility rejected: enter a multiplier from 1.0 to 10.0.");
            RefreshDecals();
        }

        private void RefreshDecals()
        {
            synchronizing = true;
            try
            {
                MenuOptions.DecalSlider.Value = Mathf.RoundToInt(decalMultiplier.Value * 10);
                MenuOptions.DecalNumeric.Value = decalMultiplier.Value;
                if (MenuOptions.DecalNumeric.uiInstance != null) MenuOptions.DecalNumeric.uiInstance.SetGenericOption(MenuOptions.DecalNumeric);
            }
            finally { synchronizing = false; }
        }

        internal void CommitInput(string text)
        {
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && Model.TrySet(value)) Apply(true);
            else { Logger.LogWarning($"Shadow distance rejected: enter an integer from 1 to {Model.Maximum}."); Refresh(); }
        }

        private void Refresh()
        {
            synchronizing = true;
            try
            {
                MenuOptions.Slider.Value = Mathf.Clamp(Mathf.RoundToInt(Model.Enabled ? Model.SelectedDistance : Model.Baseline), 1, Model.Maximum);
                if (MenuOptions.Numeric.uiInstance != null) MenuOptions.Numeric.uiInstance.SetGenericOption(MenuOptions.Numeric);
            }
            finally { synchronizing = false; }
        }

        private void Apply(bool persist)
        {
            QualitySettings.shadowDistance = Model.EffectiveDistance;
            if (isActiveAndEnabled) nearShadowController?.Apply();
            else nearShadowController?.Restore();
            if (persist)
            {
                enabledOverride.Value = Model.Enabled;
                distance.Value = Model.SelectedDistance;
                Config.Save();
                Logger.LogInfo($"Shadow distance applied: override={Model.Enabled}, requested={Model.EffectiveDistance}, actual={QualitySettings.shadowDistance}");
            }
            Refresh();
        }

        public void Tick()
        {
            if (Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 0.5f;
            resolutionController?.Apply(CurrentSun);
            if (Model.Enabled && !Mathf.Approximately(QualitySettings.shadowDistance, Model.EffectiveDistance)) Apply(false);
            nearShadowController?.Apply();
            if (glassInstalled) glassController?.Apply();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Started = false;
            Logger.LogInfo("Lifecycle OnDestroy: restoring baseline and removing patches");
            lifetime.Dispose();
            if (Model != null) QualitySettings.shadowDistance = Model.Baseline;
            try { nearShadowController?.Dispose(); }
            catch (Exception e) { Logger.LogError("Near shadow restoration failed: " + e); }
            try { glassController?.Dispose(); }
            catch (Exception e) { Logger.LogError("Glass teardown failed; continuing existing setting restoration: " + e); }
            resolutionController?.Restore();
            AmbientOcclusionLifecycle.Dispose();
            RendererOptimization.Enabled = false;
            MenuOptions.Slider.eventVariable.OnValueChanged -= OnDistanceChanged;
            MenuOptions.DecalSlider.eventVariable.OnValueChanged -= OnDecalSliderChanged;
            MenuOptions.DecalNumeric.eventVariable.OnValueChanged -= OnDecalNumericChanged;
            MenuOptions.SunResolution.eventVariable.OnValueChanged -= OnSunResolutionChanged;
            MenuOptions.OptimizeRenderer.eventVariable.OnValueChanged -= OnRendererOptimizationChanged;
            MenuOptions.OptimizeAmbientOcclusion.eventVariable.OnValueChanged -= OnAmbientOcclusionOptimizationChanged;
            MenuOptions.OptimizeDecalCpu.eventVariable.OnValueChanged -= OnDecalProxyWorkFilteringChanged;
            MenuOptions.NearShadowDetail.eventVariable.OnValueChanged -= OnNearShadowDetailChanged;
            MenuOptions.GlassSunlightOcclusion.eventVariable.OnValueChanged -= OnGlassSunlightOcclusionChanged;
            MenuOptions.RemoveBakedGlassHorizon.eventVariable.OnValueChanged -= OnRemoveBakedGlassHorizonChanged;
            MenuOptions.NeutralGlassSheen.eventVariable.OnValueChanged -= OnNeutralGlassSheenChanged;
            MenuOptions.SeamlessGlass.eventVariable.OnValueChanged -= OnSeamlessGlassChanged;
            harmony?.UnpatchSelf();
            Instance = null;
        }
    }

}
