/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 数据拉取页（Lite-Fetch）
   public/js/page-fetch.js
   ----------------
   能力：按机台/时间段/型号筛选 → 预览 → 趋势/分布/热力图表（canvas 自绘 + 热力网格）
         → 异步导出真实 xlsx/zip（含趋势/分布/热力，复用 Fetcher/FailRanker 逻辑，手写 OOXML 经 Xlsx.cs）
         → 进度可轮询（0-100）→ 完成后提供下载，统一 CSV 注入防护与 BOM。
   接口：POST /api/fetch（机台/时间/型号/数量/format/pack 筛选）、GET /api/fetch/status、/api/fetch/download、/api/fetch/jobs
         GET /api/trends、/api/distribution、/api/heatmap
   零依赖、Theme 黑白红、dataSig 变更检测、prefers-reduced-motion、token 透传、viewer 只读隐藏导出。
   ============================================================ */
(function(window){
  "use strict";
  var App = window.App;

  var refs = {};
  var lastSig = "";
  var pollTimer = null;
  var curJobId = null;

  function esc(s){ return App.esc(s); }
  function fmtTime(ts){ return App.fmtTime(ts); }

  function apiGet(path){
    var u = window.location.origin + path;
    if(App.token) u = App.withToken(u);
    return fetch(u,{credentials:"same-origin",headers:{Accept:"application/json"}}).then(function(r){
      if(r.status===401||r.status===403){ App.toast("未授权请带 ?token= 打开","err"); throw new Error("HTTP "+r.status); }
      if(!r.ok) throw new Error("HTTP "+r.status);
      return r.json();
    });
  }
  function apiPost(path, body){
    var u = window.location.origin + path;
    if(App.token) u = App.withToken(u);
    return fetch(u,{method:"POST",credentials:"same-origin",headers:{"Content-Type":"application/json",Accept:"application/json"},body:JSON.stringify(body)}).then(function(r){
      if(r.status===401||r.status===403){ App.toast("权限不足需 engineer","err"); throw new Error("HTTP "+r.status); }
      if(!r.ok) return r.text().then(function(t){ throw new Error(t||("HTTP "+r.status)); });
      var ct=r.headers.get("content-type")||"";
      if(ct.indexOf("application/json")>=0) return r.json();
      return r.text();
    });
  }

  function sig(){
    var s=App.state;
    return JSON.stringify(s.machines)+"|"+(s.sel||"");
  }

  function fillMachineSel(){
    if(!refs.machine) return;
    var cur=refs.machine.value;
    var machines=(App.state.machines||[]).map(function(m){return App.pick(m,"machine")||"";}).filter(function(x){return !!x;});
    machines.sort(function(a,b){return a.localeCompare(b,"zh");});
    var html='<option value="">全部机台</option>';
    for(var i=0;i<machines.length;i++) html+='<option value="'+esc(machines[i])+'">'+esc(machines[i])+'</option>';
    refs.machine.innerHTML=html;
    if(machines.indexOf(cur)>=0) refs.machine.value=cur;
    else if(App.state.sel) refs.machine.value=App.state.sel;
  }

  // 简单 canvas 折线（趋势）
  function drawTrend(cv, data){
    if(!cv||!cv.getContext) return;
    var dpr=window.devicePixelRatio||1;
    var w=cv.clientWidth||600, h=cv.clientHeight||180;
    cv.width=w*dpr; cv.height=h*dpr;
    var g=cv.getContext("2d");
    g.setTransform(dpr,0,0,dpr,0,0);
    g.clearRect(0,0,w,h);
    var css=getComputedStyle(cv);
    var ink=css.getPropertyValue("--ink")||"#E6EAF0";
    var dim=css.getPropertyValue("--dim")||"#9AA5B1";
    var red=css.getPropertyValue("--red")||"#FF5C5C";
    var line=css.getPropertyValue("--line")||"#262D38";
    var padL=36, padR=12, padT=10, padB=22;
    var pw=w-padL-padR, ph=h-padT-padB;
    if(!data||!data.length){
      g.fillStyle=dim; g.font="12px sans-serif"; g.fillText("暂无趋势数据（需 yld_daily 上报）",padL+10,h/2); return;
    }
    // Y 网格
    g.strokeStyle=line; g.lineWidth=1;
    for(var gi=0;gi<=2;gi++){
      var gy=padT+ph-(ph*(gi*50/100));
      g.beginPath(); g.moveTo(padL,gy); g.lineTo(w-padR,gy); g.stroke();
      g.fillStyle=dim; g.font="11px sans-serif"; g.fillText((gi*50)+"%",4,gy+4);
    }
    var step=data.length>1?pw/(data.length-1):0;
    var pts=[];
    for(var i=0;i<data.length;i++){
      var y=Math.max(0,Math.min(100, data[i].yield!=null?data[i].yield:0));
      var x=padL+i*step;
      var yy=padT+ph-(ph*(y/100));
      pts.push({x:x,y:yy,v:y,low:y<90,label:(data[i].date||"").slice(4)});
      g.fillStyle=dim; g.font="10px sans-serif"; g.textAlign="center";
      g.fillText((data[i].date||"").slice(4,6)+"/"+(data[i].date||"").slice(6,8),x,h-6);
    }
    g.strokeStyle=ink; g.lineWidth=2; g.beginPath();
    pts.forEach(function(p,i){ if(i===0) g.moveTo(p.x,p.y); else g.lineTo(p.x,p.y); });
    g.stroke();
    pts.forEach(function(p){
      g.beginPath(); g.arc(p.x,p.y,3.2,0,Math.PI*2);
      g.fillStyle=p.low?red:ink; g.fill();
      g.fillStyle=p.low?red:dim; g.font="10px sans-serif"; g.fillText(p.v.toFixed(1),p.x,p.y-8);
    });
    g.textAlign="left";
  }

  function drawDist(cv, data){
    if(!cv||!cv.getContext) return;
    var dpr=window.devicePixelRatio||1;
    var w=cv.clientWidth||600, h=120;
    cv.width=w*dpr; cv.height=h*dpr;
    var g=cv.getContext("2d");
    g.setTransform(dpr,0,0,dpr,0,0);
    g.clearRect(0,0,w,h);
    var css=getComputedStyle(cv);
    var red=css.getPropertyValue("--red")||"#FF5C5C";
    var dim=css.getPropertyValue("--dim")||"#9AA5B1";
    var rows=(data||[]).slice(0,10);
    if(!rows.length){ g.fillStyle=dim; g.font="12px sans-serif"; g.fillText("暂无分布数据",8,16); return; }
    var max=1; for(var i=0;i<rows.length;i++) if((rows[i].count||0)>max) max=rows[i].count;
    var bw=Math.min(48,(w-40)/rows.length-6);
    rows.forEach(function(d,i){
      var bh=Math.max(2,(h-26)*(d.count||0)/max);
      var x=20+i*(bw+6), y=h-18-bh;
      g.fillStyle=d.count>=5?red:"#9AA5B1";
      g.fillRect(x,y,bw,bh);
      g.fillStyle=dim; g.font="10px sans-serif"; g.fillText(String(d.count||0),x+2,y-2);
      g.save(); g.translate(x+bw/2,h-4); g.rotate(-Math.PI/5);
      var label=(d.label||"—"); if(label.length>8) label=label.slice(0,7)+"…";
      g.fillText(label,0,0); g.restore();
    });
  }

  function renderHeat(heat){
    if(!refs.heat) return;
    if(!heat||!heat.machines||!heat.dates){
      refs.heat.innerHTML='<div class="empty">暂无热力数据</div>'; return;
    }
    var machines=heat.machines||[];
    var dates=heat.dates||[];
    var matrix=heat.matrix||{};
    if(!machines.length||!dates.length){ refs.heat.innerHTML='<div class="empty">暂无热力数据</div>'; return; }
    var html='<div style="overflow:auto"><table><thead><tr><th>机台\\日期</th>';
    for(var di=0;di<dates.length;di++) html+='<th>'+esc(dates[di].slice(4,6)+"/"+dates[di].slice(6,8))+'</th>';
    html+='</tr></thead><tbody>';
    var max=0; machines.forEach(function(m){ var row=matrix[m]||{}; dates.forEach(function(d){ if((row[d]||0)>max) max=row[d]; }); });
    if(max===0) max=1;
    for(var mi=0;mi<machines.length;mi++){
      var m=machines[mi]; var rowM=matrix[m]||{};
      html+='<tr><td style="font-weight:500">'+esc(m)+'</td>';
      for(var dj=0;dj<dates.length;dj++){
        var d=dates[dj]; var cnt=rowM[d]||0;
        var alpha=cnt===0?0:0.15+0.75*(cnt/max);
        var bg=cnt===0?"transparent":"rgba(255,92,92,"+alpha+")";
        var color=cnt>=3?"#fff":cnt>0?"var(--red)":"var(--dim)";
        html+='<td style="text-align:center;background:'+bg+';color:'+color+';min-width:36px">'+cnt+'</td>';
      }
      html+='</tr>';
    }
    html+='</tbody></table></div>';
    html+='<div class="hint" style="margin-top:6px">热力：机台 × 日期 FAIL 密度，点击格子可联动下钻（当前仅展示）</div>';
    refs.heat.innerHTML=html;
  }

  function renderPreview(list){
    if(!refs.previewBody) return;
    if(!list||!list.length){ refs.previewBody.innerHTML='<tr><td colspan="10" class="empty">暂无预览（按筛选条件预览前 100 条）</td></tr>'; return; }
    var html="";
    for(var i=0;i<list.length;i++){
      var r=list[i];
      var ts=r.ts||r.Ts||r.ingestTs||r.IngestTs||"";
      html+='<tr><td>'+esc(fmtTime(ts))+'</td><td>'+esc(r.machine||r.Machine||"")+'</td><td>'+esc(r.model||r.Model||"")+'</td><td>'+esc(r.sn||r.Sn||"")+'</td><td>'+esc(r.test_date||r.TestDate||"")+'</td><td title="'+esc(r.fail_reason||r.FailReason||"")+'">'+esc((r.fail_reason||r.FailReason||"").slice(0,24))+'</td><td>'+esc(r.tester||r.Tester||"")+'</td><td>'+esc(r.station_id||r.StationId||"")+'</td><td>'+esc(r.category||r.Category||"")+'</td><td>'+esc(r.result||r.Result||"")+'</td></tr>';
    }
    refs.previewBody.innerHTML=html;
  }

  function renderJobs(jobs){
    if(!refs.jobs) return;
    if(!jobs||!jobs.length){ refs.jobs.innerHTML='<div class="empty">暂无任务</div>'; return; }
    var html='<table><thead><tr><th>ID</th><th>状态</th><th>进度</th><th>总数</th><th>文件</th><th>创建时间</th><th>操作</th></tr></thead><tbody>';
    for(var i=0;i<jobs.length;i++){
      var j=jobs[i];
      var status=j.status||j.Status||"";
      var badge=status==="done"?'<span class="badge pass">完成</span>':status==="failed"?'<span class="badge fail">失败</span>':status==="running"?'<span class="badge">进行中</span>':'<span class="badge">'+esc(status)+'</span>';
      var prog=j.progress!=null?j.progress:(j.Progress!=null?j.Progress:0);
      html+='<tr><td>'+esc(j.id||j.Id||"")+'</td><td>'+badge+'</td><td><div style="width:80px;height:8px;background:var(--bg2);border-radius:4px;overflow:hidden"><div style="width:'+prog+'%;height:100%;background:var(--red)"></div></div> '+prog+'%</td><td>'+(j.total||j.Total||0)+'</td><td>'+esc(j.fileName||j.FileName||"")+'</td><td>'+esc(j.created_at||j.CreatedAt||"")+'</td><td><a class="op" href="#" data-dl="'+esc(j.id||j.Id||"")+'">下载</a> <a class="op" href="#" data-prog="'+esc(j.id||j.Id||"")+'">进度</a></td></tr>';
    }
    html+='</tbody></table>';
    refs.jobs.innerHTML=html;
    var dls=refs.jobs.querySelectorAll("[data-dl]");
    for(var k=0;k<dls.length;k++) dls[k].addEventListener("click", function(e){ e.preventDefault(); var id=this.getAttribute("data-dl"); downloadJob(id); });
    var prs=refs.jobs.querySelectorAll("[data-prog]");
    for(var p=0;p<prs.length;p++) prs[p].addEventListener("click", function(e){ e.preventDefault(); var id=this.getAttribute("data-prog"); pollStatus(id); });
  }

  function pollStatus(id){
    if(!id) return;
    apiGet("/api/fetch/status?id="+encodeURIComponent(id)).then(function(j){
      App.toast("状态 "+(j.status||j.Status)+" 进度 "+(j.progress!=null?j.progress:j.Progress||0)+"%", j.status==="done"?"ok":"");
      if(j.preview||j.Preview) renderPreview(j.preview||j.Preview);
    }).catch(function(e){ App.toast("状态查询失败 "+e.message,"err"); });
  }
  function downloadJob(id){
    if(!id) return;
    var url="/api/fetch/download?id="+encodeURIComponent(id);
    if(App.token) url=App.withToken(url);
    window.open(url,"_blank");
  }

  function doPreview(){
    var machine=(refs.machine.value||"").trim();
    var model=(refs.model.value||"").trim();
    var from=(refs.from.value||"").trim().replace(/-/g,"");
    var to=(refs.to.value||"").trim().replace(/-/g,"");
    var limit=parseInt(refs.limit.value,10)||500;
    var format=(refs.format.value||"xlsx");
    var pack=refs.pack&&refs.pack.checked;
    var body={};
    if(machine) body.machine=machine;
    if(model) body.model=model;
    if(from) body.date_from=from;
    if(to) body.date_to=to;
    body.limit=limit;
    body.format=format;
    if(pack) body.pack=true;
    // 本地轻量预览：直接拉 trends/distribution/heatmap
    var q="?days=14"; if(machine) q+="&machine="+encodeURIComponent(machine);
    apiGet("/api/trends"+q).then(function(d){ drawTrend(refs.trendCv,d); }).catch(function(){});
    apiGet("/api/distribution?field=fail_reason&limit=10"+(machine?"&machine="+encodeURIComponent(machine):"")).then(function(d){ drawDist(refs.distCv,d); }).catch(function(){});
    apiGet("/api/heatmap"+q).then(function(h){ renderHeat(h); }).catch(function(){});
    // 明细预览：先走 /api/fails 聚合明细
    var failsQ="/api/fails?limit=100"+(machine?"&machine="+encodeURIComponent(machine):"");
    apiGet(failsQ).then(function(list){ renderPreview(list); }).catch(function(){});
  }

  function doExport(){
    if(App.auth&&App.auth.isViewer&&App.auth.isViewer()){ App.toast("访客无导出权限","err"); return; }
    var machine=(refs.machine.value||"").trim();
    var model=(refs.model.value||"").trim();
    var from=(refs.from.value||"").trim().replace(/-/g,"");
    var to=(refs.to.value||"").trim().replace(/-/g,"");
    var limit=parseInt(refs.limit.value,10)||2000;
    var format=(refs.format.value||"xlsx");
    var pack=refs.pack&&refs.pack.checked;
    var body={};
    if(machine) body.machine=machine;
    if(model) body.model=model;
    if(from) body.date_from=from;
    if(to) body.date_to=to;
    body.limit=limit;
    body.format=format;
    if(pack || format==="zip") body.pack=true;
    if(refs.status) refs.status.textContent="提交中…";
    apiPost("/api/fetch", body).then(function(res){
      var jobId=res.job_id||res.jobId||res.id;
      curJobId=jobId;
      if(refs.status) refs.status.textContent="已提交任务 "+jobId+"，生成中…（进度可轮询）";
      App.toast("已提交导出任务 "+jobId,"ok");
      if(pollTimer) clearInterval(pollTimer);
      pollTimer=setInterval(function(){
        if(!curJobId) return;
        apiGet("/api/fetch/status?id="+encodeURIComponent(curJobId)).then(function(j){
          var prog=j.progress!=null?j.progress:(j.Progress||0);
          var st=j.status||j.Status||"";
          if(refs.progress) refs.progress.style.width=prog+"%";
          if(refs.status) refs.status.textContent="任务 "+curJobId+" 状态 "+st+" 进度 "+prog+"%";
          if(j.preview||j.Preview) renderPreview(j.preview||j.Preview);
          if(st==="done"){
            clearInterval(pollTimer); pollTimer=null;
            if(refs.status) refs.status.textContent="任务完成，总数 "+(j.total||j.Total||0)+"，可下载";
            var dlUrl="/api/fetch/download?id="+encodeURIComponent(curJobId);
            if(refs.download) { refs.download.href=App.withToken(dlUrl); refs.download.style.display=""; refs.download.textContent="下载 "+(j.fileName||j.FileName||"文件"); }
            // 自动刷新最近任务与图表
            apiGet("/api/fetch/jobs?limit=10").then(function(jobs){ var arr=Array.isArray(jobs)?jobs:(jobs.jobs||[]); renderJobs(arr); }).catch(function(){});
            doPreview();
          }
          if(st==="failed"){
            clearInterval(pollTimer); pollTimer=null;
            App.toast("任务失败 "+(j.error||j.Error||""),"err");
          }
        }).catch(function(){});
      },1200);
    }).catch(function(e){ if(refs.status) refs.status.textContent="提交失败 "+e.message; App.toast("导出提交失败 "+e.message,"err"); });
  }

  function render(ctx){
    fillMachineSel();
    var curSig=sig();
    if(curSig===lastSig) return;
    lastSig=curSig;
    doPreview();
    apiGet("/api/fetch/jobs?limit=10").then(function(jobs){ var arr=Array.isArray(jobs)?jobs:(jobs.jobs||[]); renderJobs(arr); }).catch(function(){});
  }

  App.Modules["page-fetch"]={
    init:function(el,ctx){
      el.innerHTML=
        '<div class="page-node">'
        +'<div class="card"><h2>数据拉取 <span class="n">按机台/时间段/型号筛选 → 预览 → 趋势/分布/热力 → 异步导出 xlsx/zip（含趋势/分布/热力）</span></h2>'
        +'<div class="toolbar" style="flex-wrap:wrap">'
        +'<select data-ref="machine" title="机台" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)"><option value="">全部机台</option></select>'
        +'<input type="text" data-ref="model" placeholder="型号（如 E3002781）" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink);padding:0 10px">'
        +'<input type="date" data-ref="from" title="开始日期" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)">'
        +'<input type="date" data-ref="to" title="结束日期" style="height:34px;border:1px solid var(--line);border-radius:8px;background:var(--bg2);color:var(--ink)">'
        +'<select data-ref="limit" title="条数"><option value="200">200</option><option value="500" selected>500</option><option value="2000">2000</option><option value="5000">5000</option></select>'
        +'<select data-ref="format" title="导出格式"><option value="xlsx" selected>xlsx</option><option value="csv">csv</option><option value="zip">zip（含 xlsx+csv）</option></select>'
        +'<label style="display:flex;align-items:center;gap:6px;color:var(--dim)"><input type="checkbox" data-ref="pack"> 打包 zip</label>'
        +'<button class="act" data-ref="btnPreview">拉取预览</button>'
        +'<button class="act" data-ref="btnExport">异步导出</button>'
        +'</div>'
        +'<div style="display:flex;gap:12px;align-items:center;margin-top:8px"><div style="flex:1;height:8px;background:var(--bg2);border-radius:4px;overflow:hidden"><div data-ref="progress" style="width:0%;height:100%;background:var(--red);transition:width .3s"></div></div><span data-ref="status" class="n"></span> <a data-ref="download" class="act" style="display:none" target="_blank">下载</a></div>'
        +'</div>'
        +'<div class="grid2">'
        +'<div class="card"><h2>趋势 <span class="n">良率/数量按日（近14天）</span></h2><canvas data-ref="trendCv" style="width:100%;height:180px"></canvas></div>'
        +'<div class="card"><h2>分布 <span class="n">失败项 Top10</span></h2><canvas data-ref="distCv" style="width:100%;height:120px"></canvas></div>'
        +'</div>'
        +'<div class="card"><h2>热力 <span class="n">机台 × 日期 FAIL 密度</span></h2><div data-ref="heat"></div></div>'
        +'<div class="card"><h2>明细预览 <span class="n">前 100 行（导出含全量）</span></h2><div style="overflow:auto"><table><thead><tr><th>时间</th><th>机台</th><th>型号</th><th>SN</th><th>测试日期</th><th>失败项</th><th>测试员</th><th>站点</th><th>类别</th><th>结果</th></tr></thead><tbody data-ref="previewBody"></tbody></table></div></div>'
        +'<div class="card"><h2>最近任务 <span class="n">进度可轮询</span></h2><div data-ref="jobs"></div></div>'
        +'<div class="foot">数据拉取服务端化：复用 Fetcher/FailRanker 口径，手写 OOXML，经 FctShared.Xlsx，趋势/分布/热力同 chart，统一 CSV 注入防护与 BOM</div>'
        +'</div>';
      var nodes=el.querySelectorAll("[data-ref]");
      for(var i=0;i<nodes.length;i++) refs[nodes[i].getAttribute("data-ref")]=nodes[i];
      if(refs.btnPreview) refs.btnPreview.addEventListener("click", doPreview);
      if(refs.btnExport) refs.btnExport.addEventListener("click", doExport);
      // 权限显隐
      if(refs.btnExport) refs.btnExport.classList.add("hide-for-viewer");
      if(App.applyRoleVisibility) App.applyRoleVisibility(el);
      // 回车快速预览
      if(refs.model) refs.model.addEventListener("keydown", function(e){ if(e.key==="Enter") doPreview(); });
      // 联动：总览下钻 hash ?machine=&date=
      try{
        var hq=(window.location.hash.split("?")[1]||"");
        var qp=new URLSearchParams(hq);
        var qm=qp.get("machine")||qp.get("q");
        if(qm&&refs.machine){ /* 延迟到 machines 加载后由 fillMachineSel 回填 */ }
        window.addEventListener("argus:drill", function(ev){
          var d=ev.detail||{};
          if(d.link&&d.link.indexOf("#/fetch")>=0) App.Nav.go("fetch");
          if(d.q&&refs.model){ refs.model.value=d.q; doPreview(); }
        });
      }catch(e){}
      render(ctx);
    },
    render:render
  };
})(window);
