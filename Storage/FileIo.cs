using System;
using System.IO;
using System.Threading;

namespace WebScrapingRebuilder.Storage
{
    /// <summary>
    /// 文件操作的“瞬时错误”重试工具。
    ///
    /// 背景：在 Windows 上，杀毒软件 / Windows Search 索引器 / 云同步（OneDrive 等）的
    /// 过滤驱动会在文件刚写入或关闭后**短暂**持有句柄，导致紧接着的
    /// <c>File.Move</c> / <c>File.Delete</c> / 重新打开随机抛出：
    ///   - <see cref="IOException"/>：“The process cannot access the file because it is being used by another process.”
    ///   - <see cref="UnauthorizedAccessException"/>：“Access to the path ... is denied.”
    /// 这些通常是瞬时的，稍微等待后重试即可成功，不代表真正的权限问题。
    ///
    /// 注意：<see cref="UnauthorizedAccessException"/> **不是** <see cref="IOException"/> 的子类，
    /// 所以必须单独判定，否则会漏掉这类偶发失败。
    /// </summary>
    internal static class FileIo
    {
        public const int DefaultRetries = 6;

        public static bool IsTransient(Exception ex) =>
            ex is IOException || ex is UnauthorizedAccessException;

        public static void Retry(Action action) => Retry<object>(() => { action(); return null; });

        public static T Retry<T>(Func<T> func)
        {
            int delay = 40;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return func();
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    if (attempt >= DefaultRetries) throw;
                    Thread.Sleep(delay);
                    delay = Math.Min(delay * 2, 400); // 40/80/160/320/400/400 ms，总计约 1.6s
                }
            }
        }

        /// <summary>删除文件（不存在则忽略）。</summary>
        public static void Delete(string path) =>
            Retry(() => { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); });

        /// <summary>移动文件。overwrite=true 时会覆盖目标（内部走原子替换）。</summary>
        public static void Move(string source, string dest, bool overwrite) =>
            Retry(() => File.Move(source, dest, overwrite));

        /// <summary>把字节写入文件并 flush 到磁盘（用于状态文件的原子写）。</summary>
        public static void WriteAllBytesFlushed(string path, byte[] bytes) =>
            Retry(() =>
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            });
    }
}
