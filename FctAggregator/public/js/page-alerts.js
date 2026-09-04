/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 告警页（P8 告警规则中心）
   public/js/page-alerts.js
   ----------------
   基于 AggAlertService 统一规则：磁盘/CPU/离线/良率跌破（阈值来自 Config device_alert_* + yield_alert_*）。
   展示：规则配置（阈值可观测，来自 /api/alerts/rules） + 历史（来自 /api/alerts/history，支持机台/规则过滤）。
   管理员可就地改阈值/开关并 PATCH 热更新（无需重启聚合服务，v3.18.0）。
   接口：GET /api/alerts/rules（viewer）、PATCH /api/alerts/rules（admin，热更新+审计）、
         GET /api/alerts/history?machine=&rule=&limit=&offset=（viewer）
   历史落库：alert_history 表（单写者），飞书推送同步落库。
   零第三方，黑白红令牌，dataSig 变更检测，3s 不需要、手工刷新+30s 轮询，prefers-reduced-motion 适配（继承 theme）。
   ============================================================ */
(function (window) {
  "use strict";
  var App = window.App;
  var refs = {};
  var lastSig = "";
  var timer = null;
  var saving = false;   // 保存中防重复提交（PATCH 非幂等语义：重复提交会重复写审计）
  var lastSaveTs = 0;   // 上次保存时间戳

  function sig(rules, rows) {
    var a = rules ? JSON.stringify(rules) : "";
    var b = (rows || []).map(function (r) { return r.Id + r.Ts + r.Machine + r.Rule; }).join("|");
    return a + "|" + b;
  }

  // 四规则定义：key 对应 /api/alerts/rules 的字段名，thrKey 对应 PATCH body 的阈值键名，
  // def 是「只开开关、没填阈值」时服务端回落用的出厂默认（与 Config.cs 默认值保持一致）。
  var RULE_DEFS = [
    { key: "disk", title: "磁盘剩余", unit: "GB", thrKey: "threshold_gb", desc: "磁盘剩余 < 阈值 告警", min: 0, max: 100000, step: 1, def: 10 },
    { key: "cpu", title: "CPU 占用", unit: "%", thrKey: "threshold_pct", desc: "CPU ≥ 阈值 告警", min: 0, max: 100, step: 1, def: 90 },
    { key: "offline", title: "离线时长", unit: "分钟", thrKey: "threshold_minutes", desc: "未上报 ≥ 阈值 告警", min: 0, max: 10080, step: 1, def: 5 },
    { key: "yield", title: "良率跌破", unit: "%", thrKey: "threshold_pct", desc: "今日良率 < 阈值 告警", min: 0, max: 100, step: 0.1, def: 90 }
  ];

  function isAdmin() { return !!(App.auth && App.auth.isAdmin && App.auth.isAdmin()); }

  function renderRules(rules) {
    if (!rules || !refs.rulesBox) return;
    var canEdit = isAdmin();
    var html = '';
    // batch toggle (admin only)
    if (canEdit) {
      var allOn = RULE_DEFS.every(function(d) { return !!(rules[d.key] || {}).enabled; });
      html += '<div class="toolbar" style="margin-bottom:10px">'
        + '<span style="font-size:12px;color:var(--dim);margin-right:8px">批量操作</span>'
        + '<button class="act" data-batch="on">全部启用</button>'
        + '<button class="act" data-batch="off" style="background:var(--bg2);color:var(--ink);border-color:var(--line)">全部停用</button>'
        + '</div>';
    }
    html += '<div class="grid2" style="grid-template-columns:repeat(auto-fill,minmax(220px,1fr))">';
    for (var i = 0; i < RULE_DEFS.length; i++) {
      var d = RULE_DEFS[i];
      var r = rules[d.key] || {};
      var enabled = !!r.enabled;
      var thr = r[d.thrKey];
      if (thr == null) thr = d.def;
      var badge = enabled
        ? '<span class="badge" style="background:var(--red);color:#fff;border-color:var(--red)">启用</span>'
        : '<span class="badge">关闭</span>';

      if (canEdit) {
        // 编辑态：开关 + 阈值输入框（关闭时阈值禁填，与服务端「0=关闭」语义一致）
        html += '<div class="card" style="margin:0">'
          + '<h2>' + App.esc(d.title) + ' ' + badge + '</h2>'
          + '<div style="display:flex;align-items:center;gap:8px;margin:8px 0">'
          +   '<label style="display:flex;align-items:center;gap:4px;font-size:12px;cursor:pointer">'
          +     '<input type="checkbox" data-rule="' + d.key + '" data-f="enabled"' + (enabled ? " checked" : "") + '> 启用'
          +   '</label>'
          +   '<input type="number" data-rule="' + d.key + '" data-f="threshold" value="' + App.esc(String(thr)) + '"'
          +     ' min="' + d.min + '" max="' + d.max + '" step="' + d.step + '"' + (enabled ? "" : " disabled")
          +     ' style="width:96px;height:30px">'
          +   '<span style="font-size:12px;color:var(--dim)">' + App.esc(d.unit) + '</span>'
          + '</div>'
          + '<div style="font-size:11px;color:var(--dim)">' + App.esc(d.desc) + '（0=关闭）</div>'
          + '</div>';
      } else {
        // 只读态：保持 v3.17.0 的大数字展示
        html += '<div class="card" style="margin:0"><h2>' + App.esc(d.title) + ' ' + badge + '</h2>'
          + '<div style="font-size:22px;font-weight:600;margin:6px 0">' + App.esc(enabled ? (thr + " " + d.unit) : "—") + '</div>'
          + '<div style="font-size:11px;color:var(--dim)">' + App.esc(d.desc) + '</div></div>';
      }
    }
    html += '</div>';
    html += '<div class="hint" style="margin-top:8px">规则阈值来自 config.json 的 device_alert_* + yield_alert_*（0=关闭）。'
      + (canEdit ? '管理员可直接在此修改，<b>保存后立即生效、无需重启聚合服务</b>（改全局告警策略属高危操作，会写入审计）。'
                 : '修改需要管理员权限。')
      + ' 飞书 webhook ' + (rules.webhook_set ? '已配置' : '未配置（仅落库+日志）') + ' · 汇总周期 ' + (rules.summary_minutes || 60) + ' 分钟</div>';
    refs.rulesBox.innerHTML = html;
    if (canEdit && App.applyRoleVisibility) App.applyRoleVisibility(refs.rulesBox);

    // batch toggle listeners
    if (canEdit) {
      var batchOn = refs.rulesBox.querySelector('[data-batch="on"]');
      var batchOff = refs.rulesBox.querySelector('[data-batch="off"]');
      if (batchOn) batchOn.addEventListener("click", function () { batchSet(true); });
      if (batchOff) batchOff.addEventListener("click", function () { batchSet(false); });
    }

    // 开关与阈值输入联动：关闭时禁填（避免"关着却写了个数"的歧义）
    var boxes = refs.rulesBox.querySelectorAll("input[data-f='enabled']");
    for (var j = 0; j < boxes.length; j++) bindRuleToggle(boxes[j]);
  }

  function bindRuleToggle(cb) {
    cb.addEventListener("change", function () {
      var key = cb.getAttribute("data-rule");
      var inp = refs.rulesBox.querySelector("input[data-rule='" + key + "'][data-f='threshold']");
      if (inp) inp.disabled = !cb.checked;
    });
  }

  // batch set all rule toggles
  function batchSet(enabled) {
    if (!canEdit()) return;
    refs.rulesBox.querySelectorAll("input[data-f='enabled']").forEach(function(cb) { cb.checked = enabled; cb.dispatchEvent(new Event("change")); });
  }

  // 收集 UI 上的四规则改动 → PATCH body（只提交存在的字段，未动的规则服务端按「无变化」跳过）
  function collectRulesPatch() {
    if (!refs.rulesBox) return null;
    var body = {};
    for (var i = 0; i < RULE_DEFS.length; i++) {
      var d = RULE_DEFS[i];
      var cb = refs.rulesBox.querySelector("input[data-rule='" + d.key + "'][data-f='enabled']");
      var inp = refs.rulesBox.querySelector("input[data-rule='" + d.key + "'][data-f='threshold']");
      if (!cb || !inp) continue;
      var rule = { enabled: cb.checked };
      if (cb.checked) {
        var v = parseFloat(inp.value);
        if (!isFinite(v) || v < d.min || v > d.max) {
          App.toast(d.title + " 阈值需在 " + d.min + "~" + d.max + " " + d.unit + " 之间", "err");
          return null;
        }
        rule[d.thrKey] = v;
      }
      body[d.key] = rule;
    }
    return body;
  }

  function saveRules() {
    if (!isAdmin()) { App.toast("仅管理员可修改告警规则", "err"); return; }
    if (saving) return;
    var body = collectRulesPatch();
    if (!body) return;
    saving = true;
    var btn = refs.btnSave;
    if (btn) { btn.disabled = true; btn.textContent = "保存中…"; }
    if (App.toast) App.toast("正在保存并热更新…", "info");
    App.patchJSON("/api/alerts/rules", body).then(function (res) {
      saving = false;
      if (btn) { btn.disabled = false; btn.textContent = "保存规则"; }
      if (!res || res.ok === false) { App.toast("保存失败", "err"); return; }
      var ch = res.changed || [];
      lastSaveTs = Date.now();
      App.toast(ch.length ? ("已保存并生效：" + ch.length + " 项变更") : "无变化（阈值与当前一致）", "ok");
      lastSig = "";
      if (res.rules) renderRules(res.rules);
    }).catch(function (e) {
      saving = false;
      if (btn) { btn.disabled = false; btn.textContent = "保存规则"; }
      var msg = e && e.message ? String(e.message) : String(e);
      App.toast("保存失败：" + (msg.indexOf("403") >= 0 ? "无管理员权限" : msg), "err");
    });
  }

  function renderHistory(rows) {
    if (!refs.tbody) return;
    if (!rows || !rows.length) {
      refs.tbody.innerHTML = '<tr><td colspan="6" class="empty">暂无告警历史（阈值未触发或刚启动）</td></tr>';
      if (refs.count) refs.count.textContent = "共 0 条";
      return;
    }
    if (refs.count) refs.count.textContent = "共 " + rows.length + " 条（筛选后）";
    var html = "";
    for (var i = 0; i < rows.length; i++) {
      var r = rows[i];
      var ts = App.pick(r, "ts") || App.pick(r, "Ts") || "";
      var machine = App.pick(r, "machine") || "";
      var rule = App.pick(r, "rule") || "";
      var metric = App.pick(r, "metric") || "";
      var detail = App.pick(r, "detail") || "";
      var ruleBadge = rule === "yield" ? '<span class="badge fail">良率</span>' : rule === "disk" ? '<span class="badge" style="border-color:var(--amber);color:var(--amber)">磁盘</span>' : rule === "cpu" ? '<span class="badge">CPU</span>' : rule === "offline" ? '<span class="badge" style="border-color:var(--red)">离线</span>' : '<span class="badge">' + App.esc(rule) + '</span>';
      html += "<tr>"
        + "<td>" + App.esc(ts) + "</td>"
        + "<td>" + App.esc(machine) + "</td>"
        + "<td>" + ruleBadge + "</td>"
        + "<td>" + App.esc(metric) + "</td>"
        + "<td>" + App.esc(detail) + "</td>"
        + "</tr>";
    }
    refs.tbody.innerHTML = html;
  }

  function loadAll() {
    var machine = refs.machine ? refs.machine.value.trim() : "";
    var rule = refs.rule ? refs.rule.value.trim() : "";
    var limit = refs.limit ? refs.limit.value : "50";
    var qs = "?limit=" + encodeURIComponent(limit);
    if (machine) qs += "&machine=" + encodeURIComponent(machine);
    if (rule) qs += "&rule=" + encodeURIComponent(rule);
    Promise.all([
      App.fetchJSON("/api/alerts/rules").catch(function () { return null; }),
      App.fetchJSON("/api/alerts/history" + qs).catch(function () { return { rows: [] }; })
    ]).then(function (rs) {
      var rules = rs[0];
      var hist = rs[1];
      var rows = hist ? (hist.rows || hist.Rows || []) : [];
      // 兼容大小写
      if (!rows.length && hist && hist.rows === undefined && hist.Rows) rows = hist.Rows;
      var s = sig(rules, rows);
      if (s === lastSig) return;
      lastSig = s;
      renderRules(rules);
      renderHistory(rows);
      if (refs.status) refs.status.textContent = "更新于 " + new Date().toLocaleTimeString("zh-CN", { hour12: false });
    }).catch(function (e) {
      if (refs.tbody) refs.tbody.innerHTML = '<tr><td colspan="6" class="empty">加载失败：' + App.esc(e.message) + '</td></tr>';
    });
  }

  App.Modules["page-alerts"] = {
    init: function (el) {
      el.innerHTML =
        '<div class="page-node">'
        + '<div class="card"><h2>告警规则（统一配置，可观测） <span class="n" data-ref="status"></span></h2>'
        +   '<div data-ref="rulesBox"><div class="empty">加载中…</div></div>'
        +   '<div class="toolbar" style="margin-top:10px" data-require-role="admin">'
        +     '<button class="act" data-ref="btnSave">保存规则</button>'
        +     '<span class="hint">保存后立即生效，无需重启聚合服务（写审计 alerts.rules.save）</span>'
        +   '</div>'
        + '</div>'
        + '<div class="card"><h2>告警历史 <span class="n" data-ref="count"></span></h2>'
        +   '<div class="toolbar" style="margin-bottom:8px">'
        +     '<input data-ref="machine" placeholder="机台过滤（如 FCT7）" style="max-width:160px">'
        +     '<select data-ref="rule" style="height:34px"><option value="">全部规则</option><option value="yield">良率</option><option value="disk">磁盘</option><option value="cpu">CPU</option><option value="offline">离线</option></select>'
        +     '<select data-ref="limit" style="height:34px"><option value="20">20 条</option><option value="50" selected>50 条</option><option value="100">100 条</option><option value="200">200 条</option></select>'
        +     '<button class="act" data-ref="btnRefresh">刷新</button>'
        +     '<button class="act" data-ref="btnClear" style="background:var(--bg2);color:var(--ink);border-color:var(--line)">清空筛选</button>'
        +   '</div>'
        +   '<div style="overflow:auto"><table><thead><tr><th>时间</th><th>机台</th><th>规则</th><th>指标</th><th>详情</th></tr></thead><tbody data-ref="tbody"><tr><td colspan="6" class="empty">加载中…</td></tr></tbody></table></div>'
        +   '<div class="foot">历史来自 alert_history 表（单写者 WAL），阈值来自 config.json 的 device_alert_* + yield_alert_*，10 分钟防抖</div>'
        + '</div>'
        + '</div>';
      var nodes = el.querySelectorAll("[data-ref]");
      for (var i = 0; i < nodes.length; i++) refs[nodes[i].getAttribute("data-ref")] = nodes[i];
      if (refs.btnRefresh) refs.btnRefresh.addEventListener("click", function () { lastSig = ""; loadAll(); });
      if (refs.btnClear) refs.btnClear.addEventListener("click", function () { if (refs.machine) refs.machine.value = ""; if (refs.rule) refs.rule.value = ""; lastSig = ""; loadAll(); });
      if (refs.machine) refs.machine.addEventListener("keydown", function (e) { if (e.key === "Enter") { lastSig = ""; loadAll(); } });
      if (refs.rule) refs.rule.addEventListener("change", function () { lastSig = ""; loadAll(); });
      if (refs.limit) refs.limit.addEventListener("change", function () { lastSig = ""; loadAll(); });
      if (refs.btnSave) refs.btnSave.addEventListener("click", saveRules);
      // 权限显隐：data-require-role="admin" 默认隐藏，需在角色确定后按 body.role-* 放行
      if (App.applyRoleVisibility) App.applyRoleVisibility(el);
      // 角色可能还未加载完（首次进页面），加载完重渲染一次，避免管理员看到只读版
      if (App.auth && !App.auth.loaded && App.auth.refresh) {
        App.auth.refresh().then(function () {
          if (App.applyRoleVisibility) App.applyRoleVisibility(el);
          lastSig = ""; loadAll();
        }).catch(function () { });
      }
      lastSig = "";
      loadAll();
      if (timer) clearInterval(timer);
      timer = setInterval(function () { if (!el.isConnected) { clearInterval(timer); timer = null; return; } loadAll(); }, 30000);
    },
    render: function () { loadAll(); }
  };
})(window);
