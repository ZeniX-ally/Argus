/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— FAIL 明细页（P3 只读核心完整版）
   public/js/page-fails.js
   ----------------
   能力（对标本地 FailListPanel）：树/表双视图切换、搜索分词即时过滤、
   排序（按失败项分组 / 按时间倒序）、高频标色（≥5 红 ≥2 警告）、
   自定义列显隐（持久化）、CSV 导出（服务端 + 自定义列客户端）、
   聚合看板映射（机台筛选联动、计数徽章、最近 FAIL 热点）。
   风格：零依赖、Theme 黑白红（--red/--amber）、dataSig 变更检测、
   prefers-reduced-motion 适配、canvas 无（表格为主）。
   接口 URL 原样：/api/fails?limit&machine /api/export.csv /api/xmlview?id= /api/file?id=
   ============================================================ */
(function (window) {
  "use strict";
  var App = window.App;

  /* 从 log 文件名提取真实测试时间（精确到秒 yyyy-MM-dd HH:mm:ss） */
  function extractFileNameTime(pathOrName) {
    if (!pathOrName) return "";
    var name = String(pathOrName).split(/[\\/]/).pop().replace(/\.[^.]+$/, "");
    var re = /(?:^|_)(\d{14}|\d{17})(?=_|$|\.)/g;
    var m;
    while ((m = re.exec(name)) !== null) {
      var s = m[1].slice(0, 14); // yyyyMMddHHmmss
      var y = parseInt(s.slice(0, 4), 10);
      var M = parseInt(s.slice(4, 6), 10);
      var d = parseInt(s.slice(6, 8), 10);
      var H = parseInt(s.slice(8, 10), 10);
      var min = parseInt(s.slice(10, 12), 10);
      var sec = parseInt(s.slice(12, 14), 10);
      if (y >= 2020 && y <= 2099 && M >= 1 && M <= 12 && d >= 1 && d <= 31 &&
          H >= 0 && H <= 23 && min >= 0 && min <= 59 && sec >= 0 && sec <= 59) {
        return s.slice(0, 4) + "-" + s.slice(4, 6) + "-" + s.slice(6, 8) + " " +
               s.slice(8, 10) + ":" + s.slice(10, 12) + ":" + s.slice(12, 14);
      }
    }
    return "";
  }

  /* 统一解析单条记录展示时间：优先从 log 文件名提取 */
  function resolveRowTime(x) {
    var ft = extractFileNameTime(App.pick(x, "xmlPath") || App.pick(x, "xml_path"));
    if (ft) return ft;
    var bt = App.pick(x, "batchTimestamp") || App.pick(x, "batch_timestamp");
    if (bt && bt.length >= 19) return bt.slice(0, 19).replace("T", " ");
    var ts = App.pick(x, "ts") || App.pick(x, "ingestTs") || App.pick(x, "ingest_ts");
    if (ts) return ts;
    return App.pick(x, "testDate") || App.pick(x, "test_date") || "";
  }

  /* 列定义（数据明细自定义列） */
  var COLS = [
    { key: "time", label: "时间", get: function (x) { return App.fmtTime(resolveRowTime(x)); } },
    { key: "machine", label: "机台", get: function (x) { return App.pick(x, "machine") || "—"; } },
    { key: "model", label: "型号", get: function (x) { return App.pick(x, "model") || "—"; } },
    { key: "sn", label: "SN", get: function (x) { return App.pick(x, "sn") || "—"; } },
    { key: "failReason", label: "失败原因", get: function (x) { return App.pick(x, "failReason") || "—"; } },
    { key: "tester", label: "测试员", get: function (x) { return App.pick(x, "tester") || "—"; } },
    { key: "result", label: "结果", get: function (x) { return (App.pick(x, "result") || "").toUpperCase() || "—"; } }
  ];

  var STORAGE_COLS = "agg.failsCols";
  var STORAGE_VIEW = "agg.failsView";
  var STORAGE_SORT = "agg.failsSort";

  function loadCols() {
    try {
      var raw = JSON.parse(window.localStorage.getItem(STORAGE_COLS) || "null");
      if (raw && typeof raw === "object") {
        var vis = {};
        for (var i = 0; i < COLS.length; i++) vis[COLS[i].key] = true;
        for (var k in raw) if (vis.hasOwnProperty(k)) vis[k] = !!raw[k];
        // 至少保留 2 列
        var cnt = 0; for (var kk in vis) if (vis[kk]) cnt++;
        if (cnt >= 2) return vis;
      }
    } catch (e) {}
    var def = {};
    for (var j = 0; j < COLS.length; j++) def[COLS[j].key] = true;
    return def;
  }
  function saveCols(v) { try { window.localStorage.setItem(STORAGE_COLS, JSON.stringify(v)); } catch (e) {} }
  function loadView() { try { var v = window.localStorage.getItem(STORAGE_VIEW); return v === "tree" || v === "table" ? v : "table"; } catch (e) { return "table"; } }
  function saveView(v) { try { window.localStorage.setItem(STORAGE_VIEW, v); } catch (e) {} }
  function loadSort() { try { var v = window.localStorage.getItem(STORAGE_SORT); return v === "time" || v === "group" || v === "sn" ? v : "sn"; } catch (e) { return "sn"; } }
  function saveSort(v) { try { window.localStorage.setItem(STORAGE_SORT, v); } catch (e) {} }

  var colsVis = loadCols();
  var viewMode = loadView(); // tree | table
  var sortMode = loadSort(); // sn | group | time

  var refs = {};
  var lastSig = "";
  var lastFilteredSig = "";

  /* 高频计数：failReason -> count */
  function buildCounts(list) {
    var m = {};
    for (var i = 0; i < list.length; i++) {
      var k = (App.pick(list[i], "failReason") || "—").trim() || "—";
      m[k] = (m[k] || 0) + 1;
    }
    return m;
  }
  function hotClass(cnt) {
    if (cnt >= 5) return "hot5";
    if (cnt >= 2) return "hot2";
    return "";
  }
  function hotStyle(cnt) {
    if (cnt >= 5) return ' style="color:var(--red);font-weight:600"';
    if (cnt >= 2) return ' style="color:var(--amber)"';
    return "";
  }

  /* 搜索分词（与本地一致：空格分词，大小写不敏感，hay= failReason+sn+model） */
  function filterList(list, kw) {
    if (!kw) return list;
    var keys = kw.trim().toLowerCase().split(/\s+/).filter(function (k) { return k.length > 0; });
    if (!keys.length) return list;
    return list.filter(function (x) {
      var hay = ((App.pick(x, "failReason") || "") + " " + (App.pick(x, "sn") || "") + " " + (App.pick(x, "model") || "") + " " + (App.pick(x, "machine") || "")).toLowerCase();
      for (var i = 0; i < keys.length; i++) if (hay.indexOf(keys[i]) < 0) return false;
      return true;
    });
  }

  function grouped(list) {
    var groups = {};
    var order = [];
    for (var i = 0; i < list.length; i++) {
      var k = (App.pick(list[i], "failReason") || "—").trim() || "—";
      if (!groups[k]) { groups[k] = []; order.push(k); }
      groups[k].push(list[i]);
    }
    // 按次数倒序，次数相同按 failReason 字典序
    order.sort(function (a, b) {
      var ca = groups[a].length, cb = groups[b].length;
      if (cb !== ca) return cb - ca;
      return a.localeCompare(b, "zh");
    });
    var out = [];
    for (var j = 0; j < order.length; j++) {
      var gk = order[j];
      // 组内按时间倒序（优先从文件名提取的时间）
      groups[gk].sort(function (a, b) {
        var ta = resolveRowTime(a);
        var tb = resolveRowTime(b);
        if (tb !== ta) return tb < ta ? -1 : 1;
        return 0;
      });
      out.push({ key: gk, count: groups[gk].length, rows: groups[gk] });
    }
    return out;
  }

  function timeSorted(list) {
    return list.slice().sort(function (a, b) {
      var ta = resolveRowTime(a);
      var tb = resolveRowTime(b);
      if (tb !== ta) return tb < ta ? -1 : 1;
      return 0;
    });
  }

  /* 按 SN 分组聚合：相同 SN 合并问题项，并从对应 SN 的 log 文件名提取真实测试时间 */
  function groupBySn(list) {
    var groups = {};
    var order = [];
    for (var i = 0; i < list.length; i++) {
      var row = list[i];
      var sn = (App.pick(row, "sn") || "—").trim() || "—";
      if (!groups[sn]) {
        groups[sn] = {
          sn: sn,
          machine: App.pick(row, "machine") || "—",
          model: App.pick(row, "model") || "—",
          time: resolveRowTime(row),
          tester: App.pick(row, "tester") || "—",
          rows: []
        };
        order.push(sn);
      }
      var grp = groups[sn];
      grp.rows.push(row);
      var t = resolveRowTime(row);
      if (t && (!grp.time || t > grp.time)) {
        grp.time = t;
        if (!grp.machine || grp.machine === "—") grp.machine = App.pick(row, "machine") || "—";
        if (!grp.model || grp.model === "—") grp.model = App.pick(row, "model") || "—";
      }
    }
    // 组内记录按时间倒序
    for (var k = 0; k < order.length; k++) {
      groups[order[k]].rows.sort(function (a, b) {
        var ta = resolveRowTime(a), tb = resolveRowTime(b);
        return tb < ta ? -1 : (tb > ta ? 1 : 0);
      });
    }
    // 整体按 SN 最新时间倒序
    order.sort(function (a, b) {
      var ta = groups[a].time || "", tb = groups[b].time || "";
      if (tb !== ta) return tb < ta ? -1 : 1;
      return groups[b].rows.length - groups[a].rows.length;
    });
    var out = [];
    for (var j = 0; j < order.length; j++) {
      var item = groups[order[j]];
      var reasons = [];
      var seen = {};
      for (var r = 0; r < item.rows.length; r++) {
        var reason = (App.pick(item.rows[r], "failReason") || "—").trim();
        if (reason && !seen[reason]) {
          seen[reason] = true;
          reasons.push(reason);
        }
      }
      item.reasons = reasons;
      out.push(item);
    }
    return out;
  }

  /* 渲染：聚合看板映射（KPI 区） */
  function renderBoardMap() {
    if (!refs.boardMap) return;
    var s = App.state;
    var total = s.failCount || s.fails.length;
    var perMachine = {};
    for (var i = 0; i < s.fails.length; i++) {
      var m = App.pick(s.fails[i], "machine") || "—";
      perMachine[m] = (perMachine[m] || 0) + 1;
    }
    var machines = s.machines || [];
    var html = '<div class="hint" style="margin-bottom:10px">聚合映射：累计 FAIL <b style="color:var(--red)">' + total + '</b> 条';
    if (s.sel) html += '（当前筛选：' + App.esc(s.sel) + '）';
    html += ' · 机台数 ' + machines.length + ' · 最近 ' + s.fails.length + ' 条明细已加载</div>';
    if (machines.length) {
      html += '<div class="board-mini">';
      for (var j = 0; j < machines.length; j++) {
        var mm = machines[j];
        var name = App.pick(mm, "machine") || "—";
        var fc = App.pick(mm, "failCount") != null ? App.pick(mm, "failCount") : perMachine[name] || 0;
        var on = !!App.pick(mm, "online");
        var hot = fc >= 5 ? 'hot5' : fc >= 2 ? 'hot2' : '';
        html += '<span class="mini-card ' + hot + '" title="' + App.esc(name) + ' FAIL=' + fc + '">'
          + '<span class="dot ' + (on ? 'ok' : 'err') + '"></span>' + App.esc(name)
          + '<b' + (fc >= 5 ? ' style="color:var(--red)"' : fc >= 2 ? ' style="color:var(--amber)"' : '') + '> ' + fc + '</b></span>';
      }
      html += '</div>';
    }
    refs.boardMap.innerHTML = html;
  }

  /* 表格视图 */
  function renderTable(list, counts) {
    var visCols = COLS.filter(function (c) { return colsVis[c.key]; });
    if (!visCols.length) visCols = COLS.slice(0, 4);
    var thead = "<tr>";
    for (var i = 0; i < visCols.length; i++) thead += "<th>" + App.esc(visCols[i].label) + "</th>";
    thead += "<th>操作</th></tr>";
    if (refs.thead) refs.thead.innerHTML = thead;

    if (!list.length) {
      if (refs.tbody) refs.tbody.innerHTML = "";
      if (refs.empty) refs.empty.style.display = "block";
      return;
    }
    if (refs.empty) refs.empty.style.display = "none";

    var html = "";
    if (sortMode === "sn") {
      var snGroups = groupBySn(list);
      for (var s = 0; s < snGroups.length; s++) {
        var sg = snGroups[s];
        var alt = s % 2 === 0 ? "" : ' style="background:var(--bg2)"';
        var reasonsText = sg.reasons.join(", ");
        if (sg.reasons.length > 1) reasonsText += " (共 " + sg.reasons.length + " 项)";
        var hc = hotClass(sg.reasons.length);
        html += '<tr class="' + (hc || "") + '"' + alt + '>';
        for (var ci = 0; ci < visCols.length; ci++) {
          var col = visCols[ci];
          var val = "";
          if (col.key === "sn") val = sg.sn;
          else if (col.key === "time") val = App.fmtTime(sg.time);
          else if (col.key === "machine") val = sg.machine;
          else if (col.key === "model") val = sg.model;
          else if (col.key === "failReason") val = reasonsText;
          else if (col.key === "tester") val = sg.tester;
          else if (col.key === "result") val = "FAIL";
          else val = col.get(sg.rows[0]);

          var esc = App.esc(val);
          if (col.key === "failReason") {
            html += '<td title="' + App.esc(sg.reasons.join("\n")) + '"' + hotStyle(sg.reasons.length) + '>' + esc + '</td>';
          } else if (col.key === "result") {
            html += '<td>' + App.badge("FAIL", "fail") + '</td>';
          } else if (col.key === "time") {
            html += '<td title="' + App.esc(sg.time) + '">' + esc + '</td>';
          } else {
            html += '<td title="' + esc + '">' + esc + '</td>';
          }
        }
        var firstId = App.pick(sg.rows[0], "id");
        html += '<td><a class="op" href="' + App.data.xmlUrl(firstId) + '" target="_blank">报告</a>'
          + '<a class="op" href="' + App.data.fileUrl(firstId) + '" download>XML</a></td></tr>';
      }
      if (refs.tbody) refs.tbody.innerHTML = html;
      return;
    }

    var rows = [];
    if (sortMode === "time") {
      var sorted = timeSorted(list);
      for (var k = 0; k < sorted.length; k++) rows.push({ item: sorted[k], cnt: counts[(App.pick(sorted[k], "failReason") || "—").trim() || "—"] || 0, gi: k });
    } else {
      var gs = grouped(list);
      var gi = 0;
      for (var g = 0; g < gs.length; g++) {
        for (var r = 0; r < gs[g].rows.length; r++) rows.push({ item: gs[g].rows[r], cnt: gs[g].count, gi: gi });
        gi++;
      }
    }
    for (var idx = 0; idx < rows.length; idx++) {
      var x = rows[idx].item, cnt = rows[idx].cnt, gidx = rows[idx].gi;
      var alt2 = gidx % 2 === 0 ? "" : ' style="background:var(--bg2)"';
      var hc2 = hotClass(cnt);
      var cls2 = hc2 ? hc2 : "";
      html += '<tr class="' + cls2 + '"' + alt2 + '>';
      for (var ci2 = 0; ci2 < visCols.length; ci2++) {
        var col2 = visCols[ci2];
        var val2 = col2.get(x);
        var esc2 = App.esc(val2);
        if (col2.key === "failReason") {
          html += '<td title="' + esc2 + '"' + hotStyle(cnt) + '>' + esc2 + '</td>';
        } else if (col2.key === "result") {
          var rc = (val2 || "").toUpperCase();
          var badge = rc === "FAIL" ? App.badge("FAIL", "fail") : rc ? App.badge(rc, "pass") : '<span class="badge">—</span>';
          html += '<td>' + badge + '</td>';
        } else if (col2.key === "time") {
          html += '<td title="' + App.esc(resolveRowTime(x)) + '">' + esc2 + '</td>';
        } else {
          html += '<td title="' + esc2 + '">' + esc2 + '</td>';
        }
      }
      var id = App.pick(x, "id");
      html += '<td><a class="op" href="' + App.data.xmlUrl(id) + '" target="_blank">报告</a>'
        + '<a class="op" href="' + App.data.fileUrl(id) + '" download>XML</a></td></tr>';
    }
    if (refs.tbody) refs.tbody.innerHTML = html;
  }

  /* 树视图 */
  function renderTree(list, counts) {
    if (!list.length) {
      if (refs.treeBox) refs.treeBox.innerHTML = '<div class="empty">暂无 FAIL 数据</div>';
      return;
    }
    var html = "";

    // 模式 1：按 SN 合并问题折叠卡片
    if (sortMode === "sn") {
      var snGroups = groupBySn(list);
      for (var s = 0; s < snGroups.length; s++) {
        var sg = snGroups[s];
        var failBadge = sg.reasons.length >= 5
          ? '<span class="badge fail">' + sg.reasons.length + ' 项失败</span>'
          : (sg.reasons.length >= 2
            ? '<span class="badge" style="border-color:var(--amber);color:var(--amber)">' + sg.reasons.length + ' 项失败</span>'
            : '<span class="badge">' + sg.reasons.length + ' 项失败</span>');
        var reasonSummary = sg.reasons.join(", ");

        html += '<div class="tree-group" style="margin-bottom:8px;border:1px solid var(--line);border-radius:4px;overflow:hidden">'
          + '<div class="tree-head sn-tree-head" data-sn-idx="' + s + '" style="display:flex;align-items:center;justify-content:space-between;padding:8px 12px;background:var(--bg2);cursor:pointer;user-select:none">'
          +   '<div style="display:flex;align-items:center;gap:10px;flex:1;min-width:0">'
          +     '<span class="sn-toggle-icon" style="font-size:11px;color:var(--dim);width:12px">▼</span>'
          +     '<span style="font-weight:700;font-family:monospace;color:var(--ink);font-size:13px">SN: ' + App.esc(sg.sn) + '</span>'
          +     '<span style="font-size:12px;color:var(--dim)">机台: ' + App.esc(sg.machine) + '</span>'
          +     '<span style="font-size:12px;color:var(--dim)">型号: ' + App.esc(sg.model) + '</span>'
          +     '<span style="font-size:12px;color:var(--text);font-family:monospace" title="文件名时间: ' + App.esc(sg.time) + '">时间: ' + App.esc(App.fmtTime(sg.time)) + '</span>'
          +     failBadge
          +     '<span style="font-size:12px;color:var(--faint);white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:320px" title="' + App.esc(reasonSummary) + '">' + App.esc(reasonSummary) + '</span>'
          +   '</div>'
          +   '<div style="font-size:11px;color:var(--dim)">展开/折叠</div>'
          + '</div>'
          + '<div class="tree-children sn-tree-children" id="sn-group-' + s + '" style="background:var(--bg)">';

        for (var r = 0; r < sg.rows.length; r++) {
          var item = sg.rows[r];
          var fr = (App.pick(item, "failReason") || "—").trim() || "—";
          var itime = resolveRowTime(item);
          var id = App.pick(item, "id");
          var tester = App.pick(item, "tester") || "—";
          html += '<div class="tree-leaf" style="display:flex;align-items:center;justify-content:space-between;padding:6px 14px 6px 36px;border-top:1px solid var(--line-sub, #262626)">'
            + '<div style="display:flex;align-items:center;gap:12px">'
            +   '<span style="color:var(--red);font-weight:600">' + App.esc(fr) + '</span>'
            +   '<span class="leaf-meta" style="font-size:11px;color:var(--dim)">测试员: ' + App.esc(tester) + ' · 准确时间: ' + App.esc(itime) + '</span>'
            + '</div>'
            + '<div class="leaf-ops" style="display:flex;gap:6px">'
            +   '<a class="op" href="' + App.data.xmlUrl(id) + '" target="_blank">报告</a>'
            +   '<a class="op" href="' + App.data.fileUrl(id) + '" download>XML</a>'
            + '</div>'
            + '</div>';
        }
        html += '</div></div>';
      }
      refs.treeBox.innerHTML = html;
      var heads = refs.treeBox.querySelectorAll(".sn-tree-head");
      for (var hi = 0; hi < heads.length; hi++) {
        heads[hi].addEventListener("click", function () {
          var idx = this.getAttribute("data-sn-idx");
          var body = refs.treeBox.querySelector("#sn-group-" + idx);
          var icon = this.querySelector(".sn-toggle-icon");
          if (!body) return;
          var hidden = body.style.display === "none";
          body.style.display = hidden ? "block" : "none";
          if (icon) icon.textContent = hidden ? "▼" : "▶";
        });
      }
      return;
    }

    if (sortMode === "time") {
      var sorted = timeSorted(list);
      // 按日期分组展示
      var byDate = {};
      var dates = [];
      for (var i = 0; i < sorted.length; i++) {
        var rowTime = resolveRowTime(sorted[i]);
        var d = rowTime.slice(0, 10) || "未知日期";
        if (!byDate[d]) { byDate[d] = []; dates.push(d); }
        byDate[d].push(sorted[i]);
      }
      for (var di = 0; di < dates.length; di++) {
        var dk = dates[di];
        var arr = byDate[dk];
        html += '<div class="tree-group"><div class="tree-head"><span class="tree-k">' + App.esc(dk) + '</span><span class="badge">' + arr.length + ' 次</span></div>';
        html += '<div class="tree-children">';
        for (var ri = 0; ri < arr.length; ri++) {
          var x2 = arr[ri], cnt2 = counts[(App.pick(x2, "failReason") || "—").trim() || "—"] || 0;
          var itime2 = resolveRowTime(x2);
          html += '<div class="tree-leaf' + (hotClass(cnt2) ? ' ' + hotClass(cnt2) : '') + '"' + hotStyle(cnt2) + '>'
            + '<span class="leaf-main">' + App.esc(App.pick(x2, "failReason") || "—") + '</span>'
            + '<span class="leaf-meta">' + App.esc(App.pick(x2, "machine") || "") + ' · ' + App.esc(App.pick(x2, "sn") || "") + ' · ' + App.esc(App.fmtTime(itime2)) + '</span>'
            + '<span class="leaf-ops"><a class="op" href="' + App.data.xmlUrl(App.pick(x2, "id")) + '" target="_blank">报告</a><a class="op" href="' + App.data.fileUrl(App.pick(x2, "id")) + '" download>XML</a></span>'
            + '</div>';
        }
        html += '</div></div>';
      }
    } else {
      var gs = grouped(list);
      for (var gi = 0; gi < gs.length; gi++) {
        var g = gs[gi];
        var hc = hotClass(g.count);
        html += '<div class="tree-group ' + hc + '"><div class="tree-head"' + hotStyle(g.count) + '>'
          + '<span class="tree-k">' + App.esc(g.key) + '</span>'
          + '<span class="badge ' + (hc === "hot5" ? "fail" : hc === "hot2" ? "" : "") + '" style="' + (hc === "hot2" ? "border-color:var(--amber);color:var(--amber)" : "") + '">' + g.count + ' 次</span>'
          + '<span class="tree-n">' + (g.count >= 5 ? '高频' : g.count >= 2 ? '关注' : '') + '</span>'
          + '</div><div class="tree-children">';
        for (var rj = 0; rj < g.rows.length; rj++) {
          var xr = g.rows[rj];
          var itimeR = resolveRowTime(xr);
          html += '<div class="tree-leaf">'
            + '<span class="leaf-meta">' + App.esc(App.pick(xr, "machine") || "") + ' · ' + App.esc(App.pick(xr, "sn") || "") + ' · ' + App.esc(App.pick(xr, "model") || "") + ' · ' + App.esc(App.fmtTime(itimeR)) + '</span>'
            + '<span class="leaf-ops"><a class="op" href="' + App.data.xmlUrl(App.pick(xr, "id")) + '" target="_blank">报告</a><a class="op" href="' + App.data.fileUrl(App.pick(xr, "id")) + '" download>XML</a></span>'
            + '</div>';
        }
        html += '</div></div>';
      }
    }
    if (refs.treeBox) refs.treeBox.innerHTML = html;
  }

  function sig(list, kw) {
    var s = App.state;
    return JSON.stringify(s.machines) + "|" + s.failCount + "|" + (s.sel || "") + "|" + viewMode + "|" + sortMode + "|" + JSON.stringify(colsVis) + "|" + kw + "|" + list.length + "|" + (list[0] ? App.pick(list[0], "id") + (App.pick(list[0], "ingestTs") || "") : "");
  }

  function render() {
    var s = App.state;
    var kw = refs.search ? (refs.search.value || "").trim() : "";
    var filtered = filterList(s.fails || [], kw);
    var counts = buildCounts(s.fails || []);

    // dataSig 变更检测：全局数据 + 本地过滤 + 视图/排序/列配置 未变则跳过重绘
    var curSig = sig(filtered, kw);
    if (curSig === lastSig && filtered.length) return;
    lastSig = curSig;

    var scopeTxt = (s.sel ? "（机台 " + s.sel + "）" : "") + (kw ? "（过滤命中 " + filtered.length + "/" + (s.fails || []).length + "）" : "");
    if (refs.count) {
      var grpInfo = sortMode === "sn"
        ? (function () { var g = groupBySn(filtered); return g.length + " 个独立 SN（合并问题）"; })()
        : (sortMode === "group" ? (function () { var g = grouped(filtered); return g.length + " 个失败项"; })() : "按时间倒序");
      refs.count.textContent = "共 " + filtered.length + " 条" + scopeTxt + " · " + grpInfo;
    }

    var empty = !filtered.length;
    if (refs.emptyTable) refs.emptyTable.style.display = empty && viewMode === "table" ? "flex" : "none";
    if (refs.emptyTree) refs.emptyTree.style.display = empty && viewMode === "tree" ? "flex" : "none";

    // 计数与高频图例
    if (refs.legend) {
      var high = 0, warn = 0;
      for (var kk in counts) { if (counts[kk] >= 5) high++; else if (counts[kk] >= 2) warn++; }
      refs.legend.innerHTML = '<span class="badge fail">≥5 红(' + high + ')</span> <span class="badge" style="border-color:var(--amber);color:var(--amber)">≥2 警告(' + warn + ')</span> <span class="badge">正常</span>';
    }

    if (viewMode === "tree") {
      if (refs.tableWrap) refs.tableWrap.style.display = "none";
      if (refs.treeBox) refs.treeBox.style.display = "block";
      renderTree(filtered, counts);
    } else {
      if (refs.treeBox) refs.treeBox.style.display = "none";
      if (refs.tableWrap) refs.tableWrap.style.display = "block";
      renderTable(filtered, counts);
    }
  }

  /* 自定义列面板 */
  function buildColPanel() {
    if (!refs.colCfg) return;
    var html = "";
    for (var i = 0; i < COLS.length; i++) {
      var c = COLS[i];
      html += '<label class="chk" style="display:inline-flex;align-items:center;gap:4px;cursor:pointer;font-size:12px;color:var(--ink)"><input type="checkbox" data-col="' + c.key + '" ' + (colsVis[c.key] ? "checked" : "") + '> ' + App.esc(c.label) + '</label>';
    }
    refs.colCfg.innerHTML = html;
    var checks = refs.colCfg.querySelectorAll("input[data-col]");
    for (var k = 0; k < checks.length; k++) {
      checks[k].addEventListener("change", function (e) {
        var ck = e.target.getAttribute("data-col");
        colsVis[ck] = e.target.checked;
        // 至少保留 1 列
        var cnt = 0; for (var kk in colsVis) if (colsVis[kk]) cnt++;
        if (cnt === 0) { colsVis[ck] = true; e.target.checked = true; App.toast("至少保留一列", "err"); return; }
        saveCols(colsVis);
        lastSig = ""; render();
      });
    }
  }

  /* 客户端 CSV 导出（尊重自定义列与当前过滤，含 CWE-1236 防护与 BOM） */
  function exportCustomCsv() {
    var kw = refs.search ? (refs.search.value || "").trim() : "";
    var list = filterList(App.state.fails || [], kw);
    if (sortMode === "sn") {
      var snGroups = groupBySn(list);
      var visCols = COLS.filter(function (c) { return colsVis[c.key]; });
      if (!visCols.length) visCols = COLS.slice();
      var header = visCols.map(function (c) { return c.label; }).join(",");
      var lines = [header];
      for (var s = 0; s < snGroups.length; s++) {
        var sg = snGroups[s];
        var vals = visCols.map(function (c) {
          var v = "";
          if (c.key === "sn") v = sg.sn;
          else if (c.key === "time") v = sg.time;
          else if (c.key === "machine") v = sg.machine;
          else if (c.key === "model") v = sg.model;
          else if (c.key === "failReason") v = sg.reasons.join("；");
          else if (c.key === "tester") v = sg.tester;
          else if (c.key === "result") v = "FAIL";
          else v = c.get(sg.rows[0]) || "";
          v = String(v);
          if (v.length > 0 && (v[0] === "=" || v[0] === "+" || v[0] === "-" || v[0] === "@" || v[0] === "\t" || v[0] === "\r")) v = "'" + v;
          if (v.indexOf(",") >= 0 || v.indexOf('"') >= 0 || v.indexOf("\n") >= 0 || v.indexOf("\r") >= 0) v = '"' + v.replace(/"/g, '""') + '"';
          return v;
        }).join(",");
        lines.push(vals);
      }
      var csv = lines.join("\r\n");
      var bom = "\uFEFF";
      var blob = new Blob([bom + csv], { type: "text/csv;charset=utf-8" });
      var url = URL.createObjectURL(blob);
      var a = document.createElement("a");
      a.href = url; a.download = "fails_sn_merged_" + new Date().toISOString().slice(0, 10) + ".csv";
      document.body.appendChild(a); a.click();
      setTimeout(function () { document.body.removeChild(a); URL.revokeObjectURL(url); }, 500);
      return;
    }
    if (sortMode === "time") list = timeSorted(list);
    else {
      var gs = grouped(list);
      var flat = []; for (var i = 0; i < gs.length; i++) flat = flat.concat(gs[i].rows);
      list = flat;
    }
    var visCols = COLS.filter(function (c) { return colsVis[c.key]; });
    if (!visCols.length) visCols = COLS.slice();
    var header = visCols.map(function (c) { return c.label; }).join(",");
    var lines = [header];
    for (var r = 0; r < list.length; r++) {
      var vals = visCols.map(function (c) {
        var v = c.key === "time" ? resolveRowTime(list[r]) : (c.get(list[r]) || "");
        v = String(v);
        if (v.length > 0 && (v[0] === "=" || v[0] === "+" || v[0] === "-" || v[0] === "@" || v[0] === "\t" || v[0] === "\r")) v = "'" + v;
        if (v.indexOf(",") >= 0 || v.indexOf('"') >= 0 || v.indexOf("\n") >= 0 || v.indexOf("\r") >= 0) v = '"' + v.replace(/"/g, '""') + '"';
        return v;
      }).join(",");
      lines.push(vals);
    }
    var csv = lines.join("\r\n");
    var bom = "\uFEFF";
    var blob = new Blob([bom + csv], { type: "text/csv;charset=utf-8" });
    var url = URL.createObjectURL(blob);
    var a = document.createElement("a");
    a.href = url; a.download = "fails_custom_" + new Date().toISOString().slice(0, 10) + ".csv";
    document.body.appendChild(a); a.click();
    setTimeout(function () { document.body.removeChild(a); URL.revokeObjectURL(url); }, 500);
  }

  function updateSegActive() {
    var seg = document.querySelector('.seg[data-name="view"]');
    if (!seg) return;
    var items = seg.querySelectorAll(".seg-item");
    for (var i = 0; i < items.length; i++) {
      items[i].classList.toggle("active", (i === 0 && viewMode === "tree") || (i === 1 && viewMode === "table"));
    }
  }
  // 校验/收集引用：未命中时用 id 兜底（兼容 renderToolbar 用 id 生成的控件）
  function captureRefs(root) {
    var out = {};
    var ns = root.querySelectorAll("[data-ref],[id]");
    for (var i = 0; i < ns.length; i++) {
      var key = ns[i].getAttribute("data-ref") || ns[i].id;
      if (key && !out[key]) out[key] = ns[i];
    }
    return out;
  }

  App.Modules["page-fails"] = {
    init: function (el, ctx) {
      el.innerHTML =
        '<div class="page-node">'
        + '<div class="page-head">'
        +   '<h2>FAIL 明细 <span class="badge" data-ref="count" style="background:var(--bg2);border-color:var(--line);color:var(--dim)">0 条</span></h2>'
        +   '<div class="ph-ops">'
        +     '<button class="icon-btn btn-sm" data-ref="btnCols" title="自定义列">&#x2699; 列</button>'
        +   '</div>'
        + '</div>'
        + App.renderToolbar([
            { type: "input", kind: "search", id: "search", placeholder: "搜索 失败项 / SN / 型号 / 机台…（空格分词）", width: "260px" },
            { type: "sep" },
            { type: "seg", name: "view", items: ["树形", "表格"], active: viewMode === "tree" ? 0 : 1 },
            { type: "select", id: "sortSel", options: '<option value="sn">按 SN 合并问题</option><option value="group">按失败项分组</option><option value="time">按时间倒序</option>' },
            { type: "sep" },
            { type: "btn", label: "导出 CSV", cls: "btn-secondary btn-sm", id: "btnCsv" },
            { type: "btn", label: "导出当前", cls: "btn-ghost btn-sm", id: "btnCsvCustom" },
          ], true)
        + '<div data-ref="colPanel" class="panel" style="margin-bottom:var(--gap-sm);display:none">'
        +   '<div style="display:flex;flex-wrap:wrap;gap:8px 16px" data-ref="colCfg"></div>'
        + '</div>'
        + '<div data-ref="legend" style="display:flex;gap:6px;flex-wrap:wrap;margin-bottom:var(--gap-sm)"></div>'
        + '<div data-ref="tableWrap" class="table-wrap" style="position:relative">'
        +   '<table><thead data-ref="thead"></thead><tbody data-ref="tbody"></tbody></table>'
        +   '<div data-ref="emptyTable" class="empty-state" style="display:none">'
        +     '<span class="es-icon">&#x269B;</span><div class="es-text">暂无 FAIL 数据</div></div>'
        + '</div>'
        + '<div data-ref="treeBox" style="display:none">'
        +   '<div data-ref="emptyTree" class="empty-state" style="display:none">'
        +     '<span class="es-icon">&#x269B;</span><div class="es-text">暂无 FAIL 数据</div></div>'
        + '</div>'
        + '<div class="foot" style="text-align:center;color:var(--faint);font-size:11px;padding:10px 0">FAIL 明细每 3 秒自动刷新 · 高频标色 ≥5 红 ≥2 警告 · 列配置本地持久化 · 树/表双视图</div>'
        + '</div>';

      // 收集引用：并集 data-ref 与 id（toolbar 项用 id，兼容两种写法）
      var n = el.querySelectorAll("[data-ref],[id]");
      for (var i = 0; i < n.length; i++) {
        var key = n[i].getAttribute("data-ref") || n[i].id;
        if (key && !refs[key]) refs[key] = n[i];
      }

      // 初始化状态
      if (refs.search) refs.search.value = "";
      if (refs.sortSel) refs.sortSel.value = sortMode;
      updateSegActive();

      buildColPanel();

      if (refs.search) refs.search.addEventListener("input", function () { lastSig = ""; render(); });
      if (refs.sortSel) refs.sortSel.addEventListener("change", function (e) { sortMode = e.target.value; saveSort(sortMode); lastSig = ""; render(); });
      if (refs.btnCols) refs.btnCols.addEventListener("click", function () { if (refs.colPanel) refs.colPanel.style.display = refs.colPanel.style.display === "none" ? "" : "none"; });
      if (refs.btnCsv) refs.btnCsv.addEventListener("click", function () {
        if (App.auth && App.auth.isViewer && App.auth.isViewer()) { App.toast("访客无导出权限", "err"); return; }
        window.open(App.data.exportUrl(App.state.sel || ""), "_blank");
      });
      if (refs.btnCsvCustom) refs.btnCsvCustom.addEventListener("click", function () {
        if (App.auth && App.auth.isViewer && App.auth.isViewer()) { App.toast("访客无导出权限", "err"); return; }
        exportCustomCsv();
      });

      // 分段控件：视图切换
      var segEl = el.querySelector('.seg[data-name="view"]');
      if (segEl) {
        segEl.addEventListener("click", function (e) {
          var item = e.target && e.target.closest ? e.target.closest(".seg-item") : null;
          if (!item) return;
          var idx = parseInt(item.getAttribute("data-idx") || "0", 10);
          viewMode = idx === 0 ? "tree" : "table";
          saveView(viewMode);
          lastSig = "";
          updateSegActive();
          render();
        });
      }

      // 权限显隐
      if (refs.btnCsv) refs.btnCsv.classList.add("hide-for-viewer");
      if (refs.btnCsvCustom) refs.btnCsvCustom.classList.add("hide-for-viewer");
      if (App.applyRoleVisibility) App.applyRoleVisibility(el);
      // hash 联动下钻：?q= / ?id=
      try {
        var hq = (window.location.hash.split("?")[1] || "");
        var qp = new URLSearchParams(hq);
        var qv = qp.get("q");
        if (qv && refs.search) { refs.search.value = decodeURIComponent(qv); lastSig = ""; render(); }
        window.addEventListener("argus:drill", function (ev) {
          var d = ev.detail || {};
          if (d.q && refs.search) { refs.search.value = d.q; lastSig = ""; render(); }
          if (d.link && d.link.indexOf("#/fails") >= 0) App.Nav.go("fails");
        });
      } catch (e) {}

      render(ctx);
    },
    render: render
  };
})(window);
