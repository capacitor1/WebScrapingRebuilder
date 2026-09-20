using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WebScrapingRebuilder.Ui;

namespace WebScrapingRebuilder.Core
{
    /// <summary>[Url] 段中 AddParam 的解析结果：一个完整的 name=value（Value 为 null 表示只有名字）。</summary>
    public sealed class QueryParamSpec
    {
        public string Name { get; set; }
        public string Value { get; set; }

        public override string ToString() => Value == null ? Name : Name + "=" + Value;
    }

    /// <summary>INI 原始条目（用于启动时原样回显，便于核对解析结果）。</summary>
    public sealed class RawEntry
    {
        public string Section { get; set; }
        public string Key { get; set; }
        public string Value { get; set; }
        public int Line { get; set; }
    }

    /// <summary>
    /// 抓取任务配置模型 + INI 解析与校验。
    /// </summary>
    public class ScrapeConfig
    {
        public const string ValidFormat = "WebsiteScrapingVer1";

        public string Format { get; set; }
        public Uri Netloc { get; set; }              // 主入口页面（完整 URL）
        public string NetlocHost { get; set; }       // 由 Netloc 解析出的 Host（xxx.com）
        public string SaveTo { get; set; }
        public string GroupingMethod { get; set; }   // AbsoluteRaw | RelativeRaw | Mime | None
        public string AntiDuplicate { get; set; }    // Sha256 | Always | Size
        public int IntervalMs { get; set; } = 1000;
        public int RetryError { get; set; } = 10;
        public long MinDownloadSizeKb { get; set; }
        public long MaxDownloadSizeKb { get; set; } = 1048576;
        public List<string> IgnoredFileExt { get; } = new();
        public List<string> IgnoredFileMime { get; } = new();
        public List<string> ExtractFileExt { get; } = new();
        public List<string> ExtractFileMime { get; } = new();
        public string AllowedHost { get; set; } = "all"; // this | this* | all | limited
        public List<string> LimitedList { get; } = new();
        public List<KeyValuePair<string, string>> Headers { get; } = new(); // 有序、键不重复（重复时行号大的生效）

        // 可选限制项（示例 INI 中未列出，留空/为 0 则无限制）
        public long MaxDownloadCount { get; set; }
        public int MaxDepth { get; set; }

        // 中断续抓：是否忽略已存在的 InQueue.txt
        public bool IgnoreQueueTemp { get; set; }

        // 多线程下载线程数（默认 5）
        public int MultiThread { get; set; } = 5;

        // 按下 Ctrl+C 时对“下载中的文件”的处理策略：
        //   Finish      = 先让正在下载的文件下载完再退出（不再取新任务）
        //   KeepPartial = 立即切断，但保留断点（.part/.state），URL 写回 InQueue，下次自动续传
        //   Discard     = 立即切断，删除断点文件，URL 写回 InQueue，下次重新下载
        public string CtrlCAction { get; set; } = "KeepPartial";

        // [Url] 段：需要从 URL 中剔除的查询参数名（可重复，全部生效）
        public List<string> IgnoreParamNames { get; } = new();

        // [Url] 段：需要向 URL 中添加的参数（可重复，全部生效）
        public List<QueryParamSpec> AddParams { get; } = new();

        // 原始解析条目（按文件中出现顺序），用于启动回显
        public List<RawEntry> RawEntries { get; } = new();

        public static ScrapeConfig Load(string path)
        {
            string[] lines = File.ReadAllLines(path);
            var sections = new Dictionary<string, List<(string Key, string Value, int Line)>>(StringComparer.OrdinalIgnoreCase);
            var rawEntries = new List<RawEntry>();
            string current = string.Empty;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue; // 空行 / 注释

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    if (!sections.ContainsKey(current)) sections[current] = new List<(string, string, int)>();
                    continue;
                }

                // 宽容解析：第一个 '=' 为分隔符；值保留首个 '=' 之后的全部内容（仅去首尾空格），
                // 因此值中可含多个 '='、空格及任意特殊字符，无需引号或转义。
                int eq = line.IndexOf('=');
                if (eq <= 0) continue; // 无法解析的行直接忽略
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (!sections.TryGetValue(current, out var list))
                {
                    list = new List<(string, string, int)>();
                    sections[current] = list;
                }
                list.Add((key, value, i + 1));
                rawEntries.Add(new RawEntry { Section = current, Key = key, Value = value, Line = i + 1 });
            }

