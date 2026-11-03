using System;
using System.Globalization;
using System.Collections.Generic;
using Common;
using HarmonyLib;
using UI;
using UI.MainScreen;
using UnityEngine;
using UnityEngine.UI;

namespace GraphicalTweaks
{
    internal static class MenuOptions
    {
        public static readonly GenericOption<int> Slider = new GenericOption<int> { eventVariable = new IntEventVariable(), label = "Shadow distance", tooltip = "Longer ranges cover more of the scene but can soften nearby shadows and lower FPS.", useSlider = true, data1 = 1, useDefaultValue = true };
        public static readonly GenericOption<int> Numeric = new GenericOption<int> { label = "Shadow distance (number)", tooltip = "Enter a whole number in world units. Reset restores SEVO’s default distance.", useDefaultValue = true };
        public static readonly GenericOption<int> SunResolution = new GenericOption<int> { eventVariable = new IntEventVariable(), label = "Sun shadow resolution", tooltip = "Smaller shadow maps save GPU work and make shadow edges less crisp. Game default follows SEVO’s shadow quality.", useDropdown = true, data1 = 0, data2 = 4, dropdownList = new List<string> { "Game default", "512 px", "1024 px", "2048 px", "4096 px" }, useDefaultValue = true, defaultValue = 0 };
        public static readonly GenericOption<bool> OptimizeRenderer = new GenericOption<bool> { eventVariable = new BoolEventVariable(), label = "Renderer optimisation", tooltip = "Skips GPU visibility checks for passes with nothing to draw. Savings depend on the scene.", isCheckbox = true, useDefaultValue = true, defaultValue = true };
        public static readonly GenericOption<bool> OptimizeAmbientOcclusion = new GenericOption<bool> { eventVariable = new BoolEventVariable(), label = "Optimize ambient occlusion", tooltip = "Clears duplicate ambient occlusion passes and stale passes after AO is disabled. AO quality is set in SEVO’s graphics options.", isCheckbox = true, useDefaultValue = true, defaultValue = true };
        public static readonly GenericOption<bool> OptimizeDecalCpu = new GenericOption<bool> { eventVariable = new BoolEventVariable(), label = "Decal CPU optimisation (experimental)", tooltip = "Skips some off-screen decal work during quick turns. CPU use can rise when the camera is still.", isCheckbox = true, useDefaultValue = true, defaultValue = false };
        public static readonly GenericOption<int> NearShadowDetail = new GenericOption<int> { eventVariable = new IntEventVariable(), label = "Sharp shadow range", tooltip = "Smaller values concentrate detail closer to you, with coarser shadows beyond that area. Shadow distance controls the full reach.", useDropdown = true, data1 = 0, data2 = 4, dropdownList = new List<string> { "Game default", "15 units", "30 units", "60 units", "100 units" }, useDefaultValue = true, defaultValue = 0 };
        public static readonly GenericOption<bool> GlassSunlightOcclusion = new GenericOption<bool> { eventVariable = new BoolEventVariable(), label = "Glass sunlight occlusion", tooltip = "Reduces direct glare on glass inside a hull’s shadow. The pane needs sun shadow coverage for this to work.", isCheckbox = true, useDefaultValue = true, defaultValue = true };
        public static readonly GenericOption<bool> RemoveBakedGlassHorizon = new GenericOption<bool> { eventVariable = new BoolEventVariable(), label = "Remove baked glass horizon", tooltip = "Clears the blue band from SEVO’s baked glass reflection. Try Neutral glass sheen if the pane looks too clear.", isCheckbox = true, useDefaultValue = true, defaultValue = false };
        public static readonly GenericOption<int> NeutralGlassSheen = new GenericOption<int> { eventVariable = new IntEventVariable(), label = "Neutral glass sheen", tooltip = "Adds a gray reflection using the glass’s original opacity. Requires Remove baked glass horizon.", useDropdown = true, data1 = 0, data2 = 3, dropdownList = new List<string> { "Off", "Low", "Medium", "High" }, useDefaultValue = true, defaultValue = 0 };
        public static readonly GenericOption<bool> SeamlessGlass = new GenericOption<bool> { eventVariable = new BoolEventVariable(), label = "Seamless glass", tooltip = "Hides covered faces where supported glass blocks touch. Textured borders are part of the glass material.", isCheckbox = true, useDefaultValue = true, defaultValue = true };
        public static readonly GenericOption<int> DecalSlider = new GenericOption<int> { eventVariable = new IntEventVariable(), label = "Decal visibility multiplier", tooltip = "1x uses the game’s visibility range. Higher values keep small decals visible farther away and can lower FPS in busy scenes.", useSlider = true, data1 = 10, data2 = 100, useDefaultValue = true, defaultValue = 10 };
        public static readonly GenericOption<float> DecalNumeric = new GenericOption<float> { eventVariable = new FloatEventVariable(), label = "Decal multiplier (number)", tooltip = "Enter a value from 1 to 10, such as 2.5. Reset returns to 1x.", useDefaultValue = true, defaultValue = 1f };
        internal static void Initialize(ShadowDistanceModel model)
        {
            Slider.data2 = model.Maximum;
            Slider.defaultValue = Mathf.Clamp(Mathf.RoundToInt(model.Baseline), 1, model.Maximum);
            Numeric.defaultValue = Slider.defaultValue;
            Numeric.eventVariable = Slider.eventVariable;
        }
    }

