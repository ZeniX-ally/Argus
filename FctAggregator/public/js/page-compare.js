/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 多机台对比（P8 增强）
   public/js/page-compare.js
   ----------------
   独立多机台对比页：趋势（良率）、分布（FAIL）、良率条形图、设备矩阵。
   数据来源：/api/compare/trends /api/compare/distribution /api/devices
   零第三方，canvas 自绘。
   ============================================================ */
(function (window) {
  "use strict";
  var App = window.App;
  var refs = {};
  var days = 7;
  var selected = [];

  function esc(s) { return App.esc ? App.esc(String(s||"")) : String(s||""); }

  /* ---------------- 颜色池 ---------------- */
  var COLORS = ["#FF5C5C","#3DD68C","#FFBE5C","#5B8DEF","#FF8C42","#A78BFA","#22D3EE","#F472B6","#4ADE80","#94A3B8"];

  /* ---------------- 趋势绘制 ---------------- */
  function drawTrend(canvas, trendsMap, days) {
    if (!canvas || !canvas.getContext) return;
    var dpr = window.devicePixelRatio || 1;
    var w = canvas.clientWidth || 400, h = 180;
    canvas.width = w * dpr; canvas.height = h * dpr;
    var ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    var css = getComputedStyle(canvas);
    var dim = css.getPropertyValue("--dim") || "#9AA5B1";
    var line = css.getPropertyValue("--line") || "#262D38";
    var padL = 34, padR = 12, padT = 10, padB = 22;
    var pw = w - padL - padR, ph = h - padT - padB;
    // grid
    ctx.strokeStyle = line; ctx.lineWidth = 1;
    for (var g = 0; g <= 4; g++) { var gy = padT + ph - ph * g * 25 / 100; ctx.beginPath(); ctx.moveTo(padL, gy); ctx.lineTo(w - padR, gy); ctx.stroke(); ctx.fillStyle = dim; ctx.font = "10px sans-serif"; ctx.fillText((g*25)+"%", 4, gy+4); }
    var machines = Object.keys(trendsMap);
    if (!machines.length) { ctx.fillStyle = dim; ctx.fillText("选择机台后点击「对比」", padL + 10, h / 2); return; }
    var first = trendsMap[machines[0]] || [];
    var n = first.length || days || 7;
    var step = n > 1 ? pw / (n - 1) : 0;
    for (var i = 0; i < n; i++) { var label = first[i] ? (first[i].date||"").slice(4,6)+"/"+(first[i].date||"").slice(6,8) : ""; var x = padL + i * step; ctx.fillStyle = dim; ctx.textAlign = "center"; ctx.font = "10px sans-serif"; ctx.fillText(label, x, h - 6); }
    machines.forEach(function (m, idx) {
      var arr = trendsMap[m] || [];
      var col = COLORS[idx % COLORS.length];
      ctx.strokeStyle = col; ctx.lineWidth = 2; ctx.beginPath();
      for (var i = 0; i < arr.length; i++) {
        var yv = arr[i].yield != null ? arr[i].yield : 100;
        var x = padL + i * step; var y = padT + ph - ph * yv/100;
        if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.stroke();
      for (var j = 0; j < arr.length; j++) {
        var yv2 = arr[j].yield != null ? arr[j].yield : 100;
        var xx = padL + j * step; var yy = padT + ph - ph * yv2 / 100;
        ctx.beginPath(); ctx.arc(xx, yy, 2.5, 0, Math.PI * 2); ctx.fillStyle = col; ctx.fill();
      }
    });
    // legend
    ctx.textAlign = "left"; var lx = padL; var ly = padT - 2;
    machines.forEach(function (m, idx) {
      var col = COLORS[idx % COLORS.length];
      ctx.fillStyle = col; ctx.fillRect(lx, ly - 8, 10, 3);
      ctx.fillStyle = dim; ctx.font = "10px sans-serif"; ctx.fillText(m, lx + 14, ly);
      lx += ctx.measureText(m).width + 30;
    });
  }

  /* ---------------- 良率条形图绘制 ---------------- */
  function drawYieldBar(canvas, machinesData) {
    if (!canvas || !canvas.getContext) return;
    var dpr = window.devicePixelRatio || 1;
    var w = canvas.clientWidth || 400, h = machinesData.length ? 32 * machinesData.length + 20 : 60;
    canvas.width = w * dpr; canvas.height = h * dpr;
    var ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    var css = getComputedStyle(canvas);
    var dim = css.getPropertyValue("--dim") || "#9AA5B1";
    var line = css.getPropertyValue("--line") || "#262D38";
    var padL = 60, padR = 50, padT = 8, padB = 8;
    var pw = w - padL - padR;
    if (!machinesData.length) { ctx.fillStyle = dim; ctx.fillText("无数据", padL, h / 2); return; }
    var barH = Math.min(22, (h - padT - padB) / machinesData.length - 4);
    machinesData.forEach(function (d, i) {
      var y = padT + i * (barH + 4);
      var rate = d.yield != null ? d.yield : 0;
      var ok = rate >= 95;
      var col = ok ? "#3DD68C" : rate >= 85 ? "#FFBE5C" : "#FF5C5C";
      var bw = Math.max(2, pw * rate / 100);
      ctx.fillStyle = line; ctx.font = "12px sans-serif"; ctx.textAlign = "right";
      ctx.fillText(d.machine || "", padL - 6, y + barH / 2 + 4);
      ctx.fillStyle = "#1A2030"; ctx.fillRect(padL, y, pw, barH);
      ctx.fillStyle = col; ctx.fillRect(padL, y, bw, barH);
      ctx.fillStyle = "#fff"; ctx.textAlign = "left";
      ctx.fillText(rate.toFixed(1) + "%", padL + bw + 6, y + barH / 2 + 4);
    });
  }

  /* ---------------- 分布渲染 ---------------- */
  function renderDist(container, map, limit) {
    if (!container) return;
    var machines = Object.keys(map);
    if (!machines.length) { container.innerHTML = '<div class="empty">选择机台后点击「对比」</div>'; return; }
    var html = "";
    machines.forEach(function (m) {
      var arr = map[m] || [];
      html += '<div style="margin-bottom:8px"><div style="font-size:13px;font-weight:500;margin-bottom:4px">' + esc(m) + '</div>';
      if (!arr.length) { html += '<div class="empty" style="padding:6px">无 FAIL 数据</div>'; }
      else {
        html += '<table style="width:100%;font-size:12px"><thead><tr><th style="text-align:left">失败原因</th><th style="width:80px;text-align:right">次数</th><th style="width:100px">占比</th></tr></thead><tbody>';
        var total = arr.reduce(function (s, x) { return s + (x.count || 0); }, 0);
        for (var i = 0; i < Math.min(arr.length, limit || 8); i++) {
          var pct = total > 0 ? (arr[i].count / total * 100).toFixed(1) : "0.0";
          html += '<tr><td>' + esc(arr[i].label || "") + '</td><td style="text-align:right">' + (arr[i].count||0) + '</td><td>' + pct + '%</td></tr>';
        }
        html += '</tbody></table>';
      }
      html += '</div>';
    });
    container.innerHTML = html;
  }

  /* ---------------- 设备矩阵渲染 ---------------- */
  function renderDeviceMatrix(container, devices) {
    if (!container) return;
    if (!devices || !devices.length) { container.innerHTML = '<div class="empty">暂无设备数据</div>'; return; }
    var html = '<table><thead><tr><th>机台</th><th>在线</th><th>CPU</th><th>内存</th><th>磁盘</th><th>版本</th><th>最后上报</th></tr></thead><tbody>';
    devices.forEach(function (d) {
      var online = d.online;
      var cpu = d.cpu != null ? d.cpu.toFixed(0) + "%" : "--";
      var mem = d.memory != null ? d.memory.toFixed(0) + "%" : "--";
      var disk = d.disk_free != null ? d.disk_free.toFixed(0) + "GB" : "--";
      var ver = d.version ? d.version : "--";
      var last = d.last_seen ? App.fmtTime(d.last_seen) : "--";
      var dotColor = online ? "var(--ok)" : "var(--err)";
      html += '<tr>'
        + '<td><span class="dot" style="background:' + dotColor + ';width:8px;height:8px;display:inline-block;border-radius:4px;margin-right:6px"></span>' + esc(d.machine || d.hostname || "") + '</td>'
        + '<td>' + (online ? "在线" : "<span style='color:var(--err)'>离线</span>") + '</td>'
        + '<td>' + esc(cpu) + '</td><td>' + esc(mem) + '</td><td>' + esc(disk) + '</td>'
        + '<td>' + esc(ver) + '</td><td>' + esc(last) + '</td></tr>';
    });
    html += '</tbody></table>';
    container.innerHTML = html;
  }

  /* ---------------- 机台选择器渲染 ---------------- */
  function renderChecks(container, machines) {
    if (!container) return;
    var html = "";
    for (var i = 0; i < machines.length && i < 10; i++) {
      var name = machines[i].machine || machines[i].hostname || "";
      var checked = selected.indexOf(name) >= 0 ? " checked" : "";
      html += '<label style="margin-right:8px;font-size:13px;cursor:pointer"><input type="checkbox" class="cmp-chk" value="' + esc(name) + '"' + checked + '> ' + esc(name) + '</label>';
    }
    if (!machines.length) html = '<span style="color:var(--faint)">暂无机台</span>';
    container.innerHTML = html;
    container.querySelectorAll(".cmp-chk").forEach(function (cb) {
      cb.addEventListener("change", function () {
        var v = cb.value;
        if (cb.checked) { if (selected.length < 6) selected.push(v); else { cb.checked = false; App.toast && App.toast("最多对比 6 台", "err"); } }
        else { selected = selected.filter(function (x) { return x !== v; }); }
      });
    });
  }

  /* ---------------- 执行对比 ---------------- */
  function doCompare() {
    if (selected.length === 0) { App.toast && App.toast("请至少选择一台机台", "err"); return; }
    var machines = selected.join(",");
    App.toast && App.toast("对比加载中…", "info");
    Promise.all([
      App.fetchJSON("/api/compare/trends?machines=" + encodeURIComponent(machines) + "&days=" + days).catch(function () { return null; }),
      App.fetchJSON("/api/compare/distribution?machines=" + encodeURIComponent(machines) + "&field=fail_reason&limit=8").catch(function () { return null; }),
      App.fetchJSON("/api/devices").catch(function () { return { devices: [] }; })
    ]).then(function (rs) {
      var t = rs[0], d = rs[1], devs = rs[2];
      var tmap = t ? (t.trends || t.Trends || {}) : {};
      var dmap = d ? (d.distributions || d.Distributions || {}) : {};
      var devList = devs ? (devs.devices || devs.Devices || []) : [];
      if (refs.trendCanvas) drawTrend(refs.trendCanvas, tmap, days);
      if (refs.yieldCanvas) {
        var yData = selected.map(function (m) { return { machine: m, yield: extractLatestYield(tmap, m) }; });
        drawYieldBar(refs.yieldCanvas, yData);
      }
      if (refs.distBox) renderDist(refs.distBox, dmap, 8);
      if (refs.deviceBox) renderDeviceMatrix(refs.deviceBox, devList);
      App.toast && App.toast("对比完成", "ok");
    }).catch(function (e) { App.toast && App.toast("对比失败：" + (e && e.message ? e.message : e), "err"); });
  }

  function extractLatestYield(trendsMap, machine) {
    var arr = trendsMap[machine];
    if (!arr || !arr.length) return null;
    return arr[arr.length - 1].yield;
  }

  /* ---------------- 模块入口 ---------------- */
  App.Modules["page-compare"] = {
    init: function (el, ctx) {
      el.innerHTML =
        '<div class="page-node">'
        + '<h2 style="margin-bottom:12px">多机台对比</h2>'
        + '<div class="card" style="margin-bottom:12px">'
        +   '<div class="toolbar">'
        +     '<span style="font-size:13px;color:var(--dim)">选择机台（最多6台）</span>'
        +     '<span data-ref="checks" style="display:flex;flex-wrap:wrap;gap:6px"></span>'
        +     '<select data-ref="daysSel" style="height:32px">'
        +       '<option value="3">近3天</option><option value="7" selected>近7天</option><option value="14">近14天</option><option value="30">近30天</option>'
        +     '</select>'
        +     '<button class="act" data-ref="btnCompare">对比</button>'
        +     '<button class="act" data-ref="btnFullscreen" style="background:var(--bg2);color:var(--ink);border-color:var(--line)">全屏趋势</button>'
        +   '</div>'
        + '</div>'
        + '<div class="grid2" style="grid-template-columns:1fr 1fr;margin-bottom:12px">'
        +   '<div class="card"><h3 style="font-size:13px;margin:0 0 8px">良率趋势对比（近7天）</h3><canvas data-ref="trendCanvas" style="height:200px;width:100%"></canvas></div>'
        +   '<div class="card"><h3 style="font-size:13px;margin:0 0 8px">良率快照（最近1天）</h3><canvas data-ref="yieldCanvas" style="height:200px;width:100%"></canvas></div>'
        + '</div>'
        + '<div class="grid2" style="grid-template-columns:1fr 1fr">'
        +   '<div class="card"><h3 style="font-size:13px;margin:0 0 8px">FAIL 原因分布 Top8</h3><div data-ref="distBox"></div></div>'
        +   '<div class="card"><h3 style="font-size:13px;margin:0 0 8px">设备状态矩阵</h3><div data-ref="deviceBox" style="overflow:auto"></div></div>'
        + '</div>'
        + '</div>';
      var nodes = el.querySelectorAll("[data-ref]");
      for (var i = 0; i < nodes.length; i++) refs[nodes[i].getAttribute("data-ref")] = nodes[i];
      // load machines
      App.fetchJSON("/api/machines").then(function (r) {
        var ms = App.state.machines || r || [];
        var names = ms.map(function (x) { return x.machine || x.hostname || ""; }).filter(Boolean);
        selected = names.slice(0, 3);
        renderChecks(refs.checks, names);
      }).catch(function () { refs.checks.innerHTML = '<span style="color:var(--err)">机台列表加载失败</span>'; });
      if (refs.btnCompare) refs.btnCompare.addEventListener("click", doCompare);
      if (refs.daysSel) refs.daysSel.addEventListener("change", function () { days = parseInt(refs.daysSel.value) || 7; doCompare(); });
      if (refs.btnFullscreen) refs.btnFullscreen.addEventListener("click", function () {
        toggleFullscreen(refs.trendCanvas);
      });
      // auto contrast
      setTimeout(doCompare, 300);
    },
    render: function () { }
  };

  function toggleFullscreen(canvas) {
    if (!document.fullscreenElement) {
      var wrap = canvas.parentElement;
      if (wrap && wrap.requestFullscreen) wrap.requestFullscreen();
    } else {
      if (document.exitFullscreen) document.exitFullscreen();
    }
  }

})(window);
