/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 报告中心（Lite-Fetch）
   public/js/page-report.js
   ----------------
   能力：① 检索（POST /api/mesh/query 扇出，含 PASS）、② 预览（/api/xmlview + /api/report/summary，ParsedReportData BATCH/PANEL/DUT/TEST 明细，口径与 XmlReportHtml 一致）、
         ③ 摘要（KPI 四卡 + 信息网格 + 测试项表，失败标红排除项灰）、④ 归档（POST /api/report/archive + GET 列表）、⑤ 版本对比（GET /api/report/compare，before/after 高亮）。
   零依赖、Theme 黑白红、dataSig、prefers-reduced-motion、token 透传、viewer 只读隐藏归档/对比写操作。
   ============================================================ */
(function(window){
  "use strict";
  var App = window.App;
  var refs={};
  var lastSig="";
  var curResults=[];
  var curPreviewPath="";
  var curCompareA=null, curCompareB=null;

  function esc(s){ return App.esc(s); }
  function apiGet(path){
    var u=window.location.origin+path;
    if(App.token) u=App.withToken(u);
    return fetch(u,{credentials:"same-origin",headers:{Accept:"application/json"}}).then(function(r){
      if(r.status===401||r.status===403){ App.toast("未授权请带 ?token=","err"); throw new Error("HTTP "+r.status); }
      if(!r.ok) throw new Error("HTTP "+r.status);
      var ct=r.headers.get("content-type")||"";
      if(ct.indexOf("application/json")>=0) return r.json();
      return r.text();
    });
  }
  function apiPost(path, body){
    var u=window.location.origin+path;
    if(App.token) u=App.withToken(u);
    return fetch(u,{method:"POST",credentials:"same-origin",headers:{"Content-Type":"application/json",Accept:"application/json"},body:JSON.stringify(body)}).then(function(r){
      if(r.status===401||r.status===403){ App.toast("权限不足","err"); throw new Error("HTTP "+r.status); }
      if(!r.ok) return r.text().then(function(t){ throw new Error(t||("HTTP "+r.status)); });
      var ct=r.headers.get("content-type")||"";
      if(ct.indexOf("application/json")>=0) return r.json();
      return r.text();
    });
  }

  function fillMachineSel(){
    if(!refs.qMachine) return;
    var cur=refs.qMachine.value;
    var machines=(App.state.machines||[]).map(function(m){return App.pick(m,"machine")||"";}).filter(function(x){return !!x;});
    machines.sort(function(a,b){return a.localeCompare(b,"zh");});
    var html='<option value="">全部机台</option>';
    for(var i=0;i<machines.length;i++) html+='<option value="'+esc(machines[i])+'">'+esc(machines[i])+'</option>';
    refs.qMachine.innerHTML=html;
    if(machines.indexOf(cur)>=0) refs.qMachine.value=cur;
  }

  function doQuery(){
    var req={
      machine:(refs.qMachine.value||"").trim(),
      sn:(refs.qSn.value||"").trim(),
      model:(refs.qModel.value||"").trim(),
      result:(refs.qResult.value||"").trim()||"ALL",
      date_from:(refs.qFrom.value||"").trim().replace(/-/g,""),
      date_to:(refs.qTo.value||"").trim().replace(/-/g,""),
      limit: parseInt(refs.qLimit.value,10)||100,
      offset:0
    };
    if(refs.qStatus) refs.qStatus.textContent="检索中…（并发查各机台，含 PASS，5 分钟缓存）";
    if(refs.btnQuery) refs.btnQuery.disabled=true;
    var url=window.location.origin+"/api/mesh/query";
    if(App.token) url=App.withToken(url);
    fetch(url,{method:"POST",credentials:"same-origin",headers:{"Content-Type":"application/json",Accept:"application/json"},body:JSON.stringify(req)}).then(function(r){
      if(r.status===401||r.status===403){ App.toast("未授权","err"); throw new Error("HTTP "+r.status); }
      if(!r.ok) throw new Error("HTTP "+r.status);
      return r.json();
    }).then(function(data){
      curResults=(data&&data.results)||[];
      renderResultTable(data);
      if(refs.qStatus){
        var peers=data.peers||[];
        var online=peers.filter(function(p){return p.Online;}).length;
        var cached=data.cached?"（缓存命中）":"";
        refs.qStatus.textContent="共 "+(data.total||0)+" 条"+cached+" · 在线机台 "+online+"/"+peers.length+" · 耗时 "+(data.elapsed_ms||0)+"ms";
      }
    }).catch(function(e){
      if(refs.qStatus) refs.qStatus.textContent="检索失败："+e.message;
      App.toast("检索失败 "+e.message,"err");
    }).finally(function(){ if(refs.btnQuery) refs.btnQuery.disabled=false; });
  }

  function renderResultTable(data){
    var list=(data&&data.results)||[];
    if(!refs.qTbody) return;
    if(!list.length){ refs.qTbody.innerHTML=""; if(refs.qEmpty) refs.qEmpty.style.display="block"; return; }
    if(refs.qEmpty) refs.qEmpty.style.display="none";
    var html="";
    for(var i=0;i<list.length;i++){
      var x=list[i];
      var machine=x.Machine||x.machine||"—";
      var sn=x.Sn||x.sn||"—";
      var model=x.Model||x.model||"—";
      var result=(x.Result||x.result||"").toUpperCase()||"—";
      var date=x.TestDate||x.testDate||"";
      var fail=x.FailReason||x.failReason||"—";
      var size=x.FileSize!=null?x.FileSize:(x.fileSize||0);
      var path=x.XmlPath||x.xmlPath||"";
      var id=x.Id||x.id||0;
      var badge=result==="FAIL"?App.badge("FAIL","fail"):result==="PASS"?App.badge("PASS","pass"):App.badge(result,"");
      var preview="";
      if(path) preview='<a class="op" href="#" data-path="'+esc(path)+'" data-machine="'+esc(machine)+'">预览</a><a class="op" href="'+App.withToken("/api/file?path="+encodeURIComponent(path))+'" download>下载</a> <a class="op" href="#" data-archive="'+esc(path)+'" data-m="'+esc(machine)+'" data-sn="'+esc(sn)+'">归档</a>';
      else if(id) preview='<a class="op" href="'+App.data.xmlUrl(id)+'" target="_blank">报告</a>';
      var cmp='<a class="op" href="#" data-cmp="'+(path?esc(path):id)+'" data-machine="'+esc(machine)+'">对比</a>';
      html+="<tr><td>"+esc(machine)+"</td><td>"+esc(sn)+"</td><td>"+esc(model)+"</td><td>"+badge+"</td><td>"+esc(date)+"</td><td title='"+esc(fail)+"'>"+esc(fail.slice(0,32))+"</td><td>"+size+"</td><td>"+preview+" "+cmp+"</td></tr>";
    }
    refs.qTbody.innerHTML=html;
    var links=refs.qTbody.querySelectorAll("a[data-path]");
    for(var k=0;k<links.length;k++) links[k].addEventListener("click", function(e){ e.preventDefault(); var p=this.getAttribute("data-path"); var m=this.getAttribute("data-machine"); loadSummary(m,p); curPreviewPath=p; if(refs.frame) refs.frame.src=App.withToken("/api/xmlview?path="+encodeURIComponent(p)+(m?"&machine="+encodeURIComponent(m):"")); });
    var archs=refs.qTbody.querySelectorAll("a[data-archive]");
    for(var a=0;a<archs.length;a++) archs[a].addEventListener("click", function(e){ e.preventDefault(); var p=this.getAttribute("data-archive"); var m=this.getAttribute("data-m"); var sn=this.getAttribute("data-sn"); doArchive(m,sn,p); });
    var cmps=refs.qTbody.querySelectorAll("a[data-cmp]");
    for(var c=0;c<cmps.length;c++) cmps[c].addEventListener("click", function(e){ e.preventDefault(); var p=this.getAttribute("data-cmp"); var m=this.getAttribute("data-machine"); pickCompare(m,p); });
  }

  function loadSummary(machine, xmlPath){
    if(!xmlPath) return;
    if(refs.summary) refs.summary.innerHTML='<div class="view-loading">加载摘要…</div>';
    var url="/api/report/summary?path="+encodeURIComponent(xmlPath);
    if(machine) url+="&machine="+encodeURIComponent(machine);
    apiGet(url).then(function(s){
      renderSummary(s);
    }).catch(function(e){
      if(refs.summary) refs.summary.innerHTML='<div class="empty">摘要加载失败：'+esc(e.message)+'</div>';
    });
  }

  function renderSummary(s){
    if(!refs.summary) return;
    if(!s||!s.ok){ refs.summary.innerHTML='<div class="empty">无摘要</div>'; return; }
    var badge=s.panelStatus==="Passed"||s.panelStatus==="PASS"?App.badge("PASS","pass"):App.badge(s.panelStatus||"FAIL","fail");
    var html='<div class="kpis" style="margin-bottom:12px">'
      +'<div class="kpi"><div class="v">'+(s.total||0)+'</div><div class="k">测试项总数</div></div>'
      +'<div class="kpi"><div class="v" style="color:var(--red)">'+(s.fail||0)+'</div><div class="k">失败(计不良)</div></div>'
      +'<div class="kpi"><div class="v" style="color:var(--dim)">'+(s.ignored||0)+'</div><div class="k">排除项</div></div>'
      +'<div class="kpi"><div class="v">'+(s.pass||0)+'</div><div class="k">通过项</div></div></div>';
    html+='<div class="card" style="padding:10px 14px"><div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));gap:8px;font-size:12px">'
      +'<div><span style="color:var(--dim)">SN</span> <b>'+esc(s.sn||"—")+'</b> '+badge+'</div>'
      +'<div><span style="color:var(--dim)">机台</span> '+esc(s.tester||"—")+'</div>'
      +'<div><span style="color:var(--dim)">测试时间</span> '+esc(s.panelTimestamp||s.batchTimestamp||"—")+'</div>'
      +'<div><span style="color:var(--dim)">文件名</span> '+esc(s.fileName||"")+'</div>'
      +'</div></div>';
    html+='<div style="overflow:auto"><table><thead><tr><th>#</th><th>测试项</th><th>测量值</th><th>下限</th><th>上限</th><th>单位</th><th>状态</th></tr></thead><tbody>';
    var tests=s.tests||[];
    for(var i=0;i<tests.length;i++){
      var t=tests[i];
      var st=(t.status||"").toUpperCase();
      var cls=t.ignored?"ign":st==="FAILED"||st==="FAIL"?"fail":"";
      var statusText=t.ignored?"排除·不计入不良":st||"—";
      var stCls=t.ignored?"st-ign":st==="FAILED"||st==="FAIL"?"st-fail":"st-pass";
      html+='<tr class="'+cls+'"><td>'+(t.idx||i+1)+'</td><td>'+esc(t.name||"")+'</td><td>'+esc(t.value||"-")+'</td><td>'+esc(t.lolim||"-")+'</td><td>'+esc(t.hilim||"-")+'</td><td>'+esc(t.unit||"-")+'</td><td class="status '+stCls+'">'+esc(statusText)+'</td></tr>';
    }
    html+='</tbody></table></div>';
    html+='<div style="margin-top:8px;color:var(--dim);font-size:11px">'+esc(s.summaryText||"")+' · 口径与 XmlReportHtml 一致（BATCH/PANEL/DUT/TEST 明细）</div>';
    refs.summary.innerHTML=html;
  }

  function doArchive(machine, sn, path){
    if(App.auth&&App.auth.isViewer&&App.auth.isViewer()){ App.toast("访客无归档权限","err"); return; }
    var note=prompt("归档备注（可选）：","")||"";
    apiPost("/api/report/archive",{machine:machine,sn:sn,xml_path:path,note:note}).then(function(res){
      App.toast("已归档 #"+(res.id||""),"ok");
      loadArchiveList();
    }).catch(function(e){ App.toast("归档失败 "+e.message,"err"); });
  }

  function loadArchiveList(){
    if(!refs.archiveBody) return;
    var machine=(refs.arcMachine&&refs.arcMachine.value||"").trim();
    var q="/api/report/archive?limit=50"+(machine?"&machine="+encodeURIComponent(machine):"");
    apiGet(q).then(function(list){
      var arr=Array.isArray(list)?list:[];
      if(!arr.length){ refs.archiveBody.innerHTML='<tr><td colspan="6" class="empty">暂无归档</td></tr>'; return; }
      var html="";
      for(var i=0;i<arr.length;i++){
        var r=arr[i];
        html+='<tr><td>'+esc(r.machine||"")+'</td><td>'+esc(r.sn||"")+'</td><td>'+esc(r.model||"")+'</td><td>'+esc(r.archived_at||"")+'</td><td>'+esc(r.archived_by||"")+'</td><td><a class="op" href="'+App.withToken("/api/file?path="+encodeURIComponent(r.archived_path||r.xml_path||""))+'" target="_blank">查看</a> <a class="op" href="#" data-cmp-arch="'+esc(r.xml_path||"")+'">对比</a></td></tr>';
      }
      refs.archiveBody.innerHTML=html;
      var cmps=refs.archiveBody.querySelectorAll("[data-cmp-arch]");
      for(var k=0;k<cmps.length;k++) cmps[k].addEventListener("click", function(e){ e.preventDefault(); var p=this.getAttribute("data-cmp-arch"); pickCompare("",p); });
    }).catch(function(e){ if(refs.archiveBody) refs.archiveBody.innerHTML='<tr><td colspan="6" class="empty">加载失败 '+esc(e.message)+'</td></tr>'; });
  }

  function pickCompare(machine, path){
    if(!curCompareA){ curCompareA={machine:machine,path:path}; App.toast("已选对比 A："+path.slice(-24),"ok"); if(refs.cmpA) refs.cmpA.textContent=path; return; }
    if(!curCompareB){ curCompareB={machine:machine,path:path}; if(refs.cmpB) refs.cmpB.textContent=path; doDiff(); return; }
    curCompareA={machine:machine,path:path}; curCompareB=null; if(refs.cmpA) refs.cmpA.textContent=path; if(refs.cmpB) refs.cmpB.textContent="— 请选择 B —"; App.toast("已重置对比 A","ok");
  }
  function doDiff(){
    if(!curCompareA||!curCompareB) { App.toast("请先各选一个报告作为 A/B","err"); return; }
    var url="/api/report/compare?path1="+encodeURIComponent(curCompareA.path)+"&path2="+encodeURIComponent(curCompareB.path);
    if(curCompareA.machine) url+="&machine1="+encodeURIComponent(curCompareA.machine);
    if(curCompareB.machine) url+="&machine2="+encodeURIComponent(curCompareB.machine);
    if(refs.cmpStatus) refs.cmpStatus.textContent="对比中…";
    apiGet(url).then(function(diff){
      renderDiff(diff);
      if(refs.cmpStatus) refs.cmpStatus.textContent="对比完成："+(diff.tests?diff.tests.length:0)+" 项";
    }).catch(function(e){ if(refs.cmpStatus) refs.cmpStatus.textContent="对比失败 "+e.message; App.toast("对比失败 "+e.message,"err"); });
  }
  function renderDiff(diff){
    if(!refs.cmpBody) return;
    if(!diff||!diff.tests){ refs.cmpBody.innerHTML='<div class="empty">无对比数据</div>'; return; }
    var html='<div style="display:grid;grid-template-columns:1fr 1fr;gap:12px;margin-bottom:8px;font-size:12px">'
      +'<div><b>A</b> BATCH '+esc(diff.batch&&diff.batch.before||"—")+' → PANEL '+esc(diff.panel&&diff.panel.before||"—")+' SN '+esc(diff.dut&&diff.dut.before||"—")+'</div>'
      +'<div><b>B</b> BATCH '+esc(diff.batch&&diff.batch.after||"—")+' → PANEL '+esc(diff.panel&&diff.panel.after||"—")+' SN '+esc(diff.dut&&diff.dut.after||"—")+'</div></div>';
    html+='<table><thead><tr><th>测试项</th><th>状态</th><th>前值</th><th>后值</th><th>差异</th></tr></thead><tbody>';
    var tests=diff.tests||[];
    for(var i=0;i<tests.length;i++){
      var t=tests[i];
      var st=t.status||"";
      var badge=st==="added"?'<span class="badge fail">新增</span>':st==="removed"?'<span class="badge">移除</span>':st==="changed"?'<span class="badge fail">变更</span>':'<span class="badge">一致</span>';
      var before=t.before?((t.before.value||"")+" "+(t.before.status||"")):"—";
      var after=t.after?((t.after.value||"")+" "+(t.after.status||"")):"—";
      var same=t.same?"是":"否";
      var cls=st==="changed"?"fail":st==="added"?"fail":"";
      html+='<tr class="'+cls+'"><td>'+esc(t.name||"")+'</td><td>'+badge+'</td><td>'+esc(before)+'</td><td>'+esc(after)+'</td><td>'+esc(same)+'</td></tr>';
    }
    html+='</tbody></table>';
    refs.cmpBody.innerHTML=html;
  }

  function render(ctx){
    fillMachineSel();
    var curSig=JSON.stringify(App.state.machines);
    if(curSig===lastSig) return;
    lastSig=curSig;
  }

  App.Modules["page-report"]={
    init:function(el,ctx){
      el.innerHTML=
        '<div class="page-node">'
        +'<div class="card"><h2>检索 <span class="n">任意机台任意产品（含 PASS）· 并发查各机台 test_records · 缓存 5 分钟</span></h2>'
        +'<div class="toolbar" style="flex-wrap:wrap">'
        +'<select data-ref="qMachine" title="机台" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"></select>'
        +'<input type="text" data-ref="qSn" placeholder="SN（含糊）" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:0 10px">'
        +'<input type="text" data-ref="qModel" placeholder="型号" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:0 10px">'
        +'<select data-ref="qResult" title="结果" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"><option value="ALL">全部</option><option value="PASS">PASS</option><option value="FAIL">FAIL</option></select>'
        +'<input type="date" data-ref="qFrom" title="起始日期" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)">'
        +'<input type="date" data-ref="qTo" title="结束日期" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)">'
        +'<select data-ref="qLimit" title="条数"><option value="50">50</option><option value="100" selected>100</option><option value="200">200</option><option value="500">500</option></select>'
        +'<button class="act" data-ref="btnQuery">搜索</button>'
        +'</div>'
        +'<div data-ref="qStatus" class="n" style="margin-top:6px"></div>'
        +'<div data-ref="qWrap" style="overflow-x:auto;margin-top:10px"><table><thead><tr><th>机台</th><th>SN</th><th>型号</th><th>结果</th><th>测试日期</th><th>失败项</th><th>大小</th><th>操作</th></tr></thead><tbody data-ref="qTbody"></tbody></table></div>'
        +'<div class="empty" data-ref="qEmpty" style="display:none">暂无命中（调整条件后重试，PASS 需源机在线）</div>'
        +'</div>'
        +'<div class="card"><h2>预览 <span class="n">ParsedReportData BATCH/PANEL/DUT/TEST 明细，口径与 XmlReportHtml 一致</span></h2>'
        +'<div data-ref="summary"></div>'
        +'<div class="xml-wrap" style="margin-top:12px"><iframe class="xml-frame" data-ref="frame"></iframe></div>'
        +'<div class="foot">报告内容只读；原始 XML 可下载留档 · 摘要含 KPI 四卡与测试项表（失败标红、排除项灰）</div></div>'
        +'<div class="card"><h2>归档 <span class="n">已归档报告（只读，可按机台检索）</span> <button class="act" data-ref="btnArchRefresh">刷新</button> <select data-ref="arcMachine" style="height:28px;border:1px solid var(--line);border-radius:6px;background:var(--bg2);color:var(--ink)"><option value="">全部机台</option></select></h2>'
        +'<div style="overflow:auto"><table><thead><tr><th>机台</th><th>SN</th><th>型号</th><th>归档时间</th><th>归档人</th><th>操作</th></tr></thead><tbody data-ref="archiveBody"></tbody></table></div></div>'
        +'<div class="card"><h2>版本对比 <span class="n">相邻版本关键参数 before/after 表，差异高亮</span></h2>'
        +'<div style="display:grid;grid-template-columns:1fr 1fr auto;gap:10px;align-items:center">'
        +'<div style="border:1px solid var(--line);border-radius:8px;padding:8px;background:var(--bg2)"><div style="color:var(--dim);font-size:11px">对比 A</div><div data-ref="cmpA" style="font-size:12px;word-break:break-all">— 请选择 —</div></div>'
        +'<div style="border:1px solid var(--line);border-radius:8px;padding:8px;background:var(--bg2)"><div style="color:var(--dim);font-size:11px">对比 B</div><div data-ref="cmpB" style="font-size:12px;word-break:break-all">— 请选择 —</div></div>'
        +'<button class="act" data-ref="btnDiff">对比</button></div>'
        +'<div data-ref="cmpStatus" class="n" style="margin-top:6px"></div>'
        +'<div data-ref="cmpBody" style="margin-top:10px;overflow:auto"></div></div>'
        +'<div class="foot">报告中心：检索 → 预览 → 摘要 → 归档 → 版本对比 · 全链路口径与本地 XmlViewerForm 一致</div>'
        +'</div>';
      var nodes=el.querySelectorAll("[data-ref]");
      for(var i=0;i<nodes.length;i++) refs[nodes[i].getAttribute("data-ref")]=nodes[i];
      if(refs.btnQuery) refs.btnQuery.addEventListener("click", doQuery);
      if(refs.qSn) refs.qSn.addEventListener("keydown", function(e){ if(e.key==="Enter") doQuery(); });
      if(refs.qModel) refs.qModel.addEventListener("keydown", function(e){ if(e.key==="Enter") doQuery(); });
      if(refs.btnArchRefresh) refs.btnArchRefresh.addEventListener("click", loadArchiveList);
      if(refs.arcMachine) refs.arcMachine.addEventListener("change", loadArchiveList);
      if(refs.btnDiff) refs.btnDiff.addEventListener("click", doDiff);
      // 归档机台下拉同检索机台同步
      if(refs.arcMachine){
        var fillArc=function(){
          var cur=refs.arcMachine.value;
          var machines=(App.state.machines||[]).map(function(m){return App.pick(m,"machine")||"";}).filter(function(x){return !!x;});
          machines.sort(function(a,b){return a.localeCompare(b,"zh");});
          var html='<option value="">全部机台</option>';
          for(var i=0;i<machines.length;i++) html+='<option value="'+esc(machines[i])+'">'+esc(machines[i])+'</option>';
          refs.arcMachine.innerHTML=html;
          if(machines.indexOf(cur)>=0) refs.arcMachine.value=cur;
        };
        fillArc();
      }
      render(ctx);
      loadArchiveList();
    },
    render:render
  };
})(window);