    [HarmonyPatch(typeof(UI_GraphicOptions), "RebuildOptions")]
    internal static class MenuPatch
    {
        private static void Postfix(UI_GraphicOptions __instance, RectTransform ___optionParent)
        {
            Debug.Log("Graphical Tweaks: graphics menu rebuilt; plugin alive=" + (Runtime.Instance != null));
            if (Runtime.Instance == null) return;
            try
            {
                var templateData = (UITemplateData)AccessTools.Field(__instance.GetType(), "templateData").GetValue(__instance);
                float offset = ___optionParent.sizeDelta.y - 16f;
                Add(MenuOptions.Slider, ___optionParent, templateData, ref offset);
                Add(MenuOptions.Numeric, ___optionParent, templateData, ref offset);
                Add(MenuOptions.SunResolution, ___optionParent, templateData, ref offset);
                Add(MenuOptions.NearShadowDetail, ___optionParent, templateData, ref offset);
                Add(MenuOptions.OptimizeRenderer, ___optionParent, templateData, ref offset);
                Add(MenuOptions.OptimizeAmbientOcclusion, ___optionParent, templateData, ref offset);
                Add(MenuOptions.OptimizeDecalCpu, ___optionParent, templateData, ref offset);
                Add(MenuOptions.GlassSunlightOcclusion, ___optionParent, templateData, ref offset);
                Add(MenuOptions.RemoveBakedGlassHorizon, ___optionParent, templateData, ref offset);
                Add(MenuOptions.NeutralGlassSheen, ___optionParent, templateData, ref offset);
                Add(MenuOptions.SeamlessGlass, ___optionParent, templateData, ref offset);
                Add(MenuOptions.DecalSlider, ___optionParent, templateData, ref offset);
                Add(MenuOptions.DecalNumeric, ___optionParent, templateData, ref offset);
                GenericOptionCommon.FitRectToContent(offset, ___optionParent);
                Debug.Log("Graphical Tweaks: added 4 shadow, 3 optimisation, 4 glass and 2 multiplier controls");
            }
            catch (Exception e) { Debug.LogError("Graphical Tweaks menu injection failed: " + e); }
        }

        private static void Add<T>(GenericOption<T> option, Transform parent, UITemplateData templateData, ref float offset)
        {
            // Each SEVO rebuild destroys the old rows before calling SetupClassGenericOptions.
            option.translationId = "GraphicalTweaks." + option.label;
            var row = GenericOptionCommon.GetTemplateGo(option, templateData, parent);
            row.name = "GraphicalTweaks." + option.label;
            GenericOptionCommon.SetupGenericUI(row, ref offset, option, null);
            // SEVO assigns uiInstance only AFTER its first formatting callback.
            // Apply multiplier formatting once more after that assignment.
            if (ReferenceEquals(option, MenuOptions.DecalSlider)) MultiplierSliderFormatPatch.Format(MenuOptions.DecalSlider.uiInstance as UI_OptionSliderInt);
            if (ReferenceEquals(option, MenuOptions.DecalNumeric)) MultiplierNumberFormatPatch.Format(MenuOptions.DecalNumeric.uiInstance as UI_OptionFloat);
            if (ReferenceEquals(option, MenuOptions.Slider) || ReferenceEquals(option, MenuOptions.Numeric))
                BindReset(option.uiInstance, Runtime.Instance.ResetShadow);
            if (ReferenceEquals(option, MenuOptions.DecalSlider) || ReferenceEquals(option, MenuOptions.DecalNumeric))
                BindReset(option.uiInstance, Runtime.Instance.ResetDecals);
            if (ReferenceEquals(option, MenuOptions.SunResolution)) BindReset(option.uiInstance, () => Runtime.Instance.SetSunShadowResolutionChoice(0));
            if (ReferenceEquals(option, MenuOptions.OptimizeRenderer)) BindReset(option.uiInstance, () => Runtime.Instance.SetRendererOptimization(true));
            if (ReferenceEquals(option, MenuOptions.OptimizeAmbientOcclusion)) BindReset(option.uiInstance, () => Runtime.Instance.SetAmbientOcclusionOptimization(true));
            if (ReferenceEquals(option, MenuOptions.OptimizeDecalCpu)) BindReset(option.uiInstance, () => Runtime.Instance.SetDecalProxyWorkFiltering(false));
            if (ReferenceEquals(option, MenuOptions.NearShadowDetail)) BindReset(option.uiInstance, () => Runtime.Instance.SetNearShadowDetailChoice(0));
            if (ReferenceEquals(option, MenuOptions.GlassSunlightOcclusion)) BindReset(option.uiInstance, () => Runtime.Instance.SetGlassSunlightOcclusion(true));
            if (ReferenceEquals(option, MenuOptions.RemoveBakedGlassHorizon)) BindReset(option.uiInstance, () => Runtime.Instance.SetRemoveBakedGlassHorizon(false));
            if (ReferenceEquals(option, MenuOptions.NeutralGlassSheen)) BindReset(option.uiInstance, () => Runtime.Instance.SetNeutralGlassSheen(0));
            if (ReferenceEquals(option, MenuOptions.SeamlessGlass)) BindReset(option.uiInstance, () => Runtime.Instance.SetSeamlessGlass(true));
        }

