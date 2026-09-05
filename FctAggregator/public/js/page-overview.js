/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 总览页
   public/js/page-overview.js
   ----------------
   迁移自 public/demo/index.html 的"总览"块：
   KPI（在线 / 离线 / FAIL 总数 / 最近 FAIL）、
   机台状态卡片（可自定义显示 + 排序 + 密度 + 拖拽布局持久化 users.layout）、
   各机台 FAIL 柱状图、点击 KPI 下钻到明细/良率、全局搜索联动。
   接口 URL 原样：/api/machines /api/fails /api/fails/count /api/export.csv
   Lite-Settings: 机台卡拖拽排列保存到 users.layout（localStorage+后端双写），刷新恢复；
   权限显隐 viewer 隐藏导出；hash 联动下钻。
   ============================================================ */
(function (window) {
  "use strict";
  var App = window.App;

  /* 卡片显示配置（localStorage 持久化，键名 agg.card） */
  var cardCfg = (function () {
    var def = { sort: "default", density: "std", showHeartbeat: true, showQueued: true, showRecent: true };
    try {
      var raw = JSON.parse(window.localStorage.getItem("agg.card") || "{}");
      for (var k in def) if (raw[k] === undefined) raw[k] = def[k];
      return raw;
    } catch (e) { return def; }
  })();
  function saveCfg() { try { window.localStorage.setItem("agg.card", JSON.stringify(cardCfg)); } catch (e) { } }

  // 布局：overviewOrder 数组 -> machine 顺序（Lite-Settings）
  function loadOrder() {
    try {
      var lay = App.layout ? App.layout.get() : {};
      if (lay && lay.overviewOrder && Array.isArray(lay.overviewOrder)) return lay.overviewOrder;
    } catch (e) {}
    try { var raw = JSON.parse(window.localStorage.getItem("agg.layout") || "{}"); if (raw.overviewOrder) return raw.overviewOrder; } catch (e) {}
    return null;
  }
  function saveOrder(arr) {
    try {
      var lay = App.layout ? App.layout.get() : {};
      lay.overviewOrder = arr.slice();
      if (App.layout) App.layout.set(lay); else window.localStorage.setItem("agg.layout", JSON.stringify(lay));
    } catch (e) {}
  }

  var refs = {           // 模块内 DOM 引用（init 构建一次，render 只刷新数据）
    machinesBox: null, mcCount: null, chart: null,
    kOnline: null, kOffline: null, kFail: null, kLast: null, kYield: null,
    compareToggle: null, compareBody: null, compareChecks: null, btnCompare: null, compareTrend: null, compareDist: null
  };
  var dragSrc = null;
  var compareSig = "";

  function applyOrder(list, order) {
    if (!order || !order.length) return list;
    var map = {}; for (var i = 0; i < list.length; i++) map[App.pick(list[i], "machine")] = list[i];
    var out = []; var seen = {};
    for (var j = 0; j < order.length; j++) { var m = order[j]; if (map[m] && !seen[m]) { out.push(map[m]); seen[m] = true; } }
    for (var k = 0; k < list.length; k++) { var mm = App.pick(list[k], "machine"); if (!seen[mm]) out.push(list[k]); }
    return out;
  }

  /* 机台卡片渲染（含排序 / 密度 / 自定义字段 + 拖拽 + 布局恢复）+ 二次绑定点击 */
  function renderMachines() {
    var s = App.state;
    var box = refs.machinesBox;
    if (!box) return;
    var scope = s.sel ? s.machines.filter(function (x) { return App.pick(x, "machine") === s.sel; }) : s.machines;
    refs.mcCount.textContent = scope.length + " 台" + (s.sel ? "（范围：" + s.sel + "）" : "（全部机台）");
    box.className = "machines" + (cardCfg.density === "compact" ? " compact" : "") + " draggable";
    if (!scope.length) {
      box.innerHTML = '<div class="empty">暂无机台（单节点部署时仅显示本机）</div>';
      return;
    }
    var list = scope.slice();
    // 若有自定义拖拽排列且 sort=default，则优先用拖拽顺序
    var customOrder = loadOrder();
    if (cardCfg.sort === "default" && customOrder && customOrder.length) {
      list = applyOrder(list, customOrder);
    } else if (cardCfg.sort === "fail") list.sort(function (a, b) { return (App.pick(b, "failCount") || 0) - (App.pick(a, "failCount") || 0); });
    else if (cardCfg.sort === "online") list.sort(function (a, b) { return (App.pick(b, "online") ? 1 : 0) - (App.pick(a, "online") ? 1 : 0); });
    else if (cardCfg.sort === "name") list.sort(function (a, b) { return (App.pick(a, "machine") || "").localeCompare(App.pick(b, "machine") || "", "zh"); });

    var recent = App.data.recentMap();
    var canWrite = !App.auth || !App.auth.isViewer || !App.auth.isViewer();
    // viewer 也可拖拽本地，但不持久到后端（App.layout.set 内部已判）
    var html = "";
    list.forEach(function (x) {
      var name = App.pick(x, "machine") || "—";
      var fc = App.pick(x, "failCount") || 0;
      var ops = '<a class="mop" data-act="report" data-m="' + App.esc(name) + '" title="查看该机台 FAIL 报告">报告</a>'
        + '<a class="mop hide-for-viewer" data-act="csv" data-m="' + App.esc(name) + '" title="导出该机台 FAIL 为 CSV">CSV</a>';
      var extra = [];
      if (cardCfg.showHeartbeat) extra.push('<div class="st">心跳 ' + App.esc(App.pick(x, "lastHeartbeat") || "—") + "</div>");
      if (cardCfg.showQueued) extra.push('<div class="st">待推队列 ' + (App.pick(x, "queued") != null ? App.pick(x, "queued") : 0) + "</div>");
      if (cardCfg.showRecent) extra.push('<div class="st">最近 FAIL ' + (recent[name] ? App.fmtTime(recent[name]) : "—") + "</div>");
      html += App.machineCard(x, { sel: s.sel, ops: ops, extra: extra, draggable: true });
    });
    box.innerHTML = html;
    // 权限显隐应用
    if (App.applyRoleVisibility) App.applyRoleVisibility(box);

    /* 拖拽绑定 */
    var cards = box.querySelectorAll(".machine");
    for (var i = 0; i < cards.length; i++) {
      cards[i].draggable = true;
      cards[i].addEventListener("dragstart", function (e) {
        dragSrc = this;
        this.classList.add("dragging");
        e.dataTransfer.effectAllowed = "move";
        e.dataTransfer.setData("text/plain", this.getAttribute("data-m") || "");
      });
      cards[i].addEventListener("dragend", function () {
        this.classList.remove("dragging");
        box.querySelectorAll(".machine").forEach(function (c) { c.classList.remove("drag-over"); });
        dragSrc = null;
      });
      cards[i].addEventListener("dragover", function (e) {
        e.preventDefault();
        if (this !== dragSrc) this.classList.add("drag-over");
        e.dataTransfer.dropEffect = "move";
      });
      cards[i].addEventListener("dragleave", function () { this.classList.remove("drag-over"); });
      cards[i].addEventListener("drop", function (e) {
        e.preventDefault();
        this.classList.remove("drag-over");
        if (!dragSrc || dragSrc === this) return;
        var srcName = dragSrc.getAttribute("data-m");
        var dstName = this.getAttribute("data-m");
        var names = Array.from(box.querySelectorAll(".machine")).map(function (c) { return c.getAttribute("data-m"); });
        var sIdx = names.indexOf(srcName), dIdx = names.indexOf(dstName);
        if (sIdx < 0 || dIdx < 0) return;
        // 重排 names
        names.splice(sIdx, 1);
        // 插入到 dIdx 前/后根据鼠标位置近似
        var insertBefore = true;
        names.splice(dIdx + (sIdx < dIdx ? 0 : 0), 0, srcName);
        // 简化：直接 splice 到目标前
        // 重新计算：先移除 src，再在 dst 前插入
        // 去重保持顺序
        var seen2 = {}; var ordered = [];
        for (var oi = 0; oi < names.length; oi++) { if (!seen2[names[oi]]) { seen2[names[oi]] = true; ordered.push(names[oi]); } }
        // 若原 names 有重复逻辑，已修正
        // 持久化
        saveOrder(ordered);
        // 切到 default 排序使拖拽生效
        if (cardCfg.sort !== "default") { cardCfg.sort = "default"; try { window.localStorage.setItem("agg.card", JSON.stringify(cardCfg)); } catch (e) {} if (refs.cfgSort) refs.cfgSort.value = "default"; }
        renderMachines();
        if (App.toast) App.toast("布局已保存（拖拽顺序）", "ok");
      });
    }

    /* 卡片点击：报告 -> 跳 FAIL 页；CSV -> 下载；卡片本体 -> 切换机台筛选 */
    for (var ci = 0; ci < cards.length; ci++) {
      cards[ci].addEventListener("click", function (ev) {
        var op = ev.target && ev.target.closest ? ev.target.closest(".mop") : null;
        var m = this.getAttribute("data-m");
        if (op) {
          ev.stopPropagation();
          if (op.getAttribute("data-act") === "csv") {
            if (App.auth && App.auth.isViewer && App.auth.isViewer()) { if (App.toast) App.toast("访客无导出权限", "err"); return; }
            window.open(App.data.exportUrl(m), "_blank");
          } else if (op.getAttribute("data-act") === "report") {
            App.data.filter(m);
            App.Nav.go("fails");
            // 联动：报告页自动带机台过滤
            setTimeout(function () { window.dispatchEvent(new CustomEvent("argus:drill", { detail: { link: "#/fails", q: m } })); }, 200);
          }
          return;
        }
        App.data.filter(m);   // 点击卡片本体的历史行为：直接筛选该机台
      });
    }
  }

  function refreshCompareChecks() {
    if (!refs.compareChecks) return;
    var ms = App.state.machines || [];
    var html = "";
    for (var i = 0; i < ms.length && i < 10; i++) {
      var name = App.pick(ms[i], "machine") || "";
      html += '<label style="margin-right:8px;font-size:12px"><input type="checkbox" value="' + App.esc(name) + '" class="cmp-chk"> ' + App.esc(name) + '</label>';
    }
    if (!ms.length) html = '<span style="color:var(--faint)">暂无机台</span>';
    refs.compareChecks.innerHTML = html;
  }

  function drawCompareTrend(canvas, trendsMap, days) {
    if (!canvas || !canvas.getContext) return;
    var dpr = window.devicePixelRatio || 1;
    var w = canvas.clientWidth || 500, h = 160;
    canvas.width = w * dpr; canvas.height = h * dpr;
    var ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    var css = getComputedStyle(canvas);
    var dim = css.getPropertyValue("--dim") || "#9AA5B1";
    var line = css.getPropertyValue("--line") || "#262D38";
    var colors = ["#FF5C5C","#3DD68C","#FFBE5C","#5B8DEF","#FF8C42","#9AA5B1"];
    var padL = 34, padR = 12, padT = 10, padB = 22;
    var pw = w - padL - padR, ph = h - padT - padB;
    // grid
    ctx.strokeStyle = line; ctx.lineWidth = 1;
    for (var g = 0; g <= 2; g++) { var gy = padT + ph - ph * g * 50 / 100; ctx.beginPath(); ctx.moveTo(padL, gy); ctx.lineTo(w - padR, gy); ctx.stroke(); ctx.fillStyle = dim; ctx.font = "11px sans-serif"; ctx.fillText((g*50)+"%", 4, gy+4); }
    var machines = Object.keys(trendsMap);
    if (!machines.length) { ctx.fillStyle = dim; ctx.fillText("请选择机台后点对比", padL+10, h/2); return; }
    // collect all dates (assume 7 days)
    var first = trendsMap[machines[0]] || [];
    var n = first.length || days || 7;
    var step = n > 1 ? pw / (n - 1) : 0;
    // x labels
    for (var i = 0; i < n; i++) { var label = first[i] ? (first[i].date||"").slice(4,6)+"/"+(first[i].date||"").slice(6,8) : ""; var x = padL + i*step; ctx.fillStyle = dim; ctx.textAlign = "center"; ctx.font="10px sans-serif"; ctx.fillText(label, x, h-6); }
    machines.forEach(function (m, idx) {
      var arr = trendsMap[m] || [];
      var col = colors[idx % colors.length];
      ctx.strokeStyle = col; ctx.lineWidth = 2; ctx.beginPath();
      for (var i = 0; i < arr.length; i++) {
        var yVal = arr[i].yield != null ? arr[i].yield : 100;
        var x = padL + i*step; var y = padT + ph - ph * yVal/100;
        if (i===0) ctx.moveTo(x,y); else ctx.lineTo(x,y);
      }
      ctx.stroke();
      // dots
      for (var j = 0; j < arr.length; j++) {
        var yv = arr[j].yield != null ? arr[j].yield : 100;
        var xx = padL + j*step; var yy = padT + ph - ph * yv/100;
        ctx.beginPath(); ctx.arc(xx,yy,2.5,0,Math.PI*2); ctx.fillStyle = col; ctx.fill();
      }
    });
    // legend
    ctx.textAlign = "left"; var lx = padL; var ly = padT - 2;
    machines.forEach(function (m, idx) {
      var col = colors[idx % colors.length];
      ctx.fillStyle = col; ctx.fillRect(lx, ly-8, 10, 3);
      ctx.fillStyle = dim; ctx.font = "10px sans-serif"; ctx.fillText(m, lx+14, ly);
      lx += ctx.measureText(m).width + 30;
    });
  }

  function renderCompareDist(map, limit) {
    if (!refs.compareDist) return;
    var machines = Object.keys(map);
    if (!machines.length) { refs.compareDist.innerHTML = '<div class="empty">请选择机台</div>'; return; }
    var html = "";
    machines.forEach(function (m) {
      var arr = map[m] || [];
      html += '<div style="margin-bottom:8px"><div style="font-size:12px;font-weight:500">' + App.esc(m) + '</div>';
      if (!arr.length) html += '<div class="empty" style="padding:6px">无数据</div>';
      else {
        html += '<table><thead><tr><th>失败原因</th><th>次数</th></tr></thead><tbody>';
        for (var i = 0; i < Math.min(arr.length, limit||5); i++) {
          html += '<tr><td>' + App.esc(arr[i].label||"") + '</td><td>' + (arr[i].count||0) + '</td></tr>';
        }
        html += '</tbody></table>';
      }
      html += '</div>';
    });
    refs.compareDist.innerHTML = html;
  }

  function doCompare() {
    var checks = refs.compareChecks ? refs.compareChecks.querySelectorAll(".cmp-chk:checked") : [];
    var sel = [];
    for (var i = 0; i < checks.length; i++) sel.push(checks[i].value);
    if (sel.length === 0) { if (App.toast) App.toast("请至少选择一台机台", "err"); return; }
    if (sel.length > 5) { if (App.toast) App.toast("最多对比 5 台", "err"); sel = sel.slice(0,5); }
    var sig = sel.join(",") + "|7";
    if (sig === compareSig && refs.compareTrend) { /* already */ }
    compareSig = sig;
    var machines = sel.join(",");
    Promise.all([
      App.fetchJSON("/api/compare/trends?machines=" + encodeURIComponent(machines) + "&days=7").catch(function(){ return null;}),
      App.fetchJSON("/api/compare/distribution?machines=" + encodeURIComponent(machines) + "&field=fail_reason&limit=5").catch(function(){ return null;})
    ]).then(function (rs) {
      var t = rs[0], d = rs[1];
      var tmap = t ? (t.trends || t.Trends || {}) : {};
      // normalize case: keys may be lower?
      drawCompareTrend(refs.compareTrend, tmap, 7);
      var dmap = d ? (d.distributions || d.Distributions || {}) : {};
      renderCompareDist(dmap, 5);
    }).catch(function(e){ if(App.toast) App.toast("对比失败:"+e.message,"err"); });
  }

  /* 总览 KPI + 卡片 + 柱状图（render 在 init 与数据刷新时调用） */
  function render(ctx) {
    var s = App.state;
    var scope = s.sel ? s.machines.filter(function (x) { return App.pick(x, "machine") === s.sel; }) : s.machines;
    var online = scope.filter(function (x) { return App.pick(x, "online"); }).length;
    var offline = scope.length - online;
    if (refs.kOnline) refs.kOnline.textContent = online;
    if (refs.kOffline) refs.kOffline.textContent = offline;
    if (refs.kFail) refs.kFail.textContent = s.failCount;
    var last = s.fails[0];
    if (refs.kLast) refs.kLast.textContent = last ? App.fmtTime(App.pick(last, "ingestTs") || App.pick(last, "ts")) : "--";
    renderMachines();
    if (refs.chart) App.drawBar(refs.chart, scope);
    refreshCompareChecks();
  }

  /* 模块注册：init 首次进入构建骨架与事件绑定 */
  App.Modules["page-overview"] = {
    init: function (el, ctx) {
      el.innerHTML =
        '<div class="page-node">'
        + '<div class="kpis">'
        +   '<div class="kpi" data-drill="fails" style="cursor:pointer" title="点击下钻到 FAIL 明细"><div class="bar green"></div><div class="v" data-k="online">--</div><div class="k">在线机台</div></div>'
        +   '<div class="kpi" data-drill="fails" style="cursor:pointer" title="点击下钻到 FAIL 明细"><div class="bar"></div><div class="v" data-k="offline">--</div><div class="k">离线机台</div></div>'
        +   '<div class="kpi" data-drill="fails" style="cursor:pointer" title="点击下钻到 FAIL 明细"><div class="bar"></div><div class="v" data-k="fail">--</div><div class="k">FAIL 总数</div></div>'
        +   '<div class="kpi" data-drill="yield" style="cursor:pointer" title="点击下钻到良率"><div class="bar ink"></div><div class="v" data-k="yield">--</div><div class="k">今日良率</div></div>'
        +   '<div class="kpi" data-drill="yield" style="cursor:pointer" title="点击下钻到良率"><div class="bar ink"></div><div class="v" data-k="last">--</div><div class="k">最近 FAIL</div></div>'
        + '</div>'
        + '<div class="grid2">'
        +   '<div class="card">'
        +     '<h2>机台状态 <span class="n" data-ref="mcCount"></span>'
        +       '<span class="h2-ops"><button class="icon-btn" data-ref="btnCfg" title="自定义卡片显示">'
        +         '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" style="width:15px;height:15px"><circle cx="12" cy="12" r="3.2"/><path d="M12 2.8v3M12 18.2v3M2.8 12h3M18.2 12h3M5.5 5.5l2.1 2.1M16.4 16.4l2.1 2.1M18.5 5.5l-2.1 2.1M7.6 16.4l-2.1 2.1"/></svg>'
        +       '</button> <button class="icon-btn" data-ref="btnResetLayout" title="重置拖拽布局">↺</button></span></h2>'
        +     '<div class="cfgpanel" data-ref="cfgPanel" hidden>'
        +       '<div class="cfg-row">'
        +         '<label class="chk"><input type="checkbox" data-ref="cfgHeart"> 心跳</label>'
        +         '<label class="chk"><input type="checkbox" data-ref="cfgQueued"> 队列</label>'
        +         '<label class="chk"><input type="checkbox" data-ref="cfgRecent"> 最近 FAIL</label>'
        +       '</div>'
        +       '<div class="cfg-row">'
        +         '<span>排序</span><select data-ref="cfgSort">'
        +           '<option value="default">拖拽顺序</option><option value="fail">FAIL 数 ↓</option>'
        +           '<option value="online">在线优先</option><option value="name">名称</option>'
        +         '</select>'
        +         '<span>密度</span><select data-ref="cfgDensity">'
        +           '<option value="std">标准</option><option value="compact">紧凑</option>'
        +         '</select>'
        +         '<span class="hint">拖拽卡片可自定义排列，自动保存到 users.layout</span>'
        +       '</div>'
        +     '</div>'
        +     '<div class="machines" data-ref="machinesBox"></div>'
        +   '</div>'
        +   '<div class="card"><h2>各机台 FAIL 数 <span class="n">近 50 条口径</span></h2><canvas data-ref="chart"></canvas></div>'
        + '</div>'
        + '<div class="card" data-ref="compareCard"><h2>多机台对比 <span class="n">基于 /api/trends 与 /api/distribution（无需大改）</span> <label style="font-size:12px;margin-left:10px"><input type="checkbox" data-ref="compareToggle"> 对比态</label></h2>'
        +   '<div data-ref="compareBody" hidden>'
        +     '<div class="toolbar" style="margin-bottom:8px"><span style="font-size:12px;color:var(--dim)">选择机台（最多5台）</span><span data-ref="compareChecks" style="display:flex;flex-wrap:wrap;gap:4px"></span><button class="act" data-ref="btnCompare">对比</button></div>'
        +     '<div class="grid2"><div><h3 style="font-size:13px;margin:8px 0">趋势对比（近7天良率）</h3><canvas data-ref="compareTrend" style="height:160px"></canvas></div><div><h3 style="font-size:13px;margin:8px 0">分布对比（FAIL 原因 Top5）</h3><div data-ref="compareDist"></div></div></div>'
        +   '</div>'
        + '</div>'
        + '<div class="foot">总览数据每 3 秒自动刷新 · 拖拽卡片自动保存布局 · 点击 KPI 下钻到明细 · 对比态基于已有 /api/trends & /api/distribution</div>'
        + "</div>";

      /* 收集 data-ref 引用 */
      var n = el.querySelectorAll("[data-ref]");
      for (var i = 0; i < n.length; i++) {
        refs[n[i].getAttribute("data-ref")] = n[i];
      }
      refs.kOnline = el.querySelector('[data-k="online"]');
      refs.kOffline = el.querySelector('[data-k="offline"]');
      refs.kFail = el.querySelector('[data-k="fail"]');
      refs.kLast = el.querySelector('[data-k="last"]');
      refs.kYield = el.querySelector('[data-k="yield"]');
      /* 今日良率 KPI（P3）：独立轻量拉 /api/stats 今日窗口 */
      (function loadYield() {
        var d = new Date();
        var p = function (n) { return n < 10 ? "0" + n : "" + n; };
        var ymd = "" + d.getFullYear() + p(d.getMonth() + 1) + p(d.getDate());
        App.fetchJSON("/api/stats?from=" + ymd + "&to=" + ymd).then(function (rows) {
          var t = 0, pass = 0;
          (rows || []).forEach(function (r) { t += r.Total || 0; pass += r.Pass || 0; });
          var y = t ? Math.round(pass * 10000 / t) / 100 : 100.0;
          if (refs.kYield) {
            refs.kYield.textContent = y.toFixed(2) + "%";
            refs.kYield.style.color = y < 90 ? "var(--red)" : "";
          }
        }).catch(function () { });
      })();

      /* KPI 下钻联动 */
      el.querySelectorAll(".kpi[data-drill]").forEach(function (kpi) {
        kpi.addEventListener("click", function () {
          var target = kpi.getAttribute("data-drill");
          if (target === "fails") App.Nav.go("fails");
          else if (target === "yield") App.Nav.go("yield");
        });
      });

      /* 配置面板事件 */
      refs.btnCfg.addEventListener("click", function () {
        refs.cfgPanel.hidden = !refs.cfgPanel.hidden;
      });
      if (refs.compareToggle) refs.compareToggle.addEventListener("change", function (e) {
        if (refs.compareBody) refs.compareBody.hidden = !e.target.checked;
      });
      if (refs.btnCompare) refs.btnCompare.addEventListener("click", doCompare);
      if (refs.btnResetLayout) refs.btnResetLayout.addEventListener("click", function () {
        saveOrder([]);
        if (App.toast) App.toast("布局已重置", "ok");
        renderMachines();
      });
      refs.cfgHeart.checked = cardCfg.showHeartbeat;
      refs.cfgQueued.checked = cardCfg.showQueued;
      refs.cfgRecent.checked = cardCfg.showRecent;
      refs.cfgSort.value = cardCfg.sort;
      refs.cfgDensity.value = cardCfg.density;
      var applyCfg = function () { saveCfg(); renderMachines(); };
      refs.cfgHeart.addEventListener("change", function (e) { cardCfg.showHeartbeat = e.target.checked; applyCfg(); });
      refs.cfgQueued.addEventListener("change", function (e) { cardCfg.showQueued = e.target.checked; applyCfg(); });
      refs.cfgRecent.addEventListener("change", function (e) { cardCfg.showRecent = e.target.checked; applyCfg(); });
      refs.cfgSort.addEventListener("change", function (e) { cardCfg.sort = e.target.value; applyCfg(); });
      refs.cfgDensity.addEventListener("change", function (e) { cardCfg.density = e.target.value; applyCfg(); });

      // 初始尝试从后端恢复布局（异步）
      if (App.layout && App.layout.loadFromServer) {
        App.layout.loadFromServer().then(function (obj) { if (obj && obj.overviewOrder) renderMachines(); }).catch(function () {});
      }

      render(ctx);
    },
    render: render
  };
})(window);
