using System;
using System.IO;
using System.Text;

namespace WebScrapingRebuilder.Ui
{
    /// <summary>控制台输出模式：完整输出 / 仅错误 / 静默（面板模式下由 ConsoleDashboard 负责显示）。</summary>
    public enum LogConsoleMode
    {
        Verbose,
        ErrorsOnly,
        Silent,
    }

    /// <summary>
    /// 日志器：固定写入 SaveTo 目录下的 Scraping.log；控制台是否镜像输出由 <see cref="ConsoleMode"/> 控制。
    /// </summary>
    public sealed class Logger : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _sync = new object();

        /// <summary>控制台镜像模式，默认完整输出。</summary>
        public LogConsoleMode ConsoleMode { get; set; } = LogConsoleMode.Verbose;

        /// <summary>最近一条 ERROR 文本（供控制台面板显示）。</summary>
        public string LastErrorMessage { get; private set; }

        public Logger(string logFilePath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(logFilePath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _writer = new StreamWriter(logFilePath, append: true, Encoding.UTF8) { AutoFlush = true };
            Info("================================================");
            Info($"抓取任务开始: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }

        public void Info(string message) => Write("INFO", message);
        public void Warn(string message) => Write("WARN", message);
        public void Error(string message) => Write("ERROR", message);

        private void Write(string level, string message)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
            lock (_sync)
            {
                _writer.WriteLine(line);
                if (level == "ERROR") LastErrorMessage = message;

                bool toConsole = ConsoleMode == LogConsoleMode.Verbose
                    || (ConsoleMode == LogConsoleMode.ErrorsOnly && level == "ERROR");
                if (toConsole)
                {
                    try { Console.WriteLine(line); } catch { /* 忽略控制台写入失败 */ }
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _writer.Dispose();
            }
        }
    }
}
