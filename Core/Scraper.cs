using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using WebScrapingRebuilder.Hls;
using WebScrapingRebuilder.Net;
using WebScrapingRebuilder.Storage;
using WebScrapingRebuilder.Ui;

namespace WebScrapingRebuilder.Core
{
    /// <summary>
    /// 网页抓取引擎：以 Netloc 为入口做 BFS 循环抓取，支持多线程并发下载。
    /// 内部用线程安全的 HashSet 记录已见过的完整 URL（含参数），避免重复抓取；
    /// 保存文件时去掉 URL 参数（本地文件无法保存参数）。
    /// 下载先缓存到 SaveTo\.partial\ 再移动，支持基于 ETag/Last-Modified/Content-Length 的断点续传。
    /// 支持 Ctrl+C 优雅中断：写入 InQueue.txt，下次运行自动续抓。
    /// </summary>
    public sealed class Scraper
    {
        public const string InQueueFileName = "InQueue.txt";
        public const string FailedFileName = "Failed.txt";
        public const string IgnoredByPoliciesFileName = "IgnoredByPolicies.txt";

        private readonly ScrapeConfig _cfg;
        private readonly Logger _log;
        private readonly ScrapeStats _stats;
        private readonly FileSaver _saver;
        private readonly UrlNormalizer _normalizer;
        private readonly PartialStore _partials;
        private readonly HttpClient _http;

        // 断点状态文件的最小写入间隔：两次修改之间必须 >= 60 秒。
        // 写死在代码中，不可通过 INI 配置；目的是避免频繁创建/删除状态文件
        // 触发杀软/索引器的过滤驱动锁文件，导致偶发的 “Access to the path is denied.”。
        private static readonly TimeSpan StateWriteInterval = TimeSpan.FromSeconds(60);

        // IntervalMs == 0 时启用“指数退避”等待（每个工作线程独立维护）：
        // 首次等待 1s，连续下载失败则翻倍，上限 60s；任一 URL 下载成功后重置回 1s。
        private const int BackoffInitialMs = 1000;
        private const int BackoffMaxMs = 60000;
        [ThreadStatic] private static int _tlsBackoffMs;

        // 已见 URL 表：key = 归一化后的完整 URL（含 query 参数、去 fragment），完全相同才算重复；
        // value = 入队序号（单调递增），供 Ctrl+C 收尾时按“入队顺序”写回 InQueue.txt。
        private readonly ConcurrentDictionary<string, long> _seen =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();
        // 正在下载中的 URL（便于诊断）
        private readonly ConcurrentDictionary<string, Uri> _inFlight =
            new ConcurrentDictionary<string, Uri>(StringComparer.Ordinal);
        // 已完成处理的 URL（下载成功 / 跳过 / HTTP 失败 / 被策略拦截 / 深度或体积越界等）。
        // 收尾时 “_seen 中所有未完成的 URL” 即为完整的待下载队列，且顺序可按入队序号还原。
        private readonly ConcurrentDictionary<string, byte> _completed =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private long _seq;   // 入队序号发号器

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _stopLock = new object();

        private readonly UrlListFile _failedList;
        private readonly UrlListFile _ignoredList;

        // 未完成工作计数：= 队列中待处理 + 正在处理 + M3U8 独立解析中。
        // 配合 _workLock 使用：入队与“是否全部完成”的判断都在锁内进行，
        // 保证异步（M3U8 模块线程）入队的 URL 不会被提前判定为“抓取结束”而丢失。
        private readonly object _workLock = new object();
        private int _outstanding;
        private int _draining;              // 1 = Finish 模式：不再取新任务，等待在途任务完成
        private string _stopReason;         // null | "user" | "limit"

        private M3u8Module _m3u8;

        private sealed class WorkItem
        {
            public Uri Url;
            public int Depth;
            public Uri Parent;
        }

        public Scraper(ScrapeConfig cfg, Logger logger, ScrapeStats stats)
        {
            _cfg = cfg;
            _log = logger;
            _stats = stats;
            _saver = new FileSaver(cfg);
            _normalizer = new UrlNormalizer(cfg);
            _partials = new PartialStore(cfg.SaveTo);
            _failedList = new UrlListFile(Path.Combine(cfg.SaveTo, FailedFileName));
            _ignoredList = new UrlListFile(Path.Combine(cfg.SaveTo, IgnoredByPoliciesFileName));
            _stats.PendingCountProvider = () => _queue.Count + _inFlight.Count + (_m3u8?.PendingCount ?? 0);

            // M3U8 独立解析模块（写死逻辑，与主抓取线程解耦）
            _m3u8 = new M3u8Module(_log, OnM3u8Parsed, BeginAuxJob, EndAuxJob);

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 20,
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        }

        /// <summary>由 Ctrl+C 等外部信号调用：按 CtrlCAction 配置请求优雅停止。</summary>
        public void RequestStop() => RequestStopInternal("user");