            // 取值（大小写不敏感）；重复键取“行号最大”的那一项
            string Get(string section, string key)
            {
                if (!sections.TryGetValue(section, out var list)) return null;
                for (int j = list.Count - 1; j >= 0; j--)
                    if (string.Equals(list[j].Key, key, StringComparison.OrdinalIgnoreCase))
                        return list[j].Value;
                return null;
            }

            var cfg = new ScrapeConfig();

            // ---------- [Format] ----------
            cfg.Format = Get("Format", "Format") ?? string.Empty;
            if (!string.Equals(cfg.Format, ValidFormat, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"无效的配置文件：[Format] Format 必须等于 \"{ValidFormat}\"，实际为 \"{cfg.Format}\"。可能是错误或旧版本的 INI 文件。");

            // ---------- [Task] ----------
            string netloc = Get("Task", "Netloc");
            if (string.IsNullOrWhiteSpace(netloc))
                throw new InvalidDataException("缺少必需的配置项：[Task] Netloc");
            if (!Uri.TryCreate(netloc, UriKind.Absolute, out var netlocUri) ||
                (netlocUri.Scheme != Uri.UriSchemeHttp && netlocUri.Scheme != Uri.UriSchemeHttps))
                throw new InvalidDataException($"[Task] Netloc 不是合法的 http/https 完整URL：{netloc}");
            cfg.Netloc = netlocUri;
            cfg.NetlocHost = netlocUri.Host;

            cfg.SaveTo = Get("Task", "SaveTo");
            if (string.IsNullOrWhiteSpace(cfg.SaveTo))
                throw new InvalidDataException("缺少必需的配置项：[Task] SaveTo");
            try { cfg.SaveTo = Path.GetFullPath(cfg.SaveTo); } catch { /* 保留原值，交给创建目录时报错 */ }

            cfg.GroupingMethod = Get("Task", "GroupingMethod");
            if (!new[] { "AbsoluteRaw", "RelativeRaw", "Mime", "None" }.Contains(cfg.GroupingMethod))
                throw new InvalidDataException(
                    $"[Task] GroupingMethod 必须为 AbsoluteRaw/RelativeRaw/Mime/None 之一，实际为 \"{cfg.GroupingMethod}\"");

            cfg.AntiDuplicate = Get("Task", "AntiDuplicate");
            if (!new[] { "Sha256", "Always", "Size" }.Contains(cfg.AntiDuplicate))
                throw new InvalidDataException(
                    $"[Task] AntiDuplicate 必须为 Sha256/Always/Size 之一，实际为 \"{cfg.AntiDuplicate}\"");

            cfg.IntervalMs = GetInt(Get("Task", "IntervalMs"), 1000, "[Task] IntervalMs");
            cfg.RetryError = GetInt(Get("Task", "RetryError"), 10, "[Task] RetryError");
            cfg.MinDownloadSizeKb = GetLong(Get("Task", "MinDownloadSizeKb"), 0, "[Task] MinDownloadSizeKb");
            cfg.MaxDownloadSizeKb = GetLong(Get("Task", "MaxDownloadSizeKb"), 1048576, "[Task] MaxDownloadSizeKb");
            if (cfg.IntervalMs < 0) throw new InvalidDataException("[Task] IntervalMs 不能为负数");
            if (cfg.RetryError < 0) throw new InvalidDataException("[Task] RetryError 不能为负数");
            if (cfg.MinDownloadSizeKb < 0 || cfg.MaxDownloadSizeKb < 0)
                throw new InvalidDataException("[Task] Min/MaxDownloadSizeKb 不能为负数");
            // MaxDownloadSizeKb = 0 表示不限制最大下载体积，此时不校验上下界关系
            if (cfg.MaxDownloadSizeKb > 0 && cfg.MinDownloadSizeKb > cfg.MaxDownloadSizeKb)
                throw new InvalidDataException("[Task] MinDownloadSizeKb 不能大于 MaxDownloadSizeKb（MaxDownloadSizeKb=0 表示不限制）");

            cfg.IgnoredFileExt.AddRange(SplitList(Get("Task", "IgnoredFileExt")));
            cfg.IgnoredFileMime.AddRange(SplitList(Get("Task", "IgnoredFileMime")));
            cfg.ExtractFileExt.AddRange(SplitList(Get("Task", "ExtractFileExt")));
            cfg.ExtractFileMime.AddRange(SplitList(Get("Task", "ExtractFileMime")));
            if (cfg.ExtractFileExt.Count == 0 && cfg.ExtractFileMime.Count == 0)
            {
                cfg.ExtractFileExt.AddRange(new[] { "html", "js", "css", "txt" });
                cfg.ExtractFileMime.Add("text/*");
            }

            cfg.AllowedHost = Get("Task", "AllowedHost") ?? "all";
            if (!new[] { "this", "this*", "all", "limited" }.Contains(cfg.AllowedHost))
                throw new InvalidDataException(
                    $"[Task] AllowedHost 必须为 this/this*/all/limited 之一，实际为 \"{cfg.AllowedHost}\"");
            if (cfg.AllowedHost == "limited")
            {
                cfg.LimitedList.AddRange(SplitList(Get("Task", "LimitedList")));
                if (cfg.LimitedList.Count == 0)
                    throw new InvalidDataException("[Task] AllowedHost 为 limited 时，LimitedList 必须存在且不为空");
            }

            cfg.MaxDownloadCount = GetLong(Get("Task", "MaxDownloadCount"), 0, "[Task] MaxDownloadCount");
            cfg.MaxDepth = GetInt(Get("Task", "MaxDepth"), 0, "[Task] MaxDepth");

            // 中断续抓是否忽略 InQueue.txt
            cfg.IgnoreQueueTemp = GetBool(Get("Task", "IgnoreQueueTemp"), false, "[Task] IgnoreQueueTemp");

            // 多线程下载线程数（任意 Int32，运行时再夹紧到 [1,256]）
            cfg.MultiThread = GetInt(Get("Task", "MultiThread"), 5, "[Task] MultiThread");

            // Ctrl+C 行为（兼容别名 Resume/Abort）
            cfg.CtrlCAction = Get("Task", "CtrlCAction") ?? "KeepPartial";
            if (string.Equals(cfg.CtrlCAction, "Resume", StringComparison.OrdinalIgnoreCase)) cfg.CtrlCAction = "KeepPartial";
            else if (string.Equals(cfg.CtrlCAction, "Abort", StringComparison.OrdinalIgnoreCase)) cfg.CtrlCAction = "Discard";
            if (!new[] { "Finish", "KeepPartial", "Discard" }.Contains(cfg.CtrlCAction, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"[Task] CtrlCAction 必须为 Finish/KeepPartial/Discard 之一，实际为 \"{cfg.CtrlCAction}\"");
            // 归一化为首字母大写形式
            cfg.CtrlCAction = new[] { "Finish", "KeepPartial", "Discard" }
                .First(x => string.Equals(x, cfg.CtrlCAction, StringComparison.OrdinalIgnoreCase));

            // ---------- [Url] ----------
            if (sections.TryGetValue("Url", out var urlList))
            {
                foreach (var e in urlList)
                {
                    if (string.Equals(e.Key, "IgnoreParamName", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = e.Value.Trim();
                        if (v.Length > 0) cfg.IgnoreParamNames.Add(v);
                    }
                    else if (string.Equals(e.Key, "AddParam", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = e.Value.Trim();
                        if (v.Length == 0) continue;
                        int eq = v.IndexOf('=');
                        if (eq > 0)
                            cfg.AddParams.Add(new QueryParamSpec { Name = v.Substring(0, eq).Trim(), Value = v.Substring(eq + 1).Trim() });
                        else
                            cfg.AddParams.Add(new QueryParamSpec { Name = v, Value = null });
                    }
                }
            }

            // ---------- [HttpHeader] ----------
            if (sections.TryGetValue("HttpHeader", out var headerList))
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var order = new List<string>();
                foreach (var h in headerList)
                {
                    if (string.IsNullOrWhiteSpace(h.Key) || string.IsNullOrWhiteSpace(h.Value)) continue;
                    if (!values.ContainsKey(h.Key)) order.Add(h.Key);
                    values[h.Key] = h.Value; // 行号大的覆盖行号小的
                }
                foreach (var k in order)
                    cfg.Headers.Add(new KeyValuePair<string, string>(k, values[k]));
            }

            cfg.RawEntries.AddRange(rawEntries);
            return cfg;
        }

        /// <summary>
        /// 启动时把解析到的配置原样回显到日志，便于核对每一项是否被正确解析。
        /// </summary>
        public void Dump(Logger log)
        {
            log.Info("================= 配置回显：读取到的原始内容 =================");
            string last = null;
            foreach (var e in RawEntries)
            {
                if (!string.Equals(e.Section, last, StringComparison.Ordinal))
                {
                    log.Info($"[{e.Section}]");
                    last = e.Section;
                }
                log.Info($"    {e.Key} = {e.Value}");
            }

            log.Info("----------------- 配置回显：程序生效值 -----------------");
            log.Info($"    Netloc           = {Netloc}");
            log.Info($"    NetlocHost       = {NetlocHost}");
            log.Info($"    SaveTo           = {SaveTo}");
            log.Info($"    GroupingMethod   = {GroupingMethod}");
            log.Info($"    AntiDuplicate    = {AntiDuplicate}");
            log.Info($"    MultiThread      = {MultiThread}");
            log.Info($"    IntervalMs       = {IntervalMs}{(IntervalMs == 0 ? "（0 = 指数退避模式）" : "")}");
            log.Info($"    RetryError       = {RetryError}");
            log.Info($"    MinDownloadSizeKb= {MinDownloadSizeKb}");
            log.Info($"    MaxDownloadSizeKb= {MaxDownloadSizeKb}{(MaxDownloadSizeKb == 0 ? "（0 = 不限制）" : "")}");
            log.Info($"    IgnoreQueueTemp  = {IgnoreQueueTemp}");
            log.Info($"    CtrlCAction      = {CtrlCAction}");
            log.Info($"    MaxDownloadCount = {MaxDownloadCount}");
            log.Info($"    MaxDepth         = {MaxDepth}");
            log.Info($"    AllowedHost      = {AllowedHost}");
            log.Info($"    LimitedList      = {string.Join(", ", LimitedList)}");
            log.Info($"    IgnoredFileExt   = {string.Join(", ", IgnoredFileExt)}");
            log.Info($"    IgnoredFileMime  = {string.Join(", ", IgnoredFileMime)}");
            log.Info($"    ExtractFileExt   = {string.Join(", ", ExtractFileExt)}");
            log.Info($"    ExtractFileMime  = {string.Join(", ", ExtractFileMime)}");
            log.Info($"    IgnoreParamName  = {string.Join(", ", IgnoreParamNames)}");
            log.Info($"    AddParam         = {string.Join(", ", AddParams)}");
            log.Info("    Headers:");
            foreach (var h in Headers)
                log.Info($"        {h.Key} = {h.Value}");
            log.Info("================= 配置回显结束 =================");
        }

        private static int GetInt(string raw, int def, string what)
        {
            if (string.IsNullOrWhiteSpace(raw)) return def;
            if (!int.TryParse(raw.Trim(), out int v)) throw new InvalidDataException($"{what} 不是合法的整数：\"{raw}\"");
            return v;
        }

        private static long GetLong(string raw, long def, string what)
        {
            if (string.IsNullOrWhiteSpace(raw)) return def;
            if (!long.TryParse(raw.Trim(), out long v)) throw new InvalidDataException($"{what} 不是合法的整数：\"{raw}\"");
            return v;
        }

        private static bool GetBool(string raw, bool def, string what)
        {
            if (string.IsNullOrWhiteSpace(raw)) return def;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "y": case "on": return true;
                case "false": case "0": case "no": case "n": case "off": return false;
                default: throw new InvalidDataException($"{what} 不是合法的布尔值(true/false)：\"{raw}\"");
            }
        }

        private static List<string> SplitList(string raw)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return result;
            foreach (var part in raw.Split(','))
            {
                string p = part.Trim().ToLowerInvariant();
                if (p.Length > 0) result.Add(p);
            }
            return result;
        }
    }
}