        private static void BindReset(Component widget, Action resetAction)
        {
            var reset = (GameObject)AccessTools.Field(widget.GetType(), "resetGo").GetValue(widget);
            var button = reset.GetComponent<Button>() ?? reset.GetComponentInChildren<Button>(true) ?? reset.GetComponentInParent<Button>();
            if (button != null)
            {
                // Only newly cloned mod rows are rebound. Reset also works
                // when the displayed number has not changed.
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener(() => resetAction());
                button.enabled = true;
                button.interactable = true;
                if (button.targetGraphic != null) button.targetGraphic.raycastTarget = true;
            }
            else Debug.LogWarning("Graphical Tweaks: reset button missing from native template.");
        }
    }

    [HarmonyPatch(typeof(UI_OptionInt), nameof(UI_OptionInt.OnNewInput))]
    internal static class NumericPatch
    {
        private static bool Prefix(UI_OptionInt __instance)
        {
            if (Runtime.Instance == null) return true;
            bool shadow = __instance == MenuOptions.Numeric.uiInstance;
            if (!shadow) return true;
            var input = (InputField)AccessTools.Field(typeof(UI_OptionInt), "input").GetValue(__instance);
            Runtime.Instance.CommitInput(input.text);
            return false;
        }
    }

    [HarmonyPatch(typeof(UI_OptionFloat), nameof(UI_OptionFloat.OnNewInput))]
    internal static class MultiplierInputPatch
    {
        private static readonly System.Reflection.FieldInfo inputField = AccessTools.Field(typeof(UI_OptionFloat), "input");
        private static bool Prefix(UI_OptionFloat __instance)
        {
            if (Runtime.Instance == null || __instance != MenuOptions.DecalNumeric.uiInstance) return true;
            Runtime.Instance.CommitDecalInput(((InputField)inputField.GetValue(__instance)).text);
            return false;
        }
    }

    [HarmonyPatch(typeof(UI_OptionFloat), "OnValueChanged")]
    internal static class MultiplierNumberFormatPatch
    {
        private static readonly System.Reflection.FieldInfo inputField = AccessTools.Field(typeof(UI_OptionFloat), "input");
        private static void Postfix(UI_OptionFloat __instance)
            => Format(__instance);
        internal static void Format(UI_OptionFloat __instance)
        {
            if (__instance != null && __instance == MenuOptions.DecalNumeric.uiInstance)
                ((InputField)inputField.GetValue(__instance)).text = MenuOptions.DecalNumeric.Value.ToString("0.0", CultureInfo.InvariantCulture);
        }
    }

    [HarmonyPatch]
    internal static class MultiplierSliderFormatPatch
    {
        private static readonly System.Reflection.FieldInfo textField = AccessTools.Field(typeof(UI_OptionSliderInt), "text");
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UI_OptionSliderInt), "OnValueChanged");
            yield return AccessTools.Method(typeof(UI_OptionSliderInt), nameof(UI_OptionSliderInt.OnNewDataInput));
        }
        private static void Postfix(UI_OptionSliderInt __instance)
            => Format(__instance);
        internal static void Format(UI_OptionSliderInt __instance)
        {
            if (__instance != null && __instance == MenuOptions.DecalSlider.uiInstance)
                ((Text)textField.GetValue(__instance)).text = (MenuOptions.DecalSlider.Value / 10f).ToString("0.0", CultureInfo.InvariantCulture) + "×";
        }
    }
}
