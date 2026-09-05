/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 设备健康综合分页（v3.22.0 规格 04）
   public/js/page-device-health.js
   ----------------
   数据源：GET /api/devices/health（viewer 可读）
   渲染：每机台一张卡 —— 左健康分大数字（绿/黄/红）+ 级别，中 4 子项进度条
   （cpu/disk/memory/offline，score + raw + trend），右 top_concern + 建议。
   黑白红令牌运行时读，零第三方。30 秒自动刷新。
   ============================================================ */
(function (window) {
  "use strict";
  var App = window.App;

  var refs = {};

  function levelColor(level, css) {
    var red = css.getPropertyValue("--red") || "#FF5C5C";
    var amber = css.getPropertyValue("--amber") || "#FFBE5C";
    var green = css.getPropertyValue("--green") || "#3DD68C";
    return level === "critical" ? red : level === "warn" ? amber : green;
  }

  function machineCard(m, css) {
    var color = levelColor(m.level, css);
    var comps = m.components || [];
    var bars = "";
    comps.forEach(function (c) {
      var pct = Math.max(0, Math.min(100, c.score || 0));
      var ccolor = pct < 50 ? (css.getPropertyValue("--red") || "#FF5C5C")
                 : pct < 80 ? (css.getPropertyValue("--amber") || "#FFBE5C")
                 : (css.getPropertyValue("--green") || "#3DD68C");
      bars += '<div style="margin:6px 0">'
        + '<div style="display:flex;justify-content:space-between;font-size:12px">'
        + '<span>' + App.esc(c.name) + ' <span style="opacity:.6">' + App.esc(c.raw || "") + '</span></span>'
        + '<span>' + (c.score || 0).toFixed(0) + ' × ' + (c.weight || 0).toFixed(2) + '</span>'
        + '</div>'
        + '<div style="height:6px;background:rgba(128,128,128,.18);border-radius:3px;overflow:hidden">'
        + '<div style="height:100%;width:' + pct + '%;background:' + ccolor + '"></div>'
        + '</div>'
        + '<div style="font-size:11px;opacity:.55">' + App.esc(c.trend || "stable") + '</div>'
        + '</div>';
    });
    var concern = m.top_concern
      ? '<div style="margin-top:8px;font-size:12px">主要扣分项：<b style="color:' + color + '">' + App.esc(m.top_concern) + '</b></div>'
      + (m.recommendation ? '<div style="font-size:12px;opacity:.8">建议：' + App.esc(m.recommendation) + '</div>' : "")
      : "";
    return '<div class="card" style="min-width:280px;flex:1 1 280px;border-color:' + color + '">'
      + '<h2 style="display:flex;align-items:center;gap:10px">'
      +   '<span style="font-size:34px;font-weight:700;color:' + color + '">' + (m.health || 0).toFixed(0) + '</span>'
      +   '<span>' + App.esc(m.machine) + '</span>'
      +   '<span class="badge" style="color:' + color + ';border-color:' + color + '">' + App.esc(m.level) + '</span>'
      + '</h2>'
      + bars
      + concern
      + '</div>';
  }

  function loadHealth() {
    App.fetchJSON("/api/devices/health").then(function (d) {
      if (!d) return;
      var css = getComputedStyle(document.body);
      var s = d.summary || { ok: 0, warn: 0, critical: 0 };
      if (refs.summary) {
        refs.summary.textContent = "ok " + (s.ok || 0) + " · warn " + (s.warn || 0) + " · critical " + (s.critical || 0)
          + " · 更新于 " + App.esc(d.generated_at || "");
      }
      var machines = (d.machines || []).slice().sort(function (a, b) { return (a.health || 0) - (b.health || 0); });
      var html = machines.map(function (m) { return machineCard(m, css); }).join("");
      refs.grid.innerHTML = html || '<div class="hint">暂无设备数据（聚合端尚未收到 device_info 心跳）</div>';
      App.applyRoleVisibility(refs.grid);
    }).catch(function () {});
  }

  App.Modules["page-device-health"] = {
    init: function (el) {
      el.innerHTML =
        '<div class="page-node">'
        + '<div class="card">'
        +   '<h2>设备健康综合分 <span class="n" data-ref="summary">…</span></h2>'
        +   '<div style="font-size:12px;opacity:.6;margin-bottom:10px">'
        +     '综合分 = cpu 0.30 + disk 0.30 + memory 0.15 + offline 0.25（权重可在 config.json health_weight_* 调整）；'
        +     '&lt;50 critical / &lt;80 warn；health_score_enabled=false 时高亮回退单指标阈值'
        +   '</div>'
        +   '<div data-ref="grid" style="display:flex;flex-wrap:wrap;gap:12px"></div>'
        + '</div>'
        + '<div class="foot">健康分由 device_info + device_samples 7 天滑动窗口计算 · 30 秒自动刷新</div>'
        + "</div>";

      var nodes = el.querySelectorAll("[data-ref]");
      for (var i = 0; i < nodes.length; i++) refs[nodes[i].getAttribute("data-ref")] = nodes[i];

      App.applyRoleVisibility(el);
      if (!App.auth.loaded) {
        App.auth.refresh().then(function () { App.applyRoleVisibility(el); });
      }

      loadHealth();
      if (window._healthTimer) clearInterval(window._healthTimer);
      window._healthTimer = setInterval(function () {
        if (!el.isConnected) { clearInterval(window._healthTimer); return; }
        loadHealth();
      }, 30000);
    },
    render: loadHealth
  };
})(window);
