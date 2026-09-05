/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 程序调整日志（Lite-Fetch）
   public/js/page-proc.js
   ----------------
   能力：① 登记（POST /api/proc-log，版本/时间/人员/内容/影响机台/参数快照 JSON/关联报告）
         ② 时间轴（GET /api/proc-log/timeline，按 changed_at 倒序，机台维度过滤）
         ③ 参数 diff（GET /api/proc-log/diff?id1=&id2=，added/removed/changed 高亮）
   零依赖、Theme 黑白红、token 透传、viewer 只读隐藏登记、黑白红令牌、dataSig。
   ============================================================ */
(function(window){
  "use strict";
  var App = window.App;
  var refs={};
  var lastSig="";
  var curList=[];

  function esc(s){ return App.esc(s); }
  function apiGet(path){
    var u=window.location.origin+path;
    if(App.token) u=App.withToken(u);
    return fetch(u,{credentials:"same-origin",headers:{Accept:"application/json"}}).then(function(r){
      if(r.status===401||r.status===403){ App.toast("未授权","err"); throw new Error("HTTP "+r.status); }
      if(!r.ok) throw new Error("HTTP "+r.status);
      return r.json();
    });
  }
  function apiPost(path, body){
    var u=window.location.origin+path;
    if(App.token) u=App.withToken(u);
    return fetch(u,{method:"POST",credentials:"same-origin",headers:{"Content-Type":"application/json",Accept:"application/json"},body:JSON.stringify(body)}).then(function(r){
      if(r.status===401||r.status===403){ App.toast("权限不足需 engineer","err"); throw new Error("HTTP "+r.status); }
      if(!r.ok) return r.text().then(function(t){ throw new Error(t||("HTTP "+r.status)); });
      var ct=r.headers.get("content-type")||"";
      if(ct.indexOf("application/json")>=0) return r.json();
      return r.text();
    });
  }

  function fillMachineSel(){
    if(!refs.filterMachine) return;
    var cur=refs.filterMachine.value;
    var machines=(App.state.machines||[]).map(function(m){return App.pick(m,"machine")||"";}).filter(function(x){return !!x;});
    machines.sort(function(a,b){return a.localeCompare(b,"zh");});
    var html='<option value="">全部机台</option>';
    for(var i=0;i<machines.length;i++) html+='<option value="'+esc(machines[i])+'">'+esc(machines[i])+'</option>';
    refs.filterMachine.innerHTML=html;
    if(machines.indexOf(cur)>=0) refs.filterMachine.value=cur;
    // 登记表单的 scope 多选也同步
    if(refs.scopeSel){
      var scCur=Array.from(refs.scopeSel.selectedOptions||[]).map(function(o){return o.value;});
      var shtml="";
      for(var j=0;j<machines.length;j++) shtml+='<option value="'+esc(machines[j])+'">'+esc(machines[j])+'</option>';
      refs.scopeSel.innerHTML=shtml;
      // 回填
      for(var k=0;k<refs.scopeSel.options.length;k++) if(scCur.indexOf(refs.scopeSel.options[k].value)>=0) refs.scopeSel.options[k].selected=true;
    }
  }

  function loadTimeline(){
    var machine=(refs.filterMachine.value||"").trim();
    var limit=parseInt(refs.limitSel.value,10)||50;
    var q="/api/proc-log/timeline?limit="+limit+(machine?"&machine="+encodeURIComponent(machine):"");
    if(refs.timeline) refs.timeline.innerHTML='<div class="view-loading">加载时间轴…</div>';
    apiGet(q).then(function(list){
      curList=Array.isArray(list)?list:[];
      renderTimeline();
      fillDiffSels();
    }).catch(function(e){
      if(refs.timeline) refs.timeline.innerHTML='<div class="empty">加载失败 '+esc(e.message)+'</div>';
    });
  }

  function renderTimeline(){
    if(!refs.timeline) return;
    if(!curList.length){ refs.timeline.innerHTML='<div class="empty">暂无程序调整日志（登记后按时间轴回溯）</div>'; return; }
    var html='<div class="proc-timeline">';
    // 按日期分组
    var groups={};
    var order=[];
    for(var i=0;i<curList.length;i++){
      var e=curList[i];
      var date=(e.changed_at||e.ChangedAt||"").slice(0,10)||"未知日期";
      if(!groups[date]){ groups[date]=[]; order.push(date); }
      groups[date].push(e);
    }
    for(var gi=0;gi<order.length;gi++){
      var d=order[gi];
      html+='<div class="proc-date">'+esc(d)+'</div>';
      var arr=groups[d];
      for(var j=0;j<arr.length;j++){
        var r=arr[j];
        var id=r.id||r.Id;
        var ver=r.version||r.Version||"";
        var at=r.changed_at||r.ChangedAt||"";
        var by=r.changed_by||r.ChangedBy||"";
        var content=r.content||r.Content||"";
        var scope=r.scope_machines||r.ScopeMachines||"";
        var params=r.params_snapshot||r.ParamsSnapshot||"";
        var scopeTxt="";
        try{ var arrM=JSON.parse(scope); if(Array.isArray(arrM)) scopeTxt=arrM.join(", "); }catch(e){ scopeTxt=scope||""; }
        var paramsPreview="";
        try{ var obj=JSON.parse(params); if(obj&&typeof obj==="object"&&!Array.isArray(obj)) paramsPreview=Object.keys(obj).slice(0,3).join(", "); }catch(e){}
        html+='<div class="proc-item" data-id="'+id+'">'
          +'<div class="proc-dot"></div>'
          +'<div class="proc-card">'
          +'<div style="display:flex;justify-content:space-between;align-items:center">'
          +'<b>'+esc(ver)+'</b> <span style="color:var(--dim);font-size:11px">'+esc(at)+' · '+esc(by)+'</span>'
          +'<span style="display:flex;gap:6px"><a class="op" href="#" data-detail="'+id+'">详情</a> <a class="op" href="#" data-diff-a="'+id+'">设为A</a> <a class="op" href="#" data-diff-b="'+id+'">设为B</a></span>'
          +'</div>'
          +'<div style="margin-top:4px;color:var(--ink)">'+esc(content||"—")+'</div>'
          +(scopeTxt?'<div style="margin-top:4px;color:var(--dim);font-size:11px">影响机台：'+esc(scopeTxt)+'</div>':'')
          +(paramsPreview?'<div style="margin-top:2px;color:var(--faint);font-size:11px">参数：'+esc(paramsPreview)+(Object.keys(JSON.parse(params||"{}")).length>3?" …":"")+'</div>':'')
          +'</div></div>';
      }
    }
    html+='</div>';
    refs.timeline.innerHTML=html;
    // 绑定
    var details=refs.timeline.querySelectorAll("[data-detail]");
    for(var k=0;k<details.length;k++) details[k].addEventListener("click", function(e){ e.preventDefault(); showDetail(this.getAttribute("data-detail")); });
    var as=refs.timeline.querySelectorAll("[data-diff-a]");
    for(var a=0;a<as.length;a++) as[a].addEventListener("click", function(e){ e.preventDefault(); setDiffA(this.getAttribute("data-diff-a")); });
    var bs=refs.timeline.querySelectorAll("[data-diff-b]");
    for(var b=0;b<bs.length;b++) bs[b].addEventListener("click", function(e){ e.preventDefault(); setDiffB(this.getAttribute("data-diff-b")); });
  }

  function showDetail(id){
    if(!id) return;
    apiGet("/api/proc-log/detail?id="+encodeURIComponent(id)).then(function(e){
      var html='<div class="card" style="position:fixed;left:50%;top:50%;transform:translate(-50%,-50%);z-index:1000;max-width:560px;width:90%;max-height:80vh;overflow:auto;background:var(--card);border:1px solid var(--line);box-shadow:var(--shadow)">'
        +'<div style="display:flex;justify-content:space-between;align-items:center;padding:12px 16px;border-bottom:1px solid var(--line)"><b>程序日志 #'+esc(String(e.id||id))+' · '+esc(e.version||"")+'</b> <span style="cursor:pointer" data-close>✕</span></div>'
        +'<div style="padding:12px 16px;display:flex;flex-direction:column;gap:8px;font-size:12px">'
        +'<div><span style="color:var(--dim)">变更时间</span> '+esc(e.changed_at||"")+'</div>'
        +'<div><span style="color:var(--dim)">变更人</span> '+esc(e.changed_by||"")+'</div>'
        +'<div><span style="color:var(--dim)">内容</span> '+esc(e.content||"")+'</div>'
        +'<div><span style="color:var(--dim)">影响机台</span> '+esc(e.scope_machines||"")+'</div>'
        +'<div><span style="color:var(--dim)">参数快照</span> <pre style="white-space:pre-wrap;word-break:break-all;background:var(--bg2);padding:8px;border-radius:8px">'+esc(formatJson(e.params_snapshot||""))+'</pre></div>'
        +'<div><span style="color:var(--dim)">关联报告</span> '+esc(e.related_reports||"")+'</div>'
        +'</div><div style="padding:10px 16px;display:flex;justify-content:flex-end"><button class="act" data-close>关闭</button></div></div>'
        +'<div style="position:fixed;inset:0;background:rgba(0,0,0,.35);z-index:999" data-close></div>';
      var wrap=document.createElement("div"); wrap.innerHTML=html; wrap.style.position="fixed"; wrap.style.inset="0"; wrap.style.zIndex="1000";
      document.body.appendChild(wrap);
      var closes=wrap.querySelectorAll("[data-close]");
      for(var i=0;i<closes.length;i++) closes[i].addEventListener("click", function(){ wrap.remove(); });
    }).catch(function(e){ App.toast("详情加载失败 "+e.message,"err"); });
  }
  function formatJson(s){
    try{ var o=JSON.parse(s); return JSON.stringify(o,null,2); }catch(e){ return s||""; }
  }

  function fillDiffSels(){
    if(!refs.diffA||!refs.diffB) return;
    var curA=refs.diffA.value, curB=refs.diffB.value;
    var html='<option value="">— 请选择 —</option>';
    for(var i=0;i<curList.length;i++){
      var r=curList[i]; var id=r.id||r.Id; var ver=r.version||r.Version||""; var at=r.changed_at||r.ChangedAt||"";
      html+='<option value="'+id+'">#'+id+' '+esc(ver)+' '+esc(at.slice(0,16))+'</option>';
    }
    refs.diffA.innerHTML=html; refs.diffB.innerHTML=html;
    if(curA) refs.diffA.value=curA;
    if(curB) refs.diffB.value=curB;
  }
  function setDiffA(id){ if(refs.diffA){ refs.diffA.value=id; doDiff(); } }
  function setDiffB(id){ if(refs.diffB){ refs.diffB.value=id; doDiff(); } }
  function doDiff(){
    var a=(refs.diffA.value||"").trim(), b=(refs.diffB.value||"").trim();
    if(!a||!b){ App.toast("请各选一个版本作为 A/B","err"); return; }
    if(refs.diffStatus) refs.diffStatus.textContent="对比中…";
    apiGet("/api/proc-log/diff?id1="+encodeURIComponent(a)+"&id2="+encodeURIComponent(b)).then(function(diff){
      renderDiff(diff);
      if(refs.diffStatus) refs.diffStatus.textContent="对比完成：新增 "+(diff.added?diff.added.length:0)+" · 移除 "+(diff.removed?diff.removed.length:0)+" · 变更 "+(diff.changed?diff.changed.length:0);
    }).catch(function(e){ if(refs.diffStatus) refs.diffStatus.textContent="对比失败 "+e.message; App.toast("diff 失败 "+e.message,"err"); });
  }
  function renderDiff(diff){
    if(!refs.diffBody) return;
    if(!diff){ refs.diffBody.innerHTML='<div class="empty">无 diff</div>'; return; }
    var html='<div style="display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px;margin-bottom:10px">'
      +'<div style="border:1px solid var(--line);border-radius:8px;padding:8px"><div style="color:var(--dim)">新增</div><div style="font-weight:600;color:var(--red)">'+(diff.added?diff.added.length:0)+'</div>'+((diff.added||[]).slice(0,10).map(function(k){return '<div style="font-size:11px">'+esc(k)+'</div>';}).join(""))+'</div>'
      +'<div style="border:1px solid var(--line);border-radius:8px;padding:8px"><div style="color:var(--dim)">移除</div><div style="font-weight:600">'+(diff.removed?diff.removed.length:0)+'</div>'+((diff.removed||[]).slice(0,10).map(function(k){return '<div style="font-size:11px">'+esc(k)+'</div>';}).join(""))+'</div>'
      +'<div style="border:1px solid var(--line);border-radius:8px;padding:8px"><div style="color:var(--dim)">变更</div><div style="font-weight:600;color:var(--red)">'+(diff.changed?diff.changed.length:0)+'</div></div></div>';
    if(diff.changed&&diff.changed.length){
      html+='<table><thead><tr><th>参数</th><th>前值</th><th>后值</th></tr></thead><tbody>';
      for(var i=0;i<diff.changed.length;i++){
        var c=diff.changed[i];
        html+='<tr class="fail"><td>'+esc(c.key||c.Key||"")+'</td><td style="background:rgba(255,92,92,.08)">'+esc(c.before||c.Before||"")+'</td><td style="background:var(--red-soft)">'+esc(c.after||c.After||"")+'</td></tr>';
      }
      html+='</tbody></table>';
    }
    if(diff.unchanged&&diff.unchanged.length){
      html+='<div style="margin-top:8px;color:var(--dim);font-size:11px">未变更：'+esc(diff.unchanged.slice(0,10).join(", "))+(diff.unchanged.length>10?" …":"")+'</div>';
    }
    refs.diffBody.innerHTML=html;
  }

  function doCreate(){
    if(App.auth&&App.auth.isViewer&&App.auth.isViewer()){ App.toast("访客无登记权限","err"); return; }
    var version=(refs.ver.value||"").trim();
    if(!version){ App.toast("版本号必填","err"); return; }
    var changedAt=(refs.at.value||"").trim();
    if(changedAt) changedAt=changedAt.replace("T"," ");
    var changedBy=(refs.by.value||"").trim();
    var content=(refs.content.value||"").trim();
    var scope=[];
    if(refs.scopeSel){
      for(var i=0;i<refs.scopeSel.options.length;i++) if(refs.scopeSel.options[i].selected) scope.push(refs.scopeSel.options[i].value);
    }
    var paramsRaw=(refs.params.value||"").trim();
    // 校验 JSON
    if(paramsRaw){
      try{ JSON.parse(paramsRaw); }catch(e){ App.toast("参数快照不是合法 JSON","err"); return; }
    } else paramsRaw="{}";
    var body={version:version, changed_at:changedAt||new Date().toISOString().slice(0,19).replace("T"," "), changed_by:changedBy, content:content, scope_machines:JSON.stringify(scope), params_snapshot:paramsRaw, related_reports:"[]"};
    apiPost("/api/proc-log", body).then(function(res){
      App.toast("已登记 #"+(res.id||""),"ok");
      // 清空
      refs.ver.value=""; refs.at.value=""; refs.by.value=""; refs.content.value=""; refs.params.value="{}";
      if(refs.scopeSel) for(var k=0;k<refs.scopeSel.options.length;k++) refs.scopeSel.options[k].selected=false;
      loadTimeline();
    }).catch(function(e){ App.toast("登记失败 "+e.message,"err"); });
  }

  function render(ctx){
    fillMachineSel();
    var curSig=JSON.stringify(App.state.machines);
    if(curSig===lastSig) return;
    lastSig=curSig;
  }

  App.Modules["page-proc"]={
    init:function(el,ctx){
      el.innerHTML=
        '<div class="page-node">'
        +'<div class="card"><h2>程序调整日志 <span class="n">登记/时间轴/机台维度/参数 diff · 登记后按 changed_at 倒序回溯</span></h2>'
        +'<div class="toolbar" style="flex-wrap:wrap">'
        +'<select data-ref="filterMachine" title="机台维度" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"><option value="">全部机台</option></select>'
        +'<select data-ref="limitSel" title="条数"><option value="20">20</option><option value="50" selected>50</option><option value="100">100</option></select>'
        +'<button class="act" data-ref="btnRefresh">刷新时间轴</button>'
        +'</div>'
        +'<div data-ref="timeline" style="margin-top:12px"></div>'
        +'</div>'
        +'<div class="card"><h2>登记 <span class="n">版本/时间/人员/内容/影响机台/参数快照 JSON</span></h2>'
        +'<div style="display:grid;grid-template-columns:1fr 1fr;gap:10px">'
        +'<label style="display:flex;flex-direction:column;gap:4px;font-size:12px;color:var(--dim)">版本号 *<input data-ref="ver" placeholder="如 v3.16.0" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:0 10px"></label>'
        +'<label style="display:flex;flex-direction:column;gap:4px;font-size:12px;color:var(--dim)">变更时间<input data-ref="at" type="datetime-local" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:0 10px"></label>'
        +'<label style="display:flex;flex-direction:column;gap:4px;font-size:12px;color:var(--dim)">变更人<input data-ref="by" placeholder="如 张三" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:0 10px"></label>'
        +'<label style="display:flex;flex-direction:column;gap:4px;font-size:12px;color:var(--dim)">影响机台（多选）<select data-ref="scopeSel" multiple style="height:80px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:6px"></select></label>'
        +'</div>'
        +'<label style="display:flex;flex-direction:column;gap:4px;margin-top:10px;font-size:12px;color:var(--dim)">变更内容<textarea data-ref="content" rows="2" placeholder="本次变更说明" style="border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:8px"></textarea></label>'
        +'<label style="display:flex;flex-direction:column;gap:4px;margin-top:10px;font-size:12px;color:var(--dim)">参数快照 JSON（用于 diff 高亮）<textarea data-ref="params" rows="4" placeholder=\'{"paramA":"1","paramB":"2"}\' style="border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:8px;font-family:Consolas,monospace">{"example":"value"}</textarea></label>'
        +'<div style="margin-top:10px;display:flex;gap:8px"><button class="act" data-ref="btnCreate">登记</button> <span class="n">登记需 engineer 权限，时间轴按 changed_at 倒序</span></div>'
        +'</div>'
        +'<div class="card"><h2>参数对比 <span class="n">选择两个版本，diff 高亮（新增/移除/变更）</span></h2>'
        +'<div style="display:grid;grid-template-columns:1fr 1fr auto;gap:10px;align-items:center">'
        +'<select data-ref="diffA" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"><option value="">— 请选择 A —</option></select>'
        +'<select data-ref="diffB" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"><option value="">— 请选择 B —</option></select>'
        +'<button class="act" data-ref="btnDiff">对比</button></div>'
        +'<div data-ref="diffStatus" class="n" style="margin-top:6px"></div>'
        +'<div data-ref="diffBody" style="margin-top:10px;overflow:auto"></div></div>'
        +'<div class="foot">程序调整日志：登记 → 时间轴回溯（机台维度）→ 参数 diff 高亮 → 联动报告与良率（hash 跳转）</div>'
        +'</div>'
        +'<style>.proc-timeline{position:relative;padding-left:18px;border-left:2px solid var(--line)}'
        +'.proc-date{font-size:13px;font-weight:600;margin:14px 0 8px;color:var(--ink)}'
        +'.proc-item{position:relative;margin-bottom:12px}'
        +'.proc-dot{position:absolute;left:-26px;top:8px;width:10px;height:10px;border-radius:50%;background:var(--red);border:2px solid var(--card)}'
        +'.proc-card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px 12px}'
        +'</style>';
      var nodes=el.querySelectorAll("[data-ref]");
      for(var i=0;i<nodes.length;i++) refs[nodes[i].getAttribute("data-ref")]=nodes[i];
      if(refs.btnRefresh) refs.btnRefresh.addEventListener("click", loadTimeline);
      if(refs.filterMachine) refs.filterMachine.addEventListener("change", loadTimeline);
      if(refs.limitSel) refs.limitSel.addEventListener("change", loadTimeline);
      if(refs.btnCreate) refs.btnCreate.addEventListener("click", doCreate);
      if(refs.btnDiff) refs.btnDiff.addEventListener("click", doDiff);
      // 权限显隐
      if(refs.btnCreate) refs.btnCreate.classList.add("hide-for-viewer");
      if(App.applyRoleVisibility) App.applyRoleVisibility(el);
      render(ctx);
      loadTimeline();
    },
    render:render
  };
})(window);
