using System;
using System.Collections.Generic;
using System.IO;

namespace StreamDuel.PackCheck
{
    public static partial class ArtCheck
    {
        static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        static void CheckPng(ArtFile file, string id, List<ArtCheckIssue> issues)
        {
            try
            {
                // 扫描与打开之间再次核验祖先，绝不主动跟随已经出现的链接。
                VerifyAncestors(file.FullPath);
                using (var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1))
                {
                    long bytes = stream.Length;
                    var header = new byte[33];
                    int count = 0;
                    while (count < header.Length)
                    {
                        int read = stream.Read(header, count, header.Length - count);
                        if (read == 0) break;
                        count += read;
                    }
                    string failure = HeaderError(header, count, out int? width, out int? height);
                    if (bytes > MaxFileBytes)
                        issues.Add(new ArtCheckIssue("large", "error", file.Relative, id,
                            "立绘文件超过 8 MiB（8388608 字节）的推荐上限。", width, height, bytes));
                    if (failure != null)
                    {
                        issues.Add(new ArtCheckIssue("format", "error", file.Relative, id,
                            "PNG 文件头无效：" + failure, width, height, bytes));
                        return;
                    }
                    if (width < MinDimension || width > MaxDimension || height < MinDimension || height > MaxDimension)
                        issues.Add(new ArtCheckIssue("size", "error", file.Relative, id,
                            "立绘宽高均须在 128～4096 像素的推荐范围内。", width, height, bytes));
                }
            }
            catch (Exception error) when (IsPathError(error))
            {
                issues.Add(new ArtCheckIssue(error is ArgumentException ? "path" : "io", "error", file.Relative, id,
                    "无法只读检查立绘：" + PathErrorText(error)));
            }
        }

        static string HeaderError(byte[] header, int count, out int? width, out int? height)
        {
            width = height = null;
            if (count < 33) return "文件不足 33 字节。";
            for (int i = 0; i < PngSignature.Length; i++)
                if (header[i] != PngSignature[i]) return "签名不匹配，文件不是 PNG。";
            if (UInt32(header, 8) != 13) return "首个 IHDR 块长度必须为 13。";
            if (header[12] != 'I' || header[13] != 'H' || header[14] != 'D' || header[15] != 'R')
                return "首个块必须是 IHDR。";
            uint rawWidth = UInt32(header, 16), rawHeight = UInt32(header, 20);
            if (rawWidth == 0 || rawHeight == 0 || rawWidth > int.MaxValue || rawHeight > int.MaxValue)
                return "宽高必须是 1～2147483647 的整数。";
            width = (int)rawWidth;
            height = (int)rawHeight;
            byte depth = header[24], color = header[25];
            bool validDepth = color == 0 && (depth == 1 || depth == 2 || depth == 4 || depth == 8 || depth == 16)
                || color == 2 && (depth == 8 || depth == 16)
                || color == 3 && (depth == 1 || depth == 2 || depth == 4 || depth == 8)
                || (color == 4 || color == 6) && (depth == 8 || depth == 16);
            if (!validDepth) return "位深与颜色类型组合不受 PNG 规范支持。";
            if (header[26] != 0) return "压缩方法必须为 0。";
            if (header[27] != 0) return "过滤方法必须为 0。";
            if (header[28] > 1) return "隔行扫描方法必须为 0 或 1。";
            uint crc = uint.MaxValue;
            for (int i = 12; i < 29; i++)
            {
                crc ^= header[i];
                for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320U : crc >> 1;
            }
            if ((crc ^ uint.MaxValue) != UInt32(header, 29)) return "IHDR 校验和不匹配。";
            return null;
        }

        static uint UInt32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16)
                | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }
    }
}
