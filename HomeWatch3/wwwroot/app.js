const $=id=>document.getElementById(id);
const fmtTime=v=>{if(!v)return 'None';const d=new Date(v);return Number.isNaN(d.getTime())?v:d.toLocaleString()};
const age=v=>{if(!v)return '';const ms=Date.now()-new Date(v).getTime();if(ms<60000)return 'just now';const m=Math.floor(ms/60000);if(m<60)return `${m} min ago`;const h=Math.floor(m/60);return `${h} hr ago`};
async function json(url){const r=await fetch(url,{cache:'no-store'});if(!r.ok)throw new Error(`${r.status} ${r.statusText}`);return r.json()}
function activityRow(a){const domains=(a.domains||[]).map(escapeHtml).join(', ');return `<div class="row activity-row"><div><div class="primary">${escapeHtml(a.device||a.ip||'Unknown device')}</div><div class="secondary">${escapeHtml(a.ip||'No IP')} • ${escapeHtml(a.domain||'Unknown domain')}</div></div><div class="middle"><div class="secondary">${escapeHtml(domains||'No domains')}</div><div class="secondary">${a.hits||0} signal${a.hits===1?'':'s'} • ${age(a.lastSeenUtc)}</div></div><span class="badge alert">${a.confidence||0}%</span></div>`}
function alertRow(a){return `<div class="row"><div><div class="primary">${escapeHtml(a.title||'Alert')}</div><div class="secondary">${escapeHtml(a.message||'')}</div></div><div class="middle"><div class="secondary">${fmtTime(a.createdUtc)}</div></div><span class="badge alert">${escapeHtml(a.severity||'high')}</span></div>`}
function deviceRow(d){return `<div class="row"><div><div class="primary">${escapeHtml(d.name||d.lastIpAddress||'Unknown device')}</div><div class="secondary">${escapeHtml(d.vendor||'Unknown vendor')}</div></div><div class="middle"><div class="secondary">${escapeHtml(d.lastIpAddress||'No IP')} • ${escapeHtml(d.macAddress||'No MAC')}</div></div><span class="badge">${fmtTime(d.lastSeenUtc)}</span></div>`}
function escapeHtml(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
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
load();setInterval(load,15000);
