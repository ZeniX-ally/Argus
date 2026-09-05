/* ============================================================
   Argus FCT 鑱氬悎绯荤粺 路 鍓嶇鍔犺浇鍣紙v3.18.0 閲嶆瀯锛氭棤鍔ㄦ€佹敞鍏ワ級
   public/js/loader.js
   ----------------
   绾﹀畾锛?   路 鍏ㄥ眬鍛藉悕绌洪棿 App锛坵indow.App锛夈€?   路 App.Modules 涓烘ā鍧楁敞鍐岃〃锛屾ā鍧楁枃浠剁粺涓€鏍煎紡锛?       App.Modules["page-xxx"] = { init(el, ctx){...}, render(ctx){...} }
     init 浠呴娆¤繘鍏ユ椂璋冪敤锛堟瀯寤?DOM / 缁戝畾浜嬩欢锛夛紱
     render 鍦ㄦ暟鎹埛鏂版垨鍐嶆瀵艰埅鏃惰皟鐢紙閲嶇粯鏁版嵁鍖猴紝鍙€夛級銆?   路 鎵€鏈夐〉闈㈡ā鍧椾笌 core.js 鍧囬€氳繃 index.html 鐨?<script> 闈欐€佸姞杞斤紝
     鏈枃浠跺彧璐熻矗锛氳矾鐢憋紙hash锛夊垏鎹€佹ā鍧楁覆鏌撹皟搴︺€佸鑸簨浠躲€佷晶杈规爮鎶樺彔銆?     鈥斺€?涓嶅啀娉ㄥ叆浠讳綍鑴氭湰锛屽交搴曟秷闄?妯″潡鏈姞杞?/ 寮傛绔炴€?/ 寮曠敤閿欎綅"銆?   闆剁涓夋柟渚濊禆銆?   ============================================================ */
(function (window, document) {
  "use strict";

  var App = window.App = window.App || {};

  /* ---------------- 妯″潡娉ㄥ唽琛?---------------- */
  App.Modules = App.Modules || {};

  /* ---------------- 璺敱琛細name -> {module,title} ----------------
     鍗佷簩涓叆鍙ｏ細鎬昏 / FAIL / 鑹巼 / XML / 缁翠慨 / 璁惧 / 鏁版嵁鎷夊彇 / 鎶ュ憡涓績 / 绋嬪簭鏃ュ織 / 鍛婅 / 澶氭満鍙板姣?/ 璁剧疆 */
  App.Routes = {
    overview:    { module: "page-overview",    title: "鎬昏" },
    fails:       { module: "page-fails",       title: "FAIL 鏄庣粏" },
    yield:       { module: "page-yield",       title: "良率" },
    seasonality: { module: "page-yield-seasonality", title: "季节性" },
    xml:         { module: "page-xml",         title: "鍦ㄧ嚎 XML 鏌ョ湅" },
    maintenance: { module: "page-maintenance", title: "缁翠慨" },
    devices:     { module: "page-devices",     title: "璁惧" },
    health:      { module: "page-device-health", title: "健康分" },
    fetch:       { module: "page-fetch",       title: "鏁版嵁鎷夊彇" },
    report:      { module: "page-report",      title: "鎶ュ憡涓績" },
    proc:        { module: "page-proc",        title: "绋嬪簭鏃ュ織" },
    alerts:      { module: "page-alerts",      title: "鍛婅" },
    compare:     { module: "page-compare",     title: "多机台对比" },
    settings:    { module: "page-settings",    title: "璁剧疆" }
  };

  /* ---------------- 瀵艰埅锛歨ash 璺敱 ---------------- */
  App.Nav = App.Nav || {};
  var _view = null;      // #view 瀹瑰櫒锛堟儼鎬ц幏鍙栵級
  var _ctx = null;       // 褰撳墠涓婁笅鏂囩紦瀛?
  function view() {
    if (!_view) _view = document.getElementById("view");
    return _view;
  }

  function parseHash() {
    var h = (window.location.hash || "#/overview").replace(/^#\/?/, "");
    h = h.split("?")[0];           // 涓㈠純鏌ヨ娈碉紝灏嗘潵鍙仛鍙傛暟璺敱
    return App.Routes[h] ? h : "overview";
  }

  /* 鏍规嵁宸叉敞鍐屾ā鍧楁覆鏌撳搴旈〉闈㈠埌 #view */
  App.Nav.apply = function (name) {
    var route = App.Routes[name] || App.Routes.overview;
    var el = view();
    if (!el) return;

    // 1) 楂樹寒瀵艰埅鎸夐挳 / 鍒锋柊椤舵爮鏍囬
    var btns = document.querySelectorAll(".nav-btn");
    for (var i = 0; i < btns.length; i++) {
      btns[i].classList.toggle("active", btns[i].getAttribute("data-page") === name);
    }
    var title = document.getElementById("pageTitle");
    if (title) title.textContent = route.title;

    // 2) 妯″潡搴斿凡闅?index.html 闈欐€佸姞杞藉畬姣曪紱缂哄け鍒欐彁绀猴紙鐞嗚涓婁笉浼氬彂鐢燂級
    var mod = App.Modules[route.module];
    if (mod && mod.init) {
      App.Nav.show(name, mod);
    } else {
      el.innerHTML = '<div class="page-node"><div class="hint">妯″潡 ' + route.module + ' 鏈敞鍐岋紙璇锋鏌?index.html 鏄惁宸插姞杞借鑴氭湰锛?/div></div>';
    }
  };

  /* 娓叉煋宸叉敞鍐屾ā鍧楀埌 #view锛涙瘡娆″鑸兘璧?init锛堝悇妯″潡 init 鍧囬噸寤?el.innerHTML锛?     鏃?DOM 杩炲悓鐩戝惉涓€骞堕攢姣侊紝涓嶄細閲嶅缁戝畾锛夆€?try/catch 闃叉鍗曢〉鎶ラ敊鍗℃瀵艰埅 */
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
      el.innerHTML = '<div class="page-node"><div class="hint">椤甸潰 ' + route.title + ' 鍔犺浇鍑洪敊锛? + App.esc(e.message) + '<br><button class="btn btn-secondary" onclick="location.reload()">鍒锋柊閲嶈瘯</button></div></div>';
      if (App.toast) App.toast("椤甸潰鍔犺浇鍑洪敊: " + e.message, "err");
    }
  };
  App.Nav.show.display = true;

  /* 鏁版嵁鍒锋柊鍚庡洖璋冿細閲嶇粯褰撳墠婵€娲婚〉闈㈢殑鏁版嵁鍖?*/
  App.Nav.rerender = function () {
    if (_ctx && _ctx.name) {
      var m = App.Modules[App.Routes[_ctx.name].module];
      if (m && m.render) m.render(_ctx);
    }
  };

  /* 缂栫▼寮忓鑸細鍒囨崲 hash 骞剁珛鍗冲簲鐢紙hashchange 涔熷厹搴曚竴娆★級 */
  App.Nav.go = function (name) {
    if (!App.Routes[name]) name = "overview";
    var h = "#/" + name;
    if (window.location.hash !== h) {
      try { window.location.hash = h; } catch (e) { /* 蹇界暐 */ }
    }
    App.Nav.apply(name);
  };

  /* ---------------- 渚ц竟鏍忔姌鍙?/ 灞曞紑 ---------------- */
  function setCollapsed(c) {
    document.body.classList.toggle("sb-collapsed", c);
    var t = document.querySelector("#toggleSidebar .lbl");
    if (t) t.textContent = c ? "灞曞紑渚ц竟鏍? : "鏀惰捣渚ц竟鏍?;
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
      if (onMobile && cur) setCollapsed(false); // 绉诲姩绔睍寮€鍚庣偣鍚岄挳鏀惰捣
    });
    var hb = document.getElementById("hamburger");
    if (hb) hb.addEventListener("click", function () { setCollapsed(false); });
  }

  /* ---------------- 鍚姩 ---------------- */
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

  // 鎵€鏈夋ā鍧楅潤鎬佸姞杞藉畬姣曪細boot锛坈ore 瀹氫箟锛屽彧鎵ц涓€娆★級+ 棣栨杩涘叆褰撳墠 hash
  if (App.boot) App.boot();
  App.Nav.apply(parseHash());
})(window, document);

