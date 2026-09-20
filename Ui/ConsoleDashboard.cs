using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using WebScrapingRebuilder.Core;

namespace WebScrapingRebuilder.Ui
{
    /// <summary>
    /// 控制台状态面板：不再逐条打印日志，而是周期性刷新关键指标
    /// （速度、文件进度、已下载大小、错误数、正在下载的 URL 预览等）。
    /// 日志本身仍然完整写入 Scraping.log。
    /// </summary>
    public sealed class ConsoleDashboard : IDisposable
    {
        private const int MaxActiveShown = 8;

        private readonly ScrapeStats _stats;
        private readonly Logger _log;
        private readonly object _writeLock = new object();
        private Thread _thread;
        private volatile bool _running;

        public ConsoleDashboard(ScrapeStats stats, Logger log)
        {
            _stats = stats;
            _log = log;
        }

        public void Start()
        {
            try { Console.Clear(); } catch { /* 忽略 */ }
            try { Console.Write("\x1b[?25l"); } catch { /* 隐藏光标 */ }
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ConsoleDashboard" };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                try { Render(); } catch { /* 面板渲染失败不影响抓取 */ }
                Thread.Sleep(250);
            }
        }

        private void Render()
        {
            int width = SafeWidth();
            List<string> lines = BuildLines();
            var sb = new StringBuilder();
            sb.Append("\x1b[H"); // 光标归位
            foreach (string line in lines)
            {
                sb.Append(Fit(line, width));
                sb.Append("\x1b[K"); // 清除行尾
                sb.Append("\r\n");
            }
            sb.Append("\x1b[J"); // 清除下方残留
            lock (_writeLock) { Console.Write(sb.ToString()); }
        }

        public void Stop()
        {
            _running = false;
            try { _thread?.Join(800); } catch { /* 忽略 */ }
            lock (_writeLock)
            {
                try
                {
                    Console.Write("\x1b[H\x1b[J"); // 清空面板
                    Console.Write("\x1b[?25h");    // 恢复光标
                }
                catch { /* 忽略 */ }
            }
        }

        public void Dispose() => Stop();

        // ---------------- 渲染内容 ----------------

        private List<string> BuildLines()
        {
            var lines = new List<string>();
            int pending = _stats.PendingCountProvider?.Invoke() ?? 0;
            List<ActiveDownload> active = _stats.ActiveSnapshot();
            double speed = _stats.SampleSpeed();
            double avg = _stats.AverageSpeed;
            TimeSpan elapsed = DateTime.UtcNow - _stats.StartUtc;

            lines.Add("════════════════ 网页抓取重建器 ════════════════");
            lines.Add($"阶段 {_stats.Phase}    运行 {FormatDuration(elapsed)}    线程 {_stats.ThreadCount}    待处理 {pending}");
            lines.Add($"已下载 {_stats.Downloaded} 个（{FormatSize(_stats.DownloadedBytes)}）    跳过 {_stats.Skipped}    " +
                      $"提取URL {_stats.Extracted}    HTTP错误 {_stats.HttpErrors}");
            lines.Add($"速度 {FormatSpeed(speed)}    平均 {FormatSpeed(avg)}    下载中 {active.Count}");
            lines.Add("──────────────── 正在下载 ────────────────");

            if (active.Count == 0)
            {
                lines.Add("  （当前没有进行中的下载）");
            }
            else
            {
                foreach (ActiveDownload a in active.Take(MaxActiveShown))
                {
                    long received = Volatile.Read(ref a.Received);
                    long total = Volatile.Read(ref a.Total);
                    string bar = ProgressBar(received, total, 24);
                    string pct = total > 0 ? (received * 100.0 / total).ToString("F1").PadLeft(5) + "%" : "  --%";
                    string size = total > 0
                        ? $"{FormatSize(received)}/{FormatSize(total)}"
                        : FormatSize(received);
                    lines.Add($"  {bar} {pct}  {size,-18} {a.Url}");
                }
                if (active.Count > MaxActiveShown)
                    lines.Add($"  …另有 {active.Count - MaxActiveShown} 个下载中");
            }

            lines.Add("──────────────── 状态 ────────────────");
            string err = _log.LastErrorMessage;
            lines.Add($"最后错误: {(string.IsNullOrEmpty(err) ? "无" : err)}");
            return lines;
        }

        private static string ProgressBar(long received, long total, int width)
        {
            if (total <= 0) return new string('▒', width);
            double ratio = Math.Clamp(received / (double)total, 0, 1);
            int filled = (int)Math.Round(ratio * width);
            return new string('█', filled) + new string('░', width - filled);
        }

        private static string FormatDuration(TimeSpan t) =>
            $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("F2") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F2") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
        }

        private static string FormatSpeed(double bytesPerSecond) => FormatSize((long)bytesPerSecond) + "/s";

        // ---------------- 文本宽度处理（兼容中文） ----------------

        private static int SafeWidth()
        {
            try
            {
                int w = Console.WindowWidth;
                return w >= 40 ? w - 1 : 100;
            }
            catch { return 100; }
        }

        private static string Fit(string s, int width)
        {
            if (DisplayWidth(s) <= width) return s;
            var sb = new StringBuilder();
            int w = 0;
            foreach (char c in s)
            {
                int cw = IsWide(c) ? 2 : 1;
                if (w + cw > width - 1) { sb.Append('…'); break; }
                sb.Append(c);
                w += cw;
            }
            return sb.ToString();
        }

        private static int DisplayWidth(string s)
        {
            int w = 0;
            foreach (char c in s) w += IsWide(c) ? 2 : 1;
            return w;
        }

        private static bool IsWide(char c) =>
            (c >= 0x1100 && c <= 0x115F) ||
            (c >= 0x2E80 && c <= 0xA4CF) ||
            (c >= 0xAC00 && c <= 0xD7A3) ||
            (c >= 0xF900 && c <= 0xFAFF) ||
            (c >= 0xFE30 && c <= 0xFE6F) ||
            (c >= 0xFF00 && c <= 0xFF60) ||
            (c >= 0xFFE0 && c <= 0xFFE6) ||
            (c >= 0x20000 && c <= 0x3FFFD);
    }
}
