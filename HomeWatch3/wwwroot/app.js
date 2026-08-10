const $=id=>document.getElementById(id);
const fmtTime=v=>{if(!v)return 'None';const d=new Date(v);return Number.isNaN(d.getTime())?v:d.toLocaleString()};
const age=v=>{if(!v)return '';const ms=Date.now()-new Date(v).getTime();if(ms<60000)return 'just now';const m=Math.floor(ms/60000);if(m<60)return `${m} min ago`;const h=Math.floor(m/60);return `${h} hr ago`};
async function json(url,options){const r=await fetch(url,{cache:'no-store',...(options||{})});if(!r.ok){let msg=`${r.status} ${r.statusText}`;try{const body=await r.json();if(body.error)msg=body.error}catch{}throw new Error(msg)}return r.json()}
function activityRow(a){const domains=(a.domains||[]).map(escapeHtml).join(', ');const cls=a.deviceId?'row activity-row clickable':'row activity-row';const attr=a.deviceId?` data-device-id="${a.deviceId}"`:'';return `<div class="${cls}"${attr}><div><div class="primary">${escapeHtml(a.device||a.ip||'Unknown device')}</div><div class="secondary">${escapeHtml(a.ip||'No IP')} • ${escapeHtml(a.domain||'Unknown domain')}</div></div><div class="middle"><div class="secondary">${escapeHtml(domains||'No domains')}</div><div class="secondary">${a.hits||0} signal${a.hits===1?'':'s'} • ${age(a.lastSeenUtc)}</div></div><span class="badge alert">${a.confidence||0}%</span></div>`}
function alertRow(a){return `<div class="row"><div><div class="primary">${escapeHtml(a.title||'Alert')}</div><div class="secondary wrap-text">${escapeHtml(a.message||'')}</div></div><div class="middle"><div class="secondary">${fmtTime(a.createdUtc)}</div></div><span class="badge alert">${escapeHtml(a.severity||'high')}</span></div>`}
function deviceRow(d){return `<button class="row device-row clickable" data-device-id="${d.id}" type="button"><div><div class="primary">${escapeHtml(d.name||d.lastIpAddress||'Unknown device')}</div><div class="secondary">${escapeHtml(d.vendor||'Unknown vendor')}</div></div><div class="middle"><div class="secondary">${escapeHtml(d.lastIpAddress||'No IP')} • ${escapeHtml(d.macAddress||'No MAC')}</div></div><span class="badge">Details</span></button>`}
function escapeHtml(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function detailEventRow(e){return `<div class="timeline-item"><div><strong>${escapeHtml(e.domain||e.application||e.category||'Network event')}</strong><div class="secondary">${escapeHtml(e.source||'unknown source')} • ${escapeHtml(e.protocol||'unknown protocol')} • ${e.confidence||0}% confidence</div></div><time>${fmtTime(e.timestampUtc)}</time></div>`}
function detailAlertRow(a){return `<div class="timeline-item"><div><strong>${escapeHtml(a.title||'Alert')}</strong><div class="secondary wrap-text">${escapeHtml(a.message||'')}</div></div><time>${fmtTime(a.createdUtc)}</time></div>`}

let livePaused=false;
let liveRows=[];
let deviceByIp=new Map();
const pick=(o,...names)=>{for(const n of names){if(o&&o[n]!==undefined&&o[n]!==null&&String(o[n]).trim()!=='')return String(o[n])}return ''};
function liveTime(r){const raw=pick(r,'time','timestamp','created','date');if(!raw)return '';if(/^\d+$/.test(raw)){const n=Number(raw);const d=new Date((raw.length<=10?n*1000:n));return Number.isNaN(d.getTime())?raw:d.toISOString()}return raw}
function renderLive(){
  const q=($('liveFilter').value||'').trim().toLowerCase();
  const filtered=liveRows.filter(r=>{const ip=pick(r,'client','client_ip','source','src','ip');const domain=pick(r,'domain','name','qname','query');const device=deviceByIp.get(ip);const hay=`${device?.name||''} ${ip} ${domain}`.toLowerCase();return !q||hay.includes(q)}).slice(0,75);
  $('liveActivity').innerHTML=filtered.length?filtered.map(r=>{
    const ip=pick(r,'client','client_ip','source','src','ip')||'Unknown IP';
    const domain=pick(r,'domain','name','qname','query')||'Unknown domain';
    const type=pick(r,'type','qtype','query_type')||'DNS';
    const result=pick(r,'return_code','rcode','status','answer')||'';
    const device=deviceByIp.get(ip);
    const name=device?.name||ip;
    const deviceAttr=device?.id?` data-device-id="${device.id}"`:'';
    const cls=device?.id?'live-row clickable':'live-row';
    return `<div class="${cls}"${deviceAttr}><div class="live-dot"></div><div class="live-main"><div class="primary">${escapeHtml(domain)}</div><div class="secondary">${escapeHtml(name)} • ${escapeHtml(ip)}</div></div><div class="live-meta"><span class="badge">${escapeHtml(type)}</span>${result?`<span class="secondary">${escapeHtml(result)}</span>`:''}<time>${escapeHtml(age(liveTime(r))||fmtTime(liveTime(r)))}</time></div></div>`;
  }).join(''):'<div class="empty">No matching live DNS activity.</div>';
}
async function loadLive(){
  if(livePaused)return;
  try{
    const payload=await json('/api/opnsense/unbound/queries');
    liveRows=Array.isArray(payload)?payload:(Array.isArray(payload.rows)?payload.rows:[]);
    renderLive();
    $('liveStatus').textContent='Live • 5s';$('liveStatus').className='pill ok';
  }catch(e){$('liveStatus').textContent='Live feed error';$('liveStatus').className='pill bad';$('liveActivity').innerHTML=`<div class="empty">${escapeHtml(e.message)}</div>`}
}
function toggleLive(){livePaused=!livePaused;$('pauseLiveBtn').textContent=livePaused?'Resume':'Pause';$('liveStatus').textContent=livePaused?'Paused':'Live • 5s';$('liveStatus').className=livePaused?'pill neutral':'pill ok';if(!livePaused)loadLive()}

async function saveDeviceName(id){
  const input=$('deviceNameInput');const button=$('saveDeviceNameBtn');const status=$('deviceNameStatus');
  const name=input.value.trim();if(!name){status.textContent='Enter a name.';return}
  button.disabled=true;status.textContent='Saving…';
  try{
    const d=await json(`/api/devices/${id}/name`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({name})});
    $('deviceTitle').textContent=d.name;status.textContent='Saved';await load();
  }catch(e){status.textContent=e.message}finally{button.disabled=false}
}
async function openDevice(id){
  const overlay=$('deviceOverlay');
  overlay.classList.remove('hidden');overlay.setAttribute('aria-hidden','false');document.body.classList.add('no-scroll');
  $('deviceTitle').textContent='Loading…';$('deviceIdentity').textContent='';$('deviceDetailBody').innerHTML='<div class="empty">Loading device history…</div>';
  try{
    const data=await json(`/api/devices/${id}/details`);const d=data.device;const s=data.summary;
    $('deviceTitle').textContent=d.name||d.lastIpAddress||'Unknown device';
    $('deviceIdentity').textContent=`${d.lastIpAddress||'No IP'} • ${d.macAddress||'No MAC'} • ${d.vendor||'Unknown vendor'}`;
    const domains=(data.adultDomains||[]).map(x=>`<span class="domain-chip">${escapeHtml(x)}</span>`).join('');
    $('deviceDetailBody').innerHTML=`
      <div class="rename-box">
        <label class="label" for="deviceNameInput">Friendly name</label>
        <div class="rename-row"><input id="deviceNameInput" class="text-input" maxlength="80" value="${escapeHtml(d.name||'')}" placeholder="Example: Bedroom iPhone"><button id="saveDeviceNameBtn" class="button small" type="button">Save name</button></div>
        <div id="deviceNameStatus" class="muted">This name is kept by HomeWatch and will be used in alerts.</div>
      </div>
      <div class="detail-stats">
        <div><span class="label">Adult signals</span><strong>${s.adultSignals||0}</strong></div>
        <div><span class="label">Adult alerts</span><strong>${s.adultAlerts||0}</strong></div>
        <div><span class="label">Unique domains</span><strong>${s.uniqueAdultDomains||0}</strong></div>
      </div>
      <div class="detail-meta"><span><b>First seen:</b> ${fmtTime(d.firstSeenUtc)}</span><span><b>Last seen:</b> ${fmtTime(d.lastSeenUtc)}</span><span><b>Last adult signal:</b> ${fmtTime(s.lastAdultSignalUtc)}</span></div>
      <div class="detail-section"><div class="label">Observed adult domains</div><div class="chips">${domains||'<span class="muted">None recorded.</span>'}</div></div>
      <div class="detail-section"><div class="label">Recent recorded events</div><div class="timeline">${data.events.length?data.events.slice(0,30).map(detailEventRow).join(''):'<div class="empty">No recorded events for this device.</div>'}</div></div>
      <div class="detail-section"><div class="label">Alert history</div><div class="timeline">${data.alerts.length?data.alerts.slice(0,20).map(detailAlertRow).join(''):'<div class="empty">No alerts for this device.</div>'}</div></div>`;
    $('saveDeviceNameBtn').addEventListener('click',()=>saveDeviceName(id));
    $('deviceNameInput').addEventListener('keydown',e=>{if(e.key==='Enter')saveDeviceName(id)});
  }catch(e){$('deviceTitle').textContent='Unable to load device';$('deviceDetailBody').innerHTML=`<div class="empty">${escapeHtml(e.message)}</div>`}
}
function closeDevice(){const o=$('deviceOverlay');o.classList.add('hidden');o.setAttribute('aria-hidden','true');document.body.classList.remove('no-scroll')}
async function load(){
  $('refreshBtn').disabled=true;
  try{
    const [status,monitor,devices,alerts,activity]=await Promise.all([
      json('/api/status'),json('/api/monitoring/adult/status'),json('/api/devices'),json('/api/alerts?limit=25'),json('/api/adult/activity?minutes=30')
    ]);
    deviceByIp=new Map(devices.filter(d=>d.lastIpAddress).map(d=>[d.lastIpAddress,d]));
    $('servicePill').textContent='Online';$('servicePill').className='pill ok';
    $('monitorState').textContent=monitor.enabled?'Active':'Disabled';
    $('monitorSub').textContent=monitor.lastError?`Error: ${monitor.lastError}`:`Last successful poll: ${fmtTime(monitor.lastSuccessfulPollUtc)}`;
    $('deviceCount').textContent=devices.length;
    $('adultHits').textContent=monitor.adultHitsDetected ?? 0;
    $('lastAlert').textContent=fmtTime(monitor.lastAlertUtc);
    $('activity').innerHTML=activity.length?activity.map(activityRow).join(''):'<div class="empty">No adult-domain activity in the last 30 minutes.</div>';
    $('alerts').innerHTML=alerts.length?alerts.map(alertRow).join(''):'<div class="empty">No adult alerts recorded yet.</div>';
    $('devices').innerHTML=devices.length?devices.map(deviceRow).join(''):'<div class="empty">No devices synchronized yet.</div>';
    renderLive();
  }catch(e){
    $('servicePill').textContent='Offline';$('servicePill').className='pill bad';
    $('monitorState').textContent='Unavailable';$('monitorSub').textContent=e.message;
  }finally{$('refreshBtn').disabled=false}
}
$('refreshBtn').addEventListener('click',()=>{load();loadLive()});
$('pauseLiveBtn').addEventListener('click',toggleLive);
$('liveFilter').addEventListener('input',renderLive);
$('liveActivity').addEventListener('click',e=>{const row=e.target.closest('[data-device-id]');if(row)openDevice(row.dataset.deviceId)});
$('devices').addEventListener('click',e=>{const row=e.target.closest('[data-device-id]');if(row)openDevice(row.dataset.deviceId)});
$('activity').addEventListener('click',e=>{const row=e.target.closest('[data-device-id]');if(row)openDevice(row.dataset.deviceId)});
$('closeDeviceBtn').addEventListener('click',closeDevice);
$('deviceOverlay').addEventListener('click',e=>{if(e.target===$('deviceOverlay'))closeDevice()});
document.addEventListener('keydown',e=>{if(e.key==='Escape')closeDevice()});
load();loadLive();setInterval(load,15000);setInterval(loadLive,5000);
