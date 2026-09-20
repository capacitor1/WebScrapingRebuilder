using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WebScrapingRebuilder.Storage
{
    /// <summary>
    /// 一行一个 URL 的文本列表文件（Failed.txt / IgnoredByPolicies.txt）。
    /// 线程安全；写入时按行去重（同一 URL 只写一次，跨运行也不重复）。
    /// </summary>
    public sealed class UrlListFile : IDisposable
    {
        private readonly string _path;
        private readonly object _sync = new object();
        private readonly HashSet<string> _written = new HashSet<string>(StringComparer.Ordinal);
        private StreamWriter _writer;

        public UrlListFile(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(_path))
                {
                    foreach (var line in File.ReadAllLines(_path))
                    {
                        string t = line.Trim();
                        if (t.Length > 0) _written.Add(t);
                    }
                }
            }
            catch
            {
                // 读取失败不影响后续写入
            }
        }

        public void Add(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            lock (_sync)
            {
                if (!_written.Add(url)) return;
                try
                {
                    if (_writer == null)
                    {
                        string dir = Path.GetDirectoryName(_path);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        _writer = new StreamWriter(
                            new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read),
                            new UTF8Encoding(false)) { AutoFlush = true };
                    }
                    _writer.WriteLine(url);
                }
                catch
                {
                    _written.Remove(url);
                }
            }
        }

        public void Flush()
        {
            lock (_sync)
            {
                try { _writer?.Flush(); } catch { }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
        }
    }
}
