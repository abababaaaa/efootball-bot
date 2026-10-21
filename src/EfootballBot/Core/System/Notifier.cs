using System.Net.Http;
using System.Text;
using System.Text.Json;
using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;

namespace EfootballBot.Core.System;

/// <summary>
/// 完成/异常通知（Webhook）：挂机跑完或异常停止时推一条消息到用户自己配置的 URL。
/// 支持两种形态（覆盖绝大多数「一个 URL 搞定」的服务）：
/// ① URL 里含 <c>{msg}</c> 占位符 → GET 请求，占位处替换为 URL 编码的消息
///    （Bark、ntfy、Telegram Bot sendMessage 之类都吃这一口）；
/// ② 否则 POST 一段 JSON：<c>{title, message, instance, error, at}</c>
///    （飞书/钉钉/企微群机器人、自建 webhook 均可用；字段名不同时请自行加个中转）。
///
/// 设计约束：默认关闭且 URL 为空；发送在后台线程、失败只记日志——网络问题绝不允许
/// 影响挂机收尾。
/// </summary>
public static class Notifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static void Send(AppConfig cfg, string title, string body, bool isError,
        Action<string, LogLevel>? log = null)
    {
        var n = cfg.Notify;
        string url = (n.WebhookUrl ?? "").Trim();
        if (url.Length == 0) return;
        if (isError ? !n.OnError : !n.OnStop) return;

        string instance = string.IsNullOrWhiteSpace(n.InstanceName) ? "eFootballBot" : n.InstanceName.Trim();
        string text = $"{title}\n{body}";

        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = url.Contains("{msg}")
                    ? await Http.GetAsync(url.Replace("{msg}", Uri.EscapeDataString(text)))
                    : await Http.PostAsync(url, new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            title,
                            message = body,
                            instance,
                            error = isError,
                            at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        }), Encoding.UTF8, "application/json"));
                log?.Invoke($"通知已发送（HTTP {(int)resp.StatusCode}）", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                log?.Invoke($"通知发送失败：{ex.Message}", LogLevel.Warn);
            }
        });
    }
}
