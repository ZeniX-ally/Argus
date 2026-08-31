/* ============================================================
   Argus FCT 聚合系统 · 模块化前端基建
   public/js/core.js
   ----------------
   职责：fetch 封装（SameSite cookie + 可选 URL 拼 token）、
   通用渲染（卡片 / 表格 / 徽章 / toast / canvas 柱图）、
   时间格式化、共享数据状态与轮询刷新、Lite-Settings 权限/布局/收藏/全局搜索。
   模块格式：本文件不注册 page-* 模块，仅在 App 命名空间下
   暴露公共 API；由 loader 作为所有页面模块的统一依赖注入。
   ----------------
   接口保持与 demo 一致（URL 不变）：
   /api/machines  /api/fails?limit&machine  /api/fails/count
   /api/health    /api/export.csv           /api/xmlview?id=  /api/file?id=
   /api/status    /api/users/me  /api/users/me/layout  /api/search
   鉴权：后端 agg_token 有配置时 GET 校验 ?token= / Cookie / 头任一，
   不匹配一律 403 → 页面提示"重新带 token 打开"。
   ============================================================ */
(function (window, document) {
  "use strict";

  var App = window.App = window.App || {};
  // 基础命名空间（v3.18.0 静态加载：模块文件先于 loader 执行，须在此初始化）
  App.Modules = App.Modules || {};
  App.Routes = App.Routes || {};

  /* ---------------- 工具函数 ---------------- */

  /* HTML 转义（防 XSS / 表格注入） */
  App.esc = function (s) {
    return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  };

  /* 后端序列化字段为 PascalCase（Machine/FailCount…），前端统一小写取法，两态兼容 */
  App.pick = function (o, key) {
    if (!o) return undefined;
    var c = key.charAt(0).toLowerCase() + key.slice(1);
    var p = key.charAt(0).toUpperCase() + key.slice(1);
    var v = o[c] !== undefined ? o[c] : o[p];
    return v;
  };

  /* 运行时长格式化：3600s -> 1h05m */
  App.fmtUp = function (s) {
    s = Math.floor(s || 0);
    var h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60);
    return h > 0 ? h + "h" + m + "m" : m + "m" + Math.floor(s % 60) + "s";
  };

  /* 时间戳显示：取 "MM-dd HH:mm" 段（与 demo 一致） */
  App.fmtTime = function (ts) {
    if (!ts) return "--";
    var s = String(ts);
    return s.length >= 16 ? s.slice(5, 16) : s;
  };

  /* ---------------- token 处理 ----------------
     规则：优先带 ?token= 打开页面（后端 POST 推送用 X-Agg-Token 头，
     GET 看板/API 校验 token 参数或 Cookie）。这里读取 URL 上的 token，
     之后所有 API 请求 / 链接都自动拼接，保证鉴权模式可用。 */
  App.token = (function () {
    var m = /[?&]token=([^&]+)/.exec(window.location.search);
    return m ? decodeURIComponent(m[1]) : "";
  })();

  /* 给 URL 拼接 token 参数（页面内 <a> 跳转 / iframe 用） */
  App.withToken = function (url) {
    if (!App.token) return url;
    return url + (url.indexOf("?") < 0 ? "?" : "&") + "token=" + encodeURIComponent(App.token);
  };

  /* ---------------- fetch 封装 ----------------
     返回 Promise<json>；失败抛 Error("URL → HTTP 状态")；
     401/403 额外 toast 提示重新带 token 打开。 */
  App.fetchJSON = function (path) {
    var u = path.indexOf("http") === 0 ? path : (window.location.origin + path);
    if (App.token) u = App.withToken(u);
    return fetch(u, { credentials: "same-origin", headers: { "Accept": "application/json" } })
      .then(function (r) {
        if (r.status === 401 || r.status === 403) {
          App.toast("接口未授权（HTTP " + r.status + "）：请用 ?token=<聚合token> 重新打开本页", "err");
          throw new Error(u + " → HTTP " + r.status + "（未授权，需带 token 访问）");
        }
        if (!r.ok) throw new Error(u + " → HTTP " + r.status);
        return r.json();
      });
  };
  App.fetchText = function (path) {
    var u = path.indexOf("http") === 0 ? path : (window.location.origin + path);
    if (App.token) u = App.withToken(u);
    return fetch(u, { credentials: "same-origin" }).then(function (r) {
      if (!r.ok) throw new Error(u + " → HTTP " + r.status);
      return r.text();
    });
  };
  App.postJSON = function (path, body) {
    var u = path.indexOf("http") === 0 ? path : (window.location.origin + path);
    if (App.token) u = App.withToken(u);
    return fetch(u, { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json", "Accept": "application/json" }, body: JSON.stringify(body || {}) })
      .then(function (r) {
        if (r.status === 401 || r.status === 403) { App.toast("权限不足（" + r.status + "）", "err"); throw new Error("HTTP " + r.status); }
        if (!r.ok) return r.text().then(function (t) { throw new Error(t || ("HTTP " + r.status)); });
        var ct = r.headers.get("content-type") || "";
        if (ct.indexOf("application/json") >= 0) return r.json();
        return r.text();
      });
  };
  App.patchJSON = function (path, body) {
    var u = path.indexOf("http") === 0 ? path : (window.location.origin + path);
    if (App.token) u = App.withToken(u);
    return fetch(u, { method: "PATCH", credentials: "same-origin", headers: { "Content-Type": "application/json", "Accept": "application/json" }, body: JSON.stringify(body) })
      .then(function (r) {
        if (r.status === 401 || r.status === 403) { App.toast("权限不足（" + r.status + "）", "err"); throw new Error("HTTP " + r.status); }
        if (!r.ok) return r.text().then(function (t) { throw new Error(t || ("HTTP " + r.status)); });
        var ct = r.headers.get("content-type") || "";
        if (ct.indexOf("application/json") >= 0) return r.json();
        return r.text();
      });
  };

  /* ---------------- toast（轻提示） ---------------- */
  (function () {
    var zone = null;
    function ensure() {
      if (!zone) {
        zone = document.getElementById("toast-zone");
        if (!zone) {
          zone = document.createElement("div");
          zone.id = "toast-zone";
          document.body.appendChild(zone);
        }
      }
      return zone;
    }
    App.toast = function (msg, type) {
      var z = ensure();
      var t = document.createElement("div");
      t.className = "toast " + (type === "ok" ? "ok" : type === "err" ? "err" : "");
      t.textContent = msg;
      z.appendChild(t);
      window.setTimeout(function () {
        if (t.parentNode) t.parentNode.removeChild(t);
      }, 4200);
    };
  })();

  /* 全局错误横幅（#errbanner 常驻于 index.html 主区顶部） */
  App.showError = function (msg) {
    var b = document.getElementById("errbanner");
    if (b) {
      b.textContent = msg;
      b.style.display = "block";
    }
  };
  App.clearError = function () {
    var b = document.getElementById("errbanner");
    if (b) { b.textContent = ""; b.style.display = "none"; }
  };

  /* ---------------- 通用渲染（卡片 / 表格 / 徽章） ---------------- */

  /* 徽章：FAIL -> 红，PASS 类 -> 绿，其余灰 */
  App.badge = function (text, kind) {
    var cls = kind === "fail" ? " badge fail" : kind === "pass" ? " badge pass" : "";
    return '<span class="badge' + cls + '">' + App.esc(text || "—") + '</span>';
  };

  /* 表格：ths 数组 / rows 二维数组 / rowCls 可选行类函数 */
  App.table = function (ths, rows, rowCls) {
    var head = '<thead><tr>';
    for (var i = 0; i < ths.length; i++) head += "<th>" + App.esc(ths[i]) + "</th>";
    head += "</tr></thead>";
    var body = "<tbody>";
    for (var r = 0; r < rows.length; r++) {
      var cls = rowCls ? rowCls(rows[r], r) : "";
      body += "<tr" + (cls ? ' class="' + cls + '"' : "") + ">";
      for (var c = 0; c < rows[r].length; c++) body += "<td>" + rows[r][c] + "</td>";
      body += "</tr>";
    }
    body += "</tbody>";
    return "<table>" + head + body + "</table>";
  };

  /* 机台小卡（总览 / 设备共用形态；link 可选点击回调，compact 紧凑密度） */
  App.machineCard = function (x, opts) {
    opts = opts || {};
    var name = App.pick(x, "machine") || "—";
    var fc = App.pick(x, "failCount") || 0;
    var on = !!App.pick(x, "online");
    var cur = opts.sel && opts.sel === name;
    var ops = opts.ops ? '<div class="m-ops">' + opts.ops + "</div>" : "";
    var extra = opts.extra || [];
    var html = '<div class="machine' + (cur ? " cur" : "") + '" draggable="' + (opts.draggable ? "true" : "false") + '" data-m="' + App.esc(name) + '">' + ops
      + '<div class="row1"><span class="dot ' + (on ? "ok" : "err") + '"></span>'
      + '<span class="name">' + App.esc(name) + "</span></div>"
      + '<div class="st">' + (App.pick(x, "isSelf") ? "本机 · " : "") + (on ? "在线" : "离线") + "</div>"
      + '<div class="fc ' + (fc > 0 ? "hot" : fc === 0 ? "zero" : "") + '">' + fc + '</div><div class="st">累计 FAIL</div>';
    for (var i = 0; i < extra.length; i++) html += extra[i];
    return html + "</div>";
  };

  /* ---------------- Lite-Settings: 权限/布局/收藏/全局搜索 ---------------- */
  App.auth = { who: "", role: "viewer", token: "", loaded: false };
  App.auth.isViewer = function () { return App.auth.role === "viewer"; };
  App.auth.canWrite = function () { return App.auth.role === "engineer" || App.auth.role === "admin"; };
  App.auth.isAdmin = function () { return App.auth.role === "admin"; };
  App.auth.refresh = function () {
    return App.fetchJSON("/api/status").then(function (st) {
      App.auth.who = st.who || st.Who || "";
      App.auth.role = (st.role || st.Role || "viewer").toLowerCase();
      if (App.auth.role !== "viewer" && App.auth.role !== "engineer" && App.auth.role !== "admin") App.auth.role = "viewer";
      App.auth.loaded = true;
      // 同步尝试 /api/users/me 细化（layout/favorites 拥有者）
      return App.fetchJSON("/api/users/me").then(function (me) {
        if (me && me.role) App.auth.role = String(me.role).toLowerCase();
        if (me && me.name) App.auth.who = me.name;
        applyRoleClass();
        return me;
      }).catch(function () { applyRoleClass(); return st; });
    }).catch(function () {
      // 未鉴权/宽松模式仍视为 admin（保持向后兼容）
      App.auth.role = "admin"; App.auth.who = "anonymous"; App.auth.loaded = true;
      applyRoleClass();
    });
  };
  function applyRoleClass() {
    var b = document.body;
    b.classList.remove("role-viewer", "role-engineer", "role-admin");
    b.classList.add("role-" + App.auth.role);
    var rb = document.getElementById("roleBadge");
    if (rb) {
      var zh = App.auth.role === "admin" ? "管理员" : App.auth.role === "engineer" ? "工程师" : "访客";
      rb.textContent = zh + (App.auth.who ? " · " + App.auth.who : "");
      rb.className = "badge role-badge " + App.auth.role;
      rb.style.display = "";
    }
    var hb = document.getElementById("htext");
    if (hb && App.auth.who) hb.textContent = App.auth.who + "（" + App.auth.role + "）";
  }
  App.applyRoleVisibility = function (root) {
    root = root || document;
    var nodes = root.querySelectorAll("[data-require-role]");
    for (var i = 0; i < nodes.length; i++) {
      var need = nodes[i].getAttribute("data-require-role");
      var ok = need === "viewer" || (need === "engineer" && App.auth.canWrite()) || (need === "admin" && App.auth.isAdmin());
      nodes[i].style.display = ok ? "" : "none";
    }
    var hides = root.querySelectorAll(".hide-for-viewer");
    for (var j = 0; j < hides.length; j++) hides[j].style.display = App.auth.isViewer() ? "none" : "";
  };

  // 布局持久化（users.layout）：前端 localStorage + 后端同步双写
  App.layout = {
    key: "agg.layout",
    get: function () {
      try { var raw = window.localStorage.getItem(App.layout.key); return raw ? JSON.parse(raw) : {}; } catch (e) { return {}; }
    },
    set: function (obj) {
      try { window.localStorage.setItem(App.layout.key, JSON.stringify(obj || {})); } catch (e) {}
      // 后端同步（静默，失败不阻断）
      if (App.auth.loaded && App.auth.who && App.auth.who !== "anonymous" && App.auth.who !== "agg_token") {
        try { App.patchJSON("/api/users/me/layout", { layout: JSON.stringify(obj || {}) }).catch(function () {}); } catch (e) {}
      }
    },
    loadFromServer: function () {
      return App.fetchJSON("/api/users/me/layout").then(function (r) {
        var s = r.layout || r.Layout;
        if (!s) return null;
        try {
          var obj = typeof s === "string" ? JSON.parse(s) : s;
          try { window.localStorage.setItem(App.layout.key, JSON.stringify(obj)); } catch (e) {}
          return obj;
        } catch (e) { return null; }
      }).catch(function () { return null; });
    }
  };
  App.favorites = {
    key: "agg.favorites",
    get: function () {
      try { var raw = window.localStorage.getItem(App.favorites.key); return raw ? JSON.parse(raw) : []; } catch (e) { return []; }
    },
    set: function (arr) {
      try { window.localStorage.setItem(App.favorites.key, JSON.stringify(arr || [])); } catch (e) {}
      if (App.auth.loaded && App.auth.who && App.auth.who !== "anonymous" && App.auth.who !== "agg_token") {
        try { App.patchJSON("/api/users/me/favorites", { favorites: JSON.stringify(arr || []) }).catch(function () {}); } catch (e) {}
      }
    },
    add: function (item) {
      var a = App.favorites.get();
      if (a.indexOf(item) < 0) { a.push(item); App.favorites.set(a); }
    },
    remove: function (item) {
      var a = App.favorites.get().filter(function (x) { return x !== item; });
      App.favorites.set(a);
    },
    loadFromServer: function () {
      return App.fetchJSON("/api/users/me/favorites").then(function (r) {
        var s = r.favorites || r.Favorites;
        if (!s) return null;
        try {
          var arr = typeof s === "string" ? JSON.parse(s) : s;
          if (Array.isArray(arr)) { try { window.localStorage.setItem(App.favorites.key, JSON.stringify(arr)); } catch (e) {} return arr; }
        } catch (e) {}
        return null;
      }).catch(function () { return null; });
    }
  };

  // 全局搜索（空格分词，聚合 FAIL/维修/设备/良率，跳转 hash 联动下钻）
  App.search = {
    query: function (q, limit) {
      if (!q || !q.trim()) return Promise.resolve({ ok: true, q: "", tokens: [], total: 0, results: {}, flat: [] });
      return App.fetchJSON("/api/search?q=" + encodeURIComponent(q) + "&limit=" + (limit || 8));
    },
    go: function (link) {
      if (!link) return;
      var hash = link.split("#")[1] || link;
      if (hash.indexOf("#/") === 0) {
        window.location.hash = hash;
        if (App.Nav) App.Nav.apply(hash.replace(/^#\//, "").split("?")[0].split("&")[0]);
      } else if (link.indexOf("#/") === 0) {
        window.location.hash = link;
      } else {
        window.location.hash = link;
      }
      // 关闭搜索面板
      var p = document.getElementById("searchPanel");
      if (p) p.hidden = true;
    }
  };

  /* ---------------- 共享数据状态 ---------------- */
  App.state = {
    machines: [],     // /api/machines
    fails: [],        // /api/fails?limit=100（ingest 倒序）
    failCount: 0,     // /api/fails/count
    health: null,     // /api/health
    sel: ""           // 机台筛选："" 全部机台；非空指定机台
  };

  var lastSig = "";   // 数据指纹：数据未变时跳过重渲染（3s 轮询大部分周期无变化）

  function dataSig() {
    var s = App.state;
    return JSON.stringify(s.machines) + "|" + JSON.stringify(s.fails) + "|" + s.failCount;
  }

  function setHealth(ok, text) {
    var d = document.getElementById("hdot");
    var t = document.getElementById("htext");
    if (d) d.className = "dot " + (ok ? "ok" : "err");
    if (t) t.textContent = text || (ok ? "正常" : "接口异常");
  }

  /* 机台选择器：选项 = 在线机台 ∪ FAIL 明细中出现过的机台（保留当前选中） */
  function fillMachineSel() {
    var s = document.getElementById("machineSel");
    if (!s) return;
    var cur = s.value;
    var set = [];
    var seen = {};
    function add(n) {
      if (n && !seen[n]) { seen[n] = true; set.push(n); }
    }
    App.state.machines.forEach(function (m) { add(App.pick(m, "machine")); });
    App.state.fails.forEach(function (f) { add(App.pick(f, "machine")); });
    set.sort(function (a, b) { return a.localeCompare(b, "zh"); });
    if (s.options.length !== set.length + 1 || (s.options[1] && s.options[1].value !== set[0])) {
      var html = '<option value="">全部机台（汇总）</option>';
      for (var i = 0; i < set.length; i++) html += '<option value="' + App.esc(set[i]) + '">' + App.esc(set[i]) + "</option>";
      s.innerHTML = html;
    }
    if (set.indexOf(cur) >= 0) s.value = cur; else s.value = App.state.sel;
  }

  /* 数据加载：并行拉四个接口，更新状态并触发当前页重绘 */
  App.data = {
    load: function () {
      var s = App.state;
      var ms = s.sel ? "&machine=" + encodeURIComponent(s.sel) : "";
      return Promise.all([
        App.fetchJSON("/api/machines"),
        App.fetchJSON("/api/fails?limit=100" + ms),
        App.fetchJSON("/api/fails/count" + ms),
        App.fetchJSON("/api/health")
      ]).then(function (rs) {
        s.machines = rs[0] || [];
        s.fails = rs[1] || [];
        s.failCount = (rs[2] && rs[2].count) || 0;
        s.health = rs[3] || null;
        fillMachineSel();
        setHealth(true);
        App.clearError();
        var sig = dataSig();
        if (sig !== lastSig) {           // 数据未变 → 跳过全部重绘/重排（主要运行时优化）
          lastSig = sig;
          if (App.Nav && App.Nav.rerender) App.Nav.rerender();
        }
      }).catch(function (e) {
        setHealth(false);
        App.showError("数据加载失败：" + e.message + "（请确认聚合服务已启动）");
      }).then(function () {
        var u = document.getElementById("uptime");
        if (u) u.textContent = new Date().toLocaleTimeString("zh-CN", { hour12: false });
      });
    },
    /* 机台筛选入口：由页面/顶栏调用后重拉数据 */
    filter: function (sel) {
      App.state.sel = sel || "";
      try { window.localStorage.setItem("agg.machine", App.state.sel); } catch (e) { }
      App.clearError();
      App.data.load();
    },
    /* 各机台最近一条 FAIL 时间映射（fails 按 ingest 倒序） */
    recentMap: function () {
      var recent = {};
      App.state.fails.forEach(function (f) {
        var m = App.pick(f, "machine");
        if (m && !recent[m]) recent[m] = App.pick(f, "ingestTs") || App.pick(f, "ts") || "";
      });
      return recent;
    },
    /* 导出 CSV 地址（含机台过滤与 token） */
    exportUrl: function (machine) {
      var u = "/api/export.csv";
      var q = [];
      if (machine) q.push("machine=" + encodeURIComponent(machine));
      if (q.length) u += "?" + q.join("&");
      return App.withToken(u);
    },
    /* XML 报告页地址（在线查看，服务端渲染 HTML；token 自动拼接） */
    xmlUrl: function (id) { return App.withToken("/api/xmlview?id=" + id); },
    fileUrl: function (id) { return App.withToken("/api/file?id=" + id); }
  };

  /* ---------------- canvas 柱状图（零依赖，迁自 demo） ---------------- */
  App.drawBar = function (cv, data) {
    if (!cv || !cv.getContext) return;
    var dpr = window.devicePixelRatio || 1;
    var w = cv.clientWidth || 300, h = 120;
    cv.width = w * dpr; cv.height = h * dpr;
    var g = cv.getContext("2d");
    g.scale(dpr, dpr); g.clearRect(0, 0, w, h);
    var rows = (data || []).map(function (x) {
      return { name: App.pick(x, "machine") || "—", v: App.pick(x, "failCount") || 0 };
    }).sort(function (a, b) { return b.v - a.v; }).slice(0, 12);
    if (!rows.length) {
      g.fillStyle = "#5E6874"; g.font = "12px sans-serif";
      g.fillText("暂无数据", 8, 16);
      return;
    }
    var max = 1;
    for (var i = 0; i < rows.length; i++) if (rows[i].v > max) max = rows[i].v;
    var bw = Math.min(36, (w - 40) / rows.length - 6);
    rows.forEach(function (d, i) {
      var bh = Math.max(2, (h - 26) * d.v / max);
      var x = 20 + i * (bw + 6), y = h - 18 - bh;
      g.fillStyle = d.v > 0 ? "#FF5C5C" : "#9AA5B1";
      g.fillRect(x, y, bw, bh);
      g.fillStyle = "#9AA5B1"; g.font = "10px sans-serif";
      g.fillText(String(d.v), x + 2, y - 2);
      g.save(); g.translate(x + bw / 2, h - 4); g.rotate(-Math.PI / 5);
      g.fillText(d.name.length > 8 ? d.name.slice(0, 7) + "…" : d.name, 0, 0);
      g.restore();
    });
  };

  /* ---------------- 全局搜索 UI（顶栏） ---------------- */
  function initGlobalSearch() {
    var inp = document.getElementById("globalSearch");
    var panel = document.getElementById("searchPanel");
    var wrap = document.getElementById("searchWrap");
    if (!inp || !panel) return;
    var timer = null;
    function hide() { panel.hidden = true; panel.innerHTML = ""; }
    function renderGroups(data) {
      if (!data || !data.results) { hide(); return; }
      var r = data.results;
      var counts = data.counts || {};
      var html = '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:6px"><span style="font-size:11px;color:var(--dim)">共 ' + data.total + ' 条 · FAIL ' + (counts.fails||0) + ' · 维修 ' + (counts.maintenance||0) + ' · 设备 ' + (counts.devices||0) + ' · 良率 ' + (counts.yields||0) + '</span><span style="cursor:pointer;color:var(--dim)" id="searchClose">✕</span></div>';
      var groups = [
        { key: "fails", label: "FAIL 明细", icon: "⚠" },
        { key: "maintenance", label: "维修", icon: "🛠" },
        { key: "devices", label: "设备", icon: "▭" },
        { key: "yields", label: "良率", icon: "◈" }
      ];
      var has = false;
      for (var gi = 0; gi < groups.length; gi++) {
        var g = groups[gi];
        var list = r[g.key] || [];
        if (!list.length) continue;
        has = true;
        html += '<div class="search-group"><h4><span>' + g.icon + " " + g.label + '</span><span class="badge">' + list.length + '</span></h4>';
        for (var i = 0; i < list.length; i++) {
          var it = list[i];
          var title = App.esc(it.title || it.Title || "—");
          var sub = App.esc(it.subtitle || it.Subtitle || "");
          var badge = it.type || g.key;
          html += '<div class="search-item" data-link="' + App.esc(it.link || it.Link || "") + '"><span class="badge">' + App.esc(badge) + '</span><span class="t">' + title + '</span><span class="s">' + sub + '</span></div>';
        }
        html += '</div>';
      }
      if (!has) html += '<div class="empty">无匹配结果</div>';
      // 收藏快捷
      var favs = App.favorites.get();
      if (favs.length) {
        html += '<div class="search-group"><h4>收藏筛选</h4>';
        for (var fi = 0; fi < favs.length; fi++) {
          var fv = favs[fi];
          html += '<div class="search-item" data-q="' + App.esc(fv) + '"><span class="badge">★</span><span class="t">' + App.esc(fv) + '</span><span class="s">点击填入搜索</span></div>';
        }
        html += '</div>';
      }
      panel.innerHTML = html;
      panel.hidden = false;
      var close = panel.querySelector("#searchClose");
      if (close) close.addEventListener("click", hide);
      var items = panel.querySelectorAll(".search-item[data-link]");
      for (var k = 0; k < items.length; k++) {
        items[k].addEventListener("click", function () { App.search.go(this.getAttribute("data-link")); inp.value = ""; hide(); });
      }
      var qitems = panel.querySelectorAll(".search-item[data-q]");
      for (var qk = 0; qk < qitems.length; qk++) {
        qitems[qk].addEventListener("click", function () {
          inp.value = this.getAttribute("data-q");
          doSearch(inp.value);
        });
      }
    }
    function doSearch(q) {
      if (!q || !q.trim()) { hide(); return; }
      App.search.query(q).then(function (data) { renderGroups(data); }).catch(function () { panel.innerHTML = '<div class="empty">搜索失败</div>'; panel.hidden = false; });
    }
    inp.addEventListener("input", function () {
      var v = inp.value;
      if (timer) clearTimeout(timer);
      if (!v.trim()) { hide(); return; }
      timer = setTimeout(function () { doSearch(v); }, 280);
    });
    inp.addEventListener("keydown", function (e) {
      if (e.key === "Enter") { e.preventDefault(); var v = inp.value.trim(); if (v) { doSearch(v); } }
      if (e.key === "Escape") { hide(); inp.blur(); }
    });
    inp.addEventListener("focus", function () { if (inp.value.trim()) doSearch(inp.value); });
    document.addEventListener("click", function (e) {
      if (!wrap.contains(e.target)) hide();
    });
    // KPI 下钻联动：监听 KPI 点击（overview 广播事件）
    window.addEventListener("argus:drill", function (ev) {
      var d = ev.detail || {};
      if (d.q) { inp.value = d.q; doSearch(d.q); }
      if (d.link) App.search.go(d.link);
    });
  }

  /* ---------------- 统一渲染组件 ---------------- */

  App.renderPanel = function (title, body, opts) {
    opts = opts || {};
    var ops = opts.ops || "";
    var accent = opts.accent ? " accent-" + App.esc(opts.accent) : "";
    var h = title ? '<div class="ph"><h2>' + App.esc(title) + '</h2><div class="ops">' + ops + '</div></div>' : "";
    return '<div class="panel' + accent + '">' + h + '<div class="pb">' + (body || "") + '</div></div>';
  };

  App.renderToolbar = function (items, sticky) {
    var html = '<div class="toolbar' + (sticky ? ' sticky' : '') + '">';
    for (var i = 0; i < items.length; i++) {
      var it = items[i];
      var dr = it.id ? ' data-ref="' + App.esc(it.id) + '"' : '';
      if (it.type === "input") {
        html += '<input type="' + (it.kind || "text") + '" placeholder="' + App.esc(it.placeholder || "") + '" value="' + App.esc(it.value || "") + '" id="' + App.esc(it.id || "") + '"' + dr + (it.width ? ' style="width:' + it.width + '"' : '') + '>';
      } else if (it.type === "select") {
        html += '<select id="' + App.esc(it.id || "") + '"' + dr + '>' + (it.options || "") + '</select>';
      } else if (it.type === "btn") {
        var cls = "btn " + (it.cls || "btn-secondary");
        html += '<button class="' + cls + '" id="' + App.esc(it.id || "") + '"' + dr + (it.disabled ? ' disabled' : '') + '>' + App.esc(it.label) + '</button>';
      } else if (it.type === "seg") {
        html += '<div class="seg" data-name="' + App.esc(it.name || "") + '"' + dr + '>';
        for (var j = 0; j < it.items.length; j++) {
          html += '<button class="seg-item' + (j === it.active ? ' active' : '') + '" data-idx="' + j + '">' + App.esc(it.items[j]) + '</button>';
        }
        html += '</div>';
      } else if (it.type === "sep") {
        html += '<div class="sep"></div>';
      }
    }
    html += '</div>';
    return html;
  };

  App.renderTable = function (headers, rows, opts) {
    opts = opts || {};
    var emptyText = opts.emptyText || "暂无数据";
    var cls = opts.cls ? " " + opts.cls : "";
    var h = '<thead><tr>';
    for (var i = 0; i < headers.length; i++) h += "<th>" + App.esc(headers[i]) + "</th>";
    h += "</tr></thead>";
    if (!rows.length) {
      h += '<tbody><tr class="empty-row"><td colspan="' + headers.length + '">' + App.esc(emptyText) + "</td></tr></tbody>";
    } else {
      h += "<tbody>";
      for (var r = 0; r < rows.length; r++) {
        var rc = opts.rowCls ? opts.rowCls(rows[r], r) : "";
        h += "<tr" + (rc ? ' class="' + rc + '"' : "") + ">";
        for (var c = 0; c < rows[r].length; c++) h += "<td>" + (rows[r][c] || "") + "</td>";
        h += "</tr>";
      }
      h += "</tbody>";
    }
    return '<div class="table-wrap' + cls + '"><table>' + h + "</table></div>";
  };

  App.renderEmpty = function (icon, text, action) {
    var html = '<div class="empty-state"><span class="es-icon">' + App.esc(icon || "📋") + '</span>'
      + '<div class="es-text">' + App.esc(text || "暂无数据") + '</div>';
    if (action) html += '<div class="es-hint">' + App.esc(action.hint || "") + '</div>' + (action.btn ? '<button class="btn btn-secondary" id="' + App.esc(action.btnId || "") + '">' + App.esc(action.btn) + '</button>' : '');
    html += "</div>";
    return html;
  };

  App.renderSeg = function (name, items, activeIdx, onChange) {
    var html = '<div class="seg" data-name="' + App.esc(name || "") + '">';
    for (var i = 0; i < items.length; i++) {
      html += '<button class="seg-item' + (i === activeIdx ? ' active' : '') + '" data-idx="' + i + '">' + App.esc(items[i]) + '</button>';
    }
    html += "</div>";
    return html;
  };

  /* ---------------- boot：顶栏事件 + 轮询 ---------------- */
  App.boot = function () {
    var sel = document.getElementById("machineSel");
    if (sel) {
      var saved = "";
      try { saved = window.localStorage.getItem("agg.machine") || ""; } catch (e) { }
      App.state.sel = saved;
      sel.addEventListener("change", function (e) { App.data.filter(e.target.value); });
    }
    // 权限与布局/收藏后端同步
    App.auth.refresh().then(function () {
      // 登录后拉取 server layout/favorites 覆盖本地（若 server 有值）
      App.layout.loadFromServer().catch(function () {});
      App.favorites.loadFromServer().catch(function () {});
      App.applyRoleVisibility(document);
    });
    initGlobalSearch();
    App.data.load();
    window.setInterval(App.data.load, 3000);   // 3s 轮询（与 demo 一致）
    window.addEventListener("resize", function () {
      var h = (window.location.hash || "#/overview").replace(/^#\/?/, "");
      if (h === "overview" && App.Nav) App.Nav.rerender();
    });
  };
})(window, document);
