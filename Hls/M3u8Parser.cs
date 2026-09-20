using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace WebScrapingRebuilder.Hls
{
    /// <summary>M3U8 解析结果。</summary>
    public sealed class M3u8ParseResult
    {
        public List<Uri> Urls = new();   // 解析出的全部绝对 URL（分片 + 初始化段 + 密钥 + 变体等）
        public bool IsMaster;            // master 播放列表（含 #EXT-X-STREAM-INF）
        public bool IsLive;              // 无 #EXT-X-ENDLIST 的媒体播放列表 -> 直播流；仅抓取当前快照，不循环扩展
        public bool IsM3u8;              // 确实是 M3U8（含 #EXTM3U 头）
        public string Reason;            // 诊断信息
    }

    /// <summary>
    /// M3U8/HLS 静态播放列表解析器。
    ///
    /// 说明：本模块的触发与解析逻辑**写死在代码中**，不受 INI 配置（IgnoredFileExt/Mime、
    /// ExtractFileExt/Mime 等）影响。
    ///
    /// 解析范围（规则写死）：
    ///   - master 播放列表（#EXT-X-STREAM-INF / #EXT-X-MEDIA / #EXT-X-I-FRAME-STREAM-INF）；
    ///   - 媒体播放列表的分片（#EXTINF 后的 URI）；
    ///   - #EXT-X-MAP 初始化段（fMP4）；
    ///   - #EXT-X-KEY / #EXT-X-SESSION-KEY AES 加密密钥文件。
    /// 直播流（媒体播放列表缺少 #EXT-X-ENDLIST）：不做循环轮询扩展，但【仍按当前快照抓取】其中
    /// 实际出现的分片等 URL，得到一份可能不完整的直播流——这是预期行为；直接不下载任何内容
    /// 违背抓取器的目的，属于异常行为（低延迟 LL-HLS 的 #EXT-X-PART/#EXT-X-PRELOAD-HINT 不在解析范围内）。
    /// 一切 M3U8 都只抓取/解析一次（由主流程的 URL 去重保证）。
    /// </summary>
    public static class M3u8Parser
    {
        /// <summary>按后缀判定：.m3u8 / .m3u。</summary>
        public static bool IsM3u8Extension(string ext)
            => ext != null
               && (ext.Equals("m3u8", StringComparison.OrdinalIgnoreCase)
                || ext.Equals("m3u", StringComparison.OrdinalIgnoreCase));

        /// <summary>按 MIME 判定 HLS 播放列表。</summary>
        public static bool IsM3u8Mime(string mime)
        {
            if (string.IsNullOrEmpty(mime)) return false;
            int semi = mime.IndexOf(';');
            string m = (semi >= 0 ? mime.Substring(0, semi) : mime).Trim().ToLowerInvariant();
            switch (m)
            {
                case "application/vnd.apple.mpegurl":
                case "application/vnd.apple.mpegurl.audio":
                case "application/x-mpegurl":
                case "application/mpegurl":
                case "audio/mpegurl":
                case "audio/x-mpegurl":
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 解析 M3U8 文本，返回其中所有绝对 URL（相对路径按 <paramref name="baseUri"/> 还原）。
        /// </summary>
        public static M3u8ParseResult Parse(string content, Uri baseUri)
        {
            var result = new M3u8ParseResult();
            if (string.IsNullOrEmpty(content)) { result.Reason = "内容为空"; return result; }
            if (baseUri == null) { result.Reason = "缺少基准 URL"; return result; }

            string text = content.TrimStart('\uFEFF');
            if (!text.Contains("#EXTM3U", StringComparison.Ordinal))
            {
                result.Reason = "缺少 #EXTM3U 头，不是合法的 M3U8";
                return result;
            }
            result.IsM3u8 = true;

            bool hasEndList = text.Contains("#EXT-X-ENDLIST", StringComparison.Ordinal);
            bool isMaster = text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal)
                         || text.Contains("#EXT-X-MEDIA:", StringComparison.Ordinal)
                         || text.Contains("#EXT-X-I-FRAME-STREAM-INF", StringComparison.Ordinal);
            result.IsMaster = isMaster;

            // 直播判定：master 播放列表天然没有 ENDLIST，属于静态；
            // 媒体播放列表缺少 #EXT-X-ENDLIST 时视为直播流：不做循环轮询扩展，
            // 但【仍然解析并抓取当前列表中实际出现的分片/密钥/初始化段】——即抓取一份“快照”。
            // 输出可能不完整，这是预期行为；直接不下载任何内容是异常行为。
            if (!isMaster && !hasEndList)
                result.IsLive = true;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split('\n');
            bool expectVariant = false;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line[0] == '#')
                {
                    // master 播放列表：下一行（非注释）是变体播放列表 URI
                    if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
                    {
                        expectVariant = true;
                        continue;
                    }
                    expectVariant = false;

                    if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                    {
                        AddAttributeUri(line.Substring("#EXT-X-MAP:".Length), "URI", baseUri, seen, result.Urls, "初始化段(#EXT-X-MAP)");
                    }
                    else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                    {
                        string body = line.Substring("#EXT-X-KEY:".Length);
                        string method = GetAttribute(body, "METHOD");
                        if (!"NONE".Equals(method, StringComparison.OrdinalIgnoreCase))
                            AddAttributeUri(body, "URI", baseUri, seen, result.Urls, "加密密钥(#EXT-X-KEY)");
                    }
                    else if (line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
                    {
                        string body = line.Substring("#EXT-X-SESSION-KEY:".Length);
                        string method = GetAttribute(body, "METHOD");
                        if (!"NONE".Equals(method, StringComparison.OrdinalIgnoreCase))
                            AddAttributeUri(body, "URI", baseUri, seen, result.Urls, "加密密钥(#EXT-X-SESSION-KEY)");
                    }
                    else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                    {
                        AddAttributeUri(line.Substring("#EXT-X-MEDIA:".Length), "URI", baseUri, seen, result.Urls, "备用媒体(#EXT-X-MEDIA)");
                    }
                    else if (line.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.Ordinal))
                    {
                        AddAttributeUri(line.Substring("#EXT-X-I-FRAME-STREAM-INF:".Length), "URI", baseUri, seen, result.Urls, "I帧播放列表(#EXT-X-I-FRAME-STREAM-INF)");
                    }
                    // 其它标签（#EXTINF、#EXT-X-BYTERANGE、#EXT-X-TARGETDURATION、#EXT-X-PROGRAM-DATE-TIME 等）无需处理
                    continue;
                }

                // 非注释行：分片 URI（媒体播放列表）或变体播放列表 URI（master）
                AddUri(line, baseUri, seen, result.Urls, expectVariant ? "变体播放列表" : "分片");
                expectVariant = false;
            }

            if (isMaster)
                result.Reason = "master 播放列表";
            else if (result.IsLive)
                result.Reason = "直播流快照（未发现 #EXT-X-ENDLIST，仅抓取当前列出的分片）";
            else
                result.Reason = "VOD 媒体播放列表(#EXT-X-ENDLIST)";
            return result;
        }

        private static void AddAttributeUri(string attrs, string name, Uri baseUri, HashSet<string> seen, List<Uri> output, string kind)
        {
            string value = GetAttribute(attrs, name);
            if (string.IsNullOrWhiteSpace(value)) return;
            AddUri(value, baseUri, seen, output, kind);
        }

        private static void AddUri(string value, Uri baseUri, HashSet<string> seen, List<Uri> output, string kind)
        {
            value = value.Trim();
            if (value.Length == 0) return;
            value = WebUtility.HtmlDecode(value);
            if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                return;

            Uri absolute;
            try { absolute = new Uri(baseUri, value); }
            catch { return; }

            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps) return;

            // 用去 fragment 的规范化字符串去重
            string key = absolute.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped);
            if (seen.Add(key)) output.Add(absolute);
        }

        /// <summary>
        /// 解析 #EXT-X-* 标签的属性列表（逗号分隔，值可带双引号，支持 \" 转义）。
        /// 返回指定属性名的值（不存在返回 null）。
        /// </summary>
        public static string GetAttribute(string attrs, string name)
        {
            if (string.IsNullOrEmpty(attrs) || string.IsNullOrEmpty(name)) return null;
            int i = 0, n = attrs.Length;
            while (i < n)
            {
                while (i < n && (attrs[i] == ',' || char.IsWhiteSpace(attrs[i]))) i++;
                int keyStart = i;
                while (i < n && attrs[i] != '=' && attrs[i] != ',') i++;
                if (i >= n) break;
                if (attrs[i] == ',') { i++; continue; } // 该片段没有 '='，跳过
                string key = attrs.Substring(keyStart, i - keyStart).Trim();
                i++; // 跳过 '='
                string value;
                if (i < n && attrs[i] == '"')
                {
                    i++;
                    var sb = new StringBuilder();
                    while (i < n && attrs[i] != '"')
                    {
                        if (attrs[i] == '\\' && i + 1 < n) { sb.Append(attrs[i + 1]); i += 2; }
                        else sb.Append(attrs[i++]);
                    }
                    if (i < n) i++; // 跳过右引号
                    value = sb.ToString();
                }
                else
                {
                    int valStart = i;
                    while (i < n && attrs[i] != ',') i++;
                    value = attrs.Substring(valStart, i - valStart).Trim();
                }
                if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return value;
            }
            return null;
        }
    }
}
