using System;
using System.IO;
using System.Text;
using WebScrapingRebuilder.Core;
using WebScrapingRebuilder.Ui;

namespace WebScrapingRebuilder
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // 启动必须且只能带一个参数：抓取任务配置文件
            if (args.Length != 1)
            {
                Console.WriteLine("用法：WebScrapingRebuilder.exe <抓取任务配置文件.ini>");
                Console.WriteLine("必须且只能指定一个参数：指向 INI 格式的抓取任务配置文件。");
                return 1;
            }

            string configPath = args[0];
            if (!File.Exists(configPath))
            {
                Console.WriteLine($"错误：配置文件不存在：{configPath}");
                return 1;
            }

            ScrapeConfig config;
            try
            {
                config = ScrapeConfig.Load(configPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"配置文件加载失败：{ex.Message}");
                return 1;
            }

            try
            {
                Directory.CreateDirectory(config.SaveTo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"无法创建保存目录 {config.SaveTo}：{ex.Message}");
                return 1;
            }

            string logPath = Path.Combine(config.SaveTo, "Scraping.log");
            using (var logger = new Logger(logPath))
            {
                // 交互式终端：用状态面板代替刷屏日志；非交互（重定向/管道）：控制台只显示错误。
                // 可用环境变量 WSR_DASHBOARD=1/0 强制开/关。
                string dashEnv = Environment.GetEnvironmentVariable("WSR_DASHBOARD");
                bool interactive = dashEnv == "1" || (string.IsNullOrEmpty(dashEnv) && !Console.IsOutputRedirected);
                var stats = new ScrapeStats();
                ConsoleDashboard dashboard = null;
                if (interactive)
                {
                    logger.ConsoleMode = LogConsoleMode.Silent;
                    dashboard = new ConsoleDashboard(stats, logger);
                    dashboard.Start();
                }
                else
                {
                    logger.ConsoleMode = LogConsoleMode.ErrorsOnly;
                }

                Scraper scraper = null;
                bool stopRequested = false;

                ConsoleCancelEventHandler handler = (sender, e) =>
                {
                    if (stopRequested)
                    {
                        // 第二次 Ctrl+C：不拦截，立即强制退出
                        return;
                    }
                    stopRequested = true;
                    e.Cancel = true; // 拦截本次退出，交给抓取器按 CtrlCAction 收尾
                    logger.Warn("[信号] 收到 Ctrl+C：正在收尾（刷写日志、将待抓取列表写入 InQueue.txt）。再次 Ctrl+C 将强制退出。");
                    scraper?.RequestStop();
                };

                Console.CancelKeyPress += handler;
                try
                {
                    logger.Info($"加载配置文件成功：{Path.GetFullPath(configPath)}");
                    scraper = new Scraper(config, logger, stats);
                    scraper.Run();
                }
                catch (Exception ex)
                {
                    logger.Error($"程序运行出错：{ex}");
                }
                finally
                {
                    Console.CancelKeyPress -= handler;
                    if (dashboard != null) dashboard.Stop();
                }

                // 面板停止 / 或非交互模式下，把最终摘要打印到控制台
                try
                {
                    Console.WriteLine(stats.BuildSummaryText());
                    Console.WriteLine($"结果列表文件（位于 {config.SaveTo}）：Failed.txt、IgnoredByPolicies.txt、InQueue.txt(中断时)");
                }
                catch { /* 忽略 */ }
            }
            return 0;
        }
    }
}
