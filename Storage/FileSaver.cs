using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using WebScrapingRebuilder.Core;

namespace WebScrapingRebuilder.Storage
{
    /// <summary>保存结果：描述某次文件落盘的行为，供日志记录冲突处理细节。</summary>
    public sealed class SaveResult
    {
        public string FinalPath;       // 最终保存路径；null 表示未保存
        public bool DuplicateSkipped;  // Sha256 内容重复，仅保留一份
        public bool WasRenamed;        // 因文件冲突而重命名（Always / Sha256不同 / Size不同）
        public bool WasOverwritten;    // Size 相同，直接覆盖已有文件
    }

    /// <summary>
    /// 负责将下载的临时文件按分组策略保存到最终位置，
    /// 并按 AntiDuplicate 策略处理“不同 URL 落到同一本地文件”的冲突。
    /// </summary>
    public sealed class FileSaver
    {
        private readonly ScrapeConfig _cfg;
        private readonly object _sync = new object();

        public FileSaver(ScrapeConfig cfg) => _cfg = cfg;

        /// <summary>
        /// 返回最终保存路径；若因内容完全重复（Sha256 模式）而只保留一份，返回 null。
        /// 多线程下串行化：避免多个线程同时判断“文件是否存在 -> 移动”而互相覆盖。
        /// </summary>
        public SaveResult Save(string tempFile, Uri url, string mime)
        {
            lock (_sync)
            {
                return SaveCore(tempFile, url, mime);
            }
        }

        private SaveResult SaveCore(string tempFile, Uri url, string mime)
        {
            string target = ComputeTargetPath(url, mime);
            string dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) FileIo.Retry(() => Directory.CreateDirectory(dir));

            if (!File.Exists(target))
            {
                FileIo.Move(tempFile, target, overwrite: false);
                return new SaveResult { FinalPath = target };
            }

            // 本地文件冲突：按 AntiDuplicate 策略处理
            switch (_cfg.AntiDuplicate)
            {
                case "Sha256":
                    if (HashesEqual(tempFile, target))
                    {
                        FileIo.Delete(tempFile);
                        return new SaveResult { DuplicateSkipped = true }; // 内容完全相同，仅保留一份
                    }
                    return RenameResult(tempFile, target);

                case "Size":
                    if (new FileInfo(tempFile).Length == new FileInfo(target).Length)
                    {
                        // 大小相同 -> 直接覆盖
                        FileIo.Delete(target);
                        FileIo.Move(tempFile, target, overwrite: false);
                        return new SaveResult { FinalPath = target, WasOverwritten = true };
                    }
                    return RenameResult(tempFile, target);

                case "Always":
                default:
                    // 不校验，直接重命名保存新文件
                    return RenameResult(tempFile, target);
            }
        }

        private static SaveResult RenameResult(string tempFile, string target)
        {
            string final = RenameAndMove(tempFile, target);
            return new SaveResult { FinalPath = final, WasRenamed = true };
        }

        // 重命名规则：原文件名不含后缀部分加 _{index}，如 Video.mp4 -> Video_1.mp4
        private static string RenameAndMove(string tempFile, string target)
        {
            string dir = Path.GetDirectoryName(target);
            string ext = Path.GetExtension(target);
            string baseName = Path.GetFileNameWithoutExtension(target);
            for (int i = 1; ; i++)
            {
                string candidate = Path.Combine(dir, $"{baseName}_{i}{ext}");
                if (!File.Exists(candidate))
                {
                    FileIo.Move(tempFile, candidate, overwrite: false);
                    return candidate;
                }
            }
        }

        private static bool HashesEqual(string a, string b)
        {
            return FileIo.Retry(() =>
            {
                using var sha = SHA256.Create();
                using var fa = File.OpenRead(a);
                using var fb = File.OpenRead(b);
                return sha.ComputeHash(fa).SequenceEqual(sha.ComputeHash(fb));
            });
        }

        // ---------------- 目标路径计算 ----------------

        private string ComputeTargetPath(Uri url, string mime)
        {
            string absPath = url.AbsolutePath; // 不含 query / fragment
            switch (_cfg.GroupingMethod)
            {
                case "AbsoluteRaw":
                    // SaveTo\<URL的host>\<绝对路径>，如 SaveTo\www.cdn1.com\static\video\1.mp4
                    return Path.Combine(_cfg.SaveTo, Sanitize(url.Host), BuildRelPath(absPath, mime));

                case "RelativeRaw":
                    // SaveTo\<相对路径>，不加 host 文件夹
                    return Path.Combine(_cfg.SaveTo, BuildRelPath(absPath, mime));

                case "Mime":
                {
                    // SaveTo\<mime含斜杠>\<文件名>，如 SaveTo\video\mp4\1.mp4
                    // 注意：mime 自带的斜杠应拆成目录层级，逐段清洗，不能整体当作文件名
                    string rawMime = string.IsNullOrEmpty(mime) ? "unknown" : mime;
                    var mimeParts = new List<string>();
                    foreach (var part in rawMime.Split('/'))
                        mimeParts.Add(Sanitize(part));
                    string mimeDir = Path.Combine(mimeParts.ToArray());
                    return Path.Combine(_cfg.SaveTo, mimeDir, BuildFileName(absPath, mime));
                }

                case "None":
                default:
                    // 全部堆到根目录
                    return Path.Combine(_cfg.SaveTo, BuildFileName(absPath, mime));
            }
        }

