/* ============================================================
   Argus FCT 聚合系统 · 前端加载器（v3.18.0 重构：无动态注入）
   public/js/loader.js
   ----------------
   约定：
   · 全局命名空间 App（window.App）。
   · App.Modules 为模块注册表，模块文件统一格式：
       App.Modules["page-xxx"] = { init(el, ctx){...}, render(ctx){...} }
     init 仅首次进入时调用（构建 DOM / 绑定事件）；
     render 在数据刷新或再次导航时调用（重绘数据区，可选）。
   · 所有页面模块与 core.js 均通过 index.html 的 <script> 静态加载，
     本文件只负责：路由（hash）切换、模块渲染调度、导航事件、侧边栏折叠。
     —— 不再注入任何脚本，彻底消除"模块未加载 / 异步竞态 / 引用错位"。
   零第三方依赖。
   ============================================================ */
(function (window, document) {
  "use strict";

  var App = window.App = window.App || {};

  /* ---------------- 模块注册表 ---------------- */
  App.Modules = App.Modules || {};

  /* ---------------- 路由表：name -> {module,title} ----------------
     十二个入口：总览 / FAIL / 良率 / XML / 维修 / 设备 / 数据拉取 / 报告中心 / 程序日志 / 告警 / 多机台对比 / 设置 */
  App.Routes = {
    overview:    { module: "page-overview",    title: "总览" },
    fails:       { module: "page-fails",       title: "FAIL 明细" },
    yield:       { module: "page-yield",       title: "良率" },
    xml:         { module: "page-xml",         title: "在线 XML 查看" },
    maintenance: { module: "page-maintenance", title: "维修" },
    devices:     { module: "page-devices",     title: "设备" },
    fetch:       { module: "page-fetch",       title: "数据拉取" },
    report:      { module: "page-report",      title: "报告中心" },
    proc:        { module: "page-proc",        title: "程序日志" },
    alerts:      { module: "page-alerts",      title: "告警" },
    compare:     { module: "page-compare",     title: "多机台对比" },
    settings:    { module: "page-settings",    title: "设置" }
  };

  /* ---------------- 导航：hash 路由 ---------------- */
  App.Nav = App.Nav || {};
  var _view = null;      // #view 容器（惰性获取）
  var _ctx = null;       // 当前上下文缓存

  function view() {
    if (!_view) _view = document.getElementById("view");
    return _view;
  }

  function parseHash() {
    var h = (window.location.hash || "#/overview").replace(/^#\/?/, "");
    h = h.split("?")[0];           // 丢弃查询段，将来可做参数路由
    return App.Routes[h] ? h : "overview";
  }

  /* 根据已注册模块渲染对应页面到 #view */
  App.Nav.apply = function (name) {
    var route = App.Routes[name] || App.Routes.overview;
    var el = view();
    if (!el) return;

    // 1) 高亮导航按钮 / 刷新顶栏标题
    var btns = document.querySelectorAll(".nav-btn");
    for (var i = 0; i < btns.length; i++) {
      btns[i].classList.toggle("active", btns[i].getAttribute("data-page") === name);
    }
    var title = document.getElementById("pageTitle");
    if (title) title.textContent = route.title;

    // 2) 模块应已随 index.html 静态加载完毕；缺失则提示（理论上不会发生）
    var mod = App.Modules[route.module];
    if (mod && mod.init) {
      App.Nav.show(name, mod);
    } else {
      el.innerHTML = '<div class="page-node"><div class="hint">模块 ' + route.module + ' 未注册（请检查 index.html 是否已加载该脚本）</div></div>';
    }
  };

  /* 渲染已注册模块到 #view；每次导航都走 init（各模块 init 均重建 el.innerHTML，
     旧 DOM 连同监听一并销毁，不会重复绑定）— try/catch 防止单页报错卡死导航 */
  App.Nav.show = function (name, mod) {
    var el = view();
    var route = App.Routes[name] || App.Routes.overview;
    var ctx = _ctx || (_ctx = {
      App: App,
      name: name,
      route: route,
      el: el,
      nav: App.Nav
    });
    ctx.name = name;
    ctx.route = route;
    try {
      mod.init(el, ctx);
    } catch (e) {
      console.error("[nav] page", name, "render error:", e);
      el.innerHTML = '<div class="page-node"><div class="hint">页面 ' + route.title + ' 加载出错：' + App.esc(e.message) + '<br><button class="btn btn-secondary" onclick="location.reload()">刷新重试</button></div></div>';
      if (App.toast) App.toast("页面加载出错: " + e.message, "err");
    }
  };
  App.Nav.show.display = true;

  /* 数据刷新后回调：重绘当前激活页面的数据区 */
  App.Nav.rerender = function () {
    if (_ctx && _ctx.name) {
      var m = App.Modules[App.Routes[_ctx.name].module];
      if (m && m.render) m.render(_ctx);
    }
  };

  /* 编程式导航：切换 hash 并立即应用（hashchange 也兜底一次） */
  App.Nav.go = function (name) {
    if (!App.Routes[name]) name = "overview";
    var h = "#/" + name;
    if (window.location.hash !== h) {
      try { window.location.hash = h; } catch (e) { /* 忽略 */ }
    }
    App.Nav.apply(name);
  };

  /* ---------------- 侧边栏折叠 / 展开 ---------------- */
  function setCollapsed(c) {
    document.body.classList.toggle("sb-collapsed", c);
    var t = document.querySelector("#toggleSidebar .lbl");
    if (t) t.textContent = c ? "展开侧边栏" : "收起侧边栏";
    try { window.localStorage.setItem("agg.sb", c ? "1" : "0"); } catch (e) { }
  }
  function initSidebar() {
    var c = false;
    try { c = window.localStorage.getItem("agg.sb") === "1"; } catch (e) { }
    setCollapsed(c);
    var tg = document.getElementById("toggleSidebar");
    if (tg) tg.addEventListener("click", function () {
      var onMobile = window.innerWidth <= 768;
      var cur = document.body.classList.contains("sb-collapsed");
      setCollapsed(!cur);
      if (onMobile && cur) setCollapsed(false); // 移动端展开后点同钮收起
    });
    var hb = document.getElementById("hamburger");
    if (hb) hb.addEventListener("click", function () { setCollapsed(false); });
  }

  /* ---------------- 启动 ---------------- */
  initSidebar();
  var nav = document.getElementById("nav");
  if (nav) nav.addEventListener("click", function (e) {
    var t = e.target;
    var b = t && t.closest ? t.closest(".nav-btn") : null;
    if (!b) {
      var cur = t;
      while (cur && cur !== nav) {
        if (cur.classList && cur.classList.contains("nav-btn")) { b = cur; break; }
        cur = cur.parentNode;
      }
    }
    if (b && b.getAttribute("data-page")) {
      e.preventDefault();
      e.stopPropagation();
      App.Nav.go(b.getAttribute("data-page"));
    }
  });
  window.addEventListener("hashchange", function () { App.Nav.apply(parseHash()); });

  // 所有模块静态加载完毕：boot（core 定义，只执行一次）+ 首次进入当前 hash
  if (App.boot) App.boot();
  App.Nav.apply(parseHash());
})(window, document);
