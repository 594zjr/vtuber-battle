using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed partial class TournamentSession
    {
        const int MaxSaveBytes = 8 * 1024 * 1024;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        static string FullSavePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("赛事存档路径不能为空。", nameof(path));
            string result = Path.GetFullPath(path);
            if (string.IsNullOrEmpty(Path.GetFileName(result))) throw new ArgumentException("赛事存档需要文件名。", nameof(path));
            return result;
        }

        void SaveCandidate(Tournament candidate)
        {
            using (var saveLock = OpenSaveLock())
            {
                RequireUnchangedSave();
                SaveCandidateLocked(candidate);
            }
        }

        FileStream OpenSaveLock()
        {
            CheckPath(SavePath);
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
            string lockPath = SavePath + ".lock";
            CheckPath(lockPath);
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        void RequireUnchangedSave()
        {
            CheckPath(SavePath);
            string current;
            try { current = ReadSave(SavePath); }
            catch (FileNotFoundException)
            {
                if (savedText == null) return;
                throw new IOException("赛事存档已被删除，未覆盖原检查点；请重新载入。");
            }
            if (savedText == null) throw new IOException("该赛事存档已存在，请续赛或使用新的文件名；原文件未覆盖。");
            if (!string.Equals(current, savedText, StringComparison.Ordinal))
                throw new IOException("赛事存档已被其他窗口或外部程序修改，请重新载入；原文件未覆盖。");
        }

        void SaveCandidateLocked(Tournament candidate)
        {
            EnsureContent();
            string text = SerializeSave(candidate, controllers);
            ParseSave(Db, text, out var verified, out var unusedControls);
            byte[] bytes = Utf8.GetBytes(text);
            if (bytes.Length > MaxSaveBytes) throw new IOException("赛事存档超过 8 MiB，无法保存。");
            string temporary = SavePath + ".tmp";
            CheckPath(temporary);
            bool created = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // 写临时文件期间也不覆盖刚被外部修改的检查点。
                RequireUnchangedSave();
                if (savedText == null) File.Move(temporary, SavePath);
                else File.Replace(temporary, SavePath, null);
                savedText = text;
                Tournament = verified;
            }
            finally
            {
                if (created && File.Exists(temporary)) File.Delete(temporary);
            }
        }

        static string ReadSave(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaxSaveBytes) throw new FormatException("赛事存档超过 8 MiB，原文件已保留。");
                    var bytes = new byte[checked((int)stream.Length)];
                    int count = 0;
                    while (count < bytes.Length)
                    {
                        int read = stream.Read(bytes, count, bytes.Length - count);
                        if (read == 0) throw new IOException("赛事存档读取不完整，请重试。");
                        count += read;
                    }
                    int offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                    return Utf8.GetString(bytes, offset, bytes.Length - offset);
                }
            }
            catch (DecoderFallbackException error)
            {
                throw new FormatException("赛事存档不是有效的 UTF-8，原文件已保留。", error);
            }
        }

        static void CheckPath(string path)
        {
            var parents = new Stack<string>();
            for (var current = new DirectoryInfo(Path.GetDirectoryName(path)); current != null && current.Parent != null; current = current.Parent)
                parents.Push(current.FullName);
            while (parents.Count > 0) CheckFile(parents.Pop());
            CheckFile(path);
        }

        static void CheckFile(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("赛事存档不能使用符号链接或重解析点。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
