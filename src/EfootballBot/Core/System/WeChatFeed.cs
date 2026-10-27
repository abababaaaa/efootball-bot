using EfootballBot.Core.Bot;
using EfootballBot.Core.Config;

namespace EfootballBot.Core.System;

/// <summary>
/// 微信实时动态（事件推送层）：比赛完成 / 球员满级 / 警告错误日志 / 运行中摘要。
/// 在 <see cref="Notifier"/>（到达层）之上做事件分类、合并节流与每日配额——
/// 免费推送渠道（如 Server酱免费版每天 5 条）经不起每事件一条，必须收敛：
/// - 同类事件在合并窗口（NotifyConfig.MergeMinutes）内只推一条，其余并成「另有 N 起」；
/// - 每日推送总条数受 NotifyConfig.DailySendLimit 保护（按日期滚动，超限只记本地日志）；
/// - 运行中摘要按 NotifyConfig.SummaryIntervalMinutes 定时推一条（引擎会话期间）。
/// 生命周期：引擎 StartAsync 时 <see cref="SessionStart"/>，Stop 时 <see cref="SessionStop"/>。
/// 所有失败只记日志，绝不影响挂机。
/// </summary>
public static class WeChatFeed
{
    private sealed class KindState
    {
        public string Title = "";
        public DateTime LastSent = DateTime.MinValue;
        public int Pending;
        public string? LastDetail;
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<NotifyKind, KindState> States = new();
    private static AppConfig? _cfg;
    private static Func<string>? _summaryBody;
    private static Action<string, LogLevel>? _log;
    private static global::System.Threading.Timer? _timer;
    private static DateTime _lastSummaryAt = DateTime.MinValue;
    private static DateTime _quotaDay = DateTime.MinValue;
    private static int _sentToday;
    private static bool _quotaWarned;

    /// <summary>可注入时钟（单测用）。</summary>
    internal static Func<DateTime> Clock = () => DateTime.Now;

    /// <summary>单测注入：拦截实际发送（null = 走 Notifier 真发）。</summary>
    internal static Action<NotifyKind, string, string>? SendHook;

