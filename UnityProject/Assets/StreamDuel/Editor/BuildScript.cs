using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BuildScript
{
    private const string MainScenePath = "Assets/Scenes/Main.unity";

    [MenuItem("开播对决/准备主场景")]
    public static void PrepareProject()
    {
        PlayerSettings.companyName = "StreamDuel";
        PlayerSettings.productName = "开播对决";
        PlayerSettings.defaultScreenWidth = 1600;
        PlayerSettings.defaultScreenHeight = 900;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64,
            new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });
        if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
            AssetDatabase.CreateFolder("Assets", "Scenes");

        if (!File.Exists(Path.Combine(Application.dataPath, "Scenes", "Main.unity")))
        {
            // 首次打开的未命名默认场景不能附加新场景；已有未保存编辑必须先保存。
            int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
            bool hasUntitled = sceneCount == 0;
            for (int i = 0; i < sceneCount; i++)
                if (string.IsNullOrEmpty(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).path))
                    hasUntitled = true;
            if (hasUntitled)
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                    if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                        throw new BuildFailedException("请先保存当前未命名场景的修改，再准备主场景。");
            var mode = hasUntitled ? NewSceneMode.Single : NewSceneMode.Additive;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            try
            {
                if (!EditorSceneManager.SaveScene(scene, MainScenePath))
                    throw new BuildFailedException("无法保存主场景：" + MainScenePath);
            }
            finally
            {
                if (mode == NewSceneMode.Additive) EditorSceneManager.CloseScene(scene, true);
            }
        }

        var scenes = new List<EditorBuildSettingsScene>
        {
            new EditorBuildSettingsScene(MainScenePath, true)
        };
        foreach (var scene in EditorBuildSettings.scenes)
            if (scene.path != MainScenePath) scenes.Add(scene);
        EditorBuildSettings.scenes = scenes.ToArray();
        AssetDatabase.SaveAssets();
        Debug.Log("主场景已就绪，并已设为构建列表第 0 位：" + MainScenePath);
    }

    [MenuItem("开播对决/构建 Windows")]
    public static void BuildWindows()
    {
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
            throw new BuildFailedException("当前 Unity 编辑器未提供 Windows 64 位构建支持。");

        PrepareProject();
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Build", "Windows", "StreamDuel.exe"));
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray(),
            locationPathName = output,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });

        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException("Windows 构建失败：" + report.summary.result
                + "，错误 " + report.summary.totalErrors + "，警告 " + report.summary.totalWarnings + "。");

        Debug.Log("Windows 构建完成：" + output + "，错误 " + report.summary.totalErrors
            + "，警告 " + report.summary.totalWarnings + "。");
    }
}
