using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WebScrapingRebuilder.Core
{
    /// <summary>单个正在下载的任务进度（供控制台面板读取）。</summary>
    public sealed class ActiveDownload
    {
        public string Key;
        public string Url;
        public long Received;   // 已接收字节
        public long Total;      // 总字节，-1 表示未知
        public DateTime StartedUtc;
    }

    /// <summary>
    /// 线程安全的抓取统计信息。抓取线程只写入，控制台面板只读取，互不阻塞。
    /// </summary>
    public sealed class ScrapeStats
    {
        public DateTime StartUtc { get; } = DateTime.UtcNow;

        private long _downloaded;
        private long _skipped;
        private long _extracted;
        private long _httpErrors;
        private long _bytes;

        private readonly ConcurrentDictionary<string, ActiveDownload> _active =
            new ConcurrentDictionary<string, ActiveDownload>(StringComparer.Ordinal);

        private readonly object _speedLock = new object();
        private readonly Queue<(DateTime T, long Bytes)> _samples = new Queue<(DateTime, long)>();

        /// <summary>由抓取器提供：当前待处理任务数（队列 + 正在处理）。</summary>
        public Func<int> PendingCountProvider;

        /// <summary>当前阶段描述，如“抓取中”“收尾中”。</summary>
        public volatile string Phase = "初始化";

        /// <summary>下载线程数。</summary>
        public volatile int ThreadCount;

        /// <summary>停止原因：null | "user" | "limit"。</summary>
        public volatile string StopReason;

        public long Downloaded => Interlocked.Read(ref _downloaded);
        public long Skipped => Interlocked.Read(ref _skipped);
        public long Extracted => Interlocked.Read(ref _extracted);
        public long HttpErrors => Interlocked.Read(ref _httpErrors);
        public long DownloadedBytes => Interlocked.Read(ref _bytes);

        public void AddDownloaded() => Interlocked.Increment(ref _downloaded);
        public void AddSkipped() => Interlocked.Increment(ref _skipped);
        public void AddHttpError() => Interlocked.Increment(ref _httpErrors);
        public void AddExtracted(int n) => Interlocked.Add(ref _extracted, n);
        public void AddBytes(long n) => Interlocked.Add(ref _bytes, n);

        public void BeginDownload(string key, string url, long received, long total)
        {
            _active[key] = new ActiveDownload
            {
                Key = key,
                Url = url,
                Received = received,
                Total = total,
                StartedUtc = DateTime.UtcNow,
            };
        }

        public void UpdateProgress(string key, long received, long total)
        {
            if (_active.TryGetValue(key, out var ad))
            {
                Volatile.Write(ref ad.Received, received);
                Volatile.Write(ref ad.Total, total);
            }
        }

        public void EndDownload(string key) => _active.TryRemove(key, out _);

        public List<ActiveDownload> ActiveSnapshot() =>
            _active.Values.OrderBy(a => a.StartedUtc).ToList();

        /// <summary>滑动窗口测速：返回最近约 3 秒的平均下载速度（字节/秒）。</summary>
        public double SampleSpeed()
        {
            lock (_speedLock)
            {
                DateTime now = DateTime.UtcNow;
                long bytes = DownloadedBytes;
                _samples.Enqueue((now, bytes));
                while (_samples.Count > 2 && (now - _samples.Peek().T).TotalSeconds > 3.0)
                    _samples.Dequeue();

                if (_samples.Count < 2) return 0;
                var first = _samples.Peek();
                var last = _samples.Last();
                double dt = (last.T - first.T).TotalSeconds;
                if (dt <= 0) return 0;
                double v = (last.Bytes - first.Bytes) / dt;
                return v < 0 ? 0 : v;
            }
        }

        public double AverageSpeed
        {
            get
            {
                double sec = (DateTime.UtcNow - StartUtc).TotalSeconds;
                return sec <= 0 ? 0 : DownloadedBytes / sec;
            }
        }

        public string BuildSummaryText()
        {
            string reason = StopReason == "user" ? "（用户中断）"
                          : StopReason == "limit" ? "（达到下载数量限制）"
                          : "";
            return $"抓取结束{reason}：共下载 {Downloaded} 个文件，" +
                   $"跳过 {Skipped} 个，" +
                   $"提取到URL {Extracted} 条，" +
                   $"HTTP错误 {HttpErrors} 个";
        }
    }
}
