using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WebScrapingRebuilder.Storage
{
    /// <summary>
    /// 断点续传的持久化状态。关键校验信息：ETag、Last-Modified、Content-Length(TotalLength)。
    /// 约定：<see cref="BytesDownloaded"/> 永远只记录“已经确认落盘”的字节数，
    /// 绝不会大于数据文件的实际长度，从而保证崩溃后可安全回退续传、不会损坏文件。
    /// </summary>
    public sealed class PartialState
    {
        public string Url;
        public string ETag;
        public string LastModified;
        public long TotalLength = -1;   // -1 表示未知
        public long BytesDownloaded;
        public string Mime;
        public DateTime UpdatedUtc;

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("Version=1\n");
            sb.Append("Url=").Append(Url ?? string.Empty).Append('\n');
            sb.Append("ETag=").Append(ETag ?? string.Empty).Append('\n');
            sb.Append("LastModified=").Append(LastModified ?? string.Empty).Append('\n');
            sb.Append("TotalLength=").Append(TotalLength.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("BytesDownloaded=").Append(BytesDownloaded.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("Mime=").Append(Mime ?? string.Empty).Append('\n');
            sb.Append("UpdatedUtc=").Append(UpdatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            return sb.ToString();
        }

        public static PartialState Parse(string text)
        {
            var st = new PartialState();
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1);
                switch (key)
                {
                    case "Url": st.Url = value; break;
                    case "ETag": st.ETag = value; break;
                    case "LastModified": st.LastModified = value; break;
                    case "TotalLength": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out st.TotalLength); break;
                    case "BytesDownloaded": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out st.BytesDownloaded); break;
                    case "Mime": st.Mime = value; break;
                    case "UpdatedUtc": DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out st.UpdatedUtc); break;
                }
            }
            return st;
        }
    }

    /// <summary>
    /// 负责断点缓存文件（.part）与状态文件（.state）的路径计算、读写与清理。
    /// 缓存目录固定在 SaveTo\.partial\ 下，文件名取 URL 的 SHA256 前缀，保证同一 URL 稳定可续传。
    /// </summary>
    public sealed class PartialStore
    {
        public string Dir { get; }

        public PartialStore(string saveTo)
        {
            Dir = Path.Combine(saveTo, ".partial");
        }

        public string DataPath(string urlKey) => Path.Combine(Dir, UrlHash(urlKey) + ".part");
        public string StatePath(string urlKey) => Path.Combine(Dir, UrlHash(urlKey) + ".state");

        private static string UrlHash(string url) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url ?? string.Empty))).Substring(0, 16);

        public PartialState ReadState(string urlKey)
        {
            try
            {
                string path = StatePath(urlKey);
                if (!FileIo.Retry(() => File.Exists(path))) return null;
                return PartialState.Parse(FileIo.Retry(() => File.ReadAllText(path, Encoding.UTF8)));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>原子写入状态文件：先写唯一的临时文件并 flush 到磁盘，再整体替换，避免半截状态文件。</summary>
        public void WriteState(string urlKey, PartialState state)
        {
            FileIo.Retry(() => Directory.CreateDirectory(Dir));
            string path = StatePath(urlKey);
            // 临时名带随机后缀：避免与残留/并发写入的临时文件互相干扰（固定名易被瞬时占用）
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] bytes = Encoding.UTF8.GetBytes(state.Serialize());
            try
            {
                FileIo.WriteAllBytesFlushed(tmp, bytes);
                FileIo.Move(tmp, path, overwrite: true);
            }
            catch
            {
                TryDelete(tmp); // 失败时清掉临时文件，避免遗留
                throw;
            }
        }

        public void Delete(string urlKey)
        {
            // 删除是清理性质，失败也不应影响主流程
            TryDelete(DataPath(urlKey));
            TryDelete(StatePath(urlKey));
        }

        /// <summary>若缓存目录为空则删除，保持输出目录整洁。</summary>
        public void CleanupIfEmpty()
        {
            try
            {
                if (Directory.Exists(Dir) && Directory.GetFileSystemEntries(Dir).Length == 0)
                    Directory.Delete(Dir);
            }
            catch { /* 忽略 */ }
        }

        private static void TryDelete(string path)
        {
            try { FileIo.Delete(path); } catch { /* 忽略 */ }
        }
    }
}