        public void Run()
        {
            // M3U8 模块独立运行，先于工作线程启动
            _m3u8.Start();
            try
            {
                // 先把解析到的配置完整回显一遍，便于核对
                _cfg.Dump(_log);

                bool resumed = TryLoadResumeQueue();
                if (!resumed)
                    TryEnqueue(_cfg.Netloc, 0, null);
                else
                    _log.Info("[队列] 已从 InQueue.txt 恢复上次任务并续抓（续抓条目的深度按 0 处理）");

                _stats.Phase = "抓取中";
                StartWorkers();
            }
            finally
            {
                // 工作线程已结束：排空 M3U8 解析任务（结果入队，供收尾写入 InQueue），再收尾
                _m3u8.StopAndDrain();
                FinalizeRun();
            }
        }

        // ---------------- 工作线程调度 ----------------

        private void StartWorkers()
        {
            int count = NormalizeThreadCount(_cfg.MultiThread);
            _stats.ThreadCount = count;
            string pacing = _cfg.IntervalMs > 0
                ? $"固定间隔 IntervalMs={_cfg.IntervalMs}ms"
                : $"指数退避模式（IntervalMs=0：初始 {BackoffInitialMs}ms、上限 {BackoffMaxMs}ms、成功后重置）";
            _log.Info($"启动 {count} 个工作线程进行抓取（MultiThread={_cfg.MultiThread}，{pacing}；CtrlCAction={_cfg.CtrlCAction}）");

            var threads = new List<Thread>(count);
            for (int i = 0; i < count; i++)
            {
                var t = new Thread(WorkerLoop) { IsBackground = true, Name = "ScraperWorker-" + i };
                threads.Add(t);
                t.Start();
            }
            foreach (var t in threads) t.Join();
        }

        private static int NormalizeThreadCount(int configured)
        {
            if (configured <= 0) return 1;
            if (configured > 256) return 256;
            return configured;
        }

        private void WorkerLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                // Finish 模式：不再取新任务，当前在途任务处理完后退出
                if (Volatile.Read(ref _draining) != 0) break;

                if (_queue.TryDequeue(out var item))
                {
                    string key = item.Url.ToString();
                    _inFlight[key] = item.Url;
                    try
                    {
                        ProcessUrl(item.Url, item.Depth, item.Parent);
                        // 正常返回 = 该 URL 已彻底处理完（下载 / 跳过 / HTTP 失败 / 被策略拦截等），
                        // 不再属于“待下载队列”。
                        _completed[key] = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        // 用户/限制中断：该 URL 尚未处理完，**不能**标记完成；
                        // 收尾时由 “_seen - _completed” 完整写回 InQueue.txt。
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"[异常] 处理 {item.Url} 出错：{ex.Message}");
                        _completed[key] = 0;
                    }
                    finally
                    {
                        _inFlight.TryRemove(key, out _);
                        CompleteOneWork();
                    }
                    continue;
                }

