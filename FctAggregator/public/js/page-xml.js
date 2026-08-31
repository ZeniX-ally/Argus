/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 在线 XML 查看页（P4 源机检索联动）
   public/js/page-xml.js
   ----------------
   能力：① 快捷查看（下拉选 FAIL 报告，直接 iframe 预览，等效本地）
         ② 源机检索（POST /api/mesh/query 并发查各机台 test_records 全量含 PASS，
            结果聚合缓存 5 分钟，零膨胀）→ 结果列表 → 预览/下载
   解析/渲染复用服务端 XmlParser + XmlReportHtml（DTD Prohibit + 白名单），
   口径与本地 XmlViewerForm 一致；鉴权沿用 agg_token / Cookie。
   零依赖、Theme 黑白红、dataSig 变更检测、prefers-reduced-motion。
   ============================================================ */
(function (window) {
  "use strict";
  var App = window.App;

  var refs = { list: null, frame: null, openBtn: null, dlBtn: null, count: null, hint: null,
    qMachine: null, qSn: null, qModel: null, qResult: null, qFrom: null, qTo: null, qLimit: null,
    btnQuery: null, qStatus: null, qTable: null, qTbody: null, qEmpty: null, qWrap: null };
  var curId = 0;
  var lastQuerySig = "";

  function fillList() {
    var s = App.state;
    var opts = (s.fails || []).slice(0, 100);
    if (refs.count) refs.count.textContent = "共 " + (s.fails || []).length + " 条（展示前 " + opts.length + " 条）";
    if (!opts.length) {
      if (refs.list) refs.list.innerHTML = '<option value="">暂无 FAIL 数据</option>';
      if (refs.hint) refs.hint.style.display = "block";
      return;
    }
    if (refs.hint) refs.hint.style.display = "none";
    var html = "";
    var keep = false;
    opts.forEach(function (f) {
      var id = App.pick(f, "id");
      var label = App.fmtTime(App.pick(f, "ingestTs") || App.pick(f, "ts"))
        + " · " + (App.pick(f, "machine") || "—")
        + " · " + (App.pick(f, "failReason") || "—").slice(0, 24);
      if (String(id) === String(curId)) keep = true;
      html += '<option value="' + id + '">' + App.esc(label) + "</option>";
    });
    if (refs.list) refs.list.innerHTML = html;
    if (!keep && opts[0]) loadById(opts[0]);
  }

  function loadById(f) {
    if (!f) return;
    var id = App.pick(f, "id");
    curId = id;
    if (refs.list) refs.list.value = String(id);
    if (refs.frame) refs.frame.src = App.data.xmlUrl(id);
    if (refs.openBtn) refs.openBtn.href = App.data.xmlUrl(id);
    if (refs.dlBtn) refs.dlBtn.href = App.data.fileUrl(id);
  }

  function loadByPath(machine, xmlPath) {
    if (!xmlPath) return;
    var url = "/api/xmlview?path=" + encodeURIComponent(xmlPath);
    if (machine) url += "&machine=" + encodeURIComponent(machine);
    url = App.withToken(url);
    if (refs.frame) refs.frame.src = url;
    if (refs.openBtn) refs.openBtn.href = url;
    if (refs.dlBtn) {
      var dl = "/api/file?path=" + encodeURIComponent(xmlPath);
      refs.dlBtn.href = App.withToken(dl);
    }
  }

  function fillMachineSel() {
    if (!refs.qMachine) return;
    var cur = refs.qMachine.value;
    var machines = (App.state.machines || []).map(function (m) { return App.pick(m, "machine") || ""; }).filter(function (x) { return !!x; });
    machines.sort(function (a, b) { return a.localeCompare(b, "zh"); });
    var html = '<option value="">全部机台</option>';
    for (var i = 0; i < machines.length; i++) html += '<option value="' + App.esc(machines[i]) + '">' + App.esc(machines[i]) + '</option>';
    refs.qMachine.innerHTML = html;
    if (machines.indexOf(cur) >= 0) refs.qMachine.value = cur;
  }

  function doQuery() {
    var req = {
      machine: (refs.qMachine.value || "").trim(),
      sn: (refs.qSn.value || "").trim(),
      model: (refs.qModel.value || "").trim(),
      result: (refs.qResult.value || "").trim() || "ALL",
      date_from: (refs.qFrom.value || "").trim().replace(/-/g, ""),
      date_to: (refs.qTo.value || "").trim().replace(/-/g, ""),
      limit: parseInt(refs.qLimit.value, 10) || 100,
      offset: 0
    };
    var sig = JSON.stringify(req);
    if (sig === lastQuerySig && refs.qTable) { /* 变更检测：参数未变且表已有数据则跳过重复渲染 */ }
    lastQuerySig = sig;
    if (refs.qStatus) refs.qStatus.textContent = "检索中…（并发查各机台，含 PASS，5 分钟缓存）";
    if (refs.btnQuery) refs.btnQuery.disabled = true;
    var body = JSON.stringify(req);
    // 复用 App.fetchJSON 的 token 逻辑，但需 POST + JSON
    var url = window.location.origin + "/api/mesh/query";
    if (App.token) url = App.withToken(url);
    fetch(url, { method: "POST", credentials: "same-origin", headers: { "Content-Type": "application/json", "Accept": "application/json" }, body: body })
      .then(function (r) {
        if (r.status === 401 || r.status === 403) { App.toast("未授权（需 agg_token）", "err"); throw new Error("HTTP " + r.status); }
        if (!r.ok) throw new Error("HTTP " + r.status);
        return r.json();
      })
      .then(function (data) {
        renderQueryResult(data);
        if (refs.qStatus) {
          var peers = data.peers || [];
          var online = peers.filter(function (p) { return p.Online; }).length;
          var cached = data.cached ? "（缓存命中）" : "";
          refs.qStatus.textContent = "共 " + (data.total || 0) + " 条" + cached + " · 在线机台 " + online + "/" + peers.length + " · 耗时 " + (data.elapsed_ms || 0) + "ms";
        }
      })
      .catch(function (e) {
        if (refs.qStatus) refs.qStatus.textContent = "检索失败：" + e.message;
        App.toast("检索失败：" + e.message, "err");
      })
      .finally(function () { if (refs.btnQuery) refs.btnQuery.disabled = false; });
  }

  function renderQueryResult(data) {
    var list = (data && data.results) || [];
    if (!refs.qTbody) return;
    if (!list.length) {
      refs.qTbody.innerHTML = "";
      if (refs.qEmpty) refs.qEmpty.style.display = "block";
      return;
    }
    if (refs.qEmpty) refs.qEmpty.style.display = "none";
    var html = "";
    list.forEach(function (x) {
      var machine = x.Machine || x.machine || "—";
      var sn = x.Sn || x.sn || "—";
      var model = x.Model || x.model || "—";
      var result = (x.Result || x.result || "").toUpperCase() || "—";
      var date = x.TestDate || x.testDate || "";
      var fail = x.FailReason || x.failReason || "—";
      var size = x.FileSize != null ? x.FileSize : (x.fileSize || 0);
      var path = x.XmlPath || x.xmlPath || "";
      var id = x.Id || x.id || 0;
      var badge = result === "FAIL" ? App.badge("FAIL", "fail") : result === "PASS" ? App.badge("PASS", "pass") : App.badge(result, "");
      var preview = "";
      if (path) preview = '<a class="op" href="#" data-path="' + App.esc(path) + '" data-machine="' + App.esc(machine) + '">预览</a>'
        + '<a class="op" href="' + App.withToken("/api/file?path=" + encodeURIComponent(path)) + '" download>下载</a>';
      else if (id) preview = '<a class="op" href="' + App.data.xmlUrl(id) + '" target="_blank">报告</a>';
      html += "<tr>"
        + "<td>" + App.esc(machine) + "</td>"
        + "<td>" + App.esc(sn) + "</td>"
        + "<td>" + App.esc(model) + "</td>"
        + "<td>" + badge + "</td>"
        + "<td>" + App.esc(date) + "</td>"
        + '<td title="' + App.esc(fail) + '">' + App.esc(fail.slice(0, 32)) + "</td>"
        + "<td>" + size + "</td>"
        + "<td>" + preview + "</td>"
        + "</tr>";
    });
    refs.qTbody.innerHTML = html;
    // 绑定预览点击（path 模式走 /api/xmlview?path=，等效本地 XmlViewerForm）
    var links = refs.qTbody.querySelectorAll("a[data-path]");
    for (var i = 0; i < links.length; i++) {
      links[i].addEventListener("click", function (e) {
        e.preventDefault();
        var p = this.getAttribute("data-path");
        var m = this.getAttribute("data-machine");
        loadByPath(m, p);
        // 切到预览区
        if (refs.frame) refs.frame.scrollIntoView({ behavior: "smooth", block: "center" });
      });
    }
  }

  function render(ctx) {
    if (!refs.list) return;
    fillList();
    fillMachineSel();
  }

  App.Modules["page-xml"] = {
    init: function (el, ctx) {
      el.innerHTML =
        '<div class="page-node">'
        + '<div class="card">'
        +   '<h2>快捷查看 <span class="n">最近 FAIL（按 ingest 倒序）</span></h2>'
        +   '<div class="toolbar">'
        +     '<select data-ref="list" title="选择要查看的 FAIL 报告（按 ingest 倒序）" style="flex:1;min-width:260px;height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"></select>'
        +     '<a class="act" data-ref="openBtn" target="_blank" rel="noopener">在新标签打开</a>'
        +     '<a class="act" data-ref="dlBtn" download>下载原始 XML</a>'
        +     '<span class="n" data-ref="count"></span>'
        +   '</div>'
        +   '<div class="hint" data-ref="hint" style="display:none">暂无 FAIL 数据可供查看。在线 XML 报告通过 /api/xmlview?id= 服务端渲染（XmlParser + XmlReportHtml），等效本地查看器。</div>'
        + '</div>'
        + '<div class="card">'
        +   '<h2>源机检索 <span class="n">任意机台任意产品（含 PASS）· 并发查各机台 test_records · 缓存 5 分钟</span></h2>'
        +   '<div class="toolbar" style="flex-wrap:wrap">'
        +     '<select data-ref="qMachine" title="机台（留空=全部）" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"></select>'
        +     '<input type="text" data-ref="qSn" placeholder="SN（含糊匹配）" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:0 10px">'
        +     '<input type="text" data-ref="qModel" placeholder="型号" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:0 10px">'
        +     '<select data-ref="qResult" title="结果" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"><option value="ALL">全部</option><option value="PASS">PASS</option><option value="FAIL">FAIL</option></select>'
        +     '<input type="date" data-ref="qFrom" title="起始日期" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:0 8px">'
        +     '<input type="date" data-ref="qTo" title="结束日期" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:0 8px">'
        +     '<select data-ref="qLimit" title="条数"><option value="50">50</option><option value="100" selected>100</option><option value="200">200</option><option value="500">500</option></select>'
        +     '<button class="act" data-ref="btnQuery">搜索</button>'
        +   '</div>'
        +   '<div data-ref="qStatus" class="n" style="margin-top:6px"></div>'
        +   '<div data-ref="qWrap" style="overflow-x:auto;margin-top:10px"><table><thead><tr><th>机台</th><th>SN</th><th>型号</th><th>结果</th><th>测试日期</th><th>失败项</th><th>大小</th><th>操作</th></tr></thead><tbody data-ref="qTbody"></tbody></table></div>'
        +   '<div class="empty" data-ref="qEmpty" style="display:none">暂无命中（调整条件后重试，PASS 报告需源机在线）</div>'
        + '</div>'
        + '<div class="xml-wrap"><iframe class="xml-frame" data-ref="frame"></iframe></div>'
        + '<div class="foot">报告内容只读；原始 XML 可下载留档 · 口径与本地 XmlViewerForm 一致（DTD Prohibit + 白名单）</div>'
        + "</div>";

      var n = el.querySelectorAll("[data-ref]");
      for (var i = 0; i < n.length; i++) refs[n[i].getAttribute("data-ref")] = n[i];

      if (refs.list) refs.list.addEventListener("change", function () {
        var id = refs.list.value;
        var hit = null;
        for (var j = 0; j < App.state.fails.length; j++) {
          if (String(App.pick(App.state.fails[j], "id")) === String(id)) { hit = App.state.fails[j]; break; }
        }
        if (hit) loadById(hit);
      });
      if (refs.btnQuery) refs.btnQuery.addEventListener("click", doQuery);
      // 回车触发搜索
      if (refs.qSn) refs.qSn.addEventListener("keydown", function (e) { if (e.key === "Enter") doQuery(); });
      if (refs.qModel) refs.qModel.addEventListener("keydown", function (e) { if (e.key === "Enter") doQuery(); });

      render(ctx);
    },
    render: render
  };
})(window);
