/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 良率页（P3 只读核心同步）
   public/js/page-yield.js
   ----------------
   数据源 /api/stats（yld_daily，心跳携带日统计，P1 D1）：
   - 今日汇总 KPI：总测试 / PASS / FAIL / 良率（全机台）
   - 机台今日明细表：每机台 PASS/FAIL/总数/良率，低良率标红
   - 近 7 天良率趋势折线（canvas 自绘，黑白红）
   良率口径 = PASS/(PASS+FAIL)，与服务端 /api/stats 的 Yield 一致。
   页面自 3s 轮询拉取，数据未变跳过重绘（dataSig 同款思想）。
   ============================================================ */
(function (window) {
  "use strict";
  var App = window.App;

  function todayYmd() {
    var d = new Date();
    var p = function (n) { return n < 10 ? "0" + n : "" + n; };
    return "" + d.getFullYear() + p(d.getMonth() + 1) + p(d.getDate());
  }
  function daysAgoYmd(n) {
    var d = new Date();
    d.setDate(d.getDate() - n);
    var p = function (x) { return x < 10 ? "0" + x : "" + x; };
    return "" + d.getFullYear() + p(d.getMonth() + 1) + p(d.getDate());
  }
  function pct(pass, total) {
    if (!total) return 100.0;
    return Math.round(pass * 10000 / total) / 100;
  }
  function sig(rows) {
    return rows.map(function (r) { return r.Machine + r.TestDate + r.Total + r.Pass; }).join("|");
  }

  var refs = {};
  var last = "";
  var timer = null;

  /* 近 7 天全机台每日良率折线（canvas 自绘，深色主题，低良率段点红） */
  function drawTrend(canvas, daily) {
    var dpr = window.devicePixelRatio || 1;
    var w = canvas.clientWidth || 600;
    var h = canvas.clientHeight || 200;
    canvas.width = w * dpr;
    canvas.height = h * dpr;
    var ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);

    var css = getComputedStyle(canvas);
    var ink = css.getPropertyValue("--ink") || "#E6EAF0";
    var dim = css.getPropertyValue("--dim") || "#9AA5B1";
    var red = css.getPropertyValue("--red") || "#FF5C5C";
    var line = css.getPropertyValue("--line") || "#262D38";

    var padL = 34, padR = 12, padT = 10, padB = 22;
    var pw = w - padL - padR, ph = h - padT - padB;
    var max = 100, min = 0;
    var n = daily.length;
    if (!n) {
      ctx.fillStyle = dim;
      ctx.font = "12px sans-serif";
      ctx.fillText("暂无良率数据（机台升级 v3.9.0 后心跳自动上报）", padL + 10, h / 2);
      return;
    }
    // 网格 + Y 轴（0/50/100%）
    ctx.strokeStyle = line;
    ctx.lineWidth = 1;
    for (var g = 0; g <= 2; g++) {
      var gy = padT + ph - (ph * (g * 50 - min) / (max - min));
      ctx.beginPath(); ctx.moveTo(padL, gy); ctx.lineTo(w - padR, gy); ctx.stroke();
      ctx.fillStyle = dim; ctx.font = "11px sans-serif";
      ctx.fillText((g * 50) + "%", 4, gy + 4);
    }
    // X 轴日期 + 折线
    var step = n > 1 ? pw / (n - 1) : 0;
    var pts = [];
    for (var i = 0; i < n; i++) {
      var v = Math.max(0, Math.min(100, daily[i].yield));
      var x = padL + i * step;
      var y = padT + ph - (ph * (v - min) / (max - min));
      pts.push({ x: x, y: y, v: v, label: daily[i].label, low: daily[i].yield < 90 });
      ctx.fillStyle = dim; ctx.font = "11px sans-serif";
      ctx.textAlign = "center";
      ctx.fillText(daily[i].label, x, h - 6);
    }
    // 连线
    ctx.strokeStyle = ink; ctx.lineWidth = 2;
    ctx.beginPath();
    pts.forEach(function (p, i) { if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y); });
    ctx.stroke();
    // 点：正常墨黑，低良率(<90%)主红
    pts.forEach(function (p) {
      ctx.beginPath();
      ctx.arc(p.x, p.y, 3.5, 0, Math.PI * 2);
      ctx.fillStyle = p.low ? red : ink;
      ctx.fill();
      ctx.fillStyle = p.low ? red : dim;
      ctx.font = "10px sans-serif";
      ctx.fillText(p.v.toFixed(1), p.x, p.y - 8);
    });
    ctx.textAlign = "left";
  }

  function renderBody(rows) {
    var today = todayYmd();
    // 今日行（全机台）
    var todayRows = rows.filter(function (r) { return r.TestDate === today; });
    var tTotal = 0, tPass = 0, tFail = 0;
    todayRows.forEach(function (r) { tTotal += r.Total || 0; tPass += r.Pass || 0; tFail += r.Fail || 0; });
    refs.kTotal.textContent = tTotal;
    refs.kPass.textContent = tPass;
    refs.kFail.textContent = tFail;
    var y = pct(tPass, tTotal);
    refs.kYield.textContent = y.toFixed(2) + "%";
    refs.kYield.style.color = y < 90 ? "var(--red)" : "var(--ink)";

    // 机台今日明细（按良率升序，低良率置顶标红）
    var machines = todayRows.slice().sort(function (a, b) {
      return pct(a.Pass, a.Total) - pct(b.Pass, b.Total);
    });
    var html = "";
    machines.forEach(function (r) {
      var my = pct(r.Pass, r.Total);
      var low = my < 90;
      html += "<tr" + (low ? ' class="fail"' : "") + ">"
        + "<td>" + App.esc(r.Machine) + "</td>"
        + "<td>" + (r.Pass || 0) + "</td>"
        + "<td>" + (r.Fail || 0) + "</td>"
        + "<td>" + (r.Total || 0) + "</td>"
        + "<td" + (low ? ' style="color:var(--red)"' : "") + ">" + my.toFixed(2) + "%</td>"
        + "</tr>";
    });
    refs.tbody.innerHTML = machines.length ? html
      : '<tr><td colspan="5" class="empty">今日暂无良率上报（机台心跳携带日统计，v3.9.0+）</td></tr>';

    // 近 7 天趋势（全机台每日汇总）
    var daily = [];
    for (var i = 6; i >= 0; i--) {
      var ymd = daysAgoYmd(i);
      var dRows = rows.filter(function (r) { return r.TestDate === ymd; });
      var dTotal = 0, dPass = 0;
      dRows.forEach(function (r) { dTotal += r.Total || 0; dPass += r.Pass || 0; });
      var label = ymd.slice(4, 6) + "/" + ymd.slice(6, 8);
      daily.push({ label: label, yield: pct(dPass, dTotal), low: pct(dPass, dTotal) < 90 });
    }
    drawTrend(refs.canvas, daily);
  }

  function render() {
    App.fetchJSON("/api/stats?max=2000").then(function (rows) {
      rows = rows || [];
      var s = sig(rows);
      if (s === last) return;          // 数据未变跳过重绘
      last = s;
      renderBody(rows);
    }).catch(function () { /* 错误已由 fetchJSON toast */ });
  }

  App.Modules["page-yield"] = {
    init: function (el) {
      // hash 联动：?machine= / ?q=
      var initMachine = "";
      try {
        var hq = (window.location.hash.split("?")[1] || "");
        var qp = new URLSearchParams(hq);
        initMachine = qp.get("machine") || qp.get("q") || "";
        if (initMachine) { try { window.localStorage.setItem("agg.machine", initMachine); App.state.sel = initMachine; } catch (e) {} }
        window.addEventListener("argus:drill", function (ev) {
          var d = ev.detail || {};
          if (d.q) { initMachine = d.q; try { window.localStorage.setItem("agg.machine", initMachine); } catch (e) {} App.state.sel = initMachine; render(); }
          if (d.link && d.link.indexOf("#/yield") >= 0) App.Nav.go("yield");
        });
      } catch (e) {}
      el.innerHTML =
        '<div class="page-node">'
        + '<div class="kpis">'
        +   '<div class="kpi" data-drill="fails" style="cursor:pointer" title="下钻到 FAIL 明细"><div class="v" data-ref="kTotal">—</div><div class="t">今日测试总数</div></div>'
        +   '<div class="kpi" data-drill="fails" style="cursor:pointer" title="下钻到 FAIL 明细"><div class="v" data-ref="kPass">—</div><div class="t">今日 PASS</div></div>'
        +   '<div class="kpi" data-drill="fails" style="cursor:pointer" title="下钻到 FAIL 明细"><div class="v" data-ref="kFail" style="color:var(--red)">—</div><div class="t">今日 FAIL</div></div>'
        +   '<div class="kpi" data-drill="fails" style="cursor:pointer" title="下钻到 FAIL 明细"><div class="v" data-ref="kYield">—</div><div class="t">今日良率</div></div>'
        + '</div>'
        + '<div class="card">'
        +   '<h2>机台今日良率 <span class="n">低良率（&lt;90%）标红</span></h2>'
        +   '<div style="overflow-x:auto">'
        +     '<table><thead><tr><th>机台</th><th>PASS</th><th>FAIL</th><th>总数</th><th>良率</th></tr></thead>'
        +     '<tbody data-ref="tbody"></tbody></table>'
        +   '</div>'
        + '</div>'
        + '<div class="card">'
        +   '<h2>近 7 天良率趋势 <span class="n">全机台汇总</span></h2>'
        +   '<canvas data-ref="canvas" style="width:100%;height:200px;display:block"></canvas>'
        + '</div>'
        + '<div class="foot">良率口径 PASS/(PASS+FAIL)，与服务端 /api/stats 一致 · 数据 3 秒自动刷新</div>'
        + "</div>";

      var nodes = el.querySelectorAll("[data-ref]");
      for (var i = 0; i < nodes.length; i++) refs[nodes[i].getAttribute("data-ref")] = nodes[i];
      // KPI 下钻
      el.querySelectorAll(".kpi[data-drill]").forEach(function (kpi) {
        kpi.addEventListener("click", function () { App.Nav.go("fails"); });
      });
      last = "";

      render();
      if (timer) clearInterval(timer);
      timer = setInterval(function () {
        if (!el.isConnected) { clearInterval(timer); timer = null; return; }
        render();
      }, 3000);
    },
    render: render
  };
})(window);
