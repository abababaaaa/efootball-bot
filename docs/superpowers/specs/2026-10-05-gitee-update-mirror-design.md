# 自动更新增加国内源（Gitee）设计

日期：2026-10-05
状态：已确认，待编写实现计划

## 背景与目标

当前自动更新完全依赖 GitHub：

- 版本检查：`GET https://api.github.com/repos/abababaaaa/efootball-bot/releases`
- 文件下载：GitHub Release 附件（自包含 zip，约 66.8MB）

有国内用户反馈 GitHub 完全连不上，导致无法检查更新和下载新版本。

目标：

1. 新增 Gitee（码云）作为国内更新源，**默认优先使用**，国内访问稳定快速。
2. 保留 GitHub 作为兜底：Gitee 不可用时（网络错误、限流、5xx）自动回退 GitHub。
3. 发版流程自动化：打一次 `v*` tag，CI 同时发布 GitHub Release 与 Gitee 发行版，无需手动上传。
4. 用户侧无新增配置项，双源切换完全自动、无感知。

非目标（YAGNI）：

- 不做多源手动选择、不做源地址用户可配置。
- 不接入对象存储（COS/OSS）。
- 不改变更新的批处理替换/重启机制。

## 关键事实（已核实）

- Gitee 免费版发行版附件：单文件 ≤ 100MB（zip 约 66.8MB，可容纳）；每仓库附件总量 ≤ 1GB（约 15 个版本），需 CI 定期清理旧发行版。
- Gitee API v5 基址：`https://gitee.com/api/v5`；发行版相关接口与 GitHub 形态类似但字段不同（见下）。
- Gitee 匿名 API 限流官方未公开精确数字；客户端有 30 分钟本地缓存 + GitHub 兜底，风险可控。

## 方案选择

客户端检查方式二选一：

- **方案 A（选定）：Gitee Releases API**。请求 `GET /repos/{owner}/{repo}/releases`，与现有 GitHub 解析逻辑平行，改动最小；失败回退 GitHub。
- 方案 B（否决）：静态 `latest.json` + Gitee Raw。需 CI 额外维护清单文件，raw 存在重定向与 60~300 秒缓存延迟，多一个故障点。

## 详细设计

### 1. 客户端：`src/EfootballBot/Core/System/Updater.cs`

新增常量：

```csharp
public const string DefaultGiteeRepository = "abababaaaa/efootball-bot";
```

（Gitee 仓库实际路径待用户创建仓库后确认；若路径不同，仅改此常量。）

将现有 `CheckAsync` 重构为编排器，并抽出两个内部检查方法：

- `CheckAsync(repository, includePrerelease, force)`：
  1. 缓存命中逻辑不变（key 改为与具体源无关的统一 key：`$"{includePrerelease}"`，因为两个源对用户是同一逻辑目标）。
  2. 先 `CheckGiteeAsync(includePrerelease)`，HttpClient 超时 15 秒。
  3. Gitee 返回错误/异常/限流时，再 `CheckGitHubAsync(repository, includePrerelease)`（即现有逻辑原样抽出）。
  4. 两源都失败时返回 `UpdateResult.Error("Gitee 与 GitHub 均无法连接，请检查网络后重试")`。
  5. 任一源成功 → 走现有 `Cache(...)` 缓存后返回。
- 回退判定：Gitee 侧任何 `UpdateResult.IsError`（含 `HttpRequestException`、403/429、解析失败）都触发回退；`UpToDate` 与 `Available` 视为成功直接返回。

Gitee 响应模型（字段为 snake_case，与 GitHub 不同，单独定义 record）：

```csharp
private record GiteeRelease(
    string Tag_Name,
    string? Name,
    bool Prerelease,
    string? Body,
    string Created_At,
    List<GiteeAsset> Assets
);
private record GiteeAsset(string Name, long Size, string Browser_Download_Url);
```

解析与选取规则与 GitHub 一致：按 prerelease 过滤 → 按创建时间倒序取最新 → `Version.TryParse(tag.TrimStart('v'))` → 比较 `CurrentVersion` → 取首个 `.zip` 资产。Gitee 发行版列表接口默认按创建时间倒序，无需客户端排序，但保留排序以稳妥（按 `Created_At` 解析后倒序）。

`DownloadAndApplyAsync` **完全不变**：下载 URL 来自检查结果中的 `Browser_Download_Url`，Gitee 附件走国内 CDN 直连。

30 分钟缓存、`GITHUB_TOKEN` 环境变量支持、唯一临时目录、bat 替换脚本等现有行为全部保留。

