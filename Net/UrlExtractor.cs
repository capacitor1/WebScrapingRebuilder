using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace WebScrapingRebuilder.Net
{
    /// <summary>
    /// URL 提取器：使用“最通用的 Regex 匹配 + 二次校验”，
    /// 覆盖页面中出现于任何位置的绝对 URL、相对 URL、协议相对 URL、CSS url()、@import、srcset 等。
    /// </summary>
    public static class UrlExtractor
    {
        // 通用绝对 URL：http/https 开头，排除空白与 HTML/引号类边界字符。
        private static readonly Regex AbsoluteUrlRegex = new Regex(
            @"(?i)\bhttps?://[^\s<>""'`{}\|\\^]+",
            RegexOptions.Compiled);

        // HTML 属性中的 URL（支持相对路径与协议相对 //host/...）
        private static readonly Regex AttrUrlRegex = new Regex(
            @"(?i)\b(?:src|href|action|poster|data-src|data-href|data-original|data-url|data-lazy-src|background|cite|longdesc|profile|manifest|codebase|archive|usemap|content)\s*=\s*[""']([^""']*)[""']",
            RegexOptions.Compiled);

        // srcset 中的多张图片
        private static readonly Regex SrcsetRegex = new Regex(
            @"(?i)\bsrcset\s*=\s*[""']([^""']*)[""']",
            RegexOptions.Compiled);

        // CSS url(...) 引用
        private static readonly Regex CssUrlRegex = new Regex(
            @"(?i)\burl\(\s*[""']?([^""')\s]+)[""']?\s*\)",
            RegexOptions.Compiled);

        // CSS @import "..."
        private static readonly Regex ImportRegex = new Regex(
            @"(?i)@import\s+[""']([^""']+)[""']",
            RegexOptions.Compiled);

        // 代码/脚本中带引号的相对路径（fetch("/api/x")、import "./m.js" 等）
        private static readonly Regex QuotedRelRegex = new Regex(
            @"(?i)[""']((?:\.\.?/|/)[^\s""'<>`{}\\|\\^]+)[""']",
            RegexOptions.Compiled);

        /// <param name="netlocHost">配置的入口主机：始终允许（便于内网 / 单标签主机抓取）。</param>
        public static List<Uri> ExtractUrls(string content, Uri parentUrl, string netlocHost)
        {
            var found = new HashSet<Uri>();
            if (string.IsNullOrEmpty(content)) return new List<Uri>();

            // 先解码 HTML 实体（&amp; -> &），保证提取到的 URL 与真实请求一致
            string decoded = WebUtility.HtmlDecode(content);

            // 1) 任意位置的绝对 URL
            foreach (Match m in AbsoluteUrlRegex.Matches(decoded))
            {
                string candidate = TrimTrailing(m.Value);
                if (TryCreate(candidate, parentUrl, netlocHost, out var uri))
                    found.Add(uri);
            }

            // 2) 标签属性（含相对路径与 // 协议相对）
            foreach (Match m in AttrUrlRegex.Matches(decoded))
            {
                string val = m.Groups[1].Value.Trim();
                if (val.Length == 0) continue;
                if (TryCreate(val, parentUrl, netlocHost, out var uri))
                    found.Add(uri);
            }

            // 3) srcset
            foreach (Match m in SrcsetRegex.Matches(decoded))
            {
                foreach (var part in m.Groups[1].Value.Split(','))
                {
                    string[] tokens = part.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length == 0) continue;
                    if (TryCreate(tokens[0], parentUrl, netlocHost, out var uri))
                        found.Add(uri);
                }
            }

            // 4) CSS url(...)
            foreach (Match m in CssUrlRegex.Matches(decoded))
            {
                string val = m.Groups[1].Value.Trim();
                if (val.Length == 0) continue;
                if (TryCreate(val, parentUrl, netlocHost, out var uri))
                    found.Add(uri);
            }

            // 5) CSS @import
            foreach (Match m in ImportRegex.Matches(decoded))
            {
                if (TryCreate(m.Groups[1].Value.Trim(), parentUrl, netlocHost, out var uri))
                    found.Add(uri);
            }

            // 6) 带引号的相对路径（JS fetch/import、脚本字符串等）
            foreach (Match m in QuotedRelRegex.Matches(decoded))
            {
                string val = m.Groups[1].Value.Trim();
                if (val.Length == 0) continue;
                if (TryCreate(val, parentUrl, netlocHost, out var uri))
                    found.Add(uri);
            }

            return found.OrderBy(u => u.ToString(), StringComparer.Ordinal).ToList();
        }

        // 去掉结尾可能粘连的标点符号
        private static string TrimTrailing(string s)
        {
            int end = s.Length;
            while (end > 0 && ".,;:!?)]}'\"`>".IndexOf(s[end - 1]) >= 0) end--;
            return end <= 0 ? string.Empty : s.Substring(0, end);
        }

        /// <summary>
        /// 二次校验：仅接受符合格式规范、可被 Uri 正常解析，且主机名在语义上“可能存在”的 http/https 绝对 URL。
        /// 过滤 regex 匹配到但结构畸形（含空格、非法主机、错误协议等）的假 URL。
        /// </summary>
        private static bool TryCreate(string candidate, Uri baseUri, string netlocHost, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            if (candidate.Length < 8) return false; // 最短 http://a.b
            if (candidate.Any(char.IsWhiteSpace)) return false;

            // 拒绝明显非网页资源的协议
            if (candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
                return false;

            // 协议相对 URL：//host/path -> scheme://host/path
            if (candidate.StartsWith("//", StringComparison.Ordinal))
                candidate = (baseUri?.Scheme ?? "https") + ":" + candidate;

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri tmp))
            {
                // 相对路径：基于父页面解析
                if (baseUri != null && Uri.TryCreate(baseUri, candidate, out tmp))
                { /* 解析成功 */ }
                else
                    return false;
            }

            if (tmp.Scheme != Uri.UriSchemeHttp && tmp.Scheme != Uri.UriSchemeHttps) return false;
            if (string.IsNullOrEmpty(tmp.Host)) return false;

            // 主机名二次校验：过滤 example..com、.com、act.、act、带非法字符等畸形/不可能存在的主机
            if (!IsPlausibleHost(tmp.Host, netlocHost)) return false;

            // 去掉 fragment（不会发送到服务器，不参与去重与保存）
            try
            {
                var b = new UriBuilder(tmp) { Fragment = string.Empty };
                uri = b.Uri;
            }
            catch
            {
                uri = tmp;
            }
            return true;
        }

        /// <summary>
        /// 判断主机名在语义上是否可能存在：
        ///  - 允许 IPv4 / IPv6 字面量；
        ///  - 允许配置的入口主机（Netloc 的 Host，便于内网或单标签主机）；
        ///  - 允许 localhost；
        ///  - 其它主机：必须为 DNS 名称，至少两级标签（拒绝 act、act.、example..com 等），
        ///    标签 1~63 字符、不以 '-' 开头/结尾，顶级域≥2位且为字母（或 xn-- punycode）。
        /// </summary>
        public static bool IsPlausibleHost(string host, string netlocHost)
        {
            if (string.IsNullOrEmpty(host)) return false;

            // 配置的抓取入口主机始终允许（用户明确指定要抓取的主机）
            if (!string.IsNullOrEmpty(netlocHost) && host.Equals(netlocHost, StringComparison.OrdinalIgnoreCase))
                return true;

            // IPv6 字面量：[::1]
            if (host.StartsWith("[") && host.EndsWith("]")) return true;

            UriHostNameType type = Uri.CheckHostName(host);
            if (type == UriHostNameType.IPv4 || type == UriHostNameType.IPv6) return true;
            if (type != UriHostNameType.Dns) return false;       // Unknown -> 畸形主机

            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

            if (host.Length > 253) return false;
            if (host.EndsWith(".")) return false;                // 末尾点，如 act.
            if (host.StartsWith(".")) return false;
            if (host.Contains("..")) return false;               // 空标签，如 example..com

            string[] labels = host.Split('.');
            if (labels.Length < 2) return false;                 // 单标签公共主机不可能存在，如 act

            foreach (string label in labels)
            {
                if (label.Length == 0 || label.Length > 63) return false;
                if (label.StartsWith("-") || label.EndsWith("-")) return false;
                foreach (char c in label)
                {
                    if (!(char.IsLetterOrDigit(c) || c == '-')) return false;
                }
            }

            string tld = labels[labels.Length - 1];
            if (tld.Length < 2) return false;
            if (tld.StartsWith("xn--", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (char c in tld)
            {
                if (!char.IsLetter(c)) return false;             // 顶级域必须为字母
            }
            return true;
        }
    }
}
