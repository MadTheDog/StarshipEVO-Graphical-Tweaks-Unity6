using System;
using System.Collections.Generic;
using GraphicalTweaks;

static class Program
{
    static int checks;
    static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; }

    static void Main()
    {
        Check(BakedGlassHorizonPolicy.Matches(true, "CustomReflectionProbe", 128, 128, 8, true), "baked gameplay probe is admitted");
        Check(!BakedGlassHorizonPolicy.Matches(false, "CustomReflectionProbe", 128, 128, 8, true), "skybox reflection mode is preserved");
        Check(!BakedGlassHorizonPolicy.Matches(true, "Preview ReflectionProbe", 128, 128, 8, true), "preview probe is preserved");
        Check(!BakedGlassHorizonPolicy.Matches(true, "OtherReflectionProbe", 128, 128, 8, true), "other custom probes are preserved");
        Check(!BakedGlassHorizonPolicy.Matches(true, "CustomReflectionProbe", 256, 128, 8, true), "changed dimensions fail open");
        Check(!BakedGlassHorizonPolicy.Matches(true, "CustomReflectionProbe", 128, 128, 1, true), "changed mip count fails open");
        Check(!BakedGlassHorizonPolicy.Matches(true, "CustomReflectionProbe", 128, 128, 8, false), "changed format fails open");
        Check(BakedGlassHorizonPolicy.NormalizeSheen(-1) == 0 && BakedGlassHorizonPolicy.NormalizeSheen(4) == 0, "invalid sheen falls back to off");
        Check(BakedGlassHorizonPolicy.SheenRadiance(0) == 0, "off retains the black probe");
        Check(BakedGlassHorizonPolicy.SheenRadiance(1) > 0 &&
            BakedGlassHorizonPolicy.SheenRadiance(2) > BakedGlassHorizonPolicy.SheenRadiance(1) &&
            BakedGlassHorizonPolicy.SheenRadiance(3) > BakedGlassHorizonPolicy.SheenRadiance(2), "sheen presets increase in brightness");
        var events = new List<string>();
        var failures = new List<string>();
        var lifetime = new FeatureLifetime((name, error) => failures.Add(name + ":" + error.Message));
        Check(lifetime.TryStart("menu", () => events.Add("menu started"), () => events.Add("menu stopped")), "menu starts");
        Check(string.Join(",", events) == "menu started", "initialization actually runs");
        Check(!lifetime.TryStart("menu", () => events.Add("duplicate"), () => events.Add("duplicate stop")), "duplicate stage does not start");
        Check(!lifetime.TryStart("glass", () => { events.Add("glass partial"); throw new InvalidOperationException("bad ABI"); }, () => events.Add("glass rollback")), "failed optional feature is isolated");
        Check(events[events.Count - 1] == "glass rollback", "failed initialization cleans up its partial state");
        Check(failures.Count == 1 && failures[0] == "glass:bad ABI", "failure identifies its feature");
        Check(lifetime.TryStart("shadows", () => events.Add("shadows started"), () => events.Add("shadows stopped")), "independent feature survives earlier failure");
        lifetime.Dispose();
        Check(string.Join(",", events) == "menu started,glass partial,glass rollback,shadows started,shadows stopped,menu stopped", "successful features tear down in reverse order");
        int count = events.Count;
        lifetime.Dispose();
        Check(events.Count == count, "disposal is idempotent");
        Check(!lifetime.TryStart("late", () => events.Add("late"), () => { }), "disposed runtime cannot start work");

        events.Clear(); failures.Clear();
        var cleanup = new FeatureLifetime((name, error) => failures.Add(name));
        cleanup.TryStart("first", () => { }, () => events.Add("first stopped"));
        cleanup.TryStart("second", () => { }, () => throw new Exception("cleanup"));
        cleanup.Dispose();
        Check(events.Count == 1 && failures.Count == 1 && failures[0] == "second", "cleanup exception does not prevent other restoration");

        var rollback = new FeatureLifetime((name, error) => failures.Add(name));
        Check(!rollback.TryStart("both fail", () => throw new Exception("initialize"), () => throw new Exception("rollback")), "rollback errors cannot escape startup");
        Check(rollback.TryStart("next", () => events.Add("next started"), () => { }), "next feature can start after rollback error");
        rollback.Dispose();
        failures.Clear();
        int nativeStarted = 0, nativeRestored = 0;
        var admission = new FeatureLifetime((name, error) => failures.Add(name));
        Check(!admission.TryStartNative("unknown", Guid.Empty, () => nativeStarted++, () => nativeRestored++), "unknown build cannot initialize native feature");
        Check(nativeStarted == 0 && nativeRestored == 0 && failures.Count == 1, "rejected build performs no native writes or restoration");
        Check(!admission.TryStartNative("unknown", Guid.Empty, () => nativeStarted++, () => nativeRestored++) && failures.Count == 1, "unsupported feature reports once");
        Check(admission.TryStart("public shadow API", () => events.Add("shadow API"), () => events.Add("shadow reset")), "public settings survive unsupported native features");
        Check(admission.TryStartNative("known", NativeBuildProfile.ValidatedAssembly, () => nativeStarted++, () => nativeRestored++), "inspected native build is admitted");
        Check(!admission.TryStartNative("other version", new Guid("f4666bd3-9a44-486c-b149-36d0a9c82e72"), () => nativeStarted++, () => nativeRestored++), "Unity2019 build is rejected by Unity6 native integration");
        Check(!admission.TryStartNative("different Unity6", new Guid("8bc6e2c3-dad6-4cd4-89ec-0f0d60f91a6d"), () => nativeStarted++, () => nativeRestored++), "uninspected Unity6 build is rejected");
        admission.Dispose();
        Check(nativeStarted == 1 && nativeRestored == 1, "only admitted native feature acquires and restores resources");
        Console.WriteLine("PASS Unity6 runtime lifetime: " + checks + " checks");
    }
}
