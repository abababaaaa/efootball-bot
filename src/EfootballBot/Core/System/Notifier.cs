using System.Net.Http;
using System.Text;
using System.Text.Json;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;

namespace EfootballBot.Core.System;

/// <summary>通知事件种类：决定走哪个分类开关（Test 永远放行，供设置页「测试发送」）。</summary>
public enum NotifyKind
{
    Stop,
    Error,
    MatchDone,
    MaxLevel,
    Warn,
    Summary,
    Test,
}

/// <summary>
/// 完成/异常通知（Webhook）：挂机跑完或异常停止时推一条消息到用户自己配置的 URL。
/// 支持两种形态（覆盖绝大多数「一个 URL 搞定」的服务）：
/// ① URL 里含占位符 → GET 请求，占位处替换为 URL 编码文本：
///    - <c>{msg}</c> → 整条消息（title+body，旧形态，Bark/ntfy/Telegram Bot sendMessage）；
///    - <c>{title}</c> / <c>{body}</c> / <c>{instance}</c> → 分字段（Server酱
///      <c>https://sctapi.ftqq.com/KEY.send?title={title}&amp;desp={body}</c>、PushPlus 等都吃这一口）；
/// ② 都不含 → POST 一段 JSON：<c>{title, message, instance, error, at}</c>
///    （飞书/钉钉/企微群机器人、自建 webhook 均可用；字段名不同时请自行加个中转）。
///
/// 事件分类开关与合并/每日配额在 <see cref="WeChatFeed"/>（事件层）；本类只管到达。
/// 设计约束：默认关闭且 URL 为空；发送在后台线程、失败只记日志——网络问题绝不允许
/// 影响挂机收尾。
/// </summary>
public static class Notifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static void Send(AppConfig cfg, string title, string body, bool isError,
        Action<string, LogLevel>? log = null)
        => Send(cfg, title, body, isError ? NotifyKind.Error : NotifyKind.Stop, log);

    public static void Send(AppConfig cfg, string title, string body, NotifyKind kind,
        Action<string, LogLevel>? log = null)
        => _ = SendAsync(cfg, title, body, kind, log);

    /// <summary>可等待版本（设置页「测试发送」要给用户显示结果）；返回 HTTP 是否成功。</summary>
    public static async Task<bool> SendAsync(AppConfig cfg, string title, string body, NotifyKind kind,
        Action<string, LogLevel>? log = null)
    {
        var n = cfg.Notify;
        string url = (n.WebhookUrl ?? "").Trim();
        if (url.Length == 0) return false;
        if (!IsKindEnabled(n, kind)) return false;

        string instance = string.IsNullOrWhiteSpace(n.InstanceName) ? "eFootballBot" : n.InstanceName.Trim();
        string text = $"{title}\n{body}";
        try
        {
            // 企业微信群机器人：qyapi 地址自动识别，按它的 msgtype 格式发（免费、限频 20 条/分钟）；
            // 它的成败看 JSON errcode 而非 HTTP 状态码，必须单独解析
            if (IsWeComUrl(url))
            {
                using var resp = await Http.PostAsync(url, new StringContent(
                    BuildWeComPayload(instance, title, body, hasUserName: !string.IsNullOrWhiteSpace(n.InstanceName)),
                    Encoding.UTF8, "application/json"));
                string respText = await resp.Content.ReadAsStringAsync();
                bool ok = resp.IsSuccessStatusCode && IsWeComOk(respText);
                log?.Invoke(ok
                    ? $"企微通知已发送（errcode 0）"
                    : $"企微通知失败（HTTP {(int)resp.StatusCode}，{TruncateResp(respText)}）",
                    ok ? LogLevel.Debug : LogLevel.Warn);
                return ok;
            }

            string? getUrl = BuildGetUrl(url, title, body, instance, text);
            HttpResponseMessage resp2 = getUrl is not null
                ? await Http.GetAsync(getUrl)
                : await Http.PostAsync(url, new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        title,
                        message = body,
                        instance,
                        error = kind is NotifyKind.Error or NotifyKind.Warn,
                        at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    }), Encoding.UTF8, "application/json"));
            log?.Invoke($"通知已发送（HTTP {(int)resp2.StatusCode}）", LogLevel.Debug);
            return resp2.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log?.Invoke($"通知发送失败：{ex.Message}", LogLevel.Warn);
            return false;
        }
    }

    internal static bool IsWeComUrl(string url) => url.Contains("qyapi.weixin.qq.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>企微消息体用字面 UTF-8（默认编码器会把中文转义成 \uXXXX，日志/排障不可读）。</summary>
    private static readonly JsonSerializerOptions WeComJsonOptions = new()
    {
        Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>企业微信群机器人消息体：msgtype=text；用户设了实例名时加前缀区分多开。</summary>
    internal static string BuildWeComPayload(string instance, string title, string body, bool hasUserName)
        => JsonSerializer.Serialize(new
        {
            msgtype = "text",
            text = new
            {
                content = (hasUserName ? $"[{instance}] " : "") + $"{title}\n{body}",
            },
        }, WeComJsonOptions);

    /// <summary>企微返回 {"errcode":0} 才算成功（HTTP 恒 200）。</summary>
    internal static bool IsWeComOk(string respText)
    {
        try
        {
            using var doc = JsonDocument.Parse(respText);
            return doc.RootElement.TryGetProperty("errcode", out var e) && e.GetInt32() == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string TruncateResp(string s)
    {
        s = s.Replace("\r", "").Replace("\n", " ").Trim();
        return s.Length <= 80 ? s : s[..80] + "…";
    }

    /// <summary>事件种类 → 分类开关映射（Test 永远放行）。</summary>
    internal static bool IsKindEnabled(NotifyConfig n, NotifyKind kind) => kind switch
    {
        NotifyKind.Stop => n.OnStop,
        NotifyKind.Error => n.OnError,
        NotifyKind.MatchDone => n.OnMatchDone,
        NotifyKind.MaxLevel => n.OnMaxLevel,
        NotifyKind.Warn => n.OnWarnLog,
        NotifyKind.Summary => n.OnRunningSummary,
        NotifyKind.Test => true,
        _ => true,
    };

    /// <summary>
    /// URL 含占位符则替换为 GET 地址（各自 URL 编码），否则返回 null（走 POST JSON）。
    /// {title}/{body}/{instance} 分字段形态优先于旧的 {msg} 整条形态。
    /// </summary>
    internal static string? BuildGetUrl(string url, string title, string body, string instance, string text)
    {
        if (url.Contains("{title}") || url.Contains("{body}") || url.Contains("{instance}"))
            return url.Replace("{title}", Uri.EscapeDataString(title))
                      .Replace("{body}", Uri.EscapeDataString(body))
                      .Replace("{instance}", Uri.EscapeDataString(instance));
        if (url.Contains("{msg}"))
            return url.Replace("{msg}", Uri.EscapeDataString(text));
        return null;
    }
}
