using System;
using System.Collections.Generic;
using System.Linq;
using WebScrapingRebuilder.Core;

namespace WebScrapingRebuilder.Net
{
    /// <summary>
    /// URL 归一化（在加入抓取队列、写入 _seen 之前执行）：
    ///   1) 去掉 [Url] IgnoreParamName 指定的查询参数（优先级最高，先执行）；
    ///   2) 添加 [Url] AddParam 指定的完整参数（name=value；同名参数会覆盖）；
    ///   3) 去掉 fragment（不会发送到服务器）。
    /// 这样 _seen 中去重用的 URL 已不含被忽略的参数，同页面不同埋点参数不会再被重复抓取。
    /// </summary>
    public sealed class UrlNormalizer
    {
        private sealed class QueryParam
        {
            public string Name;      // 原始（可能已百分号编码）的名称
            public string Value;     // 原始值；null 表示只有名字没有 '='
            public string DecName;   // 解码后的名称，用于比较
        }

        private readonly HashSet<string> _ignoreNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<QueryParamSpec> _addParams;

        public UrlNormalizer(ScrapeConfig cfg)
        {
            foreach (var n in cfg.IgnoreParamNames)
            {
                string t = n.Trim().ToLowerInvariant();
                if (t.Length > 0) _ignoreNames.Add(t);
            }
            _addParams = cfg.AddParams;
        }

        public Uri Normalize(Uri url)
        {
            var query = ParseQuery(url.Query);
            bool changed = false;

            if (query.Count > 0 && _ignoreNames.Count > 0)
            {
                int before = query.Count;
                query = query.Where(p => !_ignoreNames.Contains(p.DecName.ToLowerInvariant())).ToList();
                if (query.Count != before) changed = true;
            }

            foreach (var spec in _addParams)
            {
                string encName = Uri.EscapeDataString(spec.Name);
                string encValue = spec.Value == null ? null : Uri.EscapeDataString(spec.Value);
                int idx = query.FindIndex(p => string.Equals(p.DecName, spec.Name, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    if (query[idx].Name != encName || query[idx].Value != encValue)
                    {
                        query[idx] = new QueryParam { Name = encName, Value = encValue, DecName = spec.Name };
                        changed = true;
                    }
                }
                else
                {
                    query.Add(new QueryParam { Name = encName, Value = encValue, DecName = spec.Name });
                    changed = true;
                }
            }

            bool hasFragment = !string.IsNullOrEmpty(url.Fragment);
            if (!changed && !hasFragment) return url; // 无变化时保持原始 Uri，避免不必要的重编码

            try
            {
                var builder = new UriBuilder(url)
                {
                    Query = BuildQuery(query),
                    Fragment = string.Empty,
                };
                return builder.Uri;
            }
            catch
            {
                return url;
            }
        }

        private static List<QueryParam> ParseQuery(string query)
        {
            var list = new List<QueryParam>();
            if (string.IsNullOrEmpty(query)) return list;
            if (query[0] == '?') query = query.Substring(1);
            if (query.Length == 0) return list;

            foreach (var seg in query.Split('&'))
            {
                if (seg.Length == 0) continue;
                int eq = seg.IndexOf('=');
                if (eq < 0)
                    list.Add(new QueryParam { Name = seg, Value = null, DecName = Decode(seg) });
                else
                    list.Add(new QueryParam { Name = seg.Substring(0, eq), Value = seg.Substring(eq + 1), DecName = Decode(seg.Substring(0, eq)) });
            }
            return list;
        }

        private static string BuildQuery(List<QueryParam> query)
        {
            if (query == null || query.Count == 0) return string.Empty;
            return string.Join("&", query.Select(p => p.Value == null ? p.Name : p.Name + "=" + p.Value));
        }

        private static string Decode(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            try { return Uri.UnescapeDataString(s); }
            catch { return s; }
        }
    }
}
