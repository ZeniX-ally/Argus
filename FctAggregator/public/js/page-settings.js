/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 设置页（Lite-Settings 完整版）
   public/js/page-settings.js
   能力：
   - 用户管理 UI（GET/POST/DELETE /api/users，角色 viewer/engineer/admin 分级 badge，创建返回 token，删除二次确认）
   - 当前登录身份显示（/api/users/me + /api/status），token 透传（App.withToken / Cookie）
   - 布局持久化 users.layout（卡片拖拽排列 JSON）与收藏 users.favorites（常用筛选）前端存取 API（GET/PATCH /api/users/me/layout 等，localStorage+后端双写）
   - 视觉黑白红、零依赖、零 CDN。
   ============================================================ */
(function (window) {
  "use strict";
  var App = window.App;

  function esc(s) { return App.esc(s); }
  function roleZh(r) { r = String(r || "").toLowerCase(); if (r === "admin") return "管理员"; if (r === "engineer") return "工程师"; return "访客"; }
  function roleBadge(role) {
    var cls = role === "admin" ? "admin" : role === "engineer" ? "engineer" : "viewer";
    return '<span class="role-badge ' + cls + '">' + esc(roleZh(role)) + ' · ' + esc(role) + '</span>';
  }

  var refs = {};
  var usersCache = [];
  var meCache = null;

  function apiGet(path) {
    var u = window.location.origin + path;
    if (App.token) u = App.withToken(u);
    return fetch(u, { credentials: "same-origin", headers: { Accept: "application/json" } }).then(function (r) {
      if (r.status === 401 || r.status === 403) throw new Error("HTTP " + r.status);
      if (!r.ok) throw new Error("HTTP " + r.status);
      return r.json();
    });
  }
  function apiSend(method, path, body) {
    var u = window.location.origin + path;
    if (App.token) u = App.withToken(u);
    var opts = { method: method, credentials: "same-origin", headers: { Accept: "application/json", "Content-Type": "application/json" } };
    if (body) opts.body = JSON.stringify(body);
    return fetch(u, opts).then(function (r) {
      if (r.status === 401 || r.status === 403) throw new Error("HTTP " + r.status);
      if (!r.ok) return r.text().then(function (t) { throw new Error(t || ("HTTP " + r.status)); });
      var ct = r.headers.get("content-type") || "";
      if (ct.indexOf("application/json") >= 0) return r.json();
      return r.text();
    });
  }

  function loadAll() {
    return Promise.all([
      apiGet("/api/users/me").catch(function (e) { return { error: e.message }; }),
      apiGet("/api/status").catch(function () { return {}; }),
      apiGet("/api/users").catch(function () { return []; }),
      apiGet("/api/users/me/layout").catch(function () { return { layout: null }; }),
      apiGet("/api/users/me/favorites").catch(function () { return { favorites: null }; })
    ]).then(function (rs) {
      meCache = rs[0];
      var status = rs[1] || {};
      var users = Array.isArray(rs[2]) ? rs[2] : [];
      usersCache = users;
      var lay = rs[3] || {}; var fav = rs[4] || {};
      return { me: meCache, status: status, users: users, layout: lay.layout || lay.Layout || null, favorites: fav.favorites || fav.Favorites || null };
    });
  }

  function renderMe(data) {
    if (!refs.meBox) return;
    var me = data.me || {};
    var st = data.status || {};
    if (me.error) {
      refs.meBox.innerHTML = '<div class="hint">当前身份：未登录或 token 无效（' + esc(me.error) + '）· 请用 <code>?token=</code> 打开或先创建用户后登录。' + '<br>提示：未配置 agg_token 且无用户时为宽松模式（admin/anonymous）。</div>';
      return;
    }
    var who = me.name || me.Name || st.who || st.Who || "—";
    var role = (me.role || me.Role || st.role || st.Role || "viewer").toLowerCase();
    var token = me.token || me.Token || "";
    var html = '<div class="card"><h2>当前登录 <span class="n">token 透传 · Cookie / ?token= / X-Agg-Token 三通道</span></h2>';
    html += '<div style="display:flex;flex-wrap:wrap;gap:10px;align-items:center">'
      + roleBadge(role)
      + '<span>用户 <b>' + esc(who) + '</b></span>'
      + '<span class="badge">角色 ' + esc(role) + '</span>'
      + (token ? '<span class="badge" title="静态令牌 32hex，点击复制" style="cursor:pointer" data-copy="' + esc(token) + '">token ' + esc(token.slice(0, 8)) + '…</span>' : '<span class="badge">无 token（宽松/agg_token）</span>')
      + '</div>';
    // token 透传说明 + 复制
    html += '<div class="hint" style="margin-top:10px">当前页面所有 API 已自动透传 token（Cookie/HttpOnly + ?token= 兜底）。创建用户后请复制其 token 并以 <code>?token=&lt;token&gt;</code> 打开新页或重新登录。</div>';
    if (st.version || st.Version) html += '<div class="st">服务版本 ' + esc(st.version || st.Version) + ' · 在线 ' + esc(String(st.machines_online ?? st.machinesOnline ?? "")) + '/' + esc(String(st.machines_total ?? "")) + ' · FAIL ' + esc(String(st.fail_total ?? "")) + '</div>';
    html += '</div>';
    refs.meBox.innerHTML = html;
    var cp = refs.meBox.querySelector("[data-copy]");
    if (cp) cp.addEventListener("click", function () {
      var t = cp.getAttribute("data-copy");
      try { navigator.clipboard.writeText(t).then(function () { App.toast("已复制 token", "ok"); }).catch(function () { prompt("复制 token", t); }); } catch (e) { prompt("复制 token", t); }
    });
  }

  function renderUsers(data) {
    if (!refs.userTable) return;
    var users = data.users || [];
    var meName = (data.me && (data.me.name || data.me.Name) || "").toLowerCase();
    // 权限：viewer 隐藏新增/删除
    var canManage = App.auth && App.auth.isAdmin && App.auth.isAdmin();
    if (refs.btnAddUser) refs.btnAddUser.style.display = canManage ? "" : "none";
    if (!users.length) {
      refs.userTable.innerHTML = '<div class="empty">暂无用户（宽松模式或仅 agg_token）</div>';
      return;
    }
    var html = '<table class="user-table"><thead><tr><th>用户名</th><th>角色</th><th>token（8位）</th><th>创建时间</th><th>操作</th></tr></thead><tbody>';
    for (var i = 0; i < users.length; i++) {
      var u = users[i];
      var name = u.Name || u.name || "";
      var role = (u.Role || u.role || "viewer").toLowerCase();
      var token = u.Token || u.token || "";
      var created = u.CreatedAt || u.createdAt || "";
      var isMe = name.toLowerCase() === meName;
      html += '<tr' + (isMe ? ' style="background:var(--bg2)"' : "") + '>'
        + '<td>' + esc(name) + (isMe ? ' <span class="badge pass">当前</span>' : "") + '</td>'
        + '<td>' + roleBadge(role) + '</td>'
        + '<td><span title="' + esc(token) + '" style="cursor:pointer" data-copy="' + esc(token) + '">' + esc(token ? token.slice(0, 8) + "…" : "—") + '</span></td>'
        + '<td>' + esc(created) + '</td>'
        + '<td>' + (canManage ? '<button class="mf-btn small hide-for-viewer" data-del="' + esc(name) + '">删除</button>' : '<span class="badge">只读</span>') + '</td>'
        + '</tr>';
    }
    html += '</tbody></table>';
    html += '<div class="hint" style="margin-top:8px">角色分级：访客 viewer（只读）· 工程师 engineer（可登记/删除/导出）· 管理员 admin（可管理用户/审计）。后端 403 已生效，前端按角色隐藏按钮。</div>';
    refs.userTable.innerHTML = html;
    // 复制
    var cps = refs.userTable.querySelectorAll("[data-copy]");
    for (var ci = 0; ci < cps.length; ci++) {
      cps[ci].addEventListener("click", function () {
        var t = this.getAttribute("data-copy");
        if (!t) return;
        try { navigator.clipboard.writeText(t).then(function () { App.toast("已复制 token", "ok"); }).catch(function () { prompt("复制", t); }); } catch (e) { prompt("复制", t); }
      });
    }
    // 删除二次确认
    var dels = refs.userTable.querySelectorAll("[data-del]");
    for (var di = 0; di < dels.length; di++) {
      dels[di].addEventListener("click", function () {
        var name = this.getAttribute("data-del");
        if (!name) return;
        if (name.toLowerCase() === meName) { if (!confirm("确定删除当前登录用户 「" + name + "」？删除后当前 token 将失效，需重新登录。")) return; }
        else { if (!confirm("确定删除用户 「" + name + "」？此操作不可撤销。")) return; }
        if (!confirm("二次确认：真的要删除 「" + name + "」 吗？")) return;
        apiSend("DELETE", "/api/users?name=" + encodeURIComponent(name)).then(function () {
          App.toast("已删除用户 " + name, "ok");
          refresh();
        }).catch(function (e) { App.toast("删除失败：" + e.message, "err"); });
      });
    }
    if (App.applyRoleVisibility) App.applyRoleVisibility(refs.userTable);
  }

  function renderLayoutFav(data) {
    if (refs.layoutBox) {
      var lay = data.layout;
      var fav = data.favorites;
      // 解析展示
      var layObj = null, favArr = null;
      try { layObj = lay ? (typeof lay === "string" ? JSON.parse(lay) : lay) : null; } catch (e) { layObj = lay; }
      try { favArr = fav ? (typeof fav === "string" ? JSON.parse(fav) : fav) : null; } catch (e) { favArr = fav; }
      var html = '<div class="card"><h2>布局持久化 <span class="n">users.layout · 卡片拖拽排列 · localStorage + 后端双写</span></h2>';
      html += '<div class="hint">总览机台卡拖拽顺序已自动保存。下方为当前账号的 layout 原始 JSON（可编辑后保存，将覆盖拖拽结果）。</div>';
      html += '<textarea id="layoutEdit" rows="4" style="width:100%;background:var(--bg2);border:1px solid var(--line);border-radius:8px;color:var(--ink);padding:8px;font-family:Consolas,monospace;font-size:12px">' + esc(lay ? (typeof lay === "string" ? lay : JSON.stringify(lay, null, 2)) : "") + '</textarea>';
      html += '<div style="margin-top:8px;display:flex;gap:8px"><button class="mf-btn primary" id="btnSaveLayout">保存 layout</button><button class="mf-btn" id="btnReloadLayout">从后端重载</button><button class="mf-btn" id="btnClearLayout">清空（本地+后端）</button> <span class="badge">本地键 agg.layout</span></div>';
      html += '</div>';
      html += '<div class="card"><h2>收藏 <span class="n">users.favorites · 常用筛选 · 空格分词</span></h2>';
      html += '<div class="hint">收藏常用搜索/筛选（如 "FCT7 5V_Rail"），全局搜索面板可一键填入。</div>';
      // chips
      var favList = Array.isArray(favArr) ? favArr : (Array.isArray(App.favorites.get()) ? App.favorites.get() : []);
      // 若后端有值，以后端为准同步本地
      if (Array.isArray(favArr) && favArr.length) { try { window.localStorage.setItem("agg.favorites", JSON.stringify(favArr)); } catch (e) {} favList = favArr; }
      html += '<div id="favChips" style="margin-bottom:8px">';
      if (favList.length) {
        for (var i = 0; i < favList.length; i++) html += '<span class="fav-chip">' + esc(favList[i]) + ' <span class="x" data-fav-del="' + esc(favList[i]) + '">✕</span></span>';
      } else html += '<span class="empty">暂无收藏</span>';
      html += '</div>';
      html += '<div style="display:flex;gap:8px"><input id="favInput" placeholder="输入收藏筛选，如 FCT7 5V_Rail（空格分词）" style="flex:1;height:34px;padding:0 10px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"><button class="mf-btn primary" id="btnAddFav">添加收藏</button><button class="mf-btn" id="btnSaveFav">保存到后端</button></div>';
      html += '<textarea id="favEdit" rows="3" style="width:100%;margin-top:8px;background:var(--bg2);border:1px solid var(--line);border-radius:8px;color:var(--ink);padding:8px;font-family:Consolas,monospace;font-size:12px">' + esc(fav ? (typeof fav === "string" ? fav : JSON.stringify(fav, null, 2)) : JSON.stringify(favList, null, 2)) + '</textarea>';
      html += '</div>';
      refs.layoutBox.innerHTML = html;
      // events layout
      var btnSave = document.getElementById("btnSaveLayout");
      if (btnSave) btnSave.addEventListener("click", function () {
        var v = document.getElementById("layoutEdit").value.trim();
        // 校验 JSON
        if (v) { try { JSON.parse(v); } catch (e) { App.toast("JSON 非法：" + e.message, "err"); return; } }
        // 双写
        try { window.localStorage.setItem("agg.layout", v ? JSON.stringify(JSON.parse(v)) : "{}"); } catch (e) {}
        apiSend("PATCH", "/api/users/me/layout", { layout: v }).then(function () { App.toast("layout 已保存（本地+后端）", "ok"); }).catch(function (e) { App.toast("后端保存失败（仅本地已存）：" + e.message, "err"); });
      });
      var btnReload = document.getElementById("btnReloadLayout");
      if (btnReload) btnReload.addEventListener("click", function () {
        apiGet("/api/users/me/layout").then(function (r) {
          var lay2 = r.layout || r.Layout || "";
          document.getElementById("layoutEdit").value = lay2 ? (typeof lay2 === "string" ? lay2 : JSON.stringify(lay2, null, 2)) : "";
          if (lay2) { try { var obj = typeof lay2 === "string" ? JSON.parse(lay2) : lay2; window.localStorage.setItem("agg.layout", JSON.stringify(obj)); } catch (e) {} }
          App.toast("已从后端重载", "ok");
        }).catch(function (e) { App.toast("重载失败：" + e.message, "err"); });
      });
      var btnClear = document.getElementById("btnClearLayout");
      if (btnClear) btnClear.addEventListener("click", function () {
        if (!confirm("清空布局？将重置拖拽顺序。")) return;
        try { window.localStorage.setItem("agg.layout", "{}"); } catch (e) {}
        document.getElementById("layoutEdit").value = "";
        apiSend("PATCH", "/api/users/me/layout", { layout: "" }).then(function () { App.toast("已清空", "ok"); }).catch(function () { App.toast("本地已清空，后端同步失败", "err"); });
      });
      // fav
      var btnAddFav = document.getElementById("btnAddFav");
      if (btnAddFav) btnAddFav.addEventListener("click", function () {
        var inp = document.getElementById("favInput");
        var v = inp.value.trim();
        if (!v) { App.toast("请输入收藏内容", "err"); return; }
        var arr = App.favorites.get();
        if (arr.indexOf(v) >= 0) { App.toast("已存在", "err"); return; }
        arr.push(v);
        App.favorites.set(arr);
        renderLayoutFav({ layout: lay, favorites: JSON.stringify(arr) });
        App.toast("已添加收藏（本地+后端已同步）", "ok");
      });
      var btnSaveFav = document.getElementById("btnSaveFav");
      if (btnSaveFav) btnSaveFav.addEventListener("click", function () {
        var v = document.getElementById("favEdit").value.trim();
        if (v) { try { var parsed = JSON.parse(v); if (!Array.isArray(parsed)) throw new Error("需为 JSON 数组"); } catch (e) { App.toast("JSON 非法（需数组）：" + e.message, "err"); return; } }
        var arr2;
        try { arr2 = v ? JSON.parse(v) : []; } catch (e) { arr2 = []; }
        App.favorites.set(arr2);
        App.toast("收藏已保存", "ok");
        renderLayoutFav({ layout: lay, favorites: JSON.stringify(arr2) });
      });
      var dels = refs.layoutBox.querySelectorAll("[data-fav-del]");
      for (var di = 0; di < dels.length; di++) {
        dels[di].addEventListener("click", function () {
          var fv = this.getAttribute("data-fav-del");
          var arr3 = App.favorites.get().filter(function (x) { return x !== fv; });
          App.favorites.set(arr3);
          renderLayoutFav({ layout: lay, favorites: JSON.stringify(arr3) });
        });
      }
    }
  }

  function refresh() {
    loadAll().then(function (data) {
      renderMe(data);
      renderUsers(data);
      renderLayoutFav(data);
      refreshGossiper();
      if (App.applyRoleVisibility) App.applyRoleVisibility(document.getElementById("view"));
    }).catch(function (e) {
      if (refs.meBox) refs.meBox.innerHTML = '<div class="hint">加载失败：' + esc(e.message) + '</div>';
    });
  }

  function refreshGossiper() {
    var card = document.querySelector("[data-ref='gossiperCard']");
    var box = refs.gossiperBox;
    if (!card || !box) return;
    App.fetchJSON("/api/gossiper/status").then(function (r) {
      if (!r || r.ok === false) { card.style.display = "none"; return; }
      card.style.display = "";
      var interval = r.interval_sec != null ? r.interval_sec + "s" : "N/A";
      var reason = r.reason || "—";
      var count = r.gossip_count != null ? r.gossip_count : 0;
      var last = r.last_at ? App.fmtTime(r.last_at) : "未开始";
      var gap = r.last_gap != null ? r.last_gap : "—";
      var colors = { "synchronized": "var(--ok)", "catching_up": "var(--amber)", "idle": "var(--dim)" };
      var dotColor = colors[reason] || "var(--dim)";
      box.innerHTML = '<div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:8px;margin-top:8px">'
        + '<div><div style="font-size:11px;color:var(--dim)">当前间隔</div><div style="font-size:20px;font-weight:600">' + esc(interval) + '</div></div>'
        + '<div><div style="font-size:11px;color:var(--dim)">自适应原因</div><div style="font-size:14px;font-weight:500"><span class="dot" style="background:' + dotColor + ';display:inline-block;width:8px;height:8px;border-radius:4px;margin-right:4px"></span>' + esc(reason) + '</div></div>'
        + '<div><div style="font-size:11px;color:var(--dim)">gossip 计数</div><div style="font-size:20px;font-weight:600">' + esc(String(count)) + '</div></div>'
        + '<div><div style="font-size:11px;color:var(--dim)">上次 gap</div><div style="font-size:20px;font-weight:600">' + esc(String(gap)) + '</div></div>'
        + '<div><div style="font-size:11px;color:var(--dim)">上次同步</div><div style="font-size:13px">' + esc(last) + '</div></div>'
        + '</div>';
    }).catch(function () { if (card) card.style.display = "none"; });
  }

  App.Modules["page-settings"] = {
    init: function (el) {
      el.innerHTML =
        '<div class="page-node">'
        + '<div data-ref="meBox"></div>'
        + '<div class="settings-grid">'
        +   '<div class="card"><h2>用户管理 <span class="n">GET/POST/DELETE /api/users · admin 专属</span> <span class="h2-ops"><button class="mf-btn small primary" data-ref="btnAddUser">+ 新建用户</button></span></h2>'
        +     '<div data-ref="userTable"><div class="view-loading">加载用户…</div></div>'
        +   '</div>'
        +   '<div class="card"><h2>新建用户 <span class="n">创建返回 token · 角色分级</span></h2>'
        +     '<div style="display:flex;flex-direction:column;gap:8px">'
        +       '<label>用户名<input id="newUserName" placeholder="如 test_viewer" style="width:100%;height:32px;padding:0 10px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"></label>'
        +       '<label>密码<input id="newUserPwd" type="password" placeholder="至少 4 位" style="width:100%;height:32px;padding:0 10px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"></label>'
        +       '<label>角色<select id="newUserRole" style="width:100%;height:32px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"><option value="viewer">viewer 访客（只读）</option><option value="engineer">engineer 工程师（可写）</option><option value="admin">admin 管理员</option></select></label>'
        +       '<button class="mf-btn primary" id="btnCreateUser">创建并获取 token</button>'
        +       '<div id="createResult" class="hint" style="display:none"></div>'
        +     '</div>'
        +   '</div>'
        + '</div>'
        + '<div data-ref="layoutBox"><div class="view-loading">加载布局与收藏…</div></div>'
        + '<div class="card" data-ref="gossiperCard" style="display:none"><h2>Gossiper 自适应状态 <span class="n">GET /api/gossiper/status</span> <span class="h2-ops"><button class="mf-btn small" data-ref="btnGossiperRefresh">刷新</button></span></h2>'
        +   '<div data-ref="gossiperBox"><div class="empty">加载中…</div></div>'
        +   '<div class="hint">Gossiper 自适应调整 gossip 间隔：正常 60s，追差距时降至 10s。peer 节点差异大时自动追齐。</div>'
        + '</div>'
        + '<div class="card"><h2>操作审计 <span class="n">admin 可查 · /api/audit</span> <span class="h2-ops"><button class="mf-btn small" id="btnLoadAudit">刷新审计</button></span></h2><div id="auditBox"><div class="empty">点击刷新加载最近审计</div></div></div>'
        + '<div class="hint">提示：未配置 agg_token 且无用户时为宽松模式；创建首个 admin 后自动收紧。所有写操作与管理接口走 agg_token 或 users.token 鉴权，viewer 越权 403。</div>'
        + "</div>";
      var nodes = el.querySelectorAll("[data-ref]");
      for (var i = 0; i < nodes.length; i++) refs[nodes[i].getAttribute("data-ref")] = nodes[i];

      // 新建用户事件
      var btnCreate = el.querySelector("#btnCreateUser");
      if (btnCreate) btnCreate.addEventListener("click", function () {
        var name = el.querySelector("#newUserName").value.trim();
        var pwd = el.querySelector("#newUserPwd").value;
        var role = el.querySelector("#newUserRole").value;
        if (!name || !pwd) { App.toast("用户名/密码必填", "err"); return; }
        if (pwd.length < 4) { App.toast("密码至少 4 位", "err"); return; }
        if (!App.auth.isAdmin()) { App.toast("仅管理员可创建用户", "err"); return; }
        apiSend("POST", "/api/users", { name: name, password: pwd, role: role }).then(function (res) {
          var token = res.token || res.Token || "";
          var box = el.querySelector("#createResult");
          box.style.display = "block";
          box.innerHTML = '创建成功：<b>' + esc(name) + '</b> ' + roleBadge(role) + '<br>token（32hex，请复制保存，仅此一次明文返回）：<br><code style="word-break:break-all;background:var(--bg2);padding:6px;border-radius:6px;display:block;margin-top:6px">' + esc(token) + '</code><br><button class="mf-btn small" data-copy-token="' + esc(token) + '">复制 token</button> <span class="badge">已写入审计</span>';
          var cp2 = box.querySelector("[data-copy-token]");
          if (cp2) cp2.addEventListener("click", function () { var t = cp2.getAttribute("data-copy-token"); try { navigator.clipboard.writeText(t).then(function () { App.toast("已复制", "ok"); }); } catch (e) { prompt("复制 token", t); } });
          App.toast("用户已创建，token 已返回", "ok");
          refresh();
        }).catch(function (e) { App.toast("创建失败：" + e.message, "err"); });
      });
      refs.btnAddUser = el.querySelector('[data-ref="btnAddUser"]');
      if (refs.btnAddUser) refs.btnAddUser.addEventListener("click", function () { el.querySelector("#newUserName").focus(); window.scrollTo(0, 0); });

      // 审计
      var btnAudit = el.querySelector("#btnLoadAudit");
      if (btnAudit) btnAudit.addEventListener("click", function () {
        var box = el.querySelector("#auditBox");
        box.innerHTML = '<div class="view-loading">加载中…</div>';
        apiGet("/api/audit?limit=50").then(function (rows) {
          if (!rows || !rows.length) { box.innerHTML = '<div class="empty">暂无审计</div>'; return; }
          var html = '<table class="user-table"><thead><tr><th>时间</th><th>操作人</th><th>动作</th><th>详情</th></tr></thead><tbody>';
          for (var i = 0; i < rows.length; i++) {
            var r = rows[i];
            html += '<tr><td>' + esc(r.Ts || r.ts || "") + '</td><td>' + esc(r.Who || r.who || "") + '</td><td>' + esc(r.Action || r.action || "") + '</td><td>' + esc(r.Detail || r.detail || "") + '</td></tr>';
          }
          html += '</tbody></table>';
          box.innerHTML = html;
        }).catch(function (e) { box.innerHTML = '<div class="hint">加载失败：' + esc(e.message) + '（需 admin）</div>'; });
      });

      // Gossiper 刷新
      if (refs.btnGossiperRefresh) refs.btnGossiperRefresh.addEventListener("click", function () { refreshGossiper(); });

      refresh();
      // 定时刷新身份（token 变更后同步）
      setInterval(function () { if (!el.isConnected) return; loadAll().then(function (d) { renderMe(d); if (App.applyRoleVisibility) App.applyRoleVisibility(el); }); }, 10000);
    },
    render: function () { refresh(); }
  };
})(window);
