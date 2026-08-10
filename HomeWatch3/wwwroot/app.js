const $=id=>document.getElementById(id);
const escapeHtml=value=>String(value??'').replace(/[&<>'"]/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[char]));
const pick=(object,...names)=>{for(const name of names){const value=object?.[name];if(value!==undefined&&value!==null&&String(value).trim()!=='')return value}return null};
const number=value=>{const parsed=Number(String(value??0).replace(/[^0-9.+-]/g,''));return Number.isFinite(parsed)?parsed:0};
const asArray=value=>Array.isArray(value)?value:[];
const age=value=>{const date=new Date(value);if(Number.isNaN(date.getTime()))return 'unknown';const seconds=Math.max(0,Math.floor((Date.now()-date.getTime())/1000));if(seconds<60)return 'just now';if(seconds<3600)return `${Math.floor(seconds/60)} min ago`;if(seconds<86400)return `${Math.floor(seconds/3600)} hr ago`;return `${Math.floor(seconds/86400)} day${seconds<172800?'':'s'} ago`};
const formatRate=bits=>{const value=Math.max(0,number(bits));if(value>=1e9)return `${(value/1e9).toFixed(2)} Gb/s`;if(value>=1e6)return `${(value/1e6).toFixed(value>=1e8?0:1)} Mb/s`;if(value>=1e3)return `${(value/1e3).toFixed(value>=1e5?0:1)} Kb/s`;return `${Math.round(value)} b/s`};
const formatBytes=bytes=>{const value=Math.max(0,number(bytes));if(value>=1e12)return `${(value/1e12).toFixed(1)} TB`;if(value>=1e9)return `${(value/1e9).toFixed(1)} GB`;if(value>=1e6)return `${(value/1e6).toFixed(1)} MB`;if(value>=1e3)return `${(value/1e3).toFixed(1)} KB`;return `${Math.round(value)} B`};

async function json(url){const response=await fetch(url,{cache:'no-store'});if(!response.ok){let message=`${response.status} ${response.statusText}`;try{const body=await response.json();message=body.error||message}catch{}throw new Error(message)}return response.json()}
async function safeJson(url){try{return {ok:true,value:await json(url),error:null}}catch(error){return {ok:false,value:null,error:error.message}}}
function setText(id,value){const element=$(id);if(element)element.textContent=value}
function part(snapshot,name){const section=snapshot?.[name];if(section?.ok===false)return {ok:false,value:null,error:section.error||`${name} unavailable`};if(section?.data!==undefined)return {ok:true,value:section.data,error:null};return section!==undefined?{ok:true,value:section,error:null}:{ok:false,value:null,error:`${name} unavailable`}}
function rowsFrom(value){
  if(Array.isArray(value))return value;
  if(!value||typeof value!=='object')return [];
  for(const key of ['rows','data','items','records','gateways','interfaces']){
    if(Array.isArray(value[key]))return value[key];
    if(value[key]&&typeof value[key]==='object'){const nestedRows=rowsFrom(value[key]);if(nestedRows.length)return nestedRows}
  }
  if(['name','gateway','identifier','interface','if','device','status','state','address','ipaddr'].some(key=>value[key]!==undefined))return [value];
  for(const nested of Object.values(value)){
    if(nested&&typeof nested==='object'){
      for(const key of ['rows','data','items','records'])if(Array.isArray(nested[key]))return nested[key];
    }
  }
  const entries=Object.entries(value).filter(([,entry])=>entry&&typeof entry==='object'&&!Array.isArray(entry));
  return entries.map(([key,entry])=>({__key:key,...entry}));
}
function statusRow(label,value,ok,detail=''){
  return `<div class="dashboard-status-row"><span class="status-dot ${ok?'is-up':'is-down'}"></span><div><strong>${escapeHtml(label)}</strong>${detail?`<small>${escapeHtml(detail)}</small>`:''}</div><span>${escapeHtml(value)}</span></div>`;
}
function widgetError(message){return `<div class="widget-empty widget-error">${escapeHtml(message||'Data unavailable')}</div>`}
function renderSafely(targetId,action){try{action()}catch(error){const target=$(targetId);if(target){target.classList.remove('hidden');target.innerHTML=widgetError(error.message)}}}

function renderSystem(data){
  const home=data.status,opn=data.opnsense.value?.health,ntop=data.ntopng,monitor=data.monitor;
  const rows=[];
  rows.push(statusRow('HomeWatch',home.ok?home.value.version:'Offline',home.ok,home.ok?'NAS service':'HomeWatch API unavailable'));
  const opnOk=data.opnsense.ok&&(opn?.reachable!==false)&&(opn?.configured!==false);
  rows.push(statusRow('OPNsense',opnOk?'Connected':'Unavailable',opnOk,opn?.error||'Firewall API'));
  const ntopOk=data.ntopng.ok&&data.ntopng.value?.available;
  rows.push(statusRow('ntopng',ntopOk?(data.ntopng.value.interfaceName||'Connected'):'Unavailable',ntopOk,data.ntopng.value?.error||'Application visibility'));
  const monitorOk=data.monitor.ok&&data.monitor.value?.enabled&&!data.monitor.value?.lastError;
  rows.push(statusRow('Safety monitor',monitorOk?'Active':data.monitor.ok?'Needs attention':'Unavailable',monitorOk,data.monitor.value?.lastError||'DNS monitoring'));
  $('systemStatus').innerHTML=rows.join('');
}

function renderTraffic(timeline){
  const points=timeline.ok?asArray(timeline.value):[];
  window.dashboardTrafficPoints=points;
  const latest=points.at(-1)||{};
  setText('downloadNow',formatRate(latest.bitsIn));
  setText('uploadNow',formatRate(latest.bitsOut));
  setText('peakDownload',formatRate(Math.max(0,...points.map(point=>number(point.bitsIn)))));
  setText('peakUpload',formatRate(Math.max(0,...points.map(point=>number(point.bitsOut)))));
  $('trafficEmpty').textContent=timeline.ok?'Waiting for live traffic samples…':timeline.error||'Traffic history unavailable';$('trafficEmpty').classList.toggle('hidden',points.length>1);
  drawTrafficChart(points);
}

function drawTrafficChart(points){
  const canvas=$('trafficChart'),wrap=canvas.parentElement,ratio=window.devicePixelRatio||1,width=Math.max(320,wrap.clientWidth),height=Math.max(220,wrap.clientHeight);
  canvas.width=Math.round(width*ratio);canvas.height=Math.round(height*ratio);canvas.style.width=`${width}px`;canvas.style.height=`${height}px`;
  const context=canvas.getContext('2d');context.setTransform(ratio,0,0,ratio,0,0);context.clearRect(0,0,width,height);
  const pad={left:8,right:8,top:18,bottom:20},chartWidth=width-pad.left-pad.right,chartHeight=height-pad.top-pad.bottom;
  context.strokeStyle='#e8ecef';context.lineWidth=1;
  for(let i=0;i<5;i++){const y=pad.top+(chartHeight/4)*i;context.beginPath();context.moveTo(pad.left,y);context.lineTo(width-pad.right,y);context.stroke()}
  if(points.length<2)return;
  const maximum=Math.max(1,...points.flatMap(point=>[number(point.bitsIn),number(point.bitsOut)]));
  const xy=(value,index)=>[pad.left+(chartWidth*index/(points.length-1)),pad.top+chartHeight-(number(value)/maximum*chartHeight)];
  const plot=(key,color,fill)=>{
    const line=new Path2D();points.forEach((point,index)=>{const [x,y]=xy(point[key],index);index?line.lineTo(x,y):line.moveTo(x,y)});
    if(fill){const area=new Path2D(line);area.lineTo(width-pad.right,pad.top+chartHeight);area.lineTo(pad.left,pad.top+chartHeight);area.closePath();context.fillStyle=fill;context.fill(area)}
    context.strokeStyle=color;context.lineWidth=2.25;context.lineJoin='round';context.lineCap='round';context.stroke(line);
  };
  plot('bitsIn','#f27a24','rgba(242,122,36,.15)');plot('bitsOut','#287eb7','rgba(40,126,183,.10)');
  context.fillStyle='#667085';context.font='11px system-ui';context.fillText(formatRate(maximum),pad.left,pad.top-5);context.fillText('now',width-pad.right-22,height-3);
}

function renderTopDevices(traffic,management){
  if(!traffic.ok){$('topDevices').innerHTML=widgetError(traffic.error);return}
  const devices=new Map(asArray(management.value).map(item=>[Number(item.device?.id),item.device]));
  const ranked=asArray(traffic.value).map(row=>({...row,total:number(row.averageBitsIn)+number(row.averageBitsOut)})).sort((a,b)=>b.total-a.total).slice(0,6);
  if(!ranked.length){$('topDevices').innerHTML='<div class="widget-empty">No active device traffic yet.</div>';return}
  const maximum=Math.max(1,...ranked.map(row=>row.total));
  $('topDevices').innerHTML=ranked.map((row,index)=>{const device=devices.get(Number(row.deviceId)),name=device?.name||row.ip||'Unknown device',link=device?`/device.html?id=${device.id}`:'/devices.html',share=Math.max(3,Math.round(row.total/maximum*100));return `<a class="rank-row" href="${link}"><span class="rank-number">${index+1}</span><div class="rank-main"><strong>${escapeHtml(name)}</strong><small>${escapeHtml(row.ip||device?.lastIpAddress||'No IP')}</small><span class="rank-track"><i style="width:${share}%"></i></span></div><div class="rank-value"><strong>${formatRate(row.total)}</strong><small>↓ ${formatRate(row.averageBitsIn)} · ↑ ${formatRate(row.averageBitsOut)}</small></div></a>`}).join('');
}

function renderApplications(ntopng){
  if(!ntopng.ok||!ntopng.value?.available){$('topApplications').innerHTML=widgetError(ntopng.value?.error||ntopng.error);return}
  setText('appInterface',ntopng.value.interfaceName||`Interface ${ntopng.value.interfaceId}`);
  const applications=asArray(ntopng.value.applications).slice(0,7);
  if(!applications.length){$('topApplications').innerHTML='<div class="widget-empty">ntopng has not reported application traffic yet.</div>';return}
  $('topApplications').innerHTML=applications.map((app,index)=>{const percent=Math.max(1,Math.min(100,number(app.percentage))),transport=app.kind==='Transport';return `<div class="app-row"><span class="app-swatch swatch-${index%7}"></span><div class="app-main"><div><strong>${escapeHtml(app.displayName||app.name)}</strong><span class="app-kind ${transport?'transport':''}">${escapeHtml(app.kind||'Application')}</span></div><span class="app-track"><i class="swatch-${index%7}" style="width:${percent}%"></i></span></div><div class="app-value"><strong>${percent.toFixed(1)}%</strong><small>${formatBytes(app.totalBytes)}</small></div></div>`}).join('');
}

function renderGateways(snapshot){
  const gateways=part(snapshot.value,'gateways');
  if(!snapshot.ok||!gateways.ok){$('gateways').innerHTML=widgetError(gateways.error||snapshot.error);return}
  const rows=rowsFrom(gateways.value).slice(0,5);
  if(!rows.length){$('gateways').innerHTML='<div class="widget-empty">No gateway rows returned.</div>';return}
  $('gateways').innerHTML=rows.map(row=>{const name=pick(row,'name','gateway','identifier','__key')||'Gateway',address=pick(row,'address','gateway_ip','ipaddr','ip')||'',raw=String(pick(row,'status','state','active')||'').toLowerCase(),up=!/(down|offline|loss|error|unreach|false)/.test(raw),status=raw&&raw!=='none'?raw:(up?'active':'down'),detail=[address,pick(row,'delay','latency'),pick(row,'loss')].filter(Boolean).join(' • ');return statusRow(name,status,up,detail)}).join('');
}

function renderInterfaces(snapshot){
  const interfaces=part(snapshot.value,'interfaces');
  if(!snapshot.ok||!interfaces.ok){$('interfaces').innerHTML=widgetError(interfaces.error||snapshot.error);return}
  const rows=rowsFrom(interfaces.value).slice(0,6);
  if(!rows.length){$('interfaces').innerHTML='<div class="widget-empty">No interface rows returned.</div>';return}
  $('interfaces').innerHTML=rows.map(row=>{const name=pick(row,'name','interface','if','device','__key')||'Interface',address=pick(row,'ipaddr','address','ip','ipv4')||'',raw=String(pick(row,'status','link_state','state')||'up').toLowerCase(),up=!/(down|offline|no carrier|false)/.test(raw),speed=pick(row,'speed','media','description')||'';return `<div class="interface-row"><span class="status-dot ${up?'is-up':'is-down'}"></span><div><strong>${escapeHtml(String(name).toUpperCase())}</strong><small>${escapeHtml([address,speed].filter(Boolean).join(' • ')||'Connected interface')}</small></div><span>${escapeHtml(raw||'up')}</span></div>`}).join('');
}

function renderInventory(management,reviews,traffic,ntopng){
  const items=management.ok?asArray(management.value):[],monitored=items.filter(item=>!item.ignored).length,ignored=items.filter(item=>item.ignored).length;
  const activeFromTraffic=traffic.ok?asArray(traffic.value).filter(row=>Date.now()-new Date(row.lastSampleUtc).getTime()<45000).length:0;
  const active=activeFromTraffic||number(ntopng.value?.localHosts)||number(ntopng.value?.activeHosts)||items.filter(item=>Date.now()-new Date(item.device?.lastSeenUtc).getTime()<600000).length;
  setText('activeDeviceCount',active);setText('activeDeviceSub',active===1?'device active on the LAN':'devices active on the LAN');
  setText('monitoredCount',management.ok?monitored:'—');setText('ignoredCount',management.ok?ignored:'—');setText('knownCount',management.ok?items.length:'—');setText('reviewCount',reviews.ok?asArray(reviews.value).length:'—');
}

function renderSafety(monitor,activity){
  const groups=activity.ok?asArray(activity.value):[],signals=groups.reduce((sum,item)=>sum+number(item.hits||1),0),enabled=monitor.ok&&monitor.value?.enabled&&!monitor.value?.lastError;
  setText('safetySignals',activity.ok?signals:'—');setText('safetySub',signals?`${groups.length} device group${groups.length===1?'':'s'} · last 30 min`:'Last 30 minutes');
  if(!monitor.ok){$('safetySummary').innerHTML=widgetError(monitor.error);return}
  if(monitor.value?.lastError){$('safetySummary').innerHTML=`<div class="safety-state attention"><span>!</span><div><strong>Monitor needs attention</strong><small>${escapeHtml(monitor.value.lastError)}</small></div></div>`;return}
  if(signals>0){$('safetySummary').innerHTML=`<div class="safety-state attention"><span>${signals}</span><div><strong>Recent safety signals</strong><small>${groups.length} monitored device group${groups.length===1?'':'s'} in the last 30 minutes. Review the evidence in History.</small></div></div>`;return}
  $('safetySummary').innerHTML=`<div class="safety-state clear"><span>✓</span><div><strong>${enabled?'Monitoring active':'Monitoring disabled'}</strong><small>${enabled?'No adult-domain signals from monitored devices in the last 30 minutes.':'Enable monitoring to collect safety signals.'}</small></div></div>`;
}

async function load(){
  $('refreshBtn').disabled=true;
  const [status,monitor,management,activity,traffic,timeline,ntopng,opnStatus,opnGateways,opnInterfaces,reviews]=await Promise.all([
    safeJson('/api/status'),safeJson('/api/monitoring/adult/status'),safeJson('/api/devices/management'),safeJson('/api/adult/activity?minutes=30'),safeJson('/api/traffic/window?seconds=30'),safeJson('/api/traffic/timeline?seconds=300'),safeJson('/api/ntopng/dashboard'),safeJson('/api/opnsense/status'),safeJson('/api/opnsense/gateways'),safeJson('/api/opnsense/interfaces/statistics'),safeJson('/api/device-reviews')
  ]);
  const opnsense={ok:opnStatus.ok||opnGateways.ok||opnInterfaces.ok,value:{health:opnStatus.value,gateways:{ok:opnGateways.ok,data:opnGateways.value,error:opnGateways.error},interfaces:{ok:opnInterfaces.ok,data:opnInterfaces.value,error:opnInterfaces.error}},error:opnStatus.error||opnGateways.error||opnInterfaces.error};
  const data={status,monitor,management,activity,traffic,timeline,ntopng,opnsense,reviews};
  $('servicePill').textContent=status.ok?'Online':'Offline';$('servicePill').className=`pill ${status.ok?'ok':'bad'}`;
  renderSafely('systemStatus',()=>renderSystem(data));renderSafely('trafficEmpty',()=>renderTraffic(timeline));renderSafely('topDevices',()=>renderTopDevices(traffic,management));renderSafely('topApplications',()=>renderApplications(ntopng));renderSafely('gateways',()=>renderGateways(opnsense));renderSafely('interfaces',()=>renderInterfaces(opnsense));try{renderInventory(management,reviews,traffic,ntopng)}catch{['activeDeviceCount','monitoredCount','ignoredCount','knownCount','reviewCount'].forEach(id=>setText(id,'—'))}renderSafely('safetySummary',()=>renderSafety(monitor,activity));
  setText('lastUpdated',status.ok?`Updated ${new Date().toLocaleTimeString()} • HomeWatch ${status.value.version}`:`Update incomplete • ${status.error}`);
  $('refreshBtn').disabled=false;
}

$('refreshBtn').addEventListener('click',load);
window.addEventListener('resize',()=>drawTrafficChart(window.dashboardTrafficPoints||[]));
load();setInterval(load,15000);
