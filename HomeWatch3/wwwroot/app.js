const $=id=>document.getElementById(id);
const fmtTime=v=>{if(!v)return 'None';const d=new Date(v);return Number.isNaN(d.getTime())?v:d.toLocaleString()};
async function json(url){const r=await fetch(url,{cache:'no-store'});if(!r.ok)throw new Error(`${r.status} ${r.statusText}`);return r.json()}
function alertRow(a){return `<div class="row"><div><div class="primary">${escapeHtml(a.title||'Alert')}</div><div class="secondary">${escapeHtml(a.message||'')}</div></div><div class="middle"><div class="secondary">${fmtTime(a.createdUtc)}</div></div><span class="badge alert">${escapeHtml(a.severity||'high')}</span></div>`}
function deviceRow(d){return `<div class="row"><div><div class="primary">${escapeHtml(d.name||d.lastIpAddress||'Unknown device')}</div><div class="secondary">${escapeHtml(d.vendor||'Unknown vendor')}</div></div><div class="middle"><div class="secondary">${escapeHtml(d.lastIpAddress||'No IP')} • ${escapeHtml(d.macAddress||'No MAC')}</div></div><span class="badge">${fmtTime(d.lastSeenUtc)}</span></div>`}
function escapeHtml(v){return String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
async function load(){
  $('refreshBtn').disabled=true;
  try{
    const [status,monitor,devices,alerts]=await Promise.all([
      json('/api/status'),json('/api/monitoring/adult/status'),json('/api/devices'),json('/api/alerts?limit=25')
    ]);
    $('servicePill').textContent='Online';$('servicePill').className='pill ok';
    $('monitorState').textContent=monitor.enabled?'Active':'Disabled';
    $('monitorSub').textContent=monitor.lastError?`Error: ${monitor.lastError}`:`Last successful poll: ${fmtTime(monitor.lastSuccessfulPollUtc)}`;
    $('deviceCount').textContent=devices.length;
    $('adultHits').textContent=monitor.adultHitsDetected ?? 0;
    $('lastAlert').textContent=fmtTime(monitor.lastAlertUtc);
    $('alerts').innerHTML=alerts.length?alerts.map(alertRow).join(''):'<div class="empty">No adult alerts recorded yet.</div>';
    $('devices').innerHTML=devices.length?devices.map(deviceRow).join(''):'<div class="empty">No devices synchronized yet.</div>';
  }catch(e){
    $('servicePill').textContent='Offline';$('servicePill').className='pill bad';
    $('monitorState').textContent='Unavailable';$('monitorSub').textContent=e.message;
  }finally{$('refreshBtn').disabled=false}
}
$('refreshBtn').addEventListener('click',load);
load();setInterval(load,15000);
