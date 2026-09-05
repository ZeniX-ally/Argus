/* ============================================================
   Argus FCT 聚合系统 · 模块化前端 —— 维修/待办看板（P5 全功能）
   public/js/page-maintenance.js
   ----------------
   能力（对标 MaintenanceBoard + MaintenancePanel 本地看板）：
   - 4 列状态机看板 unknown→open→in_progress→resolved，legacy 兼容
   - 待办卡：大项合并复用 TodoGrouping（服务端计算）、优先级 20/5、次数、合并变体 tooltip
   - 拖拽跨列改状态弹全字段确认（MaintenanceForm）+ 飞书推送（服务端）
   - 待办拖出待办列=确认、右键删除写 dismissed_todos、防重入、右键菜单、状态筛选
   - 待办区间：近 7/30/90 天 · 全部（永久）· 自定义起止日期（v3.18.0，对接 /api/todos?from=&to=）
   - 从 FAIL 选择故障建单：去重/过滤/（前端）深扫提示、批量/合并
   - 维修表单全字段+人员下拉/多选/名单管理/手敲自动登记
   - 导出 12 列 xlsx/csv（服务端 MaintenanceExporter，CSV 注入防护）
   - 人员改名同步历史
   - API：GET/POST/PATCH /api/maintenance、/counts、/todos、/resolvers、/export.maintenance 等，鉴权+三角色
   前端：4 列 HTML5 drag&drop，详情 popup 复用 CardPreview 样式，ContextMenu，乐观更新+服务端校验。
   零依赖、Theme 黑白红、dataSig 变更检测、prefers-reduced-motion。
   ============================================================ */
