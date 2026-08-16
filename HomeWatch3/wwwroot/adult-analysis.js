const $=id=>document.getElementById(id);
const esc=value=>String(value??'').replace(/[&<>'"]/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[char]));

async function request(url,options={}){const response=await fetch(url,{cache:'no-store',...options});if(!response.ok){let message=`${response.status} ${response.statusText}`;try{message=(await response.json()).error||message}catch{}throw new Error(message)}return response.json()}
const bytes=value=>{const n=Math.max(0,Number(value||0));if(n>=1e9)return `${(n/1e9).toFixed(2)} GB`;if(n>=1e6)return `${(n/1e6).toFixed(1)} MB`;if(n>=1e3)return `${(n/1e3).toFixed(1)} KB`;return `${Math.round(n)} B`};
const duration=value=>{let seconds=Math.max(0,Math.round(Number(value||0)));const hours=Math.floor(seconds/3600);seconds-=hours*3600;const minutes=Math.floor(seconds/60);seconds-=minutes*60;return [hours?`${hours}h`:null,minutes?`${minutes}m`:null,`${seconds}s`].filter(Boolean).join(' ')};
const localDate=value=>{
  if(!value)return null;
  const raw=String(value).replace(/(?:Z|[+-]\d\d:?\d\d)$/,'');
  const date=new Date(raw);
  return Number.isNaN(date.getTime())?null:date;
};
const when=(localValue,utcValue)=>{
  const date=localDate(localValue)||localDate(utcValue);
  return date?date.toLocaleString():'—';
};
const roleClass=role=>role.startsWith('Media')?'role-media':role.startsWith('Advertising')?'role-ad':role.startsWith('Browsing')?'role-assets':role.includes('service')?'role-service':'role-support';

let analysis=null;
let settings=null;

function populateDevices(devices){const select=$('analysisDevice'),selected=select.value;select.innerHTML='<option value="">All monitored devices</option>'+devices.map(device=>`<option value="${device.id}">${esc(device.name||device.lastIpAddress||`Device ${device.id}`)}</option>`).join('');select.value=selected}

function renderSettings(){
  const domains=settings?.advertisingDomains||[];
  $('adDomains').innerHTML=domains.length?domains.map(domain=>`<span class="domain-chip">${esc(domain)}<button type="button" data-remove-ad="${esc(domain)}" aria-label="Remove ${esc(domain)}">×</button></span>`).join(''):'<span class="muted">No advertising domains configured.</span>';
  document.querySelectorAll('[data-remove-ad]').forEach(button=>button.addEventListener('click',()=>removeAdDomain(button.dataset.removeAd)));
}

function flowMatches(flow,session,needle){if(!needle)return true;return [flow.hostname,flow.role,flow.application,flow.protocol,flow.remoteIp,session.service,session.deviceName,session.ip].some(value=>String(value||'').toLowerCase().includes(needle))}

function renderFlow(flow){
  const elapsed=Math.max(0,(new Date(flow.lastSeenUtc)-new Date(flow.startedUtc))/1000);
  const started=localDate(flow.localStarted)||localDate(flow.startedUtc);
  return `<tr><td>${esc(started?started.toLocaleTimeString():'—')}<span class="flow-meta">${esc(duration(elapsed))}</span></td><td><span class="flow-role ${roleClass(flow.role)}">${esc(flow.role)}</span></td><td><span class="flow-host">${esc(flow.hostname||flow.remoteIp||'Unknown destination')}</span><span class="flow-meta">${esc(flow.remoteIp||'')} ${flow.remotePort?`:${flow.remotePort}`:''}</span></td><td>${esc(bytes(flow.bytesDown))}<span class="flow-meta">↑ ${esc(bytes(flow.bytesUp))}</span></td><td>${esc(flow.protocol||'—')}<span class="flow-meta">${flow.encrypted?'Encrypted':'Clear'} · ${Number(flow.confidence||0)}%</span></td></tr>`;
}

function renderSession(session,needle){
  const flows=(session.flows||[]).filter(flow=>flowMatches(flow,session,needle));
  if(needle&&!flows.length&&!flowMatches({},session,needle))return '';
  return `<article class="analysis-session"><div class="analysis-session-head"><div><span class="badge alert">Adult session</span><h3>${esc(session.service)}</h3><a class="device-link" href="/device.html?id=${session.deviceId}">${esc(session.deviceName||session.ip)}</a><div class="secondary">${esc(session.ip)} · ${esc(when(session.localStarted,session.startedUtc))} to ${esc(when(session.localLastSeen,session.lastSeenUtc))} · local time</div></div><div class="analysis-confidence"><strong>${Number(session.confidence||0)}%</strong><span>Attribution</span></div></div><div class="analysis-metrics"><div><span>Observed window</span><strong>${esc(duration(session.observedWindowSeconds))}</strong></div><div><span>Network-active</span><strong>${esc(duration(session.networkActiveSeconds))}</strong></div><div><span>Corrected download</span><strong>${esc(bytes(session.bytesDown))}</strong></div><div><span>Media delivery</span><strong>${esc(bytes(session.mediaDeliveryBytes))}</strong></div><div><span>Large transfers</span><strong>${Number(session.significantMediaTransfers||0)}</strong></div><div><span>Media phases</span><strong>${Number(session.mediaDeliveryPhases||0)}</strong></div></div><div class="analysis-assessment">${esc(session.caveat)} ${session.filteredAdvertisingFlows?`${session.filteredAdvertisingFlows} advertising flow${session.filteredAdvertisingFlows===1?' was':'s were'} filtered from these totals.`:''}</div><details class="flow-details" ${needle?'open':''}><summary>${flows.length} visible flow${flows.length===1?'':'s'} · ${Number(session.trafficBursts||0)} activity burst${session.trafficBursts===1?'':'s'}</summary><div class="flow-table-wrap"><table class="flow-table"><thead><tr><th>Local time</th><th>Role</th><th>Destination</th><th>Traffic</th><th>Evidence</th></tr></thead><tbody>${flows.length?flows.map(renderFlow).join(''):'<tr><td colspan="5">No flows match the current filter.</td></tr>'}</tbody></table></div></details></article>`;
}

function render(){
  if(!analysis)return;
  const summary=analysis.summary||{};
  $('analysisSessions').textContent=summary.sessionCount??0;
  $('analysisActive').textContent=duration(summary.totalNetworkActiveSeconds);
  $('analysisBytes').textContent=bytes(summary.totalBytesDown);
  $('analysisMedia').textContent=bytes(summary.mediaDeliveryBytes);
  $('analysisPhases').textContent=summary.mediaDeliveryPhases??0;
  $('analysisAds').textContent=summary.filteredAdvertisingFlows??0;
  populateDevices(analysis.devices||[]);
  const needle=$('analysisFilter').value.trim().toLowerCase();
  const sessions=(analysis.sessions||[]).map(session=>renderSession(session,needle)).filter(Boolean);
  $('analysisSessionsList').innerHTML=sessions.length?sessions.join(''):'<div class="empty">No matching adult sessions were detected in this window.</div>';
  const unassigned=analysis.unassignedSignals||[];
  $('unassignedSignals').innerHTML=unassigned.length?unassigned.map(signal=>`<div class="unassigned-item"><strong>${esc(signal.domain)}</strong><span>${signal.signals} signal${signal.signals===1?'':'s'} · ${esc(bytes(signal.bytesDown))} down · ${Number(signal.confidence||0)}% confidence</span><span>${esc(when(signal.localFirstSeen,signal.firstSeenUtc))} to ${esc(when(signal.localLastSeen,signal.lastSeenUtc))} · local time${signal.advertising?' · advertising filter':''}</span></div>`).join(''):'<div class="empty">No unassigned adult signals in this window.</div>';
}

async function loadSettings(){settings=await request('/api/adult-analysis/settings');$('includeAds').checked=!settings.hideAdvertisingByDefault;renderSettings()}

async function loadAnalysis(){
  const button=$('refreshAnalysis');button.disabled=true;$('analysisStatus').textContent='Loading…';$('analysisStatus').className='pill neutral';
  try{const params=new URLSearchParams({minutes:$('analysisWindow').value,includeAds:String($('includeAds').checked),timeZone:Intl.DateTimeFormat().resolvedOptions().timeZone||'America/Toronto'});if($('analysisDevice').value)params.set('deviceId',$('analysisDevice').value);analysis=await request('/api/adult-analysis?'+params.toString());$('analysisStatus').textContent=`${analysis.summary.sessionCount} session${analysis.summary.sessionCount===1?'':'s'}`;$('analysisStatus').className='pill ok';render()}catch(error){$('analysisStatus').textContent='Unavailable';$('analysisStatus').className='pill bad';$('analysisSessionsList').innerHTML=`<div class="empty">Unable to load analysis: ${esc(error.message)}</div>`}finally{button.disabled=false}
}

async function addAdDomain(event){event.preventDefault();const domain=$('adDomainInput').value.trim();if(!domain)return;try{const result=await request('/api/adult-analysis/advertising-domains',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({domain})});settings=result.settings;$('adDomainInput').value='';renderSettings();await loadAnalysis()}catch(error){alert(error.message)}}
async function removeAdDomain(domain){try{const result=await request('/api/adult-analysis/advertising-domains?domain='+encodeURIComponent(domain),{method:'DELETE'});settings=result.settings;renderSettings();await loadAnalysis()}catch(error){alert(error.message)}}
async function changeAdVisibility(){try{settings=await request('/api/adult-analysis/settings',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({hideAdvertisingByDefault:!$('includeAds').checked})})}catch(error){alert(`Could not save the advertising preference: ${error.message}`)}await loadAnalysis()}

$('analysisWindow').addEventListener('change',loadAnalysis);$('analysisDevice').addEventListener('change',loadAnalysis);$('includeAds').addEventListener('change',changeAdVisibility);$('analysisFilter').addEventListener('input',render);$('refreshAnalysis').addEventListener('click',loadAnalysis);$('adDomainForm').addEventListener('submit',addAdDomain);
(async()=>{try{await loadSettings()}catch(error){$('adDomains').innerHTML=`<span class="muted">Unable to load ad filters: ${esc(error.message)}</span>`}await loadAnalysis()})();