/* ---------------- 域8 布局智能：访问频次 + 角色差异化 ---------------- */
App.LayoutAdvisor = {
  key: "agg.visits",
  record: function(page){
    try{
      var raw = window.localStorage.getItem(App.LayoutAdvisor.key);
      var map = raw ? JSON.parse(raw) : {};
      map[page] = (map[page]||0)+1;
      window.localStorage.setItem(App.LayoutAdvisor.key, JSON.stringify(map));
      // 同步后端 layout 双写（静默）
      if(App.layout && App.layout.set){
        var cur = App.layout.get()||{};
        cur.visits = map;
        try{ App.layout.set(cur); }catch(e){}
      }
    }catch(e){}
  },
  suggest: function(role){
    try{
      var raw = window.localStorage.getItem(App.LayoutAdvisor.key);
      var map = raw ? JSON.parse(raw) : {};
      var def = role==="viewer" ? ["overview","fails","yield","devices","maintenance"] : role==="engineer" ? ["overview","fails","maintenance","devices","yield"] : ["overview","fails","yield","maintenance","devices"];
      var ranked = Object.keys(map).sort(function(a,b){ return (map[b]||0)-(map[a]||0); });
      var set = {};
      ranked.forEach(function(k){ set[k]=1; });
      def.forEach(function(k){ if(!set[k]) ranked.push(k); });
      return ranked;
    }catch(e){ return []; }
  },
  apply: function(){
    try{
      var role = (App.auth && App.auth.role) || "viewer";
      var order = App.LayoutAdvisor.suggest(role);
      var nav = document.getElementById("nav");
      if(!nav || !order.length) return;
      var btns = Array.prototype.slice.call(nav.querySelectorAll(".nav-btn"));
      var map = {};
      btns.forEach(function(b){ map[b.getAttribute("data-page")] = b; });
      order.forEach(function(name){
        if(map[name]) nav.appendChild(map[name]);
      });
    }catch(e){}
  }
};
// 在导航时记录频次并可选重排（默认仅记录，不自动重排避免跳动；用户可在设置页一键应用）
var _origGo = App.Nav.go;
App.Nav.go = function(name){
  try{ App.LayoutAdvisor.record(name); }catch(e){}
  return _origGo.call(App.Nav, name);
};
var _origApply = App.Nav.apply;
App.Nav.apply = function(name){
  try{ App.LayoutAdvisor.record(name); }catch(e){}
  return _origApply.call(App.Nav, name);
};
// 启动时按角色建议排序（延迟到 auth 就绪后）
setTimeout(function(){ try{ App.LayoutAdvisor.apply(); }catch(e){} }, 600);