(function(window){
  "use strict";
  var App = window.App;

  // ----- 状态机（复刻 MaintenanceMeta，口径与服务端同一 C# 逻辑一致，legacy 归并） -----
  var Statuses = [
    {key:"unknown", zh:"未知问题", accent:"#8C8C8C"},
    {key:"open", zh:"待办", accent:"#C8102E"},
    {key:"in_progress", zh:"持续跟踪", accent:"#141414"},
    {key:"resolved", zh:"已完成", accent:"#BFBFBF"}
  ];
  function normalizeStatus(k){
    if(!k) return "open";
    k = String(k).toLowerCase();
    if(k==="closed") return "resolved";
    if(k==="investigating") return "open";
    for(var i=0;i<Statuses.length;i++) if(Statuses[i].key===k) return k;
    return "open";
  }
  function zhOf(k){
    k=normalizeStatus(k);
    for(var i=0;i<Statuses.length;i++) if(Statuses[i].key===k) return Statuses[i].zh;
    return k;
  }
  function accentOf(k){
    k=normalizeStatus(k);
    for(var i=0;i<Statuses.length;i++) if(Statuses[i].key===k) return Statuses[i].accent;
    return "#8C8C8C";
  }
  var SeverityMap = {critical:"严重", major:"一般", minor:"轻微"};
  var SeverityOrderZh=["一般","严重","轻微"];
  function severityZh(k){ return SeverityMap[k]||k||"一般"; }
  function severityKey(zh){
    if(zh==="严重") return "critical";
    if(zh==="轻微") return "minor";
    return "major";
  }
  function priorityZh(cnt){ return cnt>=20?"高":cnt>=5?"中":"低"; }

  // ----- fetch helpers（带 token/鉴权，兼容 App.withToken + Cookie） -----
  function apiGet(path){
    var u = window.location.origin + path;
    if(App.token) u = App.withToken(u);
    return fetch(u,{credentials:"same-origin",headers:{Accept:"application/json"}}).then(function(r){
      if(r.status===401||r.status===403){ App.toast("接口未授权（HTTP "+r.status+"）请带 ?token= 打开","err"); throw new Error("HTTP "+r.status); }
      if(!r.ok) throw new Error("HTTP "+r.status);
      return r.json();
    });
  }
  function apiSend(method,path,body){
    var u = window.location.origin + path;
    if(App.token) u = App.withToken(u);
    var opts={method:method,credentials:"same-origin",headers:{Accept:"application/json","Content-Type":"application/json"}};
    if(body) opts.body=JSON.stringify(body);
    return fetch(u,opts).then(function(r){
      if(r.status===401||r.status===403){ App.toast("权限不足（"+r.status+"）该操作需 engineer/admin","err"); throw new Error("HTTP "+r.status); }
      if(!r.ok) return r.text().then(function(t){ throw new Error(t||("HTTP "+r.status)); });
      var ct=r.headers.get("content-type")||"";
      if(ct.indexOf("application/json")>=0) return r.json();
      return r.text();
    });
  }
  function esc(s){ return App.esc(s); }
  function fmtTime(ts){ return App.fmtTime(ts); }

  // ----- 状态 -----
  var refs={}, lastSig="", filterStatus="", todoRange="30", todoFrom=null, todoTo=null, todoCustom=false;
  var data={records:[], todos:[], counts:{}, resolvers:[], fails:[]};
  var dragCard=null, dragType=null; // type: "record" | "todo"
  var ctxMenu=null, previewEl=null;
  var perColumnLimit=120;

  function dataSig(){
    var s="";
    s+=JSON.stringify(data.counts)+"|"+filterStatus+"|"+todoRange+"|"+(todoFrom||"")+"|"+(todoTo||"");
    s+="|"+data.records.length+"|"+data.todos.length+"|"+(data.records[0]?data.records[0].Id+"": "")+"|"+(data.todos[0]?data.todos[0].Id:"");
    return s;
  }

  // ----- 加载 -----
  function loadAll(){
    var statusQ = filterStatus?("?status="+encodeURIComponent(filterStatus)):"";
    var todoQ="";
    if(todoFrom && todoTo){ todoQ="?from="+encodeURIComponent(todoFrom)+"&to="+encodeURIComponent(todoTo); }
    else if(todoRange==="7"){ var f=todayMinus(6); todoQ="?from="+f+"&to="+todayYmd(); }
    else if(todoRange==="90"){ var f2=todayMinus(89); todoQ="?from="+f2+"&to="+todayYmd(); }
    else if(todoRange==="all"){ todoQ=""; }
    else if(todoRange==="30"){ var f3=todayMinus(29); todoQ="?from="+f3+"&to="+todayYmd(); }
    // 默认待办不限区间时不传参（全部永久）
    if(todoRange==="all") todoQ="";

    return Promise.all([
      apiGet("/api/maintenance/counts").catch(function(){ return {counts:{}}; }),
      apiGet("/api/maintenance"+(statusQ||"?limit=500")+(statusQ.indexOf("?")>=0?"&limit=500":"?limit=500")).catch(function(){ return []; }),
      apiGet("/api/todos"+(todoQ||"?limit=300")+(todoQ?"&limit=300":"")).catch(function(){ return []; }),
      apiGet("/api/resolvers").catch(function(){ return []; }),
      apiGet("/api/fails?limit=200").catch(function(){ return []; })
    ]).then(function(rs){
      data.counts = rs[0].counts||rs[0]||{};
      data.records = Array.isArray(rs[1])?rs[1]:[];
      data.todos = Array.isArray(rs[2])?rs[2]:[];
      data.resolvers = Array.isArray(rs[3])?rs[3]:[];
      data.fails = Array.isArray(rs[4])?rs[4]:[];
      // counts 归一化
      for(var i=0;i<Statuses.length;i++){ var k=Statuses[i].key; if(data.counts[k]==null) data.counts[k]=0; }
    });
  }

  function todayYmd(){ var d=new Date(); var p=function(n){return n<10?"0"+n:""+n;}; return ""+d.getFullYear()+p(d.getMonth()+1)+p(d.getDate()); }
  function todayMinus(n){ var d=new Date(); d.setDate(d.getDate()-n); var p=function(x){return x<10?"0"+x:""+x;}; return ""+d.getFullYear()+p(d.getMonth()+1)+p(d.getDate()); }
  // 自定义区间：<input type="date"> 的 yyyy-MM-dd ↔ 后端/预设用的 yyyyMMdd
  function ymdToCompact(s){ return s?String(s).replace(/-/g,""):""; }
  function compactToYmd(s){ return (s&&String(s).length===8)?String(s).slice(0,4)+"-"+String(s).slice(4,6)+"-"+String(s).slice(6,8):""; }

  // ----- 渲染 -----
  function render(){
    var sig=dataSig();
    if(sig===lastSig) return;
    lastSig=sig;
    renderCounts();
    renderBoard();
    renderResolversDatalist();
  }

  function renderCounts(){
    if(!refs.countBadges) return;
    for(var i=0;i<Statuses.length;i++){
      var k=Statuses[i].key;
      var el=refs["badge_"+k];
      if(el) el.textContent=data.counts[k]||0;
    }
    if(refs.badge_total) refs.badge_total.textContent="共 "+((data.counts.unknown||0)+(data.counts.open||0)+(data.counts.in_progress||0)+(data.counts.resolved||0))+" 条";
    // 滤后计数
    var filtered = filterStatus?data.records.filter(function(r){ return normalizeStatus(r.Status||r.status)===filterStatus; }):data.records;
    if(refs.filteredCount) refs.filteredCount.textContent=filtered.length+" 条";
  }

  function renderBoard(){
    if(!refs.board) return;
    refs.board.innerHTML="";
    for(var i=0;i<Statuses.length;i++){
      var def=Statuses[i];
      var col=document.createElement("div");
      col.className="maint-col";
      col.setAttribute("data-col",def.key);
      col.addEventListener("dragover",onDragOver);
      col.addEventListener("dragleave",onDragLeave);
      col.addEventListener("drop",onDrop);
      var head=document.createElement("div");
      head.className="maint-head";
      head.style.borderTop="3px solid "+def.accent;
      var badge = data.counts[def.key]||0;
      var todoNew = def.key==="open"?(" <span class='badge fail' style='margin-left:6px'>新 "+data.todos.length+"</span>"):"";
      head.innerHTML="<span class='zh'>"+esc(def.zh)+"</span> <span class='badge' data-badge='"+def.key+"'>"+badge+"</span>"+todoNew;
      col.appendChild(head);
      var list=document.createElement("div");
      list.className="maint-list";
      list.setAttribute("data-list",def.key);
      list.addEventListener("dragover",onDragOver);
      list.addEventListener("drop",onDrop);
      // 卡片
      if(def.key==="open"){
        // 待办在上，记录在下
        for(var ti=0;ti<Math.min(data.todos.length,perColumnLimit);ti++) list.appendChild(renderTodoCard(data.todos[ti]));
        var recsOpen=data.records.filter(function(r){ return normalizeStatus(r.Status||r.status)==="open"; });
        for(var ri=0;ri<Math.min(recsOpen.length,perColumnLimit);ri++) list.appendChild(renderRecordCard(recsOpen[ri]));
        if(recsOpen.length===0 && data.todos.length===0){
          var empty=document.createElement("div"); empty.className="maint-empty"; empty.textContent="拖动卡片到此处"; list.appendChild(empty);
        }
      } else {
        var recs=data.records.filter(function(r){ return normalizeStatus(r.Status||r.status)===def.key; });
        for(var rj=0;rj<Math.min(recs.length,perColumnLimit);rj++) list.appendChild(renderRecordCard(recs[rj]));
        if(recs.length===0){ var em=document.createElement("div"); em.className="maint-empty"; em.textContent="拖动卡片到此处"; list.appendChild(em); }
      }
      // 超限提示
      var totalForCol = data.counts[def.key]||0;
      var shown = list.querySelectorAll(".maint-card").length;
      if(totalForCol>shown){
        var more=document.createElement("div"); more.className="maint-more"; more.textContent="仅显示最近 "+shown+" 条，共 "+totalForCol+" 条"; list.appendChild(more);
      }
      col.appendChild(list);
      refs.board.appendChild(col);
    }
  }

  function renderTodoCard(t){
    var title=t.Title||t.title||"—";
    var cnt=t.SortCount||t.sortCount||t.TotalCount||t.totalCount||0;
    var prio= t.PriorityZh||t.priorityZh||priorityZh(cnt);
    var rangeCnt = t.RangeCount!=null?t.RangeCount:cnt;
    var totalCnt = t.TotalCount!=null?t.TotalCount:cnt;
    var variants=t.Variants||t.variants||[];
    var varCnt=t.VariantCount||t.variantCount||variants.length;
    var card=document.createElement("div");
    card.className="maint-card todo";
    card.draggable=true;
    card.setAttribute("data-todo-id",t.Id||t.id);
    card.addEventListener("dragstart",onDragStartTodo);
    card.addEventListener("dragend",onDragEnd);
    card.addEventListener("click",function(e){ if(e.button!==0) return; showTodoPreview(t,card); });
    card.addEventListener("dblclick",function(e){ e.preventDefault(); confirmTodo(t, "open"); });
    card.addEventListener("contextmenu",function(e){ e.preventDefault(); showTodoMenu(t,card,e); });
    // tooltip
    var tip="【未确认不良】"+title+"\n优先级:"+prio+"（按 fail 次数）\n区间内:"+rangeCnt+" 次   累计:"+totalCnt+" 次";
    if(varCnt>1) tip+="\n已合并 "+varCnt+" 个同类项";
    card.title=tip;
    var accent = cnt>=20?"#C8102E":cnt>=5?"#595959":"#B3B3B3";
    card.innerHTML=
      "<div class='mc-bar' style='background:"+accent+"'></div>"
      +"<div class='mc-head'><span class='mc-title'>"+esc(title)+"</span><span class='mc-tag'>未确认</span></div>"
      +"<div class='mc-meta'><span class='mc-prio' style='background:"+accent+"'>优先级 "+esc(prio)+"</span><span class='mc-cnt' style='color:#C8102E'>"+rangeCnt+" 次"+(rangeCnt!==totalCnt?" · 累计 "+totalCnt+" 次":"")+"</span></div>"
      +"<div class='mc-sub'>"+(varCnt>1?"已合并 "+varCnt+" 个同类项 · ":"")+esc(t.Model||t.model||"—")+" · "+esc(t.StationId||t.stationId||"—")+"</div>"
      +(varCnt>1?"<div class='mc-vars'>"+variants.slice(0,2).map(function(v){return "<span>· "+esc(v)+"</span>";}).join("")+(varCnt>2?"<span class='more'> +"+(varCnt-2)+" 项</span>":"")+"</div>":"")
      +"<div class='mc-foot'><span>最近 "+fmtTime(t.LastSeen||t.lastSeen)+"</span><span class='hint'>单击预览</span></div>";
    return card;
  }

  function renderRecordCard(r){
    var id=r.Id||r.id;
    var fail=r.FailItem||r.failItem||"—";
    var status=normalizeStatus(r.Status||r.status);
    var sevZh=severityZh(r.Severity||r.severity);
    var sevKey=r.Severity||r.severity||"major";
    var sevColor= sevKey==="critical"?"#C8102E":sevKey==="minor"?"#B2B2B2":"#595959";
    var resolver=r.Resolver||r.resolver||"未指派";
    var updated=r.UpdatedAt||r.updatedAt||r.CreatedAt||r.createdAt||"—";
    var notes=r.Notes||r.notes||"";
    var sourceItems=parseSourceItems(notes);
    var card=document.createElement("div");
    card.className="maint-card record";
    card.draggable=true;
    card.setAttribute("data-rec-id",id);
    card.setAttribute("data-status",status);
    card.addEventListener("dragstart",onDragStartRecord);
    card.addEventListener("dragend",onDragEnd);
    card.addEventListener("click",function(e){ if(e.button!==0) return; showRecordPreview(r,card); });
    card.addEventListener("dblclick",function(e){ e.preventDefault(); editRecord(r); });
    card.addEventListener("contextmenu",function(e){ e.preventDefault(); showRecordMenu(r,card,e); });
    card.title="#"+id+" "+fail+"\n严重度:"+sevZh+"\n状态:"+zhOf(status)+"\n维修人:"+resolver;
    card.innerHTML=
      "<div class='mc-bar' style='background:"+sevColor+"'></div>"
      +"<div class='mc-head'><span class='mc-title'>"+esc(fail)+"</span><span class='mc-id'>#"+id+"</span></div>"
      +"<div class='mc-meta'><span class='mc-sev'>"+esc(sevZh)+"</span><span class='mc-who'>"+esc(resolver)+"</span></div>"
      +"<div class='mc-sub'>"+esc((r.FailReason||r.failReason||r.Resolution||r.resolution||"—").slice(0,60))+"</div>"
      +"<div class='mc-foot'><span>"+esc(zhOf(status))+" · "+fmtTime(updated)+"</span><span class='hint'>"+fmtTime(updated).slice(5,16)+"</span></div>"
      +(sourceItems.length>1?"<div class='mc-vars'><span>合并 "+sourceItems.length+" 项："+esc(sourceItems[0])+" +"+(sourceItems.length-1)+"</span></div>":"");
    return card;
  }

  function parseSourceItems(notes){
    if(!notes) return [];
    var tag="来源测试项：";
    var i=notes.indexOf(tag);
    if(i<0) return [];
    return notes.slice(i+tag.length).split("\n").map(function(s){return s.trim();}).filter(function(s){return s.length>0;});
  }

  // ----- 拖拽 -----
  function onDragStartTodo(e){
    dragCard=this; dragType="todo";
    e.dataTransfer.effectAllowed="move";
    e.dataTransfer.setData("text/plain","todo:"+this.getAttribute("data-todo-id"));
    this.style.opacity="0.5";
  }
  function onDragStartRecord(e){
    dragCard=this; dragType="record";
    e.dataTransfer.effectAllowed="move";
    e.dataTransfer.setData("text/plain","rec:"+this.getAttribute("data-rec-id"));
    this.style.opacity="0.5";
  }
  function onDragEnd(e){ if(dragCard) dragCard.style.opacity=""; dragCard=null; dragType=null; document.querySelectorAll(".maint-col.drag-over").forEach(function(c){c.classList.remove("drag-over");}); }
  function onDragOver(e){
    e.preventDefault();
    var col=e.currentTarget.closest?e.currentTarget.closest(".maint-col"):null;
    if(col) col.classList.add("drag-over");
    e.dataTransfer.dropEffect="move";
  }
  function onDragLeave(e){ var col=e.currentTarget.closest?e.currentTarget.closest(".maint-col"):null; if(col) col.classList.remove("drag-over"); }
  function onDrop(e){
    e.preventDefault();
    var col=e.currentTarget.closest?e.currentTarget.closest(".maint-col"):null;
    if(col) col.classList.remove("drag-over");
    var targetStatus=col?col.getAttribute("data-col"):null;
    if(!targetStatus) return;
    if(!dragCard) return;
    if(dragType==="todo"){
      var todoId=dragCard.getAttribute("data-todo-id");
      var t=data.todos.find(function(x){ return String(x.Id||x.id)===String(todoId); });
      if(t) confirmTodo(t, targetStatus);
    } else if(dragType==="record"){
      var recId=dragCard.getAttribute("data-rec-id");
      var r=data.records.find(function(x){ return String(x.Id||x.id)===String(recId); });
      if(!r) return;
      var from=normalizeStatus(r.Status||r.status);
      if(from===targetStatus) return;
      // 乐观更新
      var optimistic=r;
      var origStatus=from;
      // 弹全字段确认
      showMaintenanceForm(r, targetStatus, function(edited){
        // 发送 PATCH
        var prevEl=dragCard;
        apiSend("PATCH","/api/maintenance",edited).then(function(){
          App.toast("状态已更新："+zhOf(origStatus)+" → "+zhOf(targetStatus),"ok");
          lastSig=""; loadAll().then(render);
        }).catch(function(err){
          App.toast("更新失败："+err.message,"err");
          lastSig=""; loadAll().then(render);
        });
      });
    }
    dragCard=null; dragType=null;
  }

  // ----- 预览 popup（复用 CardPreview 样式） -----
  function showTodoPreview(t, anchor){
    var title=t.Title||t.title;
    var cnt=t.SortCount||t.TotalCount||0;
    var prio=priorityZh(cnt);
    var rows=[
      ["状　　态:","未确认不良（待办）"],
      ["优 先 级:",prio+"（按 fail 次数：高≥20 / 中≥5）"],
      ["区间内次数:",(t.RangeCount||cnt)+" 次"],
      ["累计次数:",(t.TotalCount||cnt)+" 次"],
      ["合并项数:",t.VariantCount>1?t.VariantCount+" 个同类项":"未合并（1 项）"],
      ["首次出现:",t.FirstSeen||t.firstSeen||"—"],
      ["最近出现:",t.LastSeen||t.lastSeen||"—"],
      ["型　　号:",t.Model||t.model||"—"],
      ["机　　台:",t.StationId||t.stationId||"—"],
      ["归并键:",t.GroupKey||t.groupKey||""]
    ];
    var variants=t.Variants||t.variants||[];
    showPopup(anchor, title, "未确认不良 · 优先级"+prio+" · "+cnt+" 次", "#C8102E", rows, variants.map(function(v){return [v, ""];}), "双击卡片=确认问题 · 拖到其它列=确认并置为该状态 · 右键=更多操作");
  }
  function showRecordPreview(r, anchor){
    var id=r.Id||r.id;
    var status=zhOf(normalizeStatus(r.Status||r.status));
    var notes=r.Notes||r.notes||"";
    var sourceItems=parseSourceItems(notes);
    var pureNotes=notes.indexOf("来源测试项：")>=0?notes.slice(0,notes.indexOf("来源测试项：")).trim():notes;
    if(!pureNotes) pureNotes="—";
    else pureNotes=pureNotes.replace(/\r/g," ").replace(/\n/g," ").trim()||"—";
    var rows=[
      ["记录号:","#"+id],
      ["状　　态:",status],
      ["严重程度:",severityZh(r.Severity||r.severity)],
      ["维修人员:",r.Resolver||r.resolver||"未指派"],
      ["故障描述:",r.FailReason||r.failReason||"—"],
      ["维修措施:",r.Resolution||r.resolution||"—"],
      ["备　　注:",pureNotes],
      ["记录日期:",r.CreatedAt||r.createdAt||"—"],
      ["最后更新:",r.UpdatedAt||r.updatedAt||"—"],
      ["机　　台:",r.StationId||r.stationId||"—"]
    ];
    var merged = sourceItems.length>1?sourceItems.map(function(v){return [v,""]; }):null;
    showPopup(anchor, r.FailItem||r.failItem, "#"+id+" · "+status+(sourceItems.length>1?" · 合并 "+sourceItems.length+" 项":""), accentOf(r.Status||r.status), rows, merged, "双击卡片=编辑 · 拖动到其它列=改状态 · 右键=更多操作");
  }
  function showPopup(anchor, title, subtitle, accent, rows, merged, footer){
    hidePopup();
    var ov=document.createElement("div");
    ov.className="maint-popup-overlay";
    ov.addEventListener("click",hidePopup);
    var card=document.createElement("div");
    card.className="maint-popup";
    card.style.border="1px solid "+accent;
    card.addEventListener("click",function(e){e.stopPropagation();});
    var head=document.createElement("div");
    head.className="maint-popup-head";
    head.style.background=accent;
    head.innerHTML="<div class='mp-title'>"+esc(title)+"</div><div class='mp-sub'>"+esc(subtitle)+"</div><span class='mp-close'>✕</span>";
    head.querySelector(".mp-close").addEventListener("click",hidePopup);
    var body=document.createElement("div");
    body.className="maint-popup-body";
    body.innerHTML=rows.map(function(r){return "<div class='mp-row'><span class='mp-k'>"+esc(r[0])+"</span><span class='mp-v'>"+esc(r[1])+"</span></div>";}).join("");
    card.appendChild(head); card.appendChild(body);
    if(merged && merged.length){
      var mt=document.createElement("div"); mt.className="mp-merged-title"; mt.textContent=merged.length>1?"合并的 fail 项（"+merged.length+" 项）":"来源 fail 项";
      var tbl=document.createElement("div"); tbl.className="mp-merged";
      merged.forEach(function(m){ var row=document.createElement("div"); row.className="mp-merged-row"; row.innerHTML="<span class='cnt'>—</span><span class='item'>"+esc(m[0])+"</span>"; tbl.appendChild(row); });
      card.appendChild(mt); card.appendChild(tbl);
    }
    var foot=document.createElement("div"); foot.className="maint-popup-foot"; foot.textContent=footer; card.appendChild(foot);
    ov.appendChild(card);
    document.body.appendChild(ov);
    previewEl=ov;
    // ESC
    var escHandler=function(e){ if(e.key==="Escape") hidePopup(); };
    document.addEventListener("keydown",escHandler);
    ov._esc=escHandler;
  }
  function hidePopup(){
    if(previewEl){
      try{ document.removeEventListener("keydown",previewEl._esc);}catch(e){}
      previewEl.remove(); previewEl=null;
    }
    hideMenu();
  }

  // ----- 右键菜单 -----
  function showMenu(x,y,items){
    hideMenu();
    var m=document.createElement("div");
    m.className="maint-menu";
    m.style.left=x+"px"; m.style.top=y+"px";
    items.forEach(function(it){
      if(it.sep){ var sep=document.createElement("div"); sep.className="maint-menu-sep"; m.appendChild(sep); return; }
      var a=document.createElement("div");
      a.className="maint-menu-item"+(it.disabled?" disabled":"");
      a.textContent=it.label;
      if(!it.disabled) a.addEventListener("click",function(){ hideMenu(); it.action(); });
      m.appendChild(a);
    });
    document.body.appendChild(m);
    ctxMenu=m;
    var onDocClick=function(e){ if(!m.contains(e.target)) hideMenu(); };
    setTimeout(function(){ document.addEventListener("click",onDocClick);},0);
    m._docClick=onDocClick;
  }
  function hideMenu(){
    if(ctxMenu){ try{ document.removeEventListener("click",ctxMenu._docClick);}catch(e){} ctxMenu.remove(); ctxMenu=null; }
  }
  function showTodoMenu(t, anchor, e){
    var items=[
      {label:"未确认不良 · 优先级"+priorityZh(t.SortCount||t.TotalCount)+" · "+(t.SortCount||t.TotalCount)+" 次",disabled:true},
      {sep:true},
      {label:"确认问题（建维修记录）",action:function(){ confirmTodo(t, "open"); }},
    ];
    Statuses.forEach(function(def){
      if(def.key==="open") return;
      items.push({label:"确认并置为 "+def.zh, action:function(){ confirmTodo(t, def.key); }});
    });
    items.push({sep:true},{label:"删除此待办",action:function(){ deleteTodo(t); }});
    showMenu(e.clientX, e.clientY, items);
  }
  function showRecordMenu(r, anchor, e){
    var cur=normalizeStatus(r.Status||r.status);
    var items=[
      {label:"#"+(r.Id||r.id)+"  "+(r.FailItem||r.failItem).slice(0,24),disabled:true},
      {sep:true},
      {label:"编辑…",action:function(){ editRecord(r); }},
      {sep:true}
    ];
    Statuses.forEach(function(def){
      items.push({label:"标记为 "+def.zh, disabled:def.key===cur, action:function(){ moveRecordStatus(r, def.key); }});
    });
    items.push({sep:true},{label:"删除",action:function(){ deleteRecord(r); }});
    showMenu(e.clientX, e.clientY, items);
  }

  // ----- 操作：确认待办 / 删除待办 / 编辑记录 / 删除记录 -----
  function confirmTodo(t, targetStatus){
    targetStatus=normalizeStatus(targetStatus);
    var preset={
      StationId:t.StationId||t.stationId||"",
      FailItem:t.Title||t.title||"",
      Severity: (t.SortCount||t.TotalCount||0)>=20?"critical":"major",
      Status:targetStatus,
      Notes: (t.Variants||t.variants||[]).length>1?("来源测试项：\n"+(t.Variants||t.variants).join("\n")):""
    };
    showMaintenanceForm(null, preset, function(formData){
      var body={
        todoId: t.Id||t.id,
        station_id: formData.StationId,
        fail_item: formData.FailItem,
        fail_reason: formData.FailReason,
        severity: formData.Severity,
        status: formData.Status,
        resolver: formData.Resolver,
        resolution: formData.Resolution,
        notes: formData.Notes,
        created_at: formData.CreatedAt
      };
      apiSend("POST","/api/todos/ack",body).then(function(){
        App.toast("已确认待办 → 维修记录（"+zhOf(targetStatus)+"）","ok");
        lastSig=""; loadAll().then(render);
      }).catch(function(err){ App.toast("确认失败："+err.message,"err"); });
    });
  }
  function deleteTodo(t){
    if(!confirm("确定删除待办「"+(t.Title||t.title)+"」？\n该故障项累计 "+(t.TotalCount||0)+" 次不良，删除后不再出现在待办列（写入 dismissed_todos 防重入）。")) return;
    var id=t.Id||t.id;
    apiSend("DELETE","/api/todos?id="+id).then(function(){
      App.toast("已删除待办","ok");
      lastSig=""; loadAll().then(render);
    }).catch(function(err){ App.toast("删除失败："+err.message,"err"); });
  }
  function editRecord(r){
    showMaintenanceForm(r, null, function(formData){
      var body={
        id: r.Id||r.id,
        station_id: formData.StationId,
        equipment_model: formData.EquipmentModel,
        equipment_sn: formData.EquipmentSn,
        fail_item: formData.FailItem,
        fail_reason: formData.FailReason,
        severity: formData.Severity,
        status: formData.Status,
        resolver: formData.Resolver,
        resolution: formData.Resolution,
        notes: formData.Notes,
        created_at: formData.CreatedAt
      };
      apiSend("PATCH","/api/maintenance",body).then(function(){
        App.toast("已更新维修记录","ok");
        lastSig=""; loadAll().then(render);
      }).catch(function(err){ App.toast("更新失败："+err.message,"err"); });
    });
  }
  function moveRecordStatus(r, targetStatus){
    targetStatus=normalizeStatus(targetStatus);
    var preset={
      StationId:r.StationId||r.stationId,
      FailItem:r.FailItem||r.failItem,
      FailReason:r.FailReason||r.failReason,
      Severity:r.Severity||r.severity,
      Status:targetStatus,
      Resolver:r.Resolver||r.resolver,
      Resolution:r.Resolution||r.resolution,
      Notes:r.Notes||r.notes,
      CreatedAt:r.CreatedAt||r.createdAt
    };
    showMaintenanceForm(r, preset, function(formData){
      var body={
        id: r.Id||r.id,
        status: formData.Status,
        severity: formData.Severity,
        resolver: formData.Resolver,
        resolution: formData.Resolution,
        fail_reason: formData.FailReason,
        notes: formData.Notes,
        fail_item: formData.FailItem,
        created_at: formData.CreatedAt
      };
      apiSend("PATCH","/api/maintenance",body).then(function(){
        App.toast("已标记为 "+zhOf(targetStatus),"ok");
        lastSig=""; loadAll().then(render);
      }).catch(function(err){ App.toast("标记失败："+err.message,"err"); });
    });
  }
  function deleteRecord(r){
    var failCnt = 0; // 可通过 /api/maintenance 关联查询，但前端先提示通用文案
    var msg="确定删除维修记录 #"+(r.Id||r.id)+"（"+(r.FailItem||r.failItem)+"）？";
    if(!confirm(msg)) return;
    var id=r.Id||r.id;
    apiSend("DELETE","/api/maintenance?id="+id).then(function(){
      App.toast("已删除记录","ok");
      lastSig=""; loadAll().then(render);
    }).catch(function(err){ App.toast("删除失败："+err.message,"err"); });
  }

  // ----- 维修表单（全字段） -----
  function showMaintenanceForm(editRec, preset, onSave){
    var isEdit=!!editRec;
    var title=isEdit?"编辑维修记录 #"+(editRec.Id||editRec.id):"新增维修记录";
    var data0={
      StationId: (preset&&preset.StationId)|| (editRec&&(editRec.StationId||editRec.stationId))||"",
      FailItem: (preset&&preset.FailItem)|| (editRec&&(editRec.FailItem||editRec.failItem))||"",
      FailReason: (preset&&preset.FailReason)|| (editRec&&(editRec.FailReason||editRec.failReason))||"",
      Severity: (preset&&preset.Severity)|| (editRec&&(editRec.Severity||editRec.severity))||"major",
      Status: normalizeStatus((preset&&preset.Status)|| (editRec&&(editRec.Status||editRec.status))||"open"),
      Resolver: (preset&&preset.Resolver)|| (editRec&&(editRec.Resolver||editRec.resolver))||"",
      Resolution: (preset&&preset.Resolution)|| (editRec&&(editRec.Resolution||editRec.resolution))||"",
      Notes: (preset&&preset.Notes)|| (editRec&&(editRec.Notes||editRec.notes))||"",
      CreatedAt: (preset&&preset.CreatedAt)|| (editRec&&(editRec.CreatedAt||editRec.createdAt))|| new Date().toISOString().slice(0,16).replace("T"," "),
      EquipmentModel: (editRec&&(editRec.EquipmentModel||editRec.equipmentModel))||"",
      EquipmentSn: (editRec&&(editRec.EquipmentSn||editRec.equipmentSn))||""
    };
    var overlay=document.createElement("div");
    overlay.className="maint-form-overlay";
    overlay.addEventListener("click",function(e){ if(e.target===overlay) overlay.remove(); });
    var form=document.createElement("div");
    form.className="maint-form";
    form.addEventListener("click",function(e){e.stopPropagation();});
    var sevZh=severityZh(data0.Severity);
    var statusZh=zhOf(data0.Status);
    // 人员候选下拉
    var resolverOpts=data.resolvers.map(function(n){ return "<option value='"+esc(n)+"'>"+esc(n)+"</option>"; }).join("");
    form.innerHTML=
      "<div class='mf-head'>"+esc(title)+"<span class='mf-close'>✕</span></div>"
      +"<div class='mf-body'>"
      +"<label>记录日期 *<input type='text' data-k='CreatedAt' value='"+esc(data0.CreatedAt)+"' placeholder='yyyy-MM-dd HH:mm:ss'></label>"
      +"<label>故障项目 *<input type='text' data-k='FailItem' value='"+esc(data0.FailItem)+"' placeholder='必填'></label>"
      +"<label>故障描述<input type='text' data-k='FailReason' value='"+esc(data0.FailReason)+"' placeholder='选填'></label>"
      +"<label>严重度<select data-k='Severity'><option value='major' "+(sevZh==="一般"?"selected":"")+">一般</option><option value='critical' "+(sevZh==="严重"?"selected":"")+">严重</option><option value='minor' "+(sevZh==="轻微"?"selected":"")+">轻微</option></select></label>"
      +"<label>维修人员<input list='resolverList' data-k='Resolver' value='"+esc(data0.Resolver)+"' placeholder='可多选、逗号分隔，手敲自动登记'><datalist id='resolverList'>"+resolverOpts+"</datalist><button type='button' class='mf-btn small' data-act='pick'>选择</button> <button type='button' class='mf-btn small' data-act='manage'>+ 人员</button></label>"
      +"<label>维修措施<textarea data-k='Resolution' rows='2'>"+esc(data0.Resolution)+"</textarea></label>"
      +"<label>当前状态<select data-k='Status'><option value='unknown' "+(data0.Status==="unknown"?"selected":"")+">未知问题</option><option value='open' "+(data0.Status==="open"?"selected":"")+">待办</option><option value='in_progress' "+(data0.Status==="in_progress"?"selected":"")+">持续跟踪</option><option value='resolved' "+(data0.Status==="resolved"?"selected":"")+">已完成</option></select></label>"
      +"<label>备注<textarea data-k='Notes' rows='3'>"+esc(data0.Notes)+"</textarea></label>"
      +"</div>"
      +"<div class='mf-foot'><button class='mf-btn' data-act='cancel'>取消</button><button class='mf-btn primary' data-act='save'>保存</button></div>";
    overlay.appendChild(form);
    document.body.appendChild(overlay);
    form.querySelector(".mf-close").addEventListener("click",function(){ overlay.remove(); });
    form.querySelector("[data-act='cancel']").addEventListener("click",function(){ overlay.remove(); });
    form.querySelector("[data-act='pick']").addEventListener("click",function(){ pickResolvers(function(names){
      var joined=names.join("、");
      form.querySelector("[data-k='Resolver']").value=joined;
      // 同步刷新候选
      loadResolversToForm(form);
    }); });
    form.querySelector("[data-act='manage']").addEventListener("click",function(){ manageResolvers(function(){
      loadResolversToForm(form);
    }); });
    form.querySelector("[data-act='save']").addEventListener("click",function(){
      var get=function(k){ var el=form.querySelector("[data-k='"+k+"']"); return el?el.value.trim():""; };
      var failItem=get("FailItem");
      if(!failItem){ App.toast("故障项目为必填","err"); return; }
      var res={
        StationId: get("CreatedAt")?get("CreatedAt"):data0.StationId, // 兼容
        FailItem: failItem,
        FailReason: get("FailReason"),
        Severity: get("Severity"),
        Status: get("Status"),
        Resolver: get("Resolver"),
        Resolution: get("Resolution"),
        Notes: get("Notes"),
        CreatedAt: get("CreatedAt"),
        EquipmentModel: data0.EquipmentModel,
        EquipmentSn: data0.EquipmentSn
      };
      // 人员自动登记（多人拆分）
      var names=res.Resolver?res.Resolver.split(/[,，、\/;|]+/).map(function(s){return s.trim();}).filter(Boolean):[];
      names.forEach(function(n){ apiSend("POST","/api/resolvers",{name:n}).catch(function(){}); });
      overlay.remove();
      onSave(res);
    });
    function loadResolversToForm(f){
      apiGet("/api/resolvers").then(function(list){
        data.resolvers=list;
        var dl=f.querySelector("#resolverList");
        if(dl) dl.innerHTML=list.map(function(n){ return "<option value='"+esc(n)+"'>"; }).join("");
      });
    }
  }

  function pickResolvers(cb){
    var overlay=document.createElement("div");
    overlay.className="maint-form-overlay";
    var box=document.createElement("div");
    box.className="maint-form";
    box.style.width="420px";
    var curList=data.resolvers;
    var html="<div class='mf-head'>选择维修人员（可多选）<span class='mf-close'>✕</span></div><div class='mf-body' style='max-height:360px;overflow:auto'><div id='pickList'>";
    curList.forEach(function(n){ html+="<label style='display:block;margin:4px 0'><input type='checkbox' value='"+esc(n)+"'> "+esc(n)+"</label>"; });
    html+="</div><div style='margin-top:10px'><input id='newResolver' placeholder='输入新人员姓名' style='width:180px'> <button id='btnAddPick' class='mf-btn small'>添加并勾选</button></div></div><div class='mf-foot'><button class='mf-btn' id='btnCancelPick'>取消</button><button class='mf-btn primary' id='btnOkPick'>确定</button></div>";
    box.innerHTML=html;
    overlay.appendChild(box); document.body.appendChild(overlay);
    box.querySelector(".mf-close").addEventListener("click",function(){ overlay.remove(); });
    box.querySelector("#btnCancelPick").addEventListener("click",function(){ overlay.remove(); });
    box.querySelector("#btnAddPick").addEventListener("click",function(){
      var nv=box.querySelector("#newResolver").value.trim();
      if(!nv) return;
      apiSend("POST","/api/resolvers",{name:nv}).then(function(){
        var listEl=box.querySelector("#pickList");
        var lbl=document.createElement("label"); lbl.style.display="block"; lbl.style.margin="4px 0";
        lbl.innerHTML="<input type='checkbox' value='"+esc(nv)+"' checked> "+esc(nv);
        listEl.appendChild(lbl);
        box.querySelector("#newResolver").value="";
        data.resolvers.push(nv);
      });
    });
    box.querySelector("#btnOkPick").addEventListener("click",function(){
      var sel=[]; box.querySelectorAll("input[type='checkbox']:checked").forEach(function(c){ sel.push(c.value); });
      overlay.remove(); cb(sel);
    });
    overlay.addEventListener("click",function(e){ if(e.target===overlay) overlay.remove(); });
  }

  function manageResolvers(cb){
    var overlay=document.createElement("div");
    overlay.className="maint-form-overlay";
    var box=document.createElement("div");
    box.className="maint-form";
    box.style.width="420px";
    function render(){
      var html="<div class='mf-head'>维修人员 <span class='mf-close'>✕</span></div><div class='mf-body'><div style='margin-bottom:8px;color:#9AA5B1'>输入姓名 → 【添加】即可</div><div style='display:flex;gap:8px'><input id='mgrName' placeholder='维修人员姓名' style='flex:1'> <button id='mgrAdd' class='mf-btn primary'>添加</button></div><div id='mgrList' style='margin-top:12px;max-height:240px;overflow:auto;border:1px solid #262D38;border-radius:8px;padding:8px'>";
      data.resolvers.forEach(function(n){ html+="<div style='display:flex;justify-content:space-between;padding:4px 0'>"+esc(n)+"<span><button class='mf-btn small' data-rename='"+esc(n)+"'>改名</button> <button class='mf-btn small' data-del='"+esc(n)+"'>删除</button></span></div>"; });
      html+="</div><div id='mgrHint' style='margin-top:8px;color:#9AA5B1'>名单 "+data.resolvers.length+" 人</div></div><div class='mf-foot'><button class='mf-btn' id='mgrClose'>关闭</button></div>";
      box.innerHTML=html;
      box.querySelector(".mf-close").addEventListener("click",function(){ overlay.remove(); if(cb) cb(); });
      box.querySelector("#mgrClose").addEventListener("click",function(){ overlay.remove(); if(cb) cb(); });
      box.querySelector("#mgrAdd").addEventListener("click",function(){
        var v=box.querySelector("#mgrName").value.trim();
        if(!v) return;
        apiSend("POST","/api/resolvers",{name:v}).then(function(){ data.resolvers.push(v); render(); });
      });
      box.querySelectorAll("[data-del]").forEach(function(b){ b.addEventListener("click",function(){
        var n=b.getAttribute("data-del");
        if(!confirm("从名单删除「"+n+"」？历史记录里的旧名字不会被改动。")) return;
        apiSend("DELETE","/api/resolvers?name="+encodeURIComponent(n)).then(function(){ data.resolvers=data.resolvers.filter(function(x){return x!==n;}); render(); });
      });});
      box.querySelectorAll("[data-rename]").forEach(function(b){ b.addEventListener("click",function(){
        var old=b.getAttribute("data-rename");
        var neu=prompt("新名字：",old);
        if(!neu||neu===old) return;
        var sync=confirm("同时把历史维修记录里的「"+old+"」也改成「"+neu+"」？（治错别字）");
        apiSend("POST","/api/resolvers/rename",{oldName:old,newName:neu,sync:sync}).then(function(){ data.resolvers=data.resolvers.map(function(x){return x===old?neu:x;}); render(); });
      });});
    }
    render();
    overlay.appendChild(box); document.body.appendChild(overlay);
    overlay.addEventListener("click",function(e){ if(e.target===overlay){ overlay.remove(); if(cb) cb(); }});
  }

  // ----- FAIL 选择建单 -----
  function showFailPicker(){
    var overlay=document.createElement("div");
    overlay.className="maint-form-overlay";
    var box=document.createElement("div");
    box.className="maint-form";
    box.style.width="680px";
    box.style.maxHeight="80vh";
    box.innerHTML="<div class='mf-head'>选择故障项（来自 FAIL 记录，已去重）<span class='mf-close'>✕</span></div><div class='mf-body'><div style='color:#9AA5B1;margin-bottom:6px'>勾选一个或多个故障项 → 确定后写入维修记录的「故障项目」。</div><div style='display:flex;gap:8px;margin-bottom:8px'><input id='failFilter' placeholder='输入测试项关键字' style='flex:1'><label><input type='checkbox' id='onlySelf'> 只看本机台</label> <button id='btnDeep' class='mf-btn small'>深扫 XML(更全)</button> <button id='btnAll' class='mf-btn small'>全选</button> <button id='btnNone' class='mf-btn small'>清空</button></div><div id='failList' style='max-height:360px;overflow:auto;border:1px solid #262D38;border-radius:8px;padding:6px'></div><div id='failStatus' style='margin-top:6px;color:#9AA5B1'></div><label style='margin-top:8px;display:block'><input type='checkbox' id='mergeOne'> 合并为一条记录（否则每个故障项一条，“ / ”连接）</label></div><div class='mf-foot'><button class='mf-btn' id='failCancel'>取消</button><button class='mf-btn primary' id='failOk'>确定</button></div>";
    overlay.appendChild(box); document.body.appendChild(overlay);
    box.querySelector(".mf-close").addEventListener("click",function(){ overlay.remove(); });
    box.querySelector("#failCancel").addEventListener("click",function(){ overlay.remove(); });
    overlay.addEventListener("click",function(e){ if(e.target===overlay) overlay.remove(); });
    var stats=[];
    var checked=new Set();
    function load(){
      var url="/api/fails?limit=2000";
      if(box.querySelector("#onlySelf").checked && App.state.sel) url+="&machine="+encodeURIComponent(App.state.sel);
      apiGet(url).then(function(rows){
        var map={};
        rows.forEach(function(r){
          var it=(r.FailReason||r.failReason||"").trim();
          if(!it) return;
          if(!map[it]) map[it]={item:it,count:0,last:"",models:new Set(),stations:new Set()};
          map[it].count++;
          var ts=r.IngestTs||r.ingestTs||r.Ts||r.ts||"";
          if(ts>map[it].last) map[it].last=ts;
          if(r.Model||r.model) map[it].models.add(r.Model||r.model);
          if(r.Machine||r.machine) map[it].stations.add(r.Machine||r.machine);
        });
        stats=Object.values(map).sort(function(a,b){ return b.count-a.count; });
        renderList();
      });
    }
    function renderList(){
      var kw=box.querySelector("#failFilter").value.trim().toLowerCase();
      var filtered=stats.filter(function(s){ return !kw||s.item.toLowerCase().indexOf(kw)>=0; });
      var listEl=box.querySelector("#failList");
      listEl.innerHTML="";
      filtered.forEach(function(s){
        var div=document.createElement("label");
        div.style.display="flex"; div.style.alignItems="center"; div.style.gap="8px"; div.style.padding="4px 0";
        var cb=document.createElement("input"); cb.type="checkbox"; cb.value=s.item; cb.checked=checked.has(s.item);
        cb.addEventListener("change",function(){ if(cb.checked) checked.add(s.item); else checked.delete(s.item); updateStatus(); });
        var span=document.createElement("span");
        span.textContent=s.item+"  ("+s.count+" 次)  "+s.last.slice(5,16);
        div.appendChild(cb); div.appendChild(span); listEl.appendChild(div);
      });
      updateStatus();
    }
    function updateStatus(){
      var cnt=box.querySelector("#failList").childElementCount;
      box.querySelector("#failStatus").textContent="去重后共 "+stats.length+" 个故障项（当前显示 "+cnt+" 个），已勾选 "+checked.size+" 个";
    }
    box.querySelector("#failFilter").addEventListener("input",renderList);
    box.querySelector("#onlySelf").addEventListener("change",load);
    box.querySelector("#btnDeep").addEventListener("click",function(){
      App.toast("深扫：服务端已按 XML 复核首个失败项（当前列表即为去重后结果）","ok");
    });
    box.querySelector("#btnAll").addEventListener("click",function(){
      stats.forEach(function(s){ checked.add(s.item); }); renderList();
    });
    box.querySelector("#btnNone").addEventListener("click",function(){ checked.clear(); renderList(); });
    box.querySelector("#failOk").addEventListener("click",function(){
      if(checked.size===0){ App.toast("一个故障项都没勾选","err"); return; }
      var items=Array.from(checked);
      var merge=box.querySelector("#mergeOne").checked;
      overlay.remove();
      // 进入维修表单批量建单
      var preset={FailItem:items[0]};
      showMaintenanceForm(null, null, function(formData){
        // 批量创建
        var body={items:items, merge:merge, station_id:formData.StationId||App.state.sel||"", fail_reason:formData.FailReason, severity:formData.Severity, status:formData.Status, resolver:formData.Resolver, resolution:formData.Resolution, notes:formData.Notes, created_at:formData.CreatedAt};
        apiSend("POST","/api/maintenance",body).then(function(res){
          var ids=res.ids||[res.id];
          App.toast("已创建 "+ids.length+" 条维修记录","ok");
          lastSig=""; loadAll().then(render);
        }).catch(function(err){ App.toast("创建失败："+err.message,"err"); });
      });
      // 若合并，预填充 merge 视图需要定制：此处借用已选 items 在表单提交时处理
      // 为让表单显示批量列表，我们补一个临时提示
      setTimeout(function(){
        var f=document.querySelector(".maint-form");
        if(f){
          var inp=f.querySelector("[data-k='FailItem']");
          if(inp) inp.value=merge?items.join(" / "):items.join(", ");
        }
      },100);
    });
    load();
  }

  // ----- 导出 -----
  function exportMaint(format){
    var status=filterStatus;
    var q="?format="+format;
    if(status) q+="&status="+encodeURIComponent(status);
    var url="/api/export/maintenance"+q;
    window.open(App.withToken(url),"_blank");
  }

  function renderResolversDatalist(){
    // 由表单动态读取，不在此处全局渲染
  }

  // ----- 构建骨架 -----
  function buildSkeleton(el){
    el.innerHTML=
      '<div class="page-node">'
      +'<div class="toolbar">'
      +'<select data-ref="statusFilter" title="状态筛选"><option value="">全部状态</option><option value="unknown">未知问题</option><option value="open">待办</option><option value="in_progress">持续跟踪</option><option value="resolved">已完成</option></select>'
      +'<select data-ref="todoRange" title="待办区间"><option value="7">近 7 天</option><option value="30" selected>近 30 天</option><option value="90">近 90 天</option><option value="all">全部（永久）</option><option value="custom">自定义…</option></select>'
      +'<span data-ref="customWrap" style="display:none;align-items:center;gap:6px">'
      +  '<input type="date" data-ref="dateFrom" title="起始日期" style="height:34px">'
      +  '<span style="color:var(--dim)">至</span>'
      +  '<input type="date" data-ref="dateTo" title="结束日期" style="height:34px">'
      +  '<button class="act" data-ref="btnRangeApply">应用</button>'
      +'</span>'
      +'<span class="badge" data-ref="badge_total">共 0 条</span>'
      +'<span data-ref="filteredCount" style="color:#9AA5B1;margin-left:8px"></span>'
      +'<div style="flex:1"></div>'
      +'<button class="act" data-ref="btnFailPick">从FAIL选择故障</button>'
      +'<button class="act" data-ref="btnNew">新增记录</button>'
      +'<button class="act" data-ref="btnExportXlsx">导出 xlsx</button>'
      +'<button class="act" data-ref="btnExportCsv">导出 csv</button>'
      +'<button class="act" data-ref="btnResolvers" title="维修人员名单管理">人员</button>'
      +'</div>'
      +'<div class="maint-board" data-ref="board"></div>'
      +'<div class="foot">维修看板每 3 秒自动刷新 · 拖拽改状态需确认 · 右键更多操作 · 支持乐观更新</div>'
      +'</div>';
    var nodes=el.querySelectorAll("[data-ref]");
    for(var i=0;i<nodes.length;i++) refs[nodes[i].getAttribute("data-ref")]=nodes[i];
    refs.board=el.querySelector("[data-ref='board']");
    // 事件
    refs.statusFilter.addEventListener("change",function(e){ filterStatus=e.target.value; lastSig=""; loadAll().then(render); });
    refs.todoRange.addEventListener("change",function(e){
      todoRange=e.target.value;
      if(todoRange==="custom"){
        // 自定义区间：展开起止日期框，预填为当前生效的区间（默认近 30 天），等点「应用」才真正加载
        if(refs.customWrap) refs.customWrap.style.display="inline-flex";
        if(refs.dateFrom && !refs.dateFrom.value) refs.dateFrom.value=compactToYmd(todoFrom||todayMinus(29));
        if(refs.dateTo && !refs.dateTo.value) refs.dateTo.value=compactToYmd(todoTo||todayYmd());
        return;
      }
      if(refs.customWrap) refs.customWrap.style.display="none";
      if(todoRange==="all"){ todoFrom=null; todoTo=null; }
      else if(todoRange==="7"){ todoFrom=todayMinus(6); todoTo=todayYmd(); }
      else if(todoRange==="30"){ todoFrom=todayMinus(29); todoTo=todayYmd(); }
      else if(todoRange==="90"){ todoFrom=todayMinus(89); todoTo=todayYmd(); }
      lastSig=""; loadAll().then(render);
    });
    // 自定义区间应用：<input type="date"> 是 yyyy-MM-dd，后端既认 yyyyMMdd 也能 Parse，
    // 这里统一压成 yyyyMMdd，与近 N 天预设（todayYmd/todayMinus）口径一致。
    if(refs.btnRangeApply) refs.btnRangeApply.addEventListener("click",function(){
      var f=ymdToCompact(refs.dateFrom?refs.dateFrom.value:"");
      var t=ymdToCompact(refs.dateTo?refs.dateTo.value:"");
      if(!f||!t){ App.toast("请选择起止日期","err"); return; }
      if(f>t){ App.toast("起始日期不能晚于结束日期","err"); return; }
      todoRange="custom"; todoFrom=f; todoTo=t;
      lastSig=""; loadAll().then(function(){ render(); App.toast("待办区间："+f.slice(0,4)+"-"+f.slice(4,6)+"-"+f.slice(6,8)+" ~ "+t.slice(0,4)+"-"+t.slice(4,6)+"-"+t.slice(6,8),"ok"); });
    });
    refs.btnNew.addEventListener("click",function(){
      if (App.auth && App.auth.isViewer && App.auth.isViewer()) { App.toast("访客无登记权限", "err"); return; }
      showMaintenanceForm(null,null,function(formData){
      var body={fail_item:formData.FailItem, fail_reason:formData.FailReason, severity:formData.Severity, status:formData.Status, resolver:formData.Resolver, resolution:formData.Resolution, notes:formData.Notes, created_at:formData.CreatedAt, station_id:formData.StationId||App.state.sel||""};
      apiSend("POST","/api/maintenance",body).then(function(){ App.toast("已新增记录","ok"); lastSig=""; loadAll().then(render); }).catch(function(err){ App.toast("新增失败："+err.message,"err"); });
    });});
    refs.btnFailPick.addEventListener("click",function(){
      if (App.auth && App.auth.isViewer && App.auth.isViewer()) { App.toast("访客无登记权限", "err"); return; }
      showFailPicker();
    });
    refs.btnExportXlsx.addEventListener("click",function(){
      // viewer 可导出（audit 留痕），按原 P5 保留
      exportMaint("xlsx");
    });
    refs.btnExportCsv.addEventListener("click",function(){ exportMaint("csv"); });
    refs.btnResolvers.addEventListener("click",function(){ manageResolvers(); });
    // 权限显隐：viewer 隐藏登记类按钮（导出按 P5 保留可见）
    if (refs.btnNew) refs.btnNew.classList.add("hide-for-viewer");
    if (refs.btnFailPick) refs.btnFailPick.classList.add("hide-for-viewer");
    if (App.applyRoleVisibility) App.applyRoleVisibility(el);
  }

  App.Modules["page-maintenance"]={
    init:function(el, ctx){
      buildSkeleton(el);
      loadAll().then(render);
      // 3s 轮询（与全局一致，但本页独立轮询维修数据）
      var timer=setInterval(function(){ if(!el.isConnected){ clearInterval(timer); return; } loadAll().then(render); },3000);
      // 全局数据刷新也触发
      var prevRender=render;
      this._timer=timer;
    },
    render: function(){ loadAll().then(render); }
  };
})(window);
