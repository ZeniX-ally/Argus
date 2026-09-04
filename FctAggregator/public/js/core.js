/* ============================================================
   Argus FCT 鑱氬悎绯荤粺 路 妯″潡鍖栧墠绔熀寤?   public/js/core.js
   ----------------
   鑱岃矗锛歠etch 灏佽锛圫ameSite cookie + 鍙€?URL 鎷?token锛夈€?   閫氱敤娓叉煋锛堝崱鐗?/ 琛ㄦ牸 / 寰界珷 / toast / canvas 鏌卞浘锛夈€?   鏃堕棿鏍煎紡鍖栥€佸叡浜暟鎹姸鎬佷笌杞鍒锋柊銆丩ite-Settings 鏉冮檺/甯冨眬/鏀惰棌/鍏ㄥ眬鎼滅储銆?   妯″潡鏍煎紡锛氭湰鏂囦欢涓嶆敞鍐?page-* 妯″潡锛屼粎鍦?App 鍛藉悕绌洪棿涓?   鏆撮湶鍏叡 API锛涚敱 loader 浣滀负鎵€鏈夐〉闈㈡ā鍧楃殑缁熶竴渚濊禆娉ㄥ叆銆?   ----------------
   鎺ュ彛淇濇寔涓?demo 涓€鑷达紙URL 涓嶅彉锛夛細
   /api/machines  /api/fails?limit&machine  /api/fails/count
   /api/health    /api/export.csv           /api/xmlview?id=  /api/file?id=
   /api/status    /api/users/me  /api/users/me/layout  /api/search
   閴存潈锛氬悗绔?agg_token 鏈夐厤缃椂 GET 鏍￠獙 ?token= / Cookie / 澶翠换涓€锛?   涓嶅尮閰嶄竴寰?403 鈫?椤甸潰鎻愮ず"閲嶆柊甯?token 鎵撳紑"銆?   ============================================================ */