                // 队列为空：只有在“没有任何未完成工作”（队列 + 处理中 + M3U8 解析中）时才退出。
                // 判断在 _workLock 内完成，确保此刻不会有正在入队的 URL 被漏掉。
                if (NoOutstandingWork()) break;
                Thread.Sleep(20);
            }
        }

        private void CompleteOneWork()
        {
            lock (_workLock) { _outstanding--; }
        }

        private bool NoOutstandingWork()
        {
            lock (_workLock) { return _outstanding == 0 && _queue.IsEmpty; }
        }

        /// <summary>归一化 + 去重 + 入队（Netloc、页面提取、M3U8 解析结果都走这里）。</summary>
        private void TryEnqueue(Uri url, int depth, Uri parent)
        {
            Uri normalized;
            try { normalized = _normalizer.Normalize(url); }
            catch { normalized = url; }

            // 入队与 _outstanding 自增在同一把锁内完成，避免工作线程在
            // “元素已进入 _seen 但尚未入队”的瞬间误判为全部完成。
            lock (_workLock)
            {
                long seq = Interlocked.Increment(ref _seq);
                if (_seen.TryAdd(normalized.ToString(), seq))
                {
                    _outstanding++;
                    _queue.Enqueue(new WorkItem { Url = normalized, Depth = depth, Parent = parent });
                }
            }
        }

        // ---------------- M3U8 模块与主线程的衔接 ----------------

        /// <summary>M3U8 解析任务开始：占用一个未完成工作额度（防止工作线程提前退出）。</summary>
        private void BeginAuxJob()
        {
            lock (_workLock) { _outstanding++; }
        }

        /// <summary>M3U8 解析任务结束：释放额度。</summary>
        private void EndAuxJob()
        {
            lock (_workLock) { _outstanding--; }
        }

        /// <summary>M3U8 模块解析完成后的回调：把绝对 URL 返还给主线程入队，走通用下载。</summary>
        private void OnM3u8Parsed(M3u8Job job, List<Uri> urls)
        {
            int before = _seen.Count;
            // 解析出的 URL 与普通页面提取的 URL 完全同等待遇：统一走 TryEnqueue 合并进【主下载队列】，
            // 由同一批工作线程、按同一套配置规则（体积上下限、MIME/扩展名、去重、断点续传等）下载。
            // M3U8 模块的“解耦”仅指解析环节，不存在独立的下载队列或独立的下载规则。
            foreach (var u in urls)
                TryEnqueue(u, job.Depth + 1, job.ParentUrl);
            int added = _seen.Count - before;

            _stats.AddExtracted(urls.Count);
            _log.Info($"[M3U8] 已返还 {urls.Count} 条绝对 URL 给主线程通用下载（新增 {added} 条），待处理队列 {_queue.Count} 条");
        }

        private void RequestStopInternal(string reason)
        {
            lock (_stopLock)
            {
                if (_stopReason != null) return;
                _stopReason = reason;

                if (reason == "limit")
                {
                    _log.Info($"已达到可选限制 MaxDownloadCount={_cfg.MaxDownloadCount}，停止抓取");
                    try { _cts.Cancel(); } catch { }
                }
                else if (reason == "user" && string.Equals(_cfg.CtrlCAction, "Finish", StringComparison.OrdinalIgnoreCase))
                {
                    // 不取消，让正在下载的文件先下完；工作线程会在本轮结束后自然退出
                    Volatile.Write(ref _draining, 1);
                    _stats.Phase = "等待在途下载完成";
                    _log.Info("[信号] CtrlCAction=Finish：停止领取新任务，等待正在下载的文件完成后退出");
                }
                else
                {
                    _stats.Phase = "正在中断收尾";
                    try { _cts.Cancel(); } catch { }
                }
            }
        }

        // ---------------- 单个 URL 处理 ----------------

        private void ProcessUrl(Uri url, int depth, Uri parent)
        {
            // 已收到中断信号：本 URL 尚未处理，抛出让 WorkerLoop 记为“未完成”。
            // 不能直接 return，否则该 URL 会被误判为已处理，从而从 InQueue.txt 中丢失。
            if (_cts.IsCancellationRequested) throw new OperationCanceledException(_cts.Token);
            _log.Info($"[抓取] 处理：{url}（深度 {depth}，引用页 {parent?.ToString() ?? "-"}）");

            if (_cfg.MaxDepth > 0 && depth > _cfg.MaxDepth)
            {
                _log.Info($"[跳过] 超过最大深度限制 MaxDepth={_cfg.MaxDepth}：{url}");
                _stats.AddSkipped();
                return;
            }

            if (!HostAllowed(url))
            {
                _log.Info($"[跳过] 主机 {url.Host} 不在允许范围内：{url}");
                // 被 Host 策略拦截而跳过的 URL 全部记入 IgnoredByPolicies.txt（按行去重）
                _ignoredList.Add(url.ToString());
                _stats.AddSkipped();
                return;
            }

            string ext = GetExt(url);
            // M3U8/M3U 支持写死在代码中：不受 IgnoredFileExt 等忽略策略影响，遇到就一定解析下载
            bool isM3u8Ext = M3u8Parser.IsM3u8Extension(ext);
            if (!isM3u8Ext && ext != null && _cfg.IgnoredFileExt.Contains(ext))
            {
                _log.Info($"[跳过] 扩展名被忽略(.{ext})：{url}");
                _stats.AddSkipped();
                return;
            }

            string urlKey = url.ToString();
            string tempFile = null;
            try
            {
                var result = Download(url, parent);
                if (result.TempFile == null)
                {
                    ReportRequestOutcome(success: false);
                    // 失败/跳过：清理断点缓存，避免遗留无主文件
                    _partials.Delete(urlKey);
                    if (result.HttpError)
                    {
                        _stats.AddHttpError();
                        _failedList.Add(urlKey); // 下载失败 -> Failed.txt
                    }
                    return; // 跳过原因已在 Download 中记录
                }
                tempFile = result.TempFile;
                ReportRequestOutcome(success: true);

                // M3U8 支持写死在代码中：按后缀或 HLS MIME 判定并提交独立解析线程。
                // 这里的“解耦”仅限解析环节；文件本身仍受体积上下限约束，解析出的 URL 也走主下载队列。
                bool isM3u8 = isM3u8Ext || M3u8Parser.IsM3u8Mime(result.Mime);

                if (!isM3u8 && IsMimeIgnored(result.Mime))
                {
                    _log.Info($"[跳过] MIME被忽略({result.Mime})：{url}");
                    SafeDelete(tempFile);
                    _partials.Delete(urlKey);
                    _stats.AddSkipped();
                    return;
                }

                if (result.SizeBytes < _cfg.MinDownloadSizeKb * 1024L)
                {
                    _log.Info($"[跳过] 文件过小，低于 MinDownloadSizeKb={_cfg.MinDownloadSizeKb}（{result.SizeBytes / 1024.0:F2} KB）：{url}");
                    SafeDelete(tempFile);
                    _partials.Delete(urlKey);
                    _stats.AddSkipped();
                    return;
                }

                // 可解析为文本的内容 -> 提取新 URL（在移动临时文件前读取）
                List<Uri> newUrls = null;
                if (isM3u8)
                {
                    // 与主线程解耦：这里只读内存文本，提交给独立的 M3U8 模块异步解析；
                    // 解析出的绝对 URL 稍后由模块回调 TryEnqueue 返还给主线程通用下载。
                    string text = ReadTextFile(tempFile);
                    if (text == null)
                    {
                        _log.Warn($"[M3U8] 无法读取文本内容，跳过解析：{url}");
                    }
                    else
                    {
                        _log.Info($"[M3U8] 检测到 M3U8（ext={ext ?? "-"}，mime={result.Mime ?? "-"}），提交独立模块解析：{url}");
                        _m3u8.Submit(new M3u8Job { Content = text, ParentUrl = url, Depth = depth });
                    }
                }
                else if (IsExtractable(ext, result.Mime))
                {
                    _log.Info($"[提取] 按文本解析 {url}（ext={ext ?? "-"}，mime={result.Mime ?? "-"}）");
                    newUrls = ExtractFromFile(tempFile, url);
                }

                // 保存（含冲突处理）
                SaveResult save = _saver.Save(tempFile, url, result.Mime);
                tempFile = null; // 已移动或已删除
                _partials.Delete(urlKey); // 数据已落到最终位置，清掉状态文件

                if (save.DuplicateSkipped)
                {
                    _log.Info($"[冲突] 与已有文件内容相同（SHA256一致），仅保留一份：{url}");
                    _stats.AddSkipped();
                }
                else
                {
                    _stats.AddDownloaded();
                    string note = save.WasRenamed ? "（本地文件冲突，已重命名）"
                                : save.WasOverwritten ? "（大小相同，已覆盖已有文件）"
                                : "";
                    _log.Info($"[下载] {url} -> {save.FinalPath}（{result.SizeBytes / 1024.0:F2} KB，mime={result.Mime ?? "unknown"}）{note}");
                }

                // 入队新 URL（归一化后与已见过的完全相同者不再入队）
                if (newUrls != null && newUrls.Count > 0)
                {
                    int before = _seen.Count;
                    foreach (var u in newUrls)
                        TryEnqueue(u, depth + 1, url);
                    int added = _seen.Count - before;
                    _stats.AddExtracted(newUrls.Count);
                    _log.Info($"[提取] 从 {url} 提取到 {newUrls.Count} 条URL，其中新URL {added} 条，待处理队列 {_queue.Count} 条");
                }
            }
            catch (OperationCanceledException)
            {
                // CtrlCAction=KeepPartial 时保留 .part/.state，下次自动续传；Discard 时删除
                if (!ShouldKeepPartial)
                {
                    SafeDelete(tempFile);
                    _partials.Delete(urlKey);
                }
                throw; // 交给 WorkerLoop 处理
            }
            catch (Exception ex)
            {
                _log.Error($"[异常] 处理 {url} 出错（{ex.GetType().Name}）：{ex.Message}");
                _log.Info($"[异常] 堆栈：{ex.StackTrace}");
                SafeDelete(tempFile);
                _partials.Delete(urlKey);
            }

            // 可选限制：最大下载数量
            if (_cfg.MaxDownloadCount > 0 && _stats.Downloaded >= _cfg.MaxDownloadCount)
                RequestStopInternal("limit");
        }

        /// <summary>中断时是否保留断点缓存以便下次续传。</summary>
        private bool ShouldKeepPartial
        {
            get
            {
                // 用户 Ctrl+C 且配置为 KeepPartial（默认）-> 保留；limit 中断也保留
                if (_stopReason == "user" && string.Equals(_cfg.CtrlCAction, "Discard", StringComparison.OrdinalIgnoreCase))
                    return false;
                return true;
            }
        }

        // ---------------- 下载（支持断点续传） ----------------

        private DownloadResult Download(Uri url, Uri parent)
        {
            // MaxDownloadSizeKb = 0 表示不限制最大下载体积。
            // 注意：体积上限对所有文件一视同仁，M3U8 播放列表及其分片/密钥/初始化段等同样遵守。
            string urlKey = url.ToString();
            long maxBytes = _cfg.MaxDownloadSizeKb > 0 ? _cfg.MaxDownloadSizeKb * 1024L : long.MaxValue;
            string dataPath = _partials.DataPath(urlKey);
            Directory.CreateDirectory(_partials.Dir);

            PartialState state = _partials.ReadState(urlKey) ?? new PartialState { Url = urlKey };
            // 跨运行沿用状态文件中记录的上次修改时间，作为 60 秒节流基线
            DateTime lastStateWriteUtc = state.UpdatedUtc;

            for (int attempt = 0; attempt <= _cfg.RetryError; attempt++)
            {
                if (_cts.IsCancellationRequested) throw new OperationCanceledException(_cts.Token);
                WaitInterval();

                // 计算安全续传起点：以状态文件记录的已提交长度为准；若磁盘更长则回退，保证不损坏
                long resumeOffset = 0;
                if (File.Exists(dataPath) && state.BytesDownloaded > 0)
                {
                    long onDisk = new FileInfo(dataPath).Length;
                    resumeOffset = Math.Min(state.BytesDownloaded, onDisk);
                    if (resumeOffset < onDisk) TryTruncateTo(dataPath, resumeOffset);
                }
                else if (!File.Exists(dataPath))
                {
                    state.BytesDownloaded = 0;
                }

                // 上次其实已经下完（只是没来得及落盘到最终路径）
                if (resumeOffset > 0 && state.TotalLength > 0 && resumeOffset >= state.TotalLength)
                {
                    _log.Info($"[续传] 本地缓存已完整（{resumeOffset}/{state.TotalLength} 字节），直接校验保存：{url}");
                    return new DownloadResult { TempFile = dataPath, Mime = state.Mime, SizeBytes = resumeOffset };
                }

                _stats.BeginDownload(urlKey, url.ToString(), resumeOffset, state.TotalLength);
                try
                {
                    if (resumeOffset > 0)
                        _log.Info($"[续传] 发现本地断点 {resumeOffset} 字节，尝试续传：{url}");

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    ApplyHeaders(request, parent);
                    if (resumeOffset > 0)
                    {
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeOffset, null);
                        // If-Range：校验器一致才续传，否则服务器会返回 200 全量
                        if (!string.IsNullOrEmpty(state.ETag))
                            request.Headers.TryAddWithoutValidation("If-Range", state.ETag);
                        else if (!string.IsNullOrEmpty(state.LastModified))
                            request.Headers.TryAddWithoutValidation("If-Range", state.LastModified);
                    }

                    using var response = _http
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token)
                        .GetAwaiter().GetResult();
                    int code = (int)response.StatusCode;

                    // 416：范围不可满足。若本地已够长视为完成，否则丢弃断点重来
                    if (code == 416)
                    {
                        if (state.TotalLength > 0 && resumeOffset >= state.TotalLength)
                            return new DownloadResult { TempFile = dataPath, Mime = state.Mime, SizeBytes = resumeOffset };

                        _log.Warn($"[续传] 服务器返回 416，丢弃本地断点重新下载：{url}");
                        DiscardPartial(urlKey, ref state);
                        resumeOffset = 0;
                        if (attempt < _cfg.RetryError) continue;
                        return DownloadResult.FailedHttp;
                    }

                    // 不可恢复的HTTP错误（403/404等）：不重试
                    if (code >= 400 && code < 500 && code != 408 && code != 425 && code != 429)
                    {
                        _log.Info($"[跳过] HTTP {code} 不可恢复错误，不重试：{url}");
                        return DownloadResult.FailedHttp;
                    }

                    // 可恢复错误（429/5xx/408/425）：按 RetryError 重试
                    if (!response.IsSuccessStatusCode)
                    {
                        if (attempt < _cfg.RetryError)
                        {
                            _log.Warn($"[重试] HTTP {code} 可恢复错误，第 {attempt + 1}/{_cfg.RetryError} 次：{url}");
                            continue;
                        }
                        _log.Info($"[跳过] HTTP {code} 重试 {_cfg.RetryError} 次后仍失败：{url}");
                        return DownloadResult.FailedHttp;
                    }

                    bool isPartial = response.StatusCode == HttpStatusCode.PartialContent;
                    string mime = response.Content.Headers.ContentType?.MediaType ?? state.Mime;

                    long totalLength = -1;

                    if (isPartial)
                    {
                        var cr = response.Content.Headers.ContentRange;
                        if (cr == null || cr.From != resumeOffset)
                        {
                            _log.Warn($"[续传] 服务器返回的范围与请求不一致（请求自 {resumeOffset} 起），重新下载：{url}");
                            DiscardPartial(urlKey, ref state);
                            resumeOffset = 0;
                            if (attempt < _cfg.RetryError) continue;
                            return DownloadResult.FailedHttp;
                        }
                        if (cr.Length.HasValue) totalLength = cr.Length.Value;
                        _log.Info($"[续传] 服务器支持续传，从 {resumeOffset} 字节继续（总长 {totalLength}）：{url}");
                        if (totalLength > 0 && state.TotalLength > 0 && totalLength != state.TotalLength)
                        {
                            _log.Warn($"[续传] 文件总长度已变化（{state.TotalLength} -> {totalLength}），重新下载：{url}");
                            DiscardPartial(urlKey, ref state);
                            resumeOffset = 0;
                            if (attempt < _cfg.RetryError) continue;
                            return DownloadResult.FailedHttp;
                        }
                    }
                    else
                    {
                        // 200：服务器不支持 Range，或 ETag/Last-Modified 已变化 -> 从头开始
                        if (resumeOffset > 0)
                            _log.Warn($"[续传] 服务器未返回 206（不支持断点或校验器已变化），从头重新下载：{url}");
                        _partials.Delete(urlKey);
                        state = new PartialState { Url = urlKey };
                        resumeOffset = 0;
                        totalLength = response.Content.Headers.ContentLength ?? -1;
                    }

                    if (totalLength > maxBytes)
                    {
                        _log.Info($"[跳过] Content-Length 超过 MaxDownloadSizeKb={_cfg.MaxDownloadSizeKb}" +
                                  $"（{totalLength / 1024.0:F1} KB）：{url}");
                        DiscardPartial(urlKey, ref state);
                        return DownloadResult.Failed;
                    }

                    // 记录续传起点与校验信息；状态文件的实际落盘受 60 秒最小间隔限制
                    state.TotalLength = totalLength >= 0 ? totalLength : state.TotalLength;
                    state.BytesDownloaded = resumeOffset;
                    state.ETag = response.Headers.ETag?.Tag ?? state.ETag;
                    state.LastModified = response.Content.Headers.LastModified?.ToString("R") ?? state.LastModified;
                    state.Mime = mime;
                    TryCommitState(urlKey, state, ref lastStateWriteUtc, url);

                    long written = resumeOffset;
                    bool exceeded = false;
                    _stats.UpdateProgress(urlKey, written, state.TotalLength);

                    using (var fs = new FileStream(dataPath, resumeOffset > 0 ? FileMode.Open : FileMode.Create,
                                                   FileAccess.Write, FileShare.None, 81920))
                    using (var stream = response.Content.ReadAsStreamAsync(_cts.Token).GetAwaiter().GetResult())
                    {
                        if (resumeOffset > 0) { fs.SetLength(resumeOffset); fs.Seek(resumeOffset, SeekOrigin.Begin); }
                        var buffer = new byte[81920];
                        while (true)
                        {
                            int n = stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token).GetAwaiter().GetResult();
                            if (n <= 0) break;
                            if (written + n > maxBytes) { exceeded = true; break; }
                            fs.Write(buffer, 0, n);
                            written += n;
                            _stats.AddBytes(n);
                            _stats.UpdateProgress(urlKey, written, state.TotalLength);

                            // 每 >=60 秒才提交一次：先 flush 数据，再写状态（状态永不超前）
                            if (DateTime.UtcNow - lastStateWriteUtc >= StateWriteInterval)
                            {
                                fs.Flush(true);
                                state.BytesDownloaded = written;
                                TryCommitState(urlKey, state, ref lastStateWriteUtc, url);
                            }
                        }
                        fs.Flush(true);
                        state.BytesDownloaded = written;
                        TryCommitState(urlKey, state, ref lastStateWriteUtc, url);
                    }

                    if (exceeded)
                    {
                        _log.Info($"[跳过] 下载过程中超过 MaxDownloadSizeKb={_cfg.MaxDownloadSizeKb}" +
                                  $"（{written / 1024.0:F1} KB）：{url}");
                        DiscardPartial(urlKey, ref state);
                        return DownloadResult.Failed;
                    }

                    // 声明了总长度却没读完 -> 连接被截断，按可恢复错误重试（下次自动从断点续传）
                    if (state.TotalLength > 0 && written < state.TotalLength)
                    {
                        if (attempt < _cfg.RetryError)
                        {
                            _log.Warn($"[重试] 下载不完整（{written}/{state.TotalLength} 字节），将从断点续传，" +
                                      $"第 {attempt + 1}/{_cfg.RetryError} 次：{url}");
                            continue;
                        }
                        _log.Info($"[跳过] 下载不完整且重试 {_cfg.RetryError} 次后仍失败：{url}");
                        return DownloadResult.FailedHttp;
                    }

                    return new DownloadResult { TempFile = dataPath, Mime = mime, SizeBytes = written };
                }
                catch (OperationCanceledException)
                {
                    if (_cts.IsCancellationRequested) throw; // 用户/限制中断：向上传播
                    // 否则是 HttpClient 超时，按可恢复处理
                    if (attempt < _cfg.RetryError)
                    {
                        _log.Warn($"[重试] 请求超时，第 {attempt + 1}/{_cfg.RetryError} 次：{url}");
                        continue;
                    }
                    _log.Info($"[跳过] 请求超时重试 {_cfg.RetryError} 次后仍失败：{url}");
                    return DownloadResult.FailedHttp;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is SocketException || FileIo.IsTransient(ex))
                {
                    if (attempt < _cfg.RetryError)
                    {
                        _log.Warn($"[重试] 网络/文件错误 {ex.GetType().Name}：{ex.Message}，第 {attempt + 1}/{_cfg.RetryError} 次：{url}");
                        continue;
                    }
                    _log.Info($"[跳过] 网络/文件错误重试 {_cfg.RetryError} 次后仍失败：{url}（{ex.Message}）");
                    return DownloadResult.FailedHttp;
                }
                finally
                {
                    _stats.EndDownload(urlKey);
                }
            }
            return DownloadResult.FailedHttp;
        }

        private void DiscardPartial(string urlKey, ref PartialState state)
        {
            _partials.Delete(urlKey);
            state = new PartialState { Url = urlKey };
        }

        /// <summary>
        /// 按最小间隔（<see cref="StateWriteInterval"/>，即 60 秒）提交状态文件。
        /// 若距上次修改不足 60 秒则直接跳过（返回 false），以保证对同一状态文件的修改
        /// 永远不会比每 60 秒一次更频繁，从而避免频繁创建/替换文件引发的瞬时锁定。
        /// 首次写入（lastWriteUtc 为 default）总是允许。
        /// </summary>
        private bool TryCommitState(string urlKey, PartialState state, ref DateTime lastWriteUtc, Uri url)
        {
            if (lastWriteUtc != default && DateTime.UtcNow - lastWriteUtc < StateWriteInterval)
                return false;
            state.UpdatedUtc = DateTime.UtcNow;
            _partials.WriteState(urlKey, state);
            lastWriteUtc = state.UpdatedUtc;
            _log.Info($"[续传] 已提交断点状态（{state.BytesDownloaded}/{state.TotalLength} 字节）：{url}");
            return true;
        }

        private static void TryTruncateTo(string path, long length)
        {
            try
            {
                FileIo.Retry(() =>
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                    fs.SetLength(length);
                });
            }
            catch { /* 忽略 */ }
        }

        private void ApplyHeaders(HttpRequestMessage request, Uri parent)
        {
            string parentUrl = parent?.ToString() ?? string.Empty;
            foreach (var kv in _cfg.Headers)
            {
                // 特殊标签：{ParentUrl} 自动替换为父页面 URL
                string value = kv.Value.Replace("{ParentUrl}", parentUrl);
                if (string.IsNullOrWhiteSpace(value)) continue;
                try
                {
                    request.Headers.TryAddWithoutValidation(kv.Key, value);
                }
                catch (Exception ex)
                {
                    _log.Warn($"[头] 无法设置请求头 {kv.Key}：{ex.Message}");
                }
            }
        }

        // 每个工作线程在每次请求前等待。
        // IntervalMs > 0：固定等待 IntervalMs；IntervalMs == 0：按本线程的指数退避值等待（首次 1s）。
        private void WaitInterval()
        {
            int waitMs = _cfg.IntervalMs > 0
                ? _cfg.IntervalMs
                : (_tlsBackoffMs > 0 ? _tlsBackoffMs : BackoffInitialMs);
            if (waitMs <= 0) return;
            try { _cts.Token.WaitHandle.WaitOne(waitMs); }
            catch { Thread.Sleep(waitMs); }
        }

        /// <summary>
        /// 反馈一次 URL 处理结果，用于 IntervalMs == 0 时的指数退避调节：
        /// 成功则重置为初始值（1s），失败则翻倍（上限 60s）。固定间隔模式下不做任何事。
        /// </summary>
        private void ReportRequestOutcome(bool success)
        {
            if (_cfg.IntervalMs != 0) return;
            if (success)
            {
                _tlsBackoffMs = BackoffInitialMs;
                return;
            }
            int cur = _tlsBackoffMs > 0 ? _tlsBackoffMs : BackoffInitialMs;
            _tlsBackoffMs = Math.Min(cur * 2, BackoffMaxMs);
        }

        // ---------------- 队列恢复 / 收尾 ----------------

        private bool TryLoadResumeQueue()
        {
            string queuePath = Path.Combine(_cfg.SaveTo, InQueueFileName);
            if (!File.Exists(queuePath)) return false;

            if (_cfg.IgnoreQueueTemp)
            {
                _log.Info($"[队列] IgnoreQueueTemp=true，忽略已存在的 {queuePath} 并截断为 0 字节");
                TruncateFile(queuePath);
                return false;
            }

            var urls = new List<Uri>();
            foreach (var line in File.ReadAllLines(queuePath))
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                if ((Uri.TryCreate(t, UriKind.Absolute, out var u) &&
                     (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)))
                    urls.Add(u);
                else
                    _log.Warn($"[队列] 忽略无法解析的续抓行：{t}");
            }

            // 读取完成后立即截断为 0 字节：运行期间不再保留旧列表，
            // 只有在再次 Ctrl+C 收尾时才会写入最新列表；正常完成则彻底删除。
            TruncateFile(queuePath);

            if (urls.Count == 0)
            {
                _log.Info($"[队列] {queuePath} 中没有可用的 URL，按全新任务开始");
                return false;
            }

            foreach (var u in urls) TryEnqueue(u, 0, null);
            _log.Info($"[队列] 从 {queuePath} 恢复 {urls.Count} 条待抓取 URL（文件已截断为 0 字节）");
            return true;
        }

        private void FinalizeRun()
        {
            _stats.Phase = "收尾中";
            _stats.StopReason = _stopReason;

            string queuePath = Path.Combine(_cfg.SaveTo, InQueueFileName);
            bool interrupted = _stopReason != null;

            try
            {
                if (interrupted)
                {
                    // 完整的待下载队列 = 所有“已归一化入队、但尚未处理完成”的 URL。
                    // 按入队序号（_seen 的 value）排序，保证顺序与中断前的队列一致：
                    // 已被 worker 取走并在处理中的（入队更早）排在仍躺在队列里的之前。
                    // 注意：不能只统计 _queue / _inFlight，否则正在处理却因中断而中途退出的 URL
                    //（尤其 ProcessUrl 开头的取消分支）会被漏掉。
                    var distinct = _seen
                        .Where(kv => !_completed.ContainsKey(kv.Key))
                        .OrderBy(kv => kv.Value)
                        .Select(kv => kv.Key)
                        .ToList();
                    _log.Info($"[收尾] 已见 {_seen.Count} 条，已完成 {_completed.Count} 条，待下载 {distinct.Count} 条（按入队顺序）");

                    if (distinct.Count > 0)
                    {
                        // 截断写入：先写临时文件，再整体替换，保证列表永远是完整的最新版本。
                        // 临时名带随机后缀，避免与残留临时文件互相干扰（固定名易被瞬时占用）。
                        string tmp = queuePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        FileIo.Retry(() => File.WriteAllLines(tmp, distinct, new UTF8Encoding(false)));
                        FileIo.Move(tmp, queuePath, overwrite: true);
                        _log.Info($"[收尾] 待抓取 {distinct.Count} 条 URL 已覆盖写入 {queuePath}；下次运行将自动续抓（除非 IgnoreQueueTemp=true）");
                    }
                    else
                    {
                        TryDelete(queuePath);
                        _log.Info("[收尾] 没有待抓取 URL");
                    }
                }
                else
                {
                    // 正常完成：彻底删除续抓文件
                    TryDelete(queuePath);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"[收尾] 写入待抓取队列失败：{ex.Message}");
            }

            _failedList.Flush();
            _ignoredList.Flush();
            _partials.CleanupIfEmpty();

            _log.Info(_stats.BuildSummaryText());
            _log.Info($"结果列表文件（位于 {_cfg.SaveTo}）：{FailedFileName}、{IgnoredByPoliciesFileName}、{InQueueFileName}(中断时)");
            _stats.Phase = "已完成";

            _failedList.Dispose();
            _ignoredList.Dispose();
        }

        private static void TryDelete(string path)
        {
            try { FileIo.Delete(path); } catch { /* 忽略 */ }
        }

        private static void TruncateFile(string path)
        {
            try
            {
                FileIo.Retry(() =>
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                    fs.SetLength(0);
                });
            }
            catch { /* 忽略 */ }
        }

        // ---------------- 主机限制 ----------------

        private bool HostAllowed(Uri url)
        {
            string host = url.Host;
            switch (_cfg.AllowedHost)
            {
                case "this":
                    return string.Equals(host, _cfg.NetlocHost, StringComparison.OrdinalIgnoreCase);

                case "this*":
                    return string.Equals(host, _cfg.NetlocHost, StringComparison.OrdinalIgnoreCase)
                        || host.EndsWith("." + _cfg.NetlocHost, StringComparison.OrdinalIgnoreCase);

                case "limited":
                    foreach (var entry in _cfg.LimitedList)
                        if (MatchDomain(entry, host)) return true;
                    return false;

                case "all":
                default:
                    return true;
            }
        }

        private static bool MatchDomain(string pattern, string host)
        {
            string p = pattern.Trim().ToLowerInvariant();
            string h = host.ToLowerInvariant();
            if (p.StartsWith("*."))
            {
                string d = p.Substring(2);
                return string.Equals(h, d, StringComparison.Ordinal) || h.EndsWith("." + d, StringComparison.Ordinal);
            }
            return string.Equals(h, p, StringComparison.Ordinal);
        }

        // ---------------- 类型判断 ----------------

        private static string GetExt(Uri url)
        {
            string path = url.AbsolutePath;
            if (string.IsNullOrEmpty(path)) return null;
            string ext = Path.GetExtension(path);
            return string.IsNullOrEmpty(ext) ? null : ext.Substring(1).ToLowerInvariant();
        }

        private bool IsMimeIgnored(string mime) => MatchesAnyMimePattern(mime, _cfg.IgnoredFileMime);

        private bool IsExtractable(string ext, string mime)
            => (ext != null && _cfg.ExtractFileExt.Contains(ext)) || MatchesAnyMimePattern(mime, _cfg.ExtractFileMime);

        private static bool MatchesAnyMimePattern(string mime, List<string> patterns)
        {
            if (string.IsNullOrEmpty(mime) || patterns.Count == 0) return false;
            foreach (var p in patterns)
            {
                if (p == "*") return true;
                if (p.EndsWith("/*") && mime.StartsWith(p.Substring(0, p.Length - 1), StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(p, mime, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ---------------- 文本提取 ----------------

        private List<Uri> ExtractFromFile(string tempFile, Uri parentUrl)
        {
            string content = ReadTextFile(tempFile);
            if (content == null)
            {
                _log.Info($"[提取] {parentUrl} 内容疑似二进制，跳过文本解析");
                return new List<Uri>();
            }
            return UrlExtractor.ExtractUrls(content, parentUrl, _cfg.NetlocHost);
        }

        /// <summary>读取文本文件；若疑似二进制或读取失败返回 null。</summary>
        private static string ReadTextFile(string tempFile)
        {
            try
            {
                byte[] head = new byte[1024];
                int read = FileIo.Retry(() =>
                {
                    using var fs = File.OpenRead(tempFile);
                    return fs.Read(head, 0, head.Length);
                });

                for (int i = 0; i < read; i++)
                    if (head[i] == 0) return null;

                return FileIo.Retry(() =>
                {
                    using var reader = new StreamReader(tempFile, Encoding.UTF8, true);
                    return reader.ReadToEnd();
                });
            }
            catch
            {
                return null;
            }
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (path != null) FileIo.Delete(path);
            }
            catch { /* 忽略删除失败 */ }
        }

        private sealed class DownloadResult
        {
            public string TempFile;
            public string Mime;
            public long SizeBytes;
            public bool HttpError;

            public static readonly DownloadResult Failed = new DownloadResult();
            public static readonly DownloadResult FailedHttp = new DownloadResult { HttpError = true };
        }
    }
}
