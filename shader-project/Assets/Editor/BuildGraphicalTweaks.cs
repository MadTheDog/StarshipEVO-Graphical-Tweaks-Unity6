using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class BuildGraphicalTweaks
{
    [MenuItem("Graphical Tweaks/Build glass shaders")]
    public static void Build()
    {
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
        string outputRequest = Path.Combine(Application.dataPath, "../build.output");
        string configured = File.Exists(outputRequest) ? File.ReadAllText(outputRequest).Trim() : "dist/GlassRendering";
        string output = Path.GetFullPath(Path.Combine(root, configured));
        if (!output.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Shader output must remain inside this project.");
        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var paths = AssetDatabase.GetAllAssetPaths().Where(path =>
            path.StartsWith("Assets/GraphicalTweaks/", StringComparison.Ordinal) &&
            (path.EndsWith(".shader", StringComparison.Ordinal) || path.EndsWith(".compute", StringComparison.Ordinal))).OrderBy(p => p).ToArray();
        if (paths.Length == 0) throw new InvalidOperationException("No original shader assets found.");
        foreach (string path in paths)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null) continue;
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                if (message.severity.ToString() == "Error") throw new InvalidOperationException(path + ": " + message.message);
        }
        Directory.CreateDirectory(output);
        var manifest = BuildPipeline.BuildAssetBundles(output, new[] {
            new AssetBundleBuild { assetBundleName = "graphicaltweaks-glass.bundle", assetNames = paths }
        }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle |
           BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new InvalidOperationException("Shader bundle build failed.");
        var bundlePath = Path.Combine(output, "graphicaltweaks-glass.bundle");
        var built = AssetBundle.LoadFromFile(bundlePath);
        if (built == null) throw new InvalidOperationException("Fresh shader bundle failed same-Editor load validation.");
        try
        {
            var names = built.GetAllAssetNames();
            if (names.Length != paths.Length || paths.Any(path => !names.Contains(path.ToLowerInvariant())))
                throw new InvalidOperationException("Fresh shader bundle directory differs from the requested assets.");
            Debug.Log("GT Unity6 shader bundle load validated: " + string.Join(", ", names));
        }
        finally { built.Unload(true); }
        var manifestBundle = AssetBundle.LoadFromFile(Path.Combine(output, Path.GetFileName(output)));
        if (manifestBundle == null) throw new InvalidOperationException("Fresh root manifest bundle failed load validation.");
        try
        {
            if (manifestBundle.LoadAsset<AssetBundleManifest>("AssetBundleManifest") == null)
                throw new InvalidOperationException("Fresh root manifest asset is missing.");
        }
        finally { manifestBundle.Unload(true); }
        File.WriteAllText(Path.Combine(output, "build-info.txt"), Application.unityVersion + "\n" + string.Join("\n", paths));
        Debug.Log("Graphical Tweaks shader bundle build succeeded: " + output);
    }
}

// Allows a Hub-authenticated interactive Editor to run the same build without
// requiring GUI automation. The request is scoped to this development project.
[InitializeOnLoad]
public static class GraphicalTweaksBuildWatch
{
    private static double nextCheck;
    static GraphicalTweaksBuildWatch() { EditorApplication.update += Poll; }
    private static void Poll()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.timeSinceStartup < nextCheck) return;
        nextCheck = EditorApplication.timeSinceStartup + 1;
        string request = Path.GetFullPath(Path.Combine(Application.dataPath, "../build.request"));
        if (!File.Exists(request)) return;
        File.Delete(request);
        try { BuildGraphicalTweaks.Build(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); }
    }
}
