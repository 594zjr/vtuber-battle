using System;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

public static class T59Setup
{
    static SearchRequest search;

    [Serializable]
    sealed class InputVersion
    {
        public string unity, recommended, latestCompatible, verified;
    }

    public static void QueryInputVersion()
    {
        search = Client.Search("com.unity.inputsystem", false);
        EditorApplication.update += FinishSearch;
    }

    static void FinishSearch()
    {
        if (!search.IsCompleted) return;
        EditorApplication.update -= FinishSearch;
        if (search.Status != StatusCode.Success || search.Result.Length != 1)
        {
            Debug.LogError("无法查询本编辑器适用的输入系统版本：" + search.Error?.message);
            EditorApplication.Exit(1);
            return;
        }
        var versions = search.Result[0].versions;
        var result = new InputVersion
        {
            unity = Application.unityVersion,
            recommended = ReadVersion(versions, "recommended"),
            latestCompatible = ReadVersion(versions, "latestCompatible"),
            verified = ReadVersion(versions, "verified")
        };
        string path = Path.Combine(Application.dataPath, "../Logs/T59/input-version.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(result, true));
        Debug.Log("输入系统版本查询完成：" + result.recommended);
        EditorApplication.Exit(0);
    }

    static string ReadVersion(object versions, string name)
    {
        // 不同编辑器将推荐字段更名，反射只读取公开元数据而不依赖已弃用别名。
        return versions.GetType().GetProperty(name)?.GetValue(versions) as string;
    }
}