(function (window, document) {
  "use strict";

  var App = window.App = window.App || {};
  // 鍩虹鍛藉悕绌洪棿锛坴3.18.0 闈欐€佸姞杞斤細妯″潡鏂囦欢鍏堜簬 loader 鎵ц锛岄』鍦ㄦ鍒濆鍖栵級
  App.Modules = App.Modules || {};
  App.Routes = App.Routes || {};

  /* ---------------- 宸ュ叿鍑芥暟 ---------------- */

  /* HTML 杞箟锛堥槻 XSS / 琛ㄦ牸娉ㄥ叆锛?*/
  App.esc = function (s) {
    return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  };

  /* 鍚庣搴忓垪鍖栧瓧娈典负 PascalCase锛圡achine/FailCount鈥︼級锛屽墠绔粺涓€灏忓啓鍙栨硶锛屼袱鎬佸吋瀹?*/
  App.pick = function (o, key) {
    if (!o) return undefined;
    var c = key.charAt(0).toLowerCase() + key.slice(1);
    var p = key.charAt(0).toUpperCase() + key.slice(1);
    var v = o[c] !== undefined ? o[c] : o[p];
    return v;
  };

  /* 杩愯鏃堕暱鏍煎紡鍖栵細3600s -> 1h05m */
  App.fmtUp = function (s) {
    s = Math.floor(s || 0);
    var h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60);
    return h > 0 ? h + "h" + m + "m" : m + "m" + Math.floor(s % 60) + "s";
  };

  /* 鏃堕棿鎴虫樉绀猴細鍙?"MM-dd HH:mm" 娈碉紙涓?demo 涓€鑷达級 */
  App.fmtTime = function (ts) {
    if (!ts) return "--";
    var s = String(ts);
    return s.length >= 16 ? s.slice(5, 16) : s;
  };

  /* ---------------- token 澶勭悊 ----------------
     瑙勫垯锛氫紭鍏堝甫 ?token= 鎵撳紑椤甸潰锛堝悗绔?POST 鎺ㄩ€佺敤 X-Agg-Token 澶达紝
     GET 鐪嬫澘/API 鏍￠獙 token 鍙傛暟鎴?Cookie锛夈€傝繖閲岃鍙?URL 涓婄殑 token锛?     涔嬪悗鎵€鏈?API 璇锋眰 / 閾炬帴閮借嚜鍔ㄦ嫾鎺ワ紝淇濊瘉閴存潈妯″紡鍙敤銆?*/
  App.token = (function () {
    var m = /[?&]token=([^&]+)/.exec(window.location.search);
    return m ? decodeURIComponent(m[1]) : "";
  })();

  /* 缁?URL 鎷兼帴 token 鍙傛暟锛堥〉闈㈠唴 <a> 璺宠浆 / iframe 鐢級 */
  App.withToken = function (url) {
    if (!App.token) return url;
    return url + (url.indexOf("?") < 0 ? "?" : "&") + "token=" + encodeURIComponent(App.token);
  };

  /* ---------------- fetch 灏佽 ----------------
     杩斿洖 Promise<json>锛涘け璐ユ姏 Error("URL 鈫?HTTP 鐘舵€?)锛?     401/403 棰濆 toast 鎻愮ず閲嶆柊甯?token 鎵撳紑銆?*/
  App.fetchJSON = function (path) {
    var u = path.indexOf("http") === 0 ? path : (window.location.origin + path);
    if (App.token) u = App.withToken(u);
    return fetch(u, { credentials: "same-origin", headers: { "Accept": "application/json" } })
      .then(function (r) {
        if (r.status === 401 || r.status === 403) {
          App.toast("接口未授权（HTTP " + r.status + "）：请用 ?token=<聚合token> 重新打开本页", "err");
          throw new Error(u + " -> HTTP " + r.status + "（未授权，需带 token 访问）");
        }
        if (!r.ok) throw new Error(u + " -> HTTP " + r.status);
        return r.json();
      });
  };
  App.fetchText = function (path) {
    var u = path.indexOf("http") === 0 ? path : (window.location.origin + path);
    if (App.token) u = App.withToken(u);
    return fetch(u, { credentials: "same-origin" }).then(function (r) {
      if (!r.ok) throw new Error(u + " 鈫?HTTP " + r.status);
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

  /* ---------------- toast锛堣交鎻愮ず锛?---------------- */
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

  /* 鍏ㄥ眬閿欒妯箙锛?errbanner 甯搁┗浜?index.html 涓诲尯椤堕儴锛?*/
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

  /* ---------------- 閫氱敤娓叉煋锛堝崱鐗?/ 琛ㄦ牸 / 寰界珷锛?---------------- */

  /* 寰界珷锛欶AIL -> 绾紝PASS 绫?-> 缁匡紝鍏朵綑鐏?*/
  App.badge = function (text, kind) {
    var cls = kind === "fail" ? " badge fail" : kind === "pass" ? " badge pass" : "";
    return '<span class="badge' + cls + '">' + App.esc(text || "--") + '</span>';
  };

  /* 琛ㄦ牸锛歵hs 鏁扮粍 / rows 浜岀淮鏁扮粍 / rowCls 鍙€夎绫诲嚱鏁?*/
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

  /* 鏈哄彴灏忓崱锛堟€昏 / 璁惧鍏辩敤褰㈡€侊紱link 鍙€夌偣鍑诲洖璋冿紝compact 绱у噾瀵嗗害锛?*/
  App.machineCard = function (x, opts) {
    opts = opts || {};
    var name = App.pick(x, "machine") || "-";
    var fc = App.pick(x, "failCount") || 0;
    var on = !!App.pick(x, "online");
    var cur = opts.sel && opts.sel === name;
    var ops = opts.ops ? '<div class="m-ops">' + opts.ops + "</div>" : "";
    var extra = opts.extra || [];
    var html = '<div class="machine' + (cur ? " cur" : "") + '" draggable="' + (opts.draggable ? "true" : "false") + '" data-m="' + App.esc(name) + '">' + ops
      + '<div class="row1"><span class="dot ' + (on ? "ok" : "err") + '"></span>'
      + '<span class="name">' + App.esc(name) + "</span></div>"
      + '<div class="st">' + (App.pick(x, "isSelf") ? "鏈満 路 " : "") + (on ? "鍦ㄧ嚎" : "绂荤嚎") + "</div>"
      + '<div class="fc ' + (fc > 0 ? "hot" : fc === 0 ? "zero" : "") + '">' + fc + '</div><div class="st">绱 FAIL</div>';
    for (var i = 0; i < extra.length; i++) html += extra[i];
    return html + "</div>";
  };

  /* ---------------- Lite-Settings: 鏉冮檺/甯冨眬/鏀惰棌/鍏ㄥ眬鎼滅储 ---------------- */
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
      // 鍚屾灏濊瘯 /api/users/me 缁嗗寲锛坙ayout/favorites 鎷ユ湁鑰咃級
      return App.fetchJSON("/api/users/me").then(function (me) {
        if (me && me.role) App.auth.role = String(me.role).toLowerCase();
        if (me && me.name) App.auth.who = me.name;
        applyRoleClass();
        return me;
      }).catch(function () { applyRoleClass(); return st; });
    }).catch(function () {
      // 鏈壌鏉?瀹芥澗妯″紡浠嶈涓?admin锛堜繚鎸佸悜鍚庡吋瀹癸級
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
    if (hb && App.auth.who) hb.textContent = App.auth.who + " (" + App.auth.role + ")";
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

  // 甯冨眬鎸佷箙鍖栵紙users.layout锛夛細鍓嶇 localStorage + 鍚庣鍚屾鍙屽啓
  App.layout = {
    key: "agg.layout",
    get: function () {
      try { var raw = window.localStorage.getItem(App.layout.key); return raw ? JSON.parse(raw) : {}; } catch (e) { return {}; }
    },
    set: function (obj) {
      try { window.localStorage.setItem(App.layout.key, JSON.stringify(obj || {})); } catch (e) {}
      // 鍚庣鍚屾锛堥潤榛橈紝澶辫触涓嶉樆鏂級
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
      // 鍏抽棴鎼滅储闈㈡澘
      var p = document.getElementById("searchPanel");
      if (p) p.hidden = true;
    }
  };

  /* ---------------- 鍏变韩鏁版嵁鐘舵€?---------------- */
  App.state = {
    machines: [],     // /api/machines
    fails: [],        // /api/fails?limit=100锛坕ngest 鍊掑簭锛?    failCount: 0,     // /api/fails/count
    health: null,     // /api/health
    sel: ""           // 鏈哄彴绛涢€夛細"" 鍏ㄩ儴鏈哄彴锛涢潪绌烘寚瀹氭満鍙?  };

  var lastSig = "";   // 鏁版嵁鎸囩汗锛氭暟鎹湭鍙樻椂璺宠繃閲嶆覆鏌擄紙3s 杞澶ч儴鍒嗗懆鏈熸棤鍙樺寲锛?
  function dataSig() {
    var s = App.state;
    return JSON.stringify(s.machines) + "|" + JSON.stringify(s.fails) + "|" + s.failCount;
  }

  function setHealth(ok, text) {
    var d = document.getElementById("hdot");
    var t = document.getElementById("htext");
    if (d) d.className = "dot " + (ok ? "ok" : "err");
    if (t) t.textContent = text || (ok ? "姝ｅ父" : "鎺ュ彛寮傚父");
  }

  /* 鏈哄彴閫夋嫨鍣細閫夐」 = 鍦ㄧ嚎鏈哄彴 鈭?FAIL 鏄庣粏涓嚭鐜拌繃鐨勬満鍙帮紙淇濈暀褰撳墠閫変腑锛?*/
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
      var html = '<option value="">鍏ㄩ儴鏈哄彴锛堟眹鎬伙級</option>';
      for (var i = 0; i < set.length; i++) html += '<option value="' + App.esc(set[i]) + '">' + App.esc(set[i]) + "</option>";
      s.innerHTML = html;
    }
    if (set.indexOf(cur) >= 0) s.value = cur; else s.value = App.state.sel;
  }

  /* 鏁版嵁鍔犺浇锛氬苟琛屾媺鍥涗釜鎺ュ彛锛屾洿鏂扮姸鎬佸苟瑙﹀彂褰撳墠椤甸噸缁?*/
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
        if (sig !== lastSig) {           // 鏁版嵁鏈彉 鈫?璺宠繃鍏ㄩ儴閲嶇粯/閲嶆帓锛堜富瑕佽繍琛屾椂浼樺寲锛?          lastSig = sig;
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
    /* 鏈哄彴绛涢€夊叆鍙ｏ細鐢遍〉闈?椤舵爮璋冪敤鍚庨噸鎷夋暟鎹?*/
    filter: function (sel) {
      App.state.sel = sel || "";
      try { window.localStorage.setItem("agg.machine", App.state.sel); } catch (e) { }
      App.clearError();
      App.data.load();
    },
    /* 鍚勬満鍙版渶杩戜竴鏉?FAIL 鏃堕棿鏄犲皠锛坒ails 鎸?ingest 鍊掑簭锛?*/
    recentMap: function () {
      var recent = {};
      App.state.fails.forEach(function (f) {
        var m = App.pick(f, "machine");
        if (m && !recent[m]) recent[m] = App.pick(f, "ingestTs") || App.pick(f, "ts") || "";
      });
      return recent;
    },
    /* 瀵煎嚭 CSV 鍦板潃锛堝惈鏈哄彴杩囨护涓?token锛?*/
    exportUrl: function (machine) {
      var u = "/api/export.csv";
      var q = [];
      if (machine) q.push("machine=" + encodeURIComponent(machine));
      if (q.length) u += "?" + q.join("&");
      return App.withToken(u);
    },
    /* XML 鎶ュ憡椤靛湴鍧€锛堝湪绾挎煡鐪嬶紝鏈嶅姟绔覆鏌?HTML锛泃oken 鑷姩鎷兼帴锛?*/
    xmlUrl: function (id) { return App.withToken("/api/xmlview?id=" + id); },
    fileUrl: function (id) { return App.withToken("/api/file?id=" + id); }
  };

  /* ---------------- canvas 鏌辩姸鍥撅紙闆朵緷璧栵紝杩佽嚜 demo锛?---------------- */
  App.drawBar = function (cv, data) {
    if (!cv || !cv.getContext) return;
    var dpr = window.devicePixelRatio || 1;
    var w = cv.clientWidth || 300, h = 120;
    cv.width = w * dpr; cv.height = h * dpr;
    var g = cv.getContext("2d");
    g.scale(dpr, dpr); g.clearRect(0, 0, w, h);
    var rows = (data || []).map(function (x) {
      return { name: App.pick(x, "machine") || "-", v: App.pick(x, "failCount") || 0 };
    }).sort(function (a, b) { return b.v - a.v; }).slice(0, 12);
    if (!rows.length) {
      g.fillStyle = "#5E6874"; g.font = "12px sans-serif";
      g.fillText("鏆傛棤鏁版嵁", 8, 16);
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
      g.fillText(d.name.length > 8 ? d.name.slice(0, 7) + "..." : d.name, 0, 0);
      g.restore();
    });
  };

  /* ---------------- 鍏ㄥ眬鎼滅储 UI锛堥《鏍忥級 ---------------- */
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
      var html = '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:6px"><span style="font-size:11px;color:var(--dim)">鍏?' + data.total + ' 鏉?路 FAIL ' + (counts.fails||0) + ' 路 缁翠慨 ' + (counts.maintenance||0) + ' 路 璁惧 ' + (counts.devices||0) + ' 路 鑹巼 ' + (counts.yields||0) + '</span><span style="cursor:pointer;color:var(--dim)" id="searchClose">鉁?/span></div>';
      var groups = [
        { key: "fails", label: "FAIL 鏄庣粏", icon: "鈿? },
        { key: "maintenance", label: "缁翠慨", icon: "馃洜" },
        { key: "devices", label: "璁惧", icon: "鈻? },
        { key: "yields", label: "鑹巼", icon: "鈼? }
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
          var title = App.esc(it.title || it.Title || "-");
          var sub = App.esc(it.subtitle || it.Subtitle || "");
          var badge = it.type || g.key;
          html += '<div class="search-item" data-link="' + App.esc(it.link || it.Link || "") + '"><span class="badge">' + App.esc(badge) + '</span><span class="t">' + title + '</span><span class="s">' + sub + '</span></div>';
        }
        html += '</div>';
      }
      if (!has) html += '<div class="empty">鏃犲尮閰嶇粨鏋?/div>';
      // 鏀惰棌蹇嵎
      var favs = App.favorites.get();
      if (favs.length) {
        html += '<div class="search-group"><h4>鏀惰棌绛涢€?/h4>';
        for (var fi = 0; fi < favs.length; fi++) {
          var fv = favs[fi];
          html += '<div class="search-item" data-q="' + App.esc(fv) + '"><span class="badge">鈽?/span><span class="t">' + App.esc(fv) + '</span><span class="s">鐐瑰嚮濉叆鎼滅储</span></div>';
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
    // KPI 涓嬮捇鑱斿姩锛氱洃鍚?KPI 鐐瑰嚮锛坥verview 骞挎挱浜嬩欢锛?    window.addEventListener("argus:drill", function (ev) {
      var d = ev.detail || {};
      if (d.q) { inp.value = d.q; doSearch(d.q); }
      if (d.link) App.search.go(d.link);
    });
  }

  /* ---------------- 缁熶竴娓叉煋缁勪欢 ---------------- */

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
    var emptyText = opts.emptyText || "鏆傛棤鏁版嵁";
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
    var html = '<div class="empty-state"><span class="es-icon">' + App.esc(icon || "馃搵") + '</span>'
      + '<div class="es-text">' + App.esc(text || "鏆傛棤鏁版嵁") + '</div>';
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

  /* ---------------- boot锛氶《鏍忎簨浠?+ 杞 ---------------- */
  App.boot = function () {
    var sel = document.getElementById("machineSel");
    if (sel) {
      var saved = "";
      try { saved = window.localStorage.getItem("agg.machine") || ""; } catch (e) { }
      App.state.sel = saved;
      sel.addEventListener("change", function (e) { App.data.filter(e.target.value); });
    }
    // 鏉冮檺涓庡竷灞€/鏀惰棌鍚庣鍚屾
    App.auth.refresh().then(function () {
      // 鐧诲綍鍚庢媺鍙?server layout/favorites 瑕嗙洊鏈湴锛堣嫢 server 鏈夊€硷級
      App.layout.loadFromServer().catch(function () {});
      App.favorites.loadFromServer().catch(function () {});
      App.applyRoleVisibility(document);
    });
    initGlobalSearch();
    App.data.load();
    window.setInterval(App.data.load, 3000);   // 3s 杞锛堜笌 demo 涓€鑷达級
    window.addEventListener("resize", function () {
      var h = (window.location.hash || "#/overview").replace(/^#\/?/, "");
      if (h === "overview" && App.Nav) App.Nav.rerender();
    });
  };
})(window, document);

/* ---------------- 域8 预警高亮：异常机台红边脉冲 ---------------- */
App.Highlight = {
  // 基于 /api/devices + /api/devices/predict 判断异常，返回 machine->reason 映射
  fetch: function(){
    return Promise.all([
      App.fetchJSON("/api/devices").catch(function(){ return []; }),
      App.fetchJSON("/api/devices/predict").catch(function(){ return {predicts:[]}; })
    ]).then(function(rs){
      var devices = Array.isArray(rs[0]) ? rs[0] : (rs[0].devices||[]);
      var preds = rs[1].predicts || rs[1] || [];
      var map = {};
      // 预测 critical 预警
      preds.forEach(function(p){
        var m = p.machine || p.Machine;
        var lv = (p.level||p.Level||"").toLowerCase();
        if(m && lv==="critical") map[m] = p.detail||p.Detail||"预测异常";
      });
      // 前端阈值兜底（device_info 本身也可判定，但后端已算好，这里仅做脉冲来源）
      return map;
    });
  },
  applyToCards: function(container, highlightMap){
    if(!container || !highlightMap) return;
    var cards = container.querySelectorAll(".machine");
    for(var i=0;i<cards.length;i++){
      var m = cards[i].getAttribute("data-m");
      if(m && highlightMap[m]){
        cards[i].classList.add("highlight-critical");
        cards[i].setAttribute("title", highlightMap[m]);
        // 置顶：异常卡前置
        if(cards[i].parentNode) cards[i].parentNode.insertBefore(cards[i], cards[i].parentNode.firstChild);
      }
    }
  }
};