    public static void SessionStart(AppConfig cfg, Func<string> summaryBody, Action<string, LogLevel>? log)
    {
        lock (Lock)
        {
            _cfg = cfg;
            _summaryBody = summaryBody;
            _log = log;
            States.Clear();
            _lastSummaryAt = Clock();
            // 定时器只在会话期间存活；20s 一拍足够（合并窗口以分钟计）。
            // 单测注入 SendHook 时不建定时器（真实时钟的异步回调会干扰确定性断言）。
            if (_timer is null && SendHook is null)
                _timer = new global::System.Threading.Timer(_ => SafeFlush(), null,
                    TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        }
    }

    public static void SessionStop()
    {
        lock (Lock)
        {
            // 积压事件直接丢弃：停止摘要本身已带总场次/满级计数，明细无增量价值
            _timer?.Dispose();
            _timer = null;
            States.Clear();
            _cfg = null;
            _summaryBody = null;
        }
    }

    /// <summary>一场比赛完成（场次计数已由调用方更新）。</summary>
    public static void MatchDone(string module, bool keyMatch, int total, int keyTotal,
        Action<string, LogLevel>? log = null)
        => Enqueue(NotifyKind.MatchDone, $"⚽ 比赛完成 · {module}",
            $"第 {total} 场" + (keyMatch ? $"（关键 {keyTotal}）" : ""), log);

    /// <summary>检出满级球员（detail 带「第 N/11 位」或「比赛中满级提示」上下文）。</summary>
    public static void MaxLevel(string detail, int total, Action<string, LogLevel>? log = null)
        => Enqueue(NotifyKind.MaxLevel, "★ 球员满级", $"{detail} · 累计 {total}", log);

    /// <summary>警告/错误日志转发：错误立即推，警告走合并窗口。</summary>
    public static void LogLine(string msg, LogLevel lv, Action<string, LogLevel>? log = null)
    {
        if (lv is not (LogLevel.Warn or LogLevel.Error)) return;
        string detail = Truncate(msg);
        if (lv == LogLevel.Error)
            SendNow(NotifyKind.Warn, "‼ 挂机错误", detail, log);
        else
            Enqueue(NotifyKind.Warn, "⚠ 挂机警告", detail, log);
    }

    private static void Enqueue(NotifyKind kind, string title, string detail, Action<string, LogLevel>? log)
    {
        lock (Lock)
        {
            var cfg = _cfg;
            if (cfg is null || !PrecheckLocked(cfg, kind, log)) return;
            var st = State(kind, title);
            var now = Clock();
            if (st.LastSent != DateTime.MinValue && WithinWindow(cfg, st.LastSent, now))
            {
                st.Pending++;
                st.LastDetail = detail;
                return;
            }
            string body = detail + (st.Pending > 0 ? $"\n（另有 {st.Pending} 起同类未单独推送）" : "");
            ResetPending(st);
            SendLocked(cfg, kind, title, body, log, now);
        }
    }

    /// <summary>定时器/单测入口：把到期的合并积压推出去，并检查运行摘要是否到期。</summary>
    internal static void FlushDue(AppConfig cfg, Func<string>? summaryBody,
        Action<string, LogLevel>? log, DateTime now)
    {
        lock (Lock)
        {
            foreach (var (kind, st) in States)
            {
                if (st.Pending <= 0) continue;
                if (st.LastSent != DateTime.MinValue && WithinWindow(cfg, st.LastSent, now)) continue;
                string body = $"最新：{st.LastDetail}\n（合并推送：另有 {st.Pending} 起同类）";
                ResetPending(st);
                SendLocked(cfg, kind, st.Title, body, log, now);
            }
            int interval = cfg.Notify.SummaryIntervalMinutes;
            if (summaryBody is not null && cfg.Notify.OnRunningSummary && interval > 0 &&
                (_lastSummaryAt == DateTime.MinValue || (now - _lastSummaryAt).TotalMinutes >= interval))
            {
                _lastSummaryAt = now;
                SendLocked(cfg, NotifyKind.Summary, "📊 挂机运行摘要", summaryBody(), log, now);
            }
        }
    }

    private static void SendNow(NotifyKind kind, string title, string body, Action<string, LogLevel>? log)
    {
        lock (Lock)
        {
            var cfg = _cfg;
            if (cfg is null || !PrecheckLocked(cfg, kind, log)) return;
            SendLocked(cfg, kind, title, body, log, Clock());
        }
    }

    /// <summary>发送前置：会话在、URL 已配、该类开关开。配额在真正发送时再计。</summary>
    private static bool PrecheckLocked(AppConfig cfg, NotifyKind kind, Action<string, LogLevel>? log)
    {
        if (string.IsNullOrWhiteSpace(cfg.Notify.WebhookUrl)) return false;
        return Notifier.IsKindEnabled(cfg.Notify, kind);
    }

    private static void SendLocked(AppConfig cfg, NotifyKind kind, string title, string body,
        Action<string, LogLevel>? log, DateTime now)
    {
        if (!TryConsumeQuotaLocked(cfg, now, log)) return;
        var st = State(kind, title);
        st.LastSent = now;
        var hook = SendHook;
        if (hook is not null)
        {
            hook(kind, title, body);
            return;
        }
        Notifier.Send(cfg, title, body, kind, log);
    }

    private static bool TryConsumeQuotaLocked(AppConfig cfg, DateTime now, Action<string, LogLevel>? log)
    {
        if (_quotaDay != now.Date)
        {
            _quotaDay = now.Date;
            _sentToday = 0;
            _quotaWarned = false;
        }
        int limit = cfg.Notify.DailySendLimit;
        if (limit > 0 && _sentToday >= limit)
        {
            if (!_quotaWarned)
            {
                _quotaWarned = true;
                log?.Invoke($"微信推送已达今日上限（{limit} 条），后续事件只记日志，明日自动恢复", LogLevel.Warn);
            }
            return false;
        }
        if (limit > 0) _sentToday++;
        return true;
    }

    private static bool WithinWindow(AppConfig cfg, DateTime lastSent, DateTime now)
        => (now - lastSent).TotalMinutes < Math.Max(0, cfg.Notify.MergeMinutes);

    private static KindState State(NotifyKind kind, string title)
    {
        if (!States.TryGetValue(kind, out var st))
            States[kind] = st = new KindState();
        st.Title = title;
        return st;
    }

    private static void ResetPending(KindState st)
    {
        st.Pending = 0;
        st.LastDetail = null;
    }

    private static string Truncate(string msg)
    {
        msg = msg.Replace("\r", "").Replace("\n", " ").Trim();
        return msg.Length <= 200 ? msg : msg[..200] + "…";
    }

    private static void SafeFlush()
    {
        AppConfig? cfg;
        Func<string>? summary;
        Action<string, LogLevel>? log;
        lock (Lock)
        {
            cfg = _cfg;
            summary = _summaryBody;
            log = _log;
        }
        if (cfg is null) return;
        try { FlushDue(cfg, summary, log, Clock()); }
        catch { /* 推送层任何异常都不影响挂机 */ }
    }

    /// <summary>单测用：清空全部静态状态（含配额计数）。</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            _timer?.Dispose();
            _timer = null;
            States.Clear();
            _cfg = null;
            _summaryBody = null;
            _lastSummaryAt = DateTime.MinValue;
            _quotaDay = DateTime.MinValue;
            _sentToday = 0;
            _quotaWarned = false;
        }
    }
}
