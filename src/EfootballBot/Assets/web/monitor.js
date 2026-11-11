/* 局域网监控页：SSE 优先，失败回落 3 秒轮询。
   数据形状由 MonitorServer 决定：
   { status:{running,paused,mode,matches,keyMatches,playersMaxed,elapsed,lastScreen,lastError,canOperate},
     events:[{t,kind,msg}], logs:[{t,lv,msg}] } */
(function () {
  "use strict";

  var T = new URL(location.href).searchParams.get("t") || "";
  var QS = T ? "?t=" + encodeURIComponent(T) : "&t=";
  var $ = function (id) { return document.getElementById(id); };

  var el = {
    sub: $("sub"), pill: $("pill"), pillText: $("pillText"),
    matches: $("kMatches"), key: $("kKey"), maxed: $("kMaxed"), elapsed: $("kElapsed"),
    scene: $("mScene"), screen: $("mScreen"), error: $("mError"),
    events: $("events"), evCount: $("evCount"),
    log: $("log"), logCount: $("logCount"), toast: $("toast")
  };

  var lastLogKey = "", lastEventKey = "", es = null, pollTimer = null, toastTimer = null;

  function text(node, v, fallback) {
    if (!node) return;
    node.textContent = (v === null || v === undefined || v === "") ? (fallback || "—") : String(v);
  }

  function statusState(s) {
    if (!s) return { state: "idle", text: "连接中" };
    if (!s.running) return { state: "stop", text: "未运行" };
    return s.paused ? { state: "pause", text: "已暂停" } : { state: "run", text: "运行中" };
  }

  function renderStatus(s) {
    if (!s) return;
    var st = statusState(s);
    el.pill.dataset.state = st.state;
    el.pillText.textContent = st.text;
    text(el.sub, s.mode, "空闲");
    text(el.matches, s.matches, "0");
    text(el.key, s.keyMatches, "0");
    text(el.maxed, s.playersMaxed, "0");
    text(el.elapsed, s.elapsed, "0:00");
    text(el.scene, s.mode, "—");
    text(el.screen, s.lastScreen, "—");
    text(el.error, s.lastError ? s.lastError : "无", "无");
    el.error.classList.toggle("has", !!s.lastError);

    var can = s.canOperate !== false;
    Array.prototype.forEach.call(document.querySelectorAll("button[data-cmd]"), function (b) {
      b.disabled = !can;
    });
  }

  function kindClass(kind) {
    if (kind === "关键比赛") return "k-key";
    if (kind === "球员满级") return "k-max";
    return "k-ok";
  }

  function renderEvents(rows) {
    if (!rows || !rows.length) {
      if (lastEventKey !== "") { el.events.innerHTML = '<li class="muted">暂无动态</li>'; lastEventKey = ""; }
      text(el.evCount, "0 条");
      return;
    }
    var key = rows.length + "|" + rows[0].t + rows[0].msg;
    if (key === lastEventKey) return;
    lastEventKey = key;

    var html = "";
    for (var i = 0; i < rows.length; i++) {
      var r = rows[i];
      html += '<li class="' + kindClass(r.kind) + '"><span class="t">' + esc(r.t) + "</span><b>" +
        esc(r.kind) + "</b> · " + esc(r.msg) + "</li>";
    }
    el.events.innerHTML = html;
    text(el.evCount, rows.length + " 条");
  }

  function renderLog(rows) {
    if (!rows || !rows.length) {
      if (lastLogKey !== "") { el.log.innerHTML = '<div class="row muted">暂无日志</div>'; lastLogKey = ""; }
      text(el.logCount, "0 条");
      return;
    }
    var key = rows.length + "|" + rows[0].t + rows[0].msg;
    if (key === lastLogKey) return;
    lastLogKey = key;

    var nearBottom = el.log.scrollTop + el.log.clientHeight >= el.log.scrollHeight - 40;
    var html = "";
    for (var i = 0; i < rows.length; i++) {
      var r = rows[i];
      html += '<div class="row lv-' + esc(r.lv) + '"><span class="ts">' + esc(r.t) + "</span>" +
        '<span class="msg">' + esc(r.msg) + "</span></div>";
    }
    el.log.innerHTML = html;
    text(el.logCount, rows.length + " 条");
    if (nearBottom) el.log.scrollTop = el.log.scrollHeight;
  }

  function esc(s) {
    return String(s === null || s === undefined ? "" : s)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  function toast(msg, bad) {
    el.toast.textContent = msg;
    el.toast.className = "toast on" + (bad ? " bad" : "");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.toast.className = "toast"; }, 2200);
  }

  function api(path) {
    return "/api/" + path + (T ? "?t=" + encodeURIComponent(T) : "");
  }

  function act(cmd) {
    fetch(api(cmd), { method: "GET", cache: "no-store" }).then(function (r) {
      if (!r.ok) throw new Error(String(r.status));
      toast(cmd === "stop" ? "已发送停止" : "已发送 " + cmd, false);
    }).catch(function () {
      toast("操作被拒绝（token 缺失或当前状态不允许）", true);
    });
  }

  Array.prototype.forEach.call(document.querySelectorAll("button[data-cmd]"), function (b) {
    b.addEventListener("click", function () { act(b.dataset.cmd); });
  });

  /* ---- 实时通道：SSE ---- */
  function connectStream() {
    if (!("EventSource" in window)) { startPolling(); return; }
    try { es = new EventSource("/api/stream" + (T ? "?t=" + encodeURIComponent(T) : "")); }
    catch (e) { startPolling(); return; }

    es.onmessage = function (m) {
      var d;
      try { d = JSON.parse(m.data); } catch (e) { return; }
      renderStatus(d.status);
      renderEvents(d.events);
      renderLog(d.logs);
    };
    es.onopen = function () { stopPolling(); };
    es.onerror = function () {
      // 关闭后回落轮询：某些微信内核会静默断开 SSE 且不再重连
      if (es) { try { es.close(); } catch (e) { } es = null; }
      startPolling();
    };
  }

  /* ---- 兜底：3 秒轮询（与旧版行为一致） ---- */
  function startPolling() {
    if (pollTimer) return;
    pollOnce();
    pollTimer = setInterval(pollOnce, 3000);
  }

  function stopPolling() {
    if (!pollTimer) return;
    clearInterval(pollTimer);
    pollTimer = null;
  }

  function getJson(path) {
    return fetch(api(path), { cache: "no-store" }).then(function (r) {
      if (!r.ok) throw new Error(String(r.status));
      return r.json();
    });
  }

  function pollOnce() {
    getJson("status").then(renderStatus).catch(function () {
      el.pill.dataset.state = "idle";
      el.pillText.textContent = "连接失败";
    });
    getJson("events").then(function (d) { renderEvents(d.rows); }).catch(function () { });
    getJson("log?n=100").then(function (d) { renderLog(d.rows); }).catch(function () { });
  }

  connectStream();
  // 首帧先给一次数据，避免等 SSE 握手时页面空白
  pollOnce();
})();