### 2. CI：`.github/workflows/build.yml`

在 `release` 作业现有「Create Release」步骤之后，新增 PowerShell 步骤「Sync to Gitee」，`continue-on-error: true`（Gitee 失败不阻塞 GitHub Release）。脚本逻辑：

1. 读取 GitHub Release 信息（tag、name、body 可从 `gh release view` 获取，或直接用 tag 名作为标题）。
2. `POST https://gitee.com/api/v5/repos/{owner}/{repo}/releases`
   - 参数：`access_token=$env:GITEE_TOKEN`、`tag_name`、`name`、`body`、`prerelease`（tag 含 `-` 时 true）、`target_commitish=main`。
   - 幂等：若返回"已存在"类错误（4xx 且消息含已存在），改为先 `GET .../releases/tags/{tag}` 取既有 release id，继续后续附件上传。
3. `POST https://gitee.com/api/v5/repos/{owner}/{repo}/releases/{id}/attach_files`
   - multipart/form-data，字段 `file=@release-artifact/efootball-bot-win-x64.zip`。
   - 用 `curl.exe`（Windows 自带真实 curl，非 PowerShell alias）上传，确保 66MB 大文件 multipart 正确。
4. 清理旧版本：`GET .../releases?per_page=100`，仅保留最近 10 个发行版，其余 `DELETE .../releases/{id}`，规避 1GB 附件总量上限。删除失败只警告不中止。
5. 所有输出对 token 做脱敏（脚本中不打印 token）。

Gitee owner/repo 硬编码在 workflow 中（与客户端常量保持一致）。

### 3. UI：更新设置面板

文件：`src/EfootballBot/MainWindow.Settings.cs` 及对应 XAML。

- 在现有只读仓库行（GitHub）下方新增一行：标签「国内更新源（Gitee）」，值为 `Updater.DefaultGiteeRepository` 的 TextBlock（只读样式，与 GitHub 行一致），点击用资源管理器/浏览器打开 `https://gitee.com/{DefaultGiteeRepository}`。
- 版本号显示、检查更新按钮、立即更新、横幅、自动下载安装等均不变。
- `UpdateConfig`（`AppConfig.cs`）**不新增字段**；现有 Repository 字段保留（GitHub 源内部使用，UI 已不绑定编辑）。

## 错误处理

| 场景 | 行为 |
| --- | --- |
| Gitee 正常 | 使用 Gitee 结果，不请求 GitHub |
| Gitee 超时/网络错误/限流/解析失败 | 自动回退 GitHub，日志 Warn 记录 Gitee 失败原因 |
| 两源都失败 | 返回明确错误：Gitee 与 GitHub 均无法连接 |
| Gitee 附件下载中断 | 现有下载逻辑报错，用户可重试（重试时检查缓存仍给出同一 URL） |
| CI 中 Gitee 同步失败 | `continue-on-error`，GitHub Release 不受影响；日志提示下次手动补传或重跑作业 |

## 验证计划

1. `dotnet build` 通过，无新增警告。
2. 重新发布自包含单文件：
   `dotnet publish src/EfootballBot -c Release -r win-x64 -f net10.0-windows10.0.19041.0 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=PdbOnly -o publish`
   发布前先停止旧实例。
3. 启动新 exe，进入「更新」面板点「检查更新」：
   - 正常返回；日志显示命中 Gitee 源；
   - 有新版本时下载 URL 域名为 Gitee 附件域名（`*.gitee.com` / gitee 附件域）。
4. 兜底验证（可选）：临时将 Gitee 常量改错，确认自动回退 GitHub 并在日志中看到回退记录。
5. CI 改动不在本地触发（仅在下次打 tag 时生效）；本次不 push、不打 tag。

## 用户需一次性手动完成的准备

1. 注册 Gitee 账号，按提示完成实名认证（发行版公开下载需要）。
2. 新建仓库（建议路径 `abababaaaa/efootball-bot`），可用 Gitee「导入已有仓库」从 GitHub 一键镜像初始代码。
3. Gitee → 设置 → 私人令牌，生成令牌（勾选 projects 权限）。
4. GitHub 仓库 Settings → Secrets and variables → Actions → New secret：名称 `GITEE_TOKEN`，值粘贴令牌。

## 约束遵守

- 本次所有操作仅本地进行：允许本地 commit，**禁止 push origin、禁止打远程标签、禁止触发 CI/Release**，直到用户明确解除。