        private static string BuildRelPath(string absPath, string mime)
        {
            if (string.IsNullOrEmpty(absPath) || absPath == "/")
                return "index" + ExtFromMime(mime, ".html");

            var parts = new List<string>();
            string[] segs = absPath.Split('/');
            for (int i = 0; i < segs.Length; i++)
            {
                string s = segs[i];
                if (i == segs.Length - 1 && s.Length == 0) continue; // 末尾斜杠
                if (s.Length == 0 || s == ".") continue;
                if (s == "..")
                {
                    if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                    continue;
                }
                parts.Add(Sanitize(s));
            }

            if (parts.Count == 0) return "index" + ExtFromMime(mime, ".html");

            string last = parts[parts.Count - 1];
            if (Path.GetExtension(last).Length == 0)
                parts[parts.Count - 1] = last + ExtFromMime(mime, "");
            return Path.Combine(parts.ToArray());
        }

        private static string BuildFileName(string absPath, string mime)
        {
            if (string.IsNullOrEmpty(absPath) || absPath == "/")
                return "index" + ExtFromMime(mime, ".html");

            string last = absPath.TrimEnd('/').Split('/').Last();
            if (last.Length == 0) last = "index";
            last = Sanitize(last);
            if (Path.GetExtension(last).Length == 0)
                last += ExtFromMime(mime, "");
            return last;
        }

        // ---------------- 文件名/目录名清洗 ----------------

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";
            try { name = Uri.UnescapeDataString(name); } catch { /* 保留原样 */ }
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.', ' ');
            if (name.Length > 150) name = name.Substring(0, 150);
            if (name.Length == 0) name = "_";
            string core = name.Split('.')[0];
            if (ReservedNames.Contains(core)) name = "_" + name;
            return name;
        }

        // 常见 MIME -> 扩展名映射。
        // 用途：当 URL 路径本身没有后缀时（如误 GET 到的 API 地址），按响应 MIME 恢复后缀；
        // 若 MIME 为空或无法识别，则 fallback（调用处传 ""）返回空串，即“按无后缀文件保存”。
        private static string ExtFromMime(string mime, string fallback)
        {
            if (string.IsNullOrEmpty(mime)) return fallback;
            string lower = mime.ToLowerInvariant();

            switch (lower)
            {
                case "text/html": return ".html";
                case "text/css": return ".css";
                case "text/plain": return ".txt";
                case "text/javascript":
                case "application/javascript":
                case "application/x-javascript": return ".js";
                case "application/json": return ".json";
                case "application/xml":
                case "text/xml": return ".xml";
                case "image/jpeg": return ".jpg";
                case "image/png": return ".png";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
                case "image/svg+xml": return ".svg";
                case "image/x-icon": return ".ico";
                case "font/woff": return ".woff";
                case "font/woff2": return ".woff2";
                case "application/octet-stream":
                case "binary/octet-stream":
                case "application/binary":
                case "application/x-binary": return ".bin";
                case "application/x-msdownload":
                case "application/x-msdos-program": return ".exe";
                case "application/pdf": return ".pdf";
                case "application/zip": return ".zip";
                case "video/mp4": return ".mp4";
                case "audio/mpeg": return ".mp3";
                // HLS：M3U8 播放列表与 MPEG-TS 分片（无后缀 URL 时按 MIME 恢复后缀）
                case "application/vnd.apple.mpegurl":
                case "application/vnd.apple.mpegurl.audio":
                case "application/x-mpegurl":
                case "application/mpegurl":
                case "audio/mpegurl":
                case "audio/x-mpegurl": return ".m3u8";
                case "video/mp2t": return ".ts";
            }

            // image/*、video/*、audio/*、font/* 用子类型做扩展名
            if (lower.StartsWith("image/") || lower.StartsWith("video/") ||
                lower.StartsWith("audio/") || lower.StartsWith("font/"))
            {
                string subtype = lower.Substring(lower.IndexOf('/') + 1);
                if (subtype.Length > 0 && subtype.Length <= 12 && subtype.All(char.IsLetterOrDigit))
                    return "." + subtype;
            }
            return fallback;
        }
    }
}
