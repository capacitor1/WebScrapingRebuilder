using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using WebScrapingRebuilder.Ui;

namespace WebScrapingRebuilder.Hls
{
    /// <summary>提交给 M3U8 模块的解析任务（文本内容已在主流程读入内存，避免与文件移动竞争）。</summary>
    public sealed class M3u8Job
    {
        public string Content;   // M3U8 文本
        public Uri ParentUrl;    // M3U8 自身 URL（作为相对路径还原基准）
        public int Depth;        // 当前抓取深度
    }

    /// <summary>
    /// M3U8 抓取模块：**仅把“解析”环节解耦**，解析运行在独立后台线程上。
    ///
    /// 主流程发现 M3U8/M3U 文件（或 HLS MIME）后，把文本内容提交到这里；本模块负责
    /// 提取分片 / 初始化段(#EXT-X-MAP) / 密钥(#EXT-X-KEY) / 变体播放列表等 URL，
    /// 并把相对路径还原为绝对 URL，最后通过回调把 URL 交回主线程。
    ///
    /// **不存在独立的下载队列或独立的下载规则**：交回的 URL 与普通页面提取的 URL 完全一致地
    /// 合并进主下载队列，由同一批工作线程按同一套配置（体积上下限 / MIME / 去重 / 断点续传等）下载。
    ///
    /// 触发与解析规则全部写死在代码中，不受配置控制。
    /// </summary>
    public sealed class M3u8Module
    {
        private readonly Logger _log;
        private readonly Action<M3u8Job, List<Uri>> _onParsed; // 主线程回调：把解析出的绝对 URL 入队下载
        private readonly Action _jobStarted;                   // 主流程：占用一个“未完成工作”额度
        private readonly Action _jobFinished;                  // 主流程：释放额度
        private readonly ConcurrentQueue<M3u8Job> _jobs = new ConcurrentQueue<M3u8Job>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private volatile bool _stop;
        private Thread _thread;

        public M3u8Module(Logger log,
                          Action<M3u8Job, List<Uri>> onParsed,
                          Action jobStarted,
                          Action jobFinished)
        {
            _log = log;
            _onParsed = onParsed;
            _jobStarted = jobStarted;
            _jobFinished = jobFinished;
        }

        /// <summary>待解析任务数（供控制台“待处理”统计）。</summary>
        public int PendingCount => _jobs.Count;

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "M3u8Module" };
            _thread.Start();
            _log.Info("[M3U8] 独立解析模块已启动（仅解析环节解耦，提取的 URL 合并进主下载队列）");
        }

        /// <summary>提交一个 M3U8 解析任务（非阻塞，立即返回）。</summary>
        public void Submit(M3u8Job job)
        {
            _jobStarted();
            _jobs.Enqueue(job);
            _signal.Set();
        }

        /// <summary>
        /// 停止模块。停止前会把剩余任务全部解析完（解析是纯内存操作、很快），
        /// 结果仍会入队，从而保证 Ctrl+C 收尾时不会丢失已下载 M3U8 的分片 URL。
        /// </summary>
        public void StopAndDrain()
        {
            _stop = true;
            _signal.Set();
            try { _thread?.Join(); } catch { /* 忽略 */ }
            _log.Info("[M3U8] 独立解析模块已停止");
        }

        private void Loop()
        {
            while (true)
            {
                if (_jobs.TryDequeue(out var job))
                {
                    try { Process(job); }
                    catch (Exception ex) { _log.Error($"[M3U8] 解析出错：{ex.Message}"); }
                    finally { _jobFinished(); }
                    continue;
                }

                if (_stop) break;
                _signal.WaitOne(200);
            }
        }

        private void Process(M3u8Job job)
        {
            M3u8ParseResult r = M3u8Parser.Parse(job.Content, job.ParentUrl);

            if (!r.IsM3u8)
            {
                _log.Warn($"[M3U8] {job.ParentUrl} 不是合法 M3U8，忽略：{r.Reason}");
                return;
            }
            if (r.IsLive)
                _log.Warn($"[M3U8] {job.ParentUrl} 为直播流（{r.Reason}）：不循环轮询扩展，仅抓取当前快照中的分片");

            _log.Info($"[M3U8] 解析 {job.ParentUrl}（{r.Reason}），提取到 {r.Urls.Count} 条绝对 URL");
            if (r.Urls.Count > 0)
                _onParsed(job, r.Urls);
        }
    }
}
