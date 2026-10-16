using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

[InitializeOnLoad]
public sealed class AboutTextBuild : IPreprocessBuildWithReport
{
    const string ResourceFolder = "Assets/StreamDuel/Resources/About";
    static readonly string[] Sources =
    {
        "LICENSE",
        "UnityProject/Assets/StreamDuel/Resources/Packs/LICENSE.md",
        "THIRD_PARTY.md"
    };
    static readonly string[] Targets = { "CodeLicense.txt", "ContentLicense.txt", "ThirdParty.txt" };

    static AboutTextBuild()
    {
        EditorApplication.delayCall += SynchronizeAfterImport;
    }

    public int callbackOrder => -100;

    public void OnPreprocessBuild(BuildReport report)
    {
        Synchronize();
    }

    [MenuItem("开播对决/同步关于页面许可全文")]
    public static void Synchronize()
    {
        string assets = Path.GetFullPath(Application.dataPath);
        string project = Path.GetFullPath(Path.Combine(assets, ".."));
        string repository = Path.GetFullPath(Path.Combine(project, ".."));
        if (!string.Equals(Path.GetFileName(assets), "Assets", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(project), "UnityProject", StringComparison.Ordinal))
            throw new BuildFailedException("关于页面许可同步失败：工程必须位于仓库的 UnityProject/Assets 下。");

        // 先读齐源文，缺失时不能把旧副本当作可用的许可内容。
        var bytes = new byte[Sources.Length][];
        for (int i = 0; i < Sources.Length; i++)
        {
            string source = RequireInside(repository, Path.Combine(repository, Sources[i]));
            if (!File.Exists(source)) throw new BuildFailedException("缺少许可源文件：" + source);
            bytes[i] = File.ReadAllBytes(source);
            if (bytes[i].Length == 0) throw new BuildFailedException("许可源文件为空：" + source);
        }

        string destination = RequireInside(repository, Path.Combine(project, ResourceFolder));
        Directory.CreateDirectory(destination);
        for (int i = 0; i < Targets.Length; i++)
        {
            string target = RequireInside(repository, Path.Combine(destination, Targets[i]));
            if (File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(bytes[i])) continue;
            File.WriteAllBytes(target, bytes[i]);
            AssetDatabase.ImportAsset(ResourceFolder + "/" + Targets[i], ImportAssetOptions.ForceSynchronousImport);
        }
    }

    static string RequireInside(string repository, string path)
    {
        string root = Path.GetFullPath(repository).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new BuildFailedException("许可同步路径不能越出当前仓库：" + full);
        string cursor = root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (string component in full.Substring(root.Length).Split(Path.DirectorySeparatorChar))
        {
            cursor = Path.Combine(cursor, component);
            if ((File.Exists(cursor) || Directory.Exists(cursor))
                && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new BuildFailedException("许可同步路径不能通过链接跳出当前仓库：" + cursor);
        }
        return full;
    }

    static void SynchronizeAfterImport()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += SynchronizeAfterImport;
            return;
        }
        try { Synchronize(); }
        catch (Exception error) { Debug.LogException(error); }
    }
}
