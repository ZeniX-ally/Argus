/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 季节性分解页（v3.21.0 规格 03）
   public/js/page-yield-seasonality.js
   ----------------
   数据源：
   - GET /api/machines                    机台下拉（PeerStatusDto.Machine）
   - GET /api/yield/decompose?machine=&mode=&days=   分解结果
   - GET /api/yield/decompose/config      开关/模式状态（viewer 可读）
   - POST /api/yield/decompose/config     admin 热改开关/模式
   图表：canvas 自绘（黑白红，令牌运行时读），趋势 vs 观测、残差 σ 带（异常红点）、周期分量条。
   ============================================================ */
(function (window) {
  "use strict";
  var App = window.App;

  var refs = {};
  var state = { machine: "", mode: "hourly", days: 28 };

  /* ---------- 图表绘制 ---------- */

  /* 趋势 vs 观测折线（主图） */
  function drawTrend(canvas, trend, observed) {
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
    var amber = css.getPropertyValue("--amber") || "#FFBE5C";
    var line = css.getPropertyValue("--line") || "#262D38";
    var padL = 34, padR = 12, padT = 10, padB = 22;
    var pw = w - padL - padR, ph = h - padT - padB;
    var n = trend.length;
    if (!n) {
      ctx.fillStyle = dim; ctx.font = "12px sans-serif";
      ctx.fillText("暂无分解数据（该机台近期无良率记录）", padL + 10, h / 2);
      return;
    }
    ctx.strokeStyle = line; ctx.lineWidth = 1;
    for (var g = 0; g <= 2; g++) {
      var gy = padT + ph - ph * g / 2;
      ctx.beginPath(); ctx.moveTo(padL, gy); ctx.lineTo(w - padR, gy); ctx.stroke();
      ctx.fillStyle = dim; ctx.font = "11px sans-serif";
      ctx.fillText((100 - g * 50) + "%", 4, gy + 4);
    }
    var step = pw / (n - 1);
    function poly(pts, color, width, dash) {
      ctx.strokeStyle = color; ctx.lineWidth = width;
      ctx.setLineDash(dash || []);
      ctx.beginPath();
      pts.forEach(function (p, i) { if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y); });
      ctx.stroke();
      ctx.setLineDash([]);
    }
    function toPts(arr) {
      var pts = [];
      for (var i = 0; i < arr.length; i++) {
        var v = arr[i];
        var x = padL + i * step;
        var y = padT + ph - (ph * Math.max(0, Math.min(100, v)) / 100);
        pts.push({ x: x, y: y, v: v, nan: v === null || v === undefined || isNaN(v) });
      }
      return pts;
    }
    var tp = toPts(trend);
    poly(tp, ink, 2, null);
    var op = toPts(observed);
    poly(op, amber, 1.5, [4, 3]);
    // X 轴标签（每 7 天一个）
    for (var i = 0; i < n; i += 7) {
      ctx.fillStyle = dim; ctx.font = "10px sans-serif";
      ctx.textAlign = "center";
      var d = new Date(); d.setDate(d.getDate() - (n - 1 - i));
      ctx.fillText((d.getMonth() + 1) + "/" + d.getDate(), padL + i * step, h - 6);
    }
    ctx.textAlign = "left";
    ctx.fillStyle = ink; ctx.font = "11px sans-serif";
    ctx.fillText("— 趋势", padL + 4, 14);
    ctx.fillStyle = amber; ctx.fillText("— 观测(趋势+残差)", padL + 60, 14);
  }

  /* 残差 σ 带：中线 + ±ε·σ 参考线 + 异常红点 */
  function drawResidual(canvas, residual, sigma, eps) {
    var dpr = window.devicePixelRatio || 1;
    var w = canvas.clientWidth || 600;
    var h = canvas.clientHeight || 140;
    canvas.width = w * dpr;
    canvas.height = h * dpr;
    var ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    var css = getComputedStyle(canvas);
    var ink = css.getPropertyValue("--ink") || "#E6EAF0";
    var dim = css.getPropertyValue("--dim") || "#9AA5B1";
    var red = css.getPropertyValue("--red") || "#FF5C5C";
    var green = css.getPropertyValue("--green") || "#3DD68C";
    var line = css.getPropertyValue("--line") || "#262D38";
    var padL = 34, padR = 12, padT = 10, padB = 18;
    var pw = w - padL - padR, ph = h - padT - padB;
    var n = residual.length;
    if (!n) {
      ctx.fillStyle = dim; ctx.font = "12px sans-serif";
      ctx.fillText("暂无残差数据", padL + 10, h / 2);
      return;
    }
    // Y 范围：±3σ（含 ε 线）
    var half = Math.max(Math.abs(sigma * 3), 1.0);
    var yOf = function (v) { return padT + ph / 2 - (ph / 2) * (v / half); };
    // 零线 + ε 线
    ctx.strokeStyle = line; ctx.lineWidth = 1;
    [0, -eps * sigma, eps * sigma].forEach(function (lv) {
      ctx.beginPath(); ctx.moveTo(padL, yOf(lv)); ctx.lineTo(w - padR, yOf(lv)); ctx.stroke();
    });
    ctx.fillStyle = dim; ctx.font = "10px sans-serif";
    ctx.fillText("+" + eps.toFixed(1) + "σ", w - padR - 30, yOf(eps * sigma) - 3);
    ctx.fillText("-" + eps.toFixed(1) + "σ", w - padR - 30, yOf(-eps * sigma) + 11);
    var step = n > 1 ? pw / (n - 1) : 0;
    // 残差折线
    ctx.strokeStyle = ink; ctx.lineWidth = 1.5;
    ctx.beginPath();
    var drew = false;
    for (var i = 0; i < n; i++) {
      if (isNaN(residual[i])) continue;
      var x = padL + i * step;
      var y = yOf(residual[i]);
      if (!drew) { ctx.moveTo(x, y); drew = true; } else ctx.lineTo(x, y);
    }
    ctx.stroke();
    // 点：正常绿、超 ε 红
    for (var j = 0; j < n; j++) {
      if (isNaN(residual[j])) continue;
      var x2 = padL + j * step;
      var y2 = yOf(residual[j]);
      var isAnom = Math.abs(residual[j]) > eps * sigma;
      ctx.beginPath();
      ctx.arc(x2, y2, isAnom ? 5 : 2.5, 0, Math.PI * 2);
      ctx.fillStyle = isAnom ? red : green;
      ctx.fill();
      if (isAnom) {
        ctx.fillStyle = red; ctx.font = "10px sans-serif";
        ctx.fillText(residual[j].toFixed(1), x2, y2 - 8);
      }
    }
  }

  /* 周期分量条（24 或 7 桶） */
  function drawSeasonal(canvas, seasonal, labels) {
    var dpr = window.devicePixelRatio || 1;
    var w = canvas.clientWidth || 600;
    var h = canvas.clientHeight || 90;
    canvas.width = w * dpr;
    canvas.height = h * dpr;
    var ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    var css = getComputedStyle(canvas);
    var ink = css.getPropertyValue("--ink") || "#E6EAF0";
    var dim = css.getPropertyValue("--dim") || "#9AA5B1";
    var red = css.getPropertyValue("--red") || "#FF5C5C";
    var green = css.getPropertyValue("--green") || "#3DD68C";
    var line = css.getPropertyValue("--line") || "#262D38";
    var padL = 8, padR = 8, padT = 8, padB = 16;
    var pw = w - padL - padR, ph = h - padT - padB;
    var n = seasonal.length;
    if (!n) return;
    var maxA = 1;
    seasonal.forEach(function (v) { if (!isNaN(v)) maxA = Math.max(maxA, Math.abs(v)); });
    // 零线
    ctx.strokeStyle = line; ctx.lineWidth = 1;
    ctx.beginPath(); ctx.moveTo(padL, padT + ph / 2); ctx.lineTo(w - padR, padT + ph / 2); ctx.stroke();
    var bw = pw / n;
    for (var i = 0; i < n; i++) {
      var v = seasonal[i];
      var bh = (ph / 2) * Math.abs(v) / maxA;
      var x = padL + i * bw;
      var y0 = padT + ph / 2;
      ctx.fillStyle = v >= 0 ? green : red;
      ctx.fillRect(x + 1, v >= 0 ? y0 - bh : y0, bw - 2, Math.max(bh, 0.5));
      if (n <= 24) {
        ctx.fillStyle = dim; ctx.font = "9px sans-serif";
        ctx.textAlign = "center";
        ctx.fillText(labels[i], x + bw / 2, h - 4);
      }
    }
    ctx.textAlign = "left";
    ctx.fillStyle = ink; ctx.font = "11px sans-serif";
  }

  /* ---------- 数据加载 ---------- */

  function loadConfig() {
    return App.fetchJSON("/api/yield/decompose/config").then(function (c) {
      if (c && typeof c.enabled === "boolean") {
        state.enabled = c.enabled;
        state.mode = c.mode || "hourly";
        if (refs.enabled) refs.enabled.checked = c.enabled;
        if (refs.cfgMode) refs.cfgMode.value = state.mode;
        if (refs.cfgState) {
          refs.cfgState.textContent = c.enabled ? "已启用（模式 " + c.mode + "）" : "已关闭（行为 = v3.19.0 老逻辑）";
        }
      }
    }).catch(function () {});
  }

  function loadMachines() {
    return App.fetchJSON("/api/machines").then(function (list) {
      list = list || [];
      var cur = state.machine || refs.machine.value;
      var html = '<option value="">选择机台…</option>';
      list.forEach(function (m) {
        if (!m || !m.Machine) return;
        html += '<option value="' + App.esc(m.Machine) + '"' + (m.Machine === cur ? " selected" : "") + ">" + App.esc(m.Machine) + "</option>";
      });
      refs.machine.innerHTML = html;
      if (cur && refs.machine.value !== cur) refs.machine.value = cur;
    }).catch(function () {});
  }

  function loadDecompose() {
    var m = refs.machine.value;
    if (!m) {
      refs.summary.textContent = "请选择机台";
      return;
    }
    var url = "/api/yield/decompose?machine=" + encodeURIComponent(m)
      + "&mode=" + encodeURIComponent(refs.mode.value)
      + "&days=" + encodeURIComponent(refs.days.value);
    App.fetchJSON(url).then(function (d) {
      if (!d) return;
      var trend = d.Trend || [];
      var residual = d.Residual || [];
      var observed = residual.map(function (r, i) { return isNaN(r) || isNaN(trend[i]) ? NaN : trend[i] + r; });
      drawTrend(refs.canvas, trend, observed);
      drawResidual(refs.residualCanvas, residual, d.Sigma || 0, d.Epsilon || 1.5);
      var labels = [];
      var n = (d.Seasonal || []).length;
      if (n === 7) labels = ["日", "一", "二", "三", "四", "五", "六"];
      else for (var i = 0; i < n; i++) labels.push(i + "时");
      drawSeasonal(refs.seasonalCanvas, d.Seasonal || [], labels);
      var anoms = d.Anomalies || [];
      var warn = anoms.filter(function (a) { return a.Severity !== "critical"; }).length;
      var crit = anoms.length - warn;
      refs.summary.textContent = "机台 " + d.Machine + " · 模式 " + d.Mode + " · 窗口 " + d.DaysBack
        + " 天 · 整体均值 " + (d.OverallMean || 0).toFixed(2) + "% · 残差 σ " + (d.Sigma || 0).toFixed(2)
        + " · 异常 " + anoms.length + " 天（warn " + warn + " / critical " + crit + "）";
      var html = "";
      anoms.forEach(function (a) {
        html += "<tr" + (a.Severity === "critical" ? ' class="fail"' : "") + ">"
          + "<td>" + App.esc(a.Date ? String(a.Date).slice(0, 10) : "") + "</td>"
          + "<td>" + (a.Value || 0).toFixed(2) + "%</td>"
          + "<td>" + (a.Residual || 0).toFixed(2) + "</td>"
          + "<td>" + (a.ZScore || 0).toFixed(2) + "σ</td>"
          + "<td>" + App.esc(a.Severity) + "</td>"
          + "</tr>";
      });
      refs.anomTbody.innerHTML = html || '<tr><td colspan="5" class="empty">窗口内无显著异常（残差 |z| ≤ ε）</td></tr>';
    }).catch(function () {});
  }

  function saveConfig(patch) {
    App.postJSON("/api/yield/decompose/config", patch).then(function () {
      App.toast("季节性配置已保存并生效");
      loadConfig();
    }).catch(function () {});
  }

  function render() {
    loadConfig();
    loadMachines();
    loadDecompose();
  }

  App.Modules["page-yield-seasonality"] = {
    init: function (el) {
      el.innerHTML =
        '<div class="page-node">'
        + '<div class="toolbar" style="display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin-bottom:10px">'
        +   '<select data-ref="machine" style="min-width:150px"><option value="">选择机台…</option></select>'
        +   '<select data-ref="mode"><option value="hourly">hourly（日内 24 时段）</option><option value="daily">daily（24 时段+7日趋势）</option><option value="weekly">weekly（星期几 7 时段）</option></select>'
        +   '<select data-ref="days"><option value="14">近 14 天</option><option value="28" selected>近 28 天</option><option value="60">近 60 天</option></select>'
        +   '<button class="btn" data-ref="go">分析</button>'
        + '</div>'
        + '<div class="card" data-require-role="admin">'
        +   '<h2>配置 <span class="n">admin 专属，热生效免重启</span></h2>'
        +   '<label style="display:inline-flex;align-items:center;gap:6px;margin-right:16px">'
        +     '<input type="checkbox" data-ref="enabled"> 启用季节性分解（残差告警叠加到 /api/alerts/predict）'
        +   '</label>'
        +   '<label style="display:inline-flex;align-items:center;gap:6px">模式 '
        +     '<select data-ref="cfgMode"><option value="hourly">hourly</option><option value="daily">daily</option><option value="weekly">weekly</option></select>'
        +   '</label>'
        + '</div>'
        + '<div class="card">'
        +   '<h2>趋势 vs 观测 <span class="n" data-ref="summary">…</span></h2>'
        +   '<canvas data-ref="canvas" style="width:100%;height:200px;display:block"></canvas>'
        + '</div>'
        + '<div class="card">'
        +   '<h2>残差 σ 带 <span class="n">红点 = 超 ε·σ 的显著异常日</span></h2>'
        +   '<canvas data-ref="residualCanvas" style="width:100%;height:140px;display:block"></canvas>'
        + '</div>'
        + '<div class="card">'
        +   '<h2>周期分量 <span class="n">桶均值 − 整体均值</span></h2>'
        +   '<canvas data-ref="seasonalCanvas" style="width:100%;height:90px;display:block"></canvas>'
        + '</div>'
        + '<div class="card">'
        +   '<h2>异常日明细 <span class="n">|z| ≥ 2ε 判 critical</span></h2>'
        +   '<div style="overflow-x:auto"><table><thead><tr><th>日期</th><th>当日良率</th><th>残差</th><th>z 值</th><th>级别</th></tr></thead>'
        +   '<tbody data-ref="anomTbody"></tbody></table></div>'
        + '</div>'
        + '<div class="foot">季节性分解默认关闭（yield_seasonality_enabled=false），开启后残差太小自动回退老逻辑 · 30 秒自动刷新</div>'
        + "</div>";

      var nodes = el.querySelectorAll("[data-ref]");
      for (var i = 0; i < nodes.length; i++) refs[nodes[i].getAttribute("data-ref")] = nodes[i];
      refs.go.addEventListener("click", loadDecompose);
      refs.mode.addEventListener("change", loadDecompose);
      refs.days.addEventListener("change", loadDecompose);
      refs.enabled.addEventListener("change", function () { saveConfig({ enabled: refs.enabled.checked }); });
      refs.cfgMode.addEventListener("change", function () { saveConfig({ mode: refs.cfgMode.value }); });
      refs.machine.addEventListener("change", loadDecompose);

      // 角色显隐（admin 区），未加载完则刷新后再应用
      App.applyRoleVisibility(el);
      if (!App.auth.loaded) {
        App.auth.refresh().then(function () { App.applyRoleVisibility(el); });
      }

      render();
      if (window._seasonTimer) clearInterval(window._seasonTimer);
      window._seasonTimer = setInterval(function () {
        if (!el.isConnected) { clearInterval(window._seasonTimer); return; }
        render();
      }, 30000);
    },
    render: render
  };
})(window);
