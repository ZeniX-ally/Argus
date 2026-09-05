/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 设备页（P6，Agent C）
   public/js/page-devices.js
   ----------------
   展示：设备总览卡片网格（状态灯 + CPU/内存/磁盘迷你条）+ 单设备详情（L1 全量 + FCT.ini + 3s 自动刷新）+ 历史趋势（CPU/内存/磁盘折线，canvas 自绘）。
   告警：磁盘 <10GB / CPU >90% / 离线 >5min 进飞书（服务端 AggAlertService），前端仅高亮标红 + tooltip。
   硬件约束：≤30 台、前端 10 浏览器 × 3s 轮询 = 3.3 req/s（5s 缓存后命中内存）。
   数据源：GET /api/devices（5s 缓存，总览）、GET /api/devices/{machine}（详情）、GET /api/devices/{machine}/samples?limit=200（趋势）。
   零第三方依赖，离线可用（无 CDN），尊重 prefers-reduced-motion，变更检测 dataSig。
   ============================================================ */
(function (window) {
  "use strict";
  var App = window.App;

  var refs = {};
  var lastSig = "";
  var timer = null;
  var selected = ""; // 当前选中机台
  var devicesCache = []; // 最近一次 /api/devices 结果

  function sig(list) {
    return (list || []).map(function (d) { return d.machine + "|" + d.last_seen + "|" + d.cpu_usage + "|" + d.disk_free_gb; }).join(";");
  }

  function fmtUptime(sec) {
    sec = sec || 0;
    var d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60);
    if (d > 0) return d + "天" + h + "时";
    if (h > 0) return h + "时" + m + "分";
    return m + "分";
  }

  function deviceStatus(d) {
    // 在线判定：服务端已算 online 字段；未采集（hostname 空且 cpu 0）视为 unknown
    if (!d.hostname && !d.cpu_model && !d.ip) return "unknown";
    return d.online ? "online" : "offline";
  }

  function statusDot(d) {
    var st = deviceStatus(d);
    if (st === "unknown") return '<span class="dot" style="background:var(--faint)" title="未采集"></span>';
    if (st === "online") return '<span class="dot ok" title="在线"></span>';
    return '<span class="dot err" title="离线"></span>';
  }

  function miniBar(label, used, total, unit, warn) {
    if (!total) return '<div class="st">' + App.esc(label) + ' —</div>';
    var pct = Math.round(used / total * 100);
    var w = Math.min(100, Math.max(2, pct));
    var col = warn ? "var(--red)" : "var(--ink)";
    return '<div class="dev-bar"><span class="k">' + App.esc(label) + '</span><div class="track"><div class="fill" style="width:' + w + '%;background:' + col + '"></div></div><span class="v">' + used + '/' + total + unit + '</span></div>';
  }

  function renderGrid(list) {
    if (!refs.grid) return;
    if (!list || !list.length) {
      refs.grid.innerHTML = '<div class="empty">暂无设备（机台升级 v3.14.0 后 5 分钟内自动上报）</div>';
      return;
    }
    // 排序：在线优先，其次名称
    var sorted = list.slice().sort(function (a, b) {
      var sa = deviceStatus(a), sb = deviceStatus(b);
      if (sa !== sb) {
        if (sa === "online") return -1;
        if (sb === "online") return 1;
        if (sa === "unknown") return 1;
        if (sb === "unknown") return -1;
      }
      return (a.machine || "").localeCompare(b.machine || "", "zh");
    });
    var html = "";
    for (var i = 0; i < sorted.length; i++) {
      var d = sorted[i];
      var st = deviceStatus(d);
      var cur = selected === d.machine ? " cur" : "";
      var offlineWarn = st === "offline" ? "offline" : "";
      var diskWarn = d.disk_free_gb > 0 && d.disk_free_gb < 10;
      var cpuWarn = d.cpu_usage >= 90;
      var warnCls = (diskWarn || cpuWarn) ? " warn" : "";
      html += '<div class="device-card' + cur + " " + offlineWarn + warnCls + '" data-m="' + App.esc(d.machine) + '">'
        + '<div class="row1">' + statusDot(d) + '<span class="name">' + App.esc(d.machine) + '</span>'
        + (d.hostname ? '<span class="host">' + App.esc(d.hostname) + '</span>' : '') + '</div>'
        + '<div class="st">IP ' + App.esc(d.ip || "—") + ' · ' + App.esc(d.argus_version || "") + '</div>'
        + '<div class="fc ' + (cpuWarn ? "hot" : "") + '">' + (d.cpu_usage != null ? d.cpu_usage.toFixed(1) + "%" : "—") + '</div><div class="st">CPU ' + (d.cpu_model ? App.esc(d.cpu_model).slice(0, 24) : "—") + '</div>'
        + miniBar("内存", d.mem_used_mb, d.mem_total_mb, "MB", false)
        + miniBar("磁盘剩余", Math.round(d.disk_free_gb || 0), Math.round(d.disk_total_gb || 0), "GB", diskWarn)
        + '<div class="st">在线 ' + (d.last_seen ? App.fmtTime(d.last_seen) : "—") + ' · 运行 ' + fmtUptime(d.uptime_sec) + '</div>'
        + (st === "unknown" ? '<div class="hint-sm">未采集（等待心跳/全量上报）</div>' : "")
        + (diskWarn ? '<div class="alert-sm">磁盘 <10GB</div>' : "")
        + (cpuWarn ? '<div class="alert-sm">CPU ≥90%</div>' : "")
        + "</div>";
    }
    refs.grid.innerHTML = html;
    // 点击卡片打开详情
    var cards = refs.grid.querySelectorAll(".device-card");
    for (var j = 0; j < cards.length; j++) {
      cards[j].addEventListener("click", function () {
        var m = this.getAttribute("data-m");
        selected = m;
        openDetail(m);
        renderGrid(devicesCache);
      });
    }
  }

  function openDetail(machine) {
    if (!refs.detail) return;
    refs.detail.hidden = false;
    refs.detail.innerHTML = '<div class="view-loading">加载 ' + App.esc(machine) + ' 详情…</div>';
    // 并行拉详情 + 采样
    Promise.all([
      App.fetchJSON("/api/devices/" + encodeURIComponent(machine)),
      App.fetchJSON("/api/devices/" + encodeURIComponent(machine) + "/samples?limit=200")
    ]).then(function (rs) {
      var detail = rs[0];
      var samples = rs[1] || [];
      renderDetail(detail, samples);
    }).catch(function (e) {
      refs.detail.innerHTML = '<div class="hint">加载失败：' + App.esc(e.message) + '</div>';
    });
  }

  function renderDetail(detail, samples) {
    var info = detail.info || detail;
    var fct = detail.fct;
    var html = '<div class="card">'
      + '<h2>' + App.esc(info.Machine || info.machine || selected) + ' <span class="n">' + (info.Online || info.online ? "在线" : "离线") + ' · ' + App.esc(info.Hostname || info.hostname || "") + '</span>'
      + '<span class="h2-ops"><button class="icon-btn" id="btnCloseDetail" title="关闭">✕</button></span></h2>'
      + '<div class="dev-detail-grid">'
      + '<div><span class="k">主机</span><span class="v">' + App.esc(info.Hostname || info.hostname || "—") + '</span></div>'
      + '<div><span class="k">系统</span><span class="v">' + App.esc((info.Os || info.os || "") + " " + (info.OsVersion || info.os_version || "")) + '</span></div>'
      + '<div><span class="k">IP/MAC</span><span class="v">' + App.esc(info.Ip || info.ip || "—") + " / " + App.esc(info.Mac || info.mac || "—") + '</span></div>'
      + '<div><span class="k">CPU</span><span class="v">' + App.esc(info.CpuModel || info.cpu_model || "—") + ' (' + (info.CpuCores || info.cpu_cores || 0) + ' 核) ' + (info.CpuUsage != null ? info.CpuUsage.toFixed(1) + "%" : "") + '</span></div>'
      + '<div><span class="k">内存</span><span class="v">' + (info.MemUsedMb || info.mem_used_mb || 0) + " / " + (info.MemTotalMb || info.mem_total_mb || 0) + " MB" + '</span></div>'
      + '<div><span class="k">磁盘</span><span class="v">' + (info.DiskFreeGb != null ? info.DiskFreeGb.toFixed(1) : "—") + " / " + (info.DiskTotalGb != null ? info.DiskTotalGb.toFixed(1) : "—") + " GB 剩余" + '</span></div>'
      + '<div><span class="k">版本</span><span class="v">' + App.esc(info.ArgusVersion || info.argus_version || "—") + '</span></div>'
      + '<div><span class="k">运行</span><span class="v">' + fmtUptime(info.UptimeSec || info.uptime_sec) + '</span></div>'
      + '<div><span class="k">上报</span><span class="v">' + App.esc(info.LastSeen || info.last_seen || "—") + '</span></div>'
      + '</div></div>';

    // FCT.ini
    if (fct) {
      html += '<div class="card"><h2>FCT.ini <span class="n">' + App.esc(fct.IniPath || fct.ini_path || "") + ' ' + (fct.Found || fct.found ? "✓" : "✗") + '</span></h2>';
      if (fct.Error || fct.error) html += '<div class="hint">错误：' + App.esc(fct.Error || fct.error) + '</div>';
      var models = fct.Models || fct.models || [];
      if (models.length) html += '<div class="st">型号：' + models.map(function (x) { return App.esc(x); }).join(" / ") + '</div>';
      var fws = fct.FwVersions || fct.fw_versions || [];
      if (fws.length) html += '<div class="st">固件：' + fws.map(function (x) { return App.esc(x.Label || x.label) + "=" + App.esc(x.Version || x.version); }).join(" / ") + '</div>';
      var devs = fct.Devices || fct.devices || [];
      if (devs.length) {
        html += '<table style="margin-top:8px"><thead><tr><th>设备</th><th>端口</th><th>类型</th><th>在线</th></tr></thead><tbody>';
        for (var i = 0; i < devs.length; i++) {
          var d = devs[i];
          html += "<tr><td>" + App.esc(d.Name || d.name) + "</td><td>" + App.esc(d.Port || d.port) + "</td><td>" + App.esc(d.Type || d.type) + "</td><td>" + (d.Online || d.online ? "●" : "○") + "</td></tr>";
        }
        html += "</tbody></table>";
      }
      html += "</div>";
    }

    // 趋势
    html += '<div class="card"><h2>历史趋势 <span class="n">近 ' + (samples.length) + ' 采样（5 分钟/点，保留 7 天）</span></h2>'
      + '<canvas id="devTrend" style="width:100%;height:180px;display:block"></canvas>'
      + '<div class="hint" style="margin-top:8px">CPU（红）/ 内存（墨黑）/ 磁盘剩余（灰）—— canvas 自绘，零依赖</div></div>';

    refs.detail.innerHTML = html;
    var close = document.getElementById("btnCloseDetail");
    if (close) close.addEventListener("click", function () { refs.detail.hidden = true; selected = ""; renderGrid(devicesCache); });
    // 绘制趋势
    setTimeout(function () { drawTrend(document.getElementById("devTrend"), samples); }, 30);
  }

  function drawTrend(canvas, samples) {
    if (!canvas || !canvas.getContext) return;
    var dpr = window.devicePixelRatio || 1;
    var w = canvas.clientWidth || 600, h = canvas.clientHeight || 180;
    canvas.width = w * dpr; canvas.height = h * dpr;
    var ctx = canvas.getContext("2d");
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, w, h);
    var css = getComputedStyle(canvas);
    var ink = css.getPropertyValue("--ink") || "#E6EAF0";
    var dim = css.getPropertyValue("--dim") || "#9AA5B1";
    var red = css.getPropertyValue("--red") || "#FF5C5C";
    var line = css.getPropertyValue("--line") || "#262D38";
    var faint = css.getPropertyValue("--faint") || "#5E6874";
    var padL = 36, padR = 12, padT = 10, padB = 22;
    var pw = w - padL - padR, ph = h - padT - padB;
    if (!samples || !samples.length) {
      ctx.fillStyle = dim; ctx.font = "12px sans-serif";
      ctx.fillText("暂无采样（机台 5 分钟上报一条，等待首个采样）", padL + 10, h / 2);
      return;
    }
    // 取近 60 点以内避免过密
    if (samples.length > 60) samples = samples.slice(samples.length - 60);
    // 网格
    ctx.strokeStyle = line; ctx.lineWidth = 1;
    for (var g = 0; g <= 2; g++) {
      var gy = padT + ph - ph * g / 2;
      ctx.beginPath(); ctx.moveTo(padL, gy); ctx.lineTo(w - padR, gy); ctx.stroke();
    }
    var n = samples.length;
    var step = n > 1 ? pw / (n - 1) : 0;
    // 归一化：CPU 0-100，内存按最大，磁盘按总盘（固定）
    var maxMem = 0, maxDisk = 0;
    for (var i = 0; i < n; i++) {
      if (samples[i].MemUsedMb > maxMem) maxMem = samples[i].MemUsedMb;
      if (samples[i].DiskFreeGb > maxDisk) maxDisk = samples[i].DiskFreeGb;
    }
    if (maxMem < 100) maxMem = 100;
    if (maxDisk < 10) maxDisk = 10;
    function yCpu(v) { return padT + ph - ph * (Math.min(100, v) / 100); }
    function yMem(v) { return padT + ph - ph * (v / maxMem); }
    function yDisk(v) { return padT + ph - ph * (v / maxDisk); }
    // CPU 线（红）
    ctx.strokeStyle = red; ctx.lineWidth = 1.6; ctx.beginPath();
    for (var k = 0; k < n; k++) {
      var x = padL + k * step, y = yCpu(samples[k].CpuUsage || 0);
      if (k === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.stroke();
    // 内存线（墨黑）
    ctx.strokeStyle = ink; ctx.lineWidth = 1.2; ctx.beginPath();
    for (var k2 = 0; k2 < n; k2++) {
      var x2 = padL + k2 * step, y2 = yMem(samples[k2].MemUsedMb || 0);
      if (k2 === 0) ctx.moveTo(x2, y2); else ctx.lineTo(x2, y2);
    }
    ctx.stroke();
    // 磁盘线（灰虚线）
    ctx.strokeStyle = dim; ctx.lineWidth = 1; ctx.setLineDash([4, 3]); ctx.beginPath();
    for (var k3 = 0; k3 < n; k3++) {
      var x3 = padL + k3 * step, y3 = yDisk(samples[k3].DiskFreeGb || 0);
      if (k3 === 0) ctx.moveTo(x3, y3); else ctx.lineTo(x3, y3);
    }
    ctx.stroke(); ctx.setLineDash([]);
    // X 轴时间（首/中/尾）
    ctx.fillStyle = dim; ctx.font = "10px sans-serif"; ctx.textAlign = "center";
    if (n > 0) {
      var fmt = function (ts) { return ts ? ts.slice(5, 16) : ""; };
      ctx.fillText(fmt(samples[0].Ts || samples[0].ts), padL, h - 4);
      if (n > 2) ctx.fillText(fmt(samples[Math.floor(n / 2)].Ts || samples[Math.floor(n / 2)].ts), padL + pw / 2, h - 4);
      ctx.fillText(fmt(samples[n - 1].Ts || samples[n - 1].ts), w - padR, h - 4);
    }
    // 图例
    ctx.textAlign = "left"; ctx.font = "11px sans-serif";
    ctx.fillStyle = red; ctx.fillRect(padL, 4, 10, 3); ctx.fillText("CPU%", padL + 14, 10);
    ctx.fillStyle = ink; ctx.fillRect(padL + 70, 4, 10, 3); ctx.fillText("内存 MB", padL + 84, 10);
    ctx.fillStyle = dim; ctx.fillRect(padL + 150, 4, 10, 3); ctx.fillText("磁盘剩余 GB", padL + 164, 10);
    ctx.fillStyle = faint; ctx.font = "10px sans-serif";
    ctx.fillText("内存峰 " + maxMem + "MB  磁盘峰 " + maxDisk.toFixed(1) + "GB", padL, padT - 2);
  }

  function load() {
    App.fetchJSON("/api/devices").then(function (list) {
      list = list || [];
      devicesCache = list;
      var s = sig(list);
      if (s === lastSig) return;
      lastSig = s;
      renderGrid(list);
      // 若已选中，刷新详情的采样（不重建详情，仅重绘趋势）
      if (selected) {
        // 静默刷新选中详情的趋势（不闪）
        App.fetchJSON("/api/devices/" + encodeURIComponent(selected) + "/samples?limit=200").then(function (samples) {
          var cv = document.getElementById("devTrend");
          if (cv) drawTrend(cv, samples || []);
        }).catch(function () {});
      }
    }).catch(function () { /* toast 已由 fetchJSON 处理 */ });
  }

  App.Modules["page-devices"] = {
    init: function (el) {
      el.innerHTML =
        '<div class="page-node">'
        + '<div class="toolbar">'
        + '<input type="text" id="devSearch" placeholder="搜索 机台/host/IP（空格分词）" style="min-width:220px">'
        + '<button class="act" id="btnDevRefresh">刷新</button>'
        + '<span class="hint" id="devCount"></span>'
        + '</div>'
        + '<div id="deviceGrid" class="device-grid"></div>'
        + '<div id="deviceDetail" class="device-detail" hidden></div>'
        + '<div class="foot">设备状态每 3 秒刷新（5s 服务端缓存）· 趋势 canvas 自绘 · 磁盘/CPU/离线告警进飞书（服务端）</div>'
        + "</div>";
      refs.grid = el.querySelector("#deviceGrid");
      refs.detail = el.querySelector("#deviceDetail");
      refs.search = el.querySelector("#devSearch");
      refs.count = el.querySelector("#devCount");
      var btn = el.querySelector("#btnDevRefresh");
      if (btn) btn.addEventListener("click", function () { lastSig = ""; load(); });
      if (refs.search) refs.search.addEventListener("input", function () {
        var kw = refs.search.value.trim().toLowerCase();
        if (!kw) { renderGrid(devicesCache); return; }
        var toks = kw.split(/\s+/);
        var filtered = devicesCache.filter(function (d) {
          var hay = (d.machine + " " + d.hostname + " " + d.ip + " " + d.cpu_model).toLowerCase();
          for (var i = 0; i < toks.length; i++) if (hay.indexOf(toks[i]) < 0) return false;
          return true;
        });
        renderGrid(filtered);
      });
      load();
      if (timer) clearInterval(timer);
      timer = setInterval(function () {
        if (!el.isConnected) { clearInterval(timer); timer = null; return; }
        load();
      }, 3000);
      // 计数
      setInterval(function () {
        if (refs.count) refs.count.textContent = devicesCache.length + " 台（在线 " + devicesCache.filter(function (d) { return d.online; }).length + "）";
      }, 1000);
    },
    render: function () { lastSig = ""; load(); }
  };
})(window);
