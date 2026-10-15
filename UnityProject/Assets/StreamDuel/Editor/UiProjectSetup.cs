using System.IO;
using StreamDuel.Game;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public static class UiProjectSetup
{
    [MenuItem("开播对决/准备界面资源")]
    public static void Prepare()
    {
        const string root = "Assets/StreamDuel/";
        var regular = Require<Font>(root + "Fonts/NotoSansSC-Regular.otf");
        var bold = Require<Font>(root + "Fonts/NotoSansSC-Bold.otf");
        var theme = Require<StyleSheet>(root + "UI/Theme.uss");
        var runtimeTheme = Require<ThemeStyleSheet>(root + "UI/RuntimeTheme.tss");

        var panel = LoadOrCreate<PanelSettings>(root + "UI/PanelSettings.asset");
        panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
        panel.referenceResolution = new Vector2Int(1920, 1080);
        panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
        panel.match = 0.5f;
        panel.themeStyleSheet = runtimeTheme;
        panel.sortingOrder = 10;
        EditorUtility.SetDirty(panel);

        var resources = LoadOrCreate<ScreenResources>(root + "Resources/StreamDuelUI.asset");
        resources.PanelSettings = panel;
        resources.Theme = theme;
        resources.RegularFont = regular;
        resources.BoldFont = bold;
        EditorUtility.SetDirty(resources);
        AssetDatabase.SaveAssets();
        Debug.Log("界面资源已就绪：1920×1080 等比缩放、Noto Sans SC 与运行时主题。");
    }

    public static void BuildWindows()
    {
        Prepare();
        BuildScript.BuildWindows();
    }

    static T Require<T>(string path) where T : Object
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset == null) throw new FileNotFoundException("缺少界面资源：" + path, path);
        return asset;
    }

    static T LoadOrCreate<T>(string path) where T : ScriptableObject
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset != null) return asset;
        asset = ScriptableObject.CreateInstance<T>();
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }
}
