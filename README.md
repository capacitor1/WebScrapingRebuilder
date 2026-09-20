# 网页抓取重建器（WebScrapingRebuilder）

一个用 C# 编写的命令行网页抓取工具：以配置文件中给定的入口 URL 为起点，**多线程广度优先（BFS）递归抓取**，
下载页面中引用的所有资源并按规则分类保存到本地，同时输出详细日志与实时控制台状态面板。

- 通用 **Regex + 二次校验** 提取 URL，递归抓取；
- 支持 **多线程**、请求节流（固定间隔 / 指数退避）、失败重试；
- 支持 **M3U8 / HLS** 静态点播视频的自动解析与分片抓取；
- 支持 **下载缓存**、Ctrl+C 优雅中断与 **断点续传**；
- 提供 **反重复策略** 与多种本地目录分组方式；
- 所有配置项集中在一个 INI 文件中。

> **配置项说明全部写在 [`sample-scrape.ini`](sample-scrape.ini) 里**（每一项的含义、取值范围、默认值、示例与注释）。
> 本文档只说明通用语法、运行方式与行为语义，不重复罗列配置项。

## 目录

- [构建与发布](#构建与发布)
- [使用](#使用)
- [配置文件](#配置文件)
- [控制台显示](#控制台显示)
- [核心行为](#核心行为)
  - [抓取流程（BFS）](#抓取流程bfs)
  - [URL 提取：通用 Regex + 二次校验](#url-提取通用-regex--二次校验)
  - [去重](#去重)
  - [多线程与请求节流](#多线程与请求节流)
  - [M3U8 / HLS](#m3u8--hls)
  - [Ctrl+C 优雅中断与续抓](#ctrlc-优雅中断与续抓)
  - [InQueue.txt 的写入语义](#inqutetxt-的写入语义)
  - [下载缓存与断点续传](#下载缓存与断点续传)
  - [结果列表文件](#结果列表文件)
  - [无后缀 URL 的处理](#无后缀-url-的处理)
  - [日志（Scraping.log）](#日志scrapinglog)
- [源码目录结构](#源码目录结构)
- [测试](#测试)
- [已知取舍与限制](#已知取舍与限制)

## 构建与发布

要求 .NET SDK 8.0 或更高。

```bash
# 普通构建
cd WebScrapingRebuilder
dotnet build -c Release
# 输出：bin/Release/net8.0/win-x64/WebScrapingRebuilder.exe

# 发布：单文件 / 仅 Windows x64 / 不启用 AOT / 框架依赖（体积更小，目标机需装 .NET 8 运行时）
dotnet publish -c Release
# 输出：bin/Release/net8.0/win-x64/publish/WebScrapingRebuilder.exe
```

Windows 下也可直接双击根目录的 `publish.cmd`，效果同上。相关属性已写入 `WebScrapingRebuilder.csproj`：
`RuntimeIdentifier=win-x64`、`SelfContained=false`、`PublishSingleFile=true`、`PublishAot=false`、
`DebugType=none`、`SatelliteResourceLanguages=en`。

## 使用

启动时**必须且只能带一个参数**：抓取任务配置文件路径（INI 格式）。

```bash
WebScrapingRebuilder.exe D:\path\to\task.ini
# 或
dotnet run --project WebScrapingRebuilder -- D:\path\to\task.ini
```

程序启动后：

1. 校验并加载 INI 配置（`[Format]` 必须为 `WebsiteScrapingVer1`，否则报错退出）；
2. **先把解析到的配置原样回显到日志**（既有原始键值，也有程序生效值，用于核对）；
3. 在 `SaveTo` 目录创建固定名称的日志文件 **`Scraping.log`**（追加写入）并开始抓取；
4. 抓取是**循环**的：处理完入口页后，从解析出的新 URL 继续抓取，直到没有新 URL
   （或达到可选的 `MaxDownloadCount` / `MaxDepth` 限制）；
5. 按 `MultiThread` 指定的线程数并发下载，队列清空后自然结束。

退出码：配置非法 / 无法启动时为非 0；正常完成或被 Ctrl+C 优雅中断后为 0。

## 配置文件

配置文件为 INI 格式，分为 `[Format]`、`[Task]`、`[Url]`（可选）、`[HttpHeader]` 四段。
**所有配置项的完整说明、取值范围、默认值与示例都在 [`sample-scrape.ini`](sample-scrape.ini) 中**，
请以该文件为准；下面只说明解析语法。

**宽容解析规则：**

- 逐行解析；**整行**以 `;` 开头视为注释（行内 `;` 不是注释），空行忽略；
- 段头写作 `[Section]`；
- 键值行只以**第一个 `=`** 作为分隔符：`键 = 第一个等号之前(去空格)`，`值 = 第一个等号之后整段(去首尾空格)`；
- 因此**值中可以包含任意多个 `=`、空格和特殊字符，无需引号，也无需转义**。例如：

  ```ini
  User-Agent = Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36
  AddParam = token=a=b
  ```

  会分别解析为 `User-Agent = Mozilla/5.0 (...) ... Safari/537.36`、`AddParam = token=a=b`。
- 同一段内普通键重复出现时，取**行号最大（最后出现）**的那一项；
  但 `[Url]` 段的 `IgnoreParamName` / `AddParam` 是**可重复键，全部生效**。

## 控制台显示

- **交互式终端**：不再滚动刷日志，而是用一块**每 250ms 原地刷新**的状态面板显示：
  当前阶段、运行时长、线程数、待处理数、已下载/跳过/提取计数、实时速度与平均速度、
  累计下载大小、错误数，以及**最多 8 个正在下载的 URL 预览**（含百分比进度条与已下/总大小）和最后一条错误。
  详细日志仍完整写入 `Scraping.log`，不丢信息。
- **输出被重定向**（管道 / 文件）时：自动退化为“仅输出错误”的静默模式；结束时始终打印一行结果摘要。
- 可用环境变量强制开关面板：`WSR_DASHBOARD=1` 强制开启，`WSR_DASHBOARD=0` 强制关闭。

## 核心行为

### 抓取流程（BFS）

入口 URL 为深度 0，其直接引用的资源为深度 1，依此类推。工作线程从共享队列中取任务，
下载资源；若资源是文本（HTML/CSS/JS/TXT 等）则从中提取新 URL，归一化、去重后入队，直到队列为空。

### URL 提取：通用 Regex + 二次校验

- 使用最通用的正则匹配文本中任意位置出现的 URL，覆盖：
  - 绝对 URL（`http(s)://...`）；
  - HTML 属性（`src` / `href` / `action` / `poster` / `data-src` 等）中的相对路径与协议相对 URL（`//host/...`）；
  - 脚本中被引号包裹的相对路径（如 `fetch("/api/x")`、`import "./m.js"`）；
  - `srcset`、CSS `url(...)`、`@import`。
- **二次校验**：所有候选必须能通过 `Uri` 解析、协议为 http/https、主机名符合规范。
  除结构畸形（`example..com`、含空格、非法协议等）外，还会过滤**语义上不可能存在**的主机名：
  - 允许：IPv4 / IPv6 字面量、`localhost`、以及**配置的入口主机**（便于内网或单标签主机抓取）；
  - 拒绝：末尾带点（`https://act./`）、单标签（`https://act/`）、空标签（`example..com`）、
    标签长度超限、标签以 `-` 开头/结尾、顶级域少于 2 位或含非字母（punycode `xn--` 除外）。
- 匹配前先做 HTML 实体解码（如 `&amp;`）。

### 去重

- 内部维护线程安全的 `HashSet<string>` 记录**归一化后的完整 URL**（含查询参数）防止重复抓取；
  即使 `a.js?v=1` 与 `a.js?v=2` 内容一致、下载后指向同一本地文件，仍算两个不同的 URL，都会下载。
- 若配置了 `[Url]` 的 `IgnoreParamName`，这些参数会在入队/去重前被剔除，
  因此带不同埋点参数的同一资源只会被抓取一次。
- 本地文件名不含 URL 查询参数（文件系统无法保存参数），落到同一文件时由 `AntiDuplicate` 策略处理冲突。

### 多线程与请求节流

- 使用 `MultiThread` 个工作线程从共享队列并发取任务；队列为空且所有在途任务结束后自然结束。
- 归一化、去重、入队、统计、日志、文件落盘均为线程安全。
- **请求节流**由 `IntervalMs` 控制：
  - `IntervalMs > 0`：每个线程在每次请求前**固定等待**该毫秒数，总速率约为 `MultiThread / IntervalMs`；
  - `IntervalMs = 0`：进入**指数退避模式**（每个线程独立计时）——首次等待 1s；某个 URL 下载失败后等待翻倍
    （上限 60s）；任一 URL 下载成功后立即重置回 1s。适合不想固定间隔、但希望在连续出错时自动放缓的场景。
- 单文件请求内部对可恢复错误自动重试（次数由 `RetryError` 指定）；403/404 等不可恢复状态不重试。

### M3U8 / HLS

针对网页中常见的 HLS 视频，内置 **M3U8 抓取模块**。其触发与解析规则**全部写死在代码里**（不受 `IgnoredFileExt` / `IgnoredFileMime` / `ExtractFileExt` 等配置影响），但**下载环节没有任何特权**：M3U8 播放列表及其分片 / 密钥 / 初始化段与普通文件一样，需遵守 `MinDownloadSizeKb` / `MaxDownloadSizeKb` 等全部规则。

- **触发条件**（满足其一）：URL 后缀为 `.m3u8` / `.m3u`；或响应 MIME 为 HLS 类型
  （`application/vnd.apple.mpegurl`、`application/x-mpegURL`、`audio/mpegurl`、`audio/x-mpegurl` 等）。
- **仅解析环节解耦**：M3U8 格式特殊，主流程把文本读入内存后立即提交给独立的后台解析线程；
  解析完成后的 URL **与普通页面提取的 URL 完全一致地合并进主下载队列**，由同一批工作线程、
  按同一套配置规则下载。**不存在独立的下载队列，也不存在独立的下载规则**。
- **支持范围**（仅静态点播）：
  - **VOD 媒体播放列表**：带 `#EXT-X-ENDLIST` 时为标准静态点播；
  - **直播流（缺少 `#EXT-X-ENDLIST`）**：**不做循环轮询扩展，但仍按当前快照抓取**列表中实际出现的分片，
    得到一份可能不完整的直播流——这是预期行为，不会“一个文件都不下载就跳过”；
  - **master 播放列表**：`#EXT-X-STREAM-INF` / `#EXT-X-MEDIA` / `#EXT-X-I-FRAME-STREAM-INF` 的变体 URI；
  - **`#EXT-X-MAP`**：fMP4 初始化段（如 `init.mp4`）；
  - **`#EXT-X-KEY` / `#EXT-X-SESSION-KEY`**：AES 加密的密钥文件（`METHOD != NONE` 时抓取 `URI`）。
- **相对路径还原**：分片 / 密钥 / 变体等 URI 一律相对**该 M3U8 自身 URL** 解析为绝对 URL，
  **全部返还给主线程的下载队列**，走通用的下载 / 去重 / 保存流程；因此 master 中的变体播放列表会被递归处理。
- **只抓一次**：每个 M3U8 与其他 URL 一样参与去重，任何播放列表都只下载 / 解析一次。
- 每个 M3U8 仍会作为普通文件保存在 `SaveTo` 下，便于核对。

```m3u8
#EXTM3U
#EXT-X-VERSION:7
#EXT-X-TARGETDURATION:4
#EXT-X-MAP:URI="init.mp4"                 ; 初始化段（支持）
#EXT-X-KEY:METHOD=AES-128,URI="key.bin"   ; 密钥文件（支持）
#EXTINF:4.0,
seg0.m4s                                  ; 分片（支持）
#EXT-X-ENDLIST                            ; VOD 标志（缺失则按直播流快照抓取）
```

### Ctrl+C 优雅中断与续抓

- 按一次 **Ctrl+C**：拦截退出信号，**刷写全部日志**，按 `CtrlCAction` 处理在途下载，
  并把“队列中 + 被中断的”URL 写入 `SaveTo\InQueue.txt`，然后正常退出（退出码 0）。
- 按两次 **Ctrl+C**：第二次不再拦截，立即强制退出。
- 下次运行同一配置时：
  - `IgnoreQueueTemp=false`（默认）：自动读取 `InQueue.txt` 继续抓取（不会重复抓入口页）；
  - `IgnoreQueueTemp=true`：忽略 `InQueue.txt`，从 `Netloc` 重新开始；
  - 若上次是 `KeepPartial` 中断，命中同一 URL 时会自动从 `.part` 断点续传。
- 任务正常完成后自动删除 `InQueue.txt`。
- 注意：`InQueue.txt` 每行一个 URL，不记录深度，续抓时深度按 0 处理（详见配置说明中的 `CtrlCAction`）。

### InQueue.txt 的写入语义

- **启动时**：若存在 `InQueue.txt`，读取后立即**截断为 0 字节**（不再保留旧列表），
  运行期间该文件为空，避免下一次误读旧列表。
- **再次 Ctrl+C（或达到 `MaxDownloadCount`）**：把**当前完整**的待下载队列**覆盖写入**该文件
  （先写临时文件再整体替换，保证内容完整）。列表内容 = “所有已入队、但尚未处理完成的 URL”
  （不含已下载 / 已跳过 / 已失败等处理完的条目），**严格按入队顺序**排列，因此不会遗漏任何待下载项，
  顺序也与中断前的队列一致。
- **正常抓取完毕**：彻底删除该文件。

### 下载缓存与断点续传

**先缓存、再移动**：每次下载都先写入 `SaveTo\.partial\<URL 的 SHA256 前 16 位>.part`，
下载完成后先做体积 / MIME 校验与文本解析，再由 `FileSaver` 移动到最终路径。
最终路径永远只在文件完整后才出现。

**断点续传**：与 `.part` 同名的 `.state` 状态文件（纯文本 key=value）保存续传所需的关键校验信息：

```
Version=1
Url=http://127.0.0.1:8765/big/range.bin
ETag="big-v1"
LastModified=Wed, 10 Sep 2025 00:00:00 GMT
TotalLength=3145728
BytesDownloaded=1500000
Mime=application/octet-stream
UpdatedUtc=2026-09-11T00:00:00.0000000Z
```

- 恢复时发送 `Range: bytes=<已下载>-` 并携带 `If-Range: <ETag 或 Last-Modified>`；
  服务器返回 **206** 且 `Content-Range` 校验通过则续传；若返回 **200**（不支持 Range 或校验器已变）则从头重新下载。
- **状态永远先于数据落盘**：写入数据前先写状态文件；下载过程中会先把数据 `Flush(true)` 到磁盘，再更新状态的
  `BytesDownloaded`。因此状态记录的“已提交长度”**永不大于**磁盘上的真实数据；即使中途断电/崩溃，
  下次也会先按状态长度截断 `.part` 再续传，不会出现文件损坏。
- **状态文件修改的最小间隔固定为 60 秒**（写死在代码中，**不可通过 INI 配置**）：
  同一 `.state` 两次写入之间至少相隔 60000ms，跨运行以状态文件里的 `UpdatedUtc` 为节流基线。
  这是为了减少临时文件的创建/替换频率——过于频繁地写状态文件会撞上杀软/索引器/云同步的过滤驱动瞬时锁，
  偶发抛出 `Access to the path is denied.`。因此：短于 60 秒的下载只会写一次初始状态（`BytesDownloaded=起始偏移`），
  未提交的进度在下次运行时不参与续传（会按已提交长度截断后重下），而长下载每 ≥60 秒提交一次检查点。
- 声明了 `Content-Length` 却未读完的下载会按可恢复错误重试，并从断点继续；最终失败 / 策略跳过会清理 `.part` 与 `.state`。
- 所有文件操作（移动 / 删除 / 写入 / 重新打开）对 `IOException` 与 `UnauthorizedAccessException` 做**瞬时错误退避重试**
  （最多 6 次、指数退避）；下载重试过滤器同样把 `UnauthorizedAccessException` 视为可恢复错误，
  避免偶发的杀软锁文件导致 URL 直接被判失败。

**`CtrlCAction` 三种策略：**

| 值 | 行为 |
| --- | --- |
| `Finish` | 不再领取新任务，**先让正在下载的文件下载完成**再退出；这些文件正常入库。 |
| `KeepPartial` | **默认**。立即中断在途请求，但**保留** `.part`/`.state`；URL 写回 `InQueue.txt`，下次运行自动从断点续传。 |
| `Discard` | 立即中断在途请求并**删除**断点文件；URL 写回 `InQueue.txt`，下次重新下载。 |

（兼容别名：`Resume` = `KeepPartial`，`Abort` = `Discard`。达到 `MaxDownloadCount` 的自动中断等同 `KeepPartial`，以便下次续传。）

### 结果列表文件

位于 `SaveTo` 下，一行一个 URL：

| 文件 | 内容 |
| --- | --- |
| `IgnoredByPolicies.txt` | 启用 Host 限制时，**所有**被 Host 策略拦截而跳过的 URL（逐行去重；每条同时写入日志）。 |
| `Failed.txt` | **任何**下载失败的 URL（HTTP 4xx/5xx、重试耗尽的网络错误/超时）。体积/MIME/扩展名跳过属于策略性跳过，不在此列。 |
| `InQueue.txt` | 中断时**全部尚未处理完成**（含被中断的在途 URL）的待下载 URL，**按入队顺序**排列，用于下次续抓；运行期间为空、正常完成后删除。 |

### 无后缀 URL 的处理

当 URL 路径本身没有后缀时（如 `/api/data`）：

1. 先用响应 MIME 尝试恢复后缀（例如 `application/json` → `.json`、`text/html` → `.html`）；
2. `application/octet-stream`（不少服务器的兜底类型）映射为 **`.bin`**，落盘即为 `.bin` 文件；
3. 若 MIME 也无法恢复（服务器未提供 `Content-Type`，或类型未知），**仍按普通文件保存，只是没有后缀**（不会丢弃）；
   此时也**不会解析其内容**（不从中提取 URL），避免对未知数据产生误判。

### 日志（Scraping.log）

固定名 `Scraping.log`，记录：

- 启动时**原样回显**读取到的全部配置项与程序生效值；
- 对哪个文件执行了抓取、从中提取到多少条新 URL；
- 下载了什么文件、原始 URL 与本地文件的映射；
- 哪些请求因 HTTP 错误（403/404 等）被跳过、哪些重试后放弃；
- 续传相关事件（发现本地断点、服务器是否支持续传、从中断处继续）；
- 重试过程、大小/MIME/扩展名被忽略的原因、文件冲突处理结果、最终统计摘要。

> 控制台只显示状态面板（交互式）或错误（重定向），**所有明细日志仅写入该文件**，避免刷屏。

## 源码目录结构

```
WebScrapingRebuilder/
├── WebScrapingRebuilder.csproj
├── Program.cs                 # 入口：参数校验、加载配置、Ctrl+C 信号处理
├── sample-scrape.ini          # 全部配置项的完整参考示例（唯一权威说明）
├── Core/
│   ├── ScrapeConfig.cs        # INI 宽容解析 + 配置模型 + 校验 + 配置回显
│   ├── ScrapeStats.cs         # 线程安全运行统计（速度、进度、活动下载表）
│   └── Scraper.cs             # 多线程循环抓取引擎：去重、下载、重试、取消/续抓、日志
├── Net/
│   ├── UrlExtractor.cs        # 通用 Regex + 二次校验（含主机合理性）的 URL 提取器
│   └── UrlNormalizer.cs       # [Url] 段的参数剔除/添加归一化
├── Storage/
│   ├── FileSaver.cs           # 分组保存 + 反重复策略 + 文件名清洗（线程安全）
│   ├── FileIo.cs              # 文件操作瞬时错误退避重试（IOException/UnauthorizedAccessException）
│   ├── PartialDownload.cs     # 断点续传：.part 缓存 + .state 状态文件（原子写入，最小 60s 修改间隔）
│   └── UrlListFile.cs         # 一行一个 URL 的列表文件（Failed/InQueue 等，去重、线程安全）
├── Hls/
│   ├── M3u8Parser.cs          # M3U8/HLS 解析器（写死，支持 EXT-X-MAP/KEY；直播流按快照抓取分片）
│   └── M3u8Module.cs          # M3U8 独立解析模块（独立后台线程，与主抓取线程解耦）
├── Ui/
│   ├── Logger.cs              # Scraping.log 日志器 + 控制台输出模式
│   └── ConsoleDashboard.cs    # 交互式控制台状态面板（ANSI 原地刷新，CJK 宽度感知）
└── README.md
```

## 已知取舍与限制

- **JS 相对路径**：使用启发式匹配引号内路径，复杂拼接（模板字符串、运行时计算）可能漏抓。
- **重定向**：不合并重定向前后 URL 的等价性（同一资源经不同重定向可能被分别下载）。
- **长路径**：未启用 Windows 长路径（>260 字符）支持，超长路径的本地保存可能失败。
- **续抓深度**：`InQueue.txt` 不记录深度与 Referer，续抓条目深度按 0、父页面信息丢失。
- **断点提交粒度**：受 60 秒最小提交间隔限制，短于 60 秒的下载不会提交中间进度。
- **M3U8**：未特殊处理 `#EXT-X-BYTERANGE`（单文件多区间）与 LL-HLS 的
  `#EXT-X-PART` / `#EXT-X-PRELOAD-HINT`；无后缀 URL 且服务器返回 `application/octet-stream` 时无法识别为 HLS。
  又因体积下限对所有文件生效，若 `MinDownloadSizeKb` 设得比播放列表本身还大，播放列表会被当作过小文件跳过
  （其分片自然也不会被抓取）；需要抓 HLS 时建议把 `MinDownloadSizeKb` 设为 0。
- **直播**：不对直播流做循环轮询/持续扩展，只抓取首次读到的快照分片，输出可能不完整；
  低延迟 LL-HLS 的 `#EXT-X-PART` / `#EXT-X-PRELOAD-HINT` 也不在解析范围内。
