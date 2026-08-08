const $=id=>document.getElementById(id);
const fmtTime=v=>{if(!v)return 'None';const d=new Date(v);return Number.isNaN(d.getTime())?v:d.toLocaleString()};
const age=v=>{if(!v)return '';const ms=Date.now()-new Date(v).getTime();if(ms<60000)return 'just now';const m=Math.floor(ms/60000);if(m<60)return `${m} min ago`;const h=Math.floor(m/60);return `${h} hr ago`};
async function json(url){const r=await fetch(url,{cache:'no-store'});if(!r.ok)throw new Error(`${r.status} ${r.statusText}`);return r.json()}
function activityRow(a){const domains=(a.domains||[]).map(escapeHtml).join(', ');const cls=a.deviceId?'row activity-row clickable':'row activity-row';const attr=a.deviceId?` data-device-id="${a.deviceId}"`:'';return `<div class="${cls}"${attr}><div><div class="primary">${escapeHtml(a.device||a.ip||'Unknown device')}</div><div class="secondary">${escapeHtml(a.ip||'No IP')} • ${escapeHtml(a.domain||'Unknown domain')}</div></div><div class="middle"><div class="secondary">${escapeHtml(domains||'No domains')}</div><div class="secondary">${a.hits||0} signal${a.hits===1?'':'s'} • ${age(a.lastSeenUtc)}</div></div><span class="badge alert">${a.confidence||0}%</span></div>`}
function alertRow(a){return `<div class="row"><div><div class="primary">${escapeHtml(a.title||'Alert')}</div><div class="secondary wrap-text">${escapeHtml(a.message||'')}</div></div><div class="middle"><div class="secondary">${fmtTime(a.createdUtc)}</div></div><span class="badge alert">${escapeHtml(a.severity||'high')}</span></div>`}
function deviceRow(d){return `<button class="row device-row clickable" data-device-id="${d.id}" type="button"><div><div class="primary">${escapeHtml(d.name||d.lastIpAddress||'Unknown device')}</div><div class="secondary">${escapeHtml(d.vendor||'Unknown vendor')}</div></div><div class="middle"><div class="secondary">${escapeHtml(d.lastIpAddress||'No IP')} • ${escapeHtml(d.macAddress||'No MAC')}</div></div><span class="badge">Details</span></button>`}
function escapeHtml(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function detailEventRow(e){return `<div class="timeline-item"><div><strong>${escapeHtml(e.domain||e.application||e.category||'Network event')}</strong><div class="secondary">${escapeHtml(e.source||'unknown source')} • ${escapeHtml(e.protocol||'unknown protocol')} • ${e.confidence||0}% confidence</div></div><time>${fmtTime(e.timestampUtc)}</time></div>`}
function detailAlertRow(a){return `<div class="timeline-item"><div><strong>${escapeHtml(a.title||'Alert')}</strong><div class="secondary wrap-text">${escapeHtml(a.message||'')}</div></div><time>${fmtTime(a.createdUtc)}</time></div>`}
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
      <div class="detail-stats">
        <div><span class="label">Adult signals</span><strong>${s.adultSignals||0}</strong></div>
        <div><span class="label">Adult alerts</span><strong>${s.adultAlerts||0}</strong></div>
        <div><span class="label">Unique domains</span><strong>${s.uniqueAdultDomains||0}</strong></div>
      </div>
      <div class="detail-meta"><span><b>First seen:</b> ${fmtTime(d.firstSeenUtc)}</span><span><b>Last seen:</b> ${fmtTime(d.lastSeenUtc)}</span><span><b>Last adult signal:</b> ${fmtTime(s.lastAdultSignalUtc)}</span></div>
      <div class="detail-section"><div class="label">Observed adult domains</div><div class="chips">${domains||'<span class="muted">None recorded.</span>'}</div></div>
      <div class="detail-section"><div class="label">Recent recorded events</div><div class="timeline">${data.events.length?data.events.slice(0,30).map(detailEventRow).join(''):'<div class="empty">No recorded events for this device.</div>'}</div></div>
      <div class="detail-section"><div class="label">Alert history</div><div class="timeline">${data.alerts.length?data.alerts.slice(0,20).map(detailAlertRow).join(''):'<div class="empty">No alerts for this device.</div>'}</div></div>`;
  }catch(e){$('deviceTitle').textContent='Unable to load device';$('deviceDetailBody').innerHTML=`<div class="empty">${escapeHtml(e.message)}</div>`}
}
function closeDevice(){const o=$('deviceOverlay');o.classList.add('hidden');o.setAttribute('aria-hidden','true');document.body.classList.remove('no-scroll')}
async function load(){
  $('refreshBtn').disabled=true;
  try{
    const [status,monitor,devices,alerts,activity]=await Promise.all([
      json('/api/status'),json('/api/monitoring/adult/status'),json('/api/devices'),json('/api/alerts?limit=25'),json('/api/adult/activity?minutes=30')
    ]);
    $('servicePill').textContent='Online';$('servicePill').className='pill ok';
    $('monitorState').textContent=monitor.enabled?'Active':'Disabled';
    $('monitorSub').textContent=monitor.lastError?`Error: ${monitor.lastError}`:`Last successful poll: ${fmtTime(monitor.lastSuccessfulPollUtc)}`;
    $('deviceCount').textContent=devices.length;
    $('adultHits').textContent=monitor.adultHitsDetected ?? 0;
    $('lastAlert').textContent=fmtTime(monitor.lastAlertUtc);
    $('activity').innerHTML=activity.length?activity.map(activityRow).join(''):'<div class="empty">No adult-domain activity in the last 30 minutes.</div>';
    $('alerts').innerHTML=alerts.length?alerts.map(alertRow).join(''):'<div class="empty">No adult alerts recorded yet.</div>';
    $('devices').innerHTML=devices.length?devices.map(deviceRow).join(''):'<div class="empty">No devices synchronized yet.</div>';
  }catch(e){
    $('servicePill').textContent='Offline';$('servicePill').className='pill bad';
    $('monitorState').textContent='Unavailable';$('monitorSub').textContent=e.message;
  }finally{$('refreshBtn').disabled=false}
}
$('refreshBtn').addEventListener('click',load);
$('devices').addEventListener('click',e=>{const row=e.target.closest('[data-device-id]');if(row)openDevice(row.dataset.deviceId)});
$('activity').addEventListener('click',e=>{const row=e.target.closest('[data-device-id]');if(row)openDevice(row.dataset.deviceId)});
$('closeDeviceBtn').addEventListener('click',closeDevice);
$('deviceOverlay').addEventListener('click',e=>{if(e.target===$('deviceOverlay'))closeDevice()});
document.addEventListener('keydown',e=>{if(e.key==='Escape')closeDevice()});
load();setInterval(load,15000);
