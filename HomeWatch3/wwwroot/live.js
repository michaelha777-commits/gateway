const $=id=>document.getElementById(id);
let livePaused=false,liveRows=[],trafficRecords=[],management=[],activeSessionDeviceId=null;
const escapeHtml=v=>String(v??'').replace(/[&<>'\"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','\"':'&quot;'}[c]));
const pick=(o,...names)=>{for(const n of names){if(o&&o[n]!==undefined&&o[n]!==null&&String(o[n]).trim()!=='')return String(o[n])}return ''};
const age=v=>{if(!v)return '';const d=new Date(v);if(Number.isNaN(d.getTime()))return '';const ms=Date.now()-d.getTime();if(ms<60000)return 'just now';const m=Math.floor(ms/60000);if(m<60)return `${m} min ago`;return `${Math.floor(m/60)} hr ago`};
const fmtTime=v=>{if(!v)return 'Unknown';const d=new Date(v);return Number.isNaN(d.getTime())?String(v):d.toLocaleString()};
async function json(url,options){const r=await fetch(url,{cache:'no-store',...(options||{})});if(!r.ok){let msg=`${r.status} ${r.statusText}`;try{const b=await r.json();if(b.error)msg=b.error}catch{}throw new Error(msg)}return r.json()}
function liveTime(r){const raw=pick(r,'time','timestamp','created','date');if(!raw)return '';if(/^\d+$/.test(raw)){const n=Number(raw);return new Date(raw.length<=10?n*1000:n).toISOString()}return raw}
async function setIgnored(id,ignored){try{await json(`/api/devices/${id}/ignored`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({ignored})});if(activeSessionDeviceId===Number(id)&&ignored)closeLiveSession();await loadDevices()}catch(e){alert(`Unable to update device: ${e.message}`)}}
function trafficByIp(){return new Map(trafficRecords.map(r=>[String(r.address||''),r]))}
function trafficSummary(t){if(!t)return '';const remotes=(t.details||[]).slice(0,3).map(x=>x.address).filter(Boolean);const rate=t.rate||'';const inRate=t.rate_in||'';const outRate=t.rate_out||'';const bits=[];if(rate)bits.push(`Traffic ${rate}`);if(inRate||outRate)bits.push(`↓ ${inRate||'0'}  ↑ ${outRate||'0'}`);if(remotes.length)bits.push(`Remote ${remotes.join(', ')}`);return bits.join(' • ')}
function currentMaps(){
  const devices=management.map(x=>x.device);
  return {devices,byIp:new Map(devices.filter(d=>d.lastIpAddress).map(d=>[d.lastIpAddress,d])),traffic:trafficByIp(),ignoredIds:new Set(management.filter(x=>x.ignored).map(x=>Number(x.device.id)))};
}
function render(){
  const {byIp,traffic,ignoredIds}=currentMaps();
  const q=($('liveFilter').value||'').trim().toLowerCase();
  const rows=liveRows.filter(r=>{const ip=pick(r,'client','client_ip','source','src','ip');const device=byIp.get(ip);if(device&&ignoredIds.has(Number(device.id)))return false;const domain=pick(r,'domain','name','qname','query');const t=traffic.get(ip);const remotes=(t?.details||[]).map(x=>x.address).join(' ');const hay=`${device?.name||''} ${ip} ${domain} ${remotes}`.toLowerCase();return !q||hay.includes(q)}).slice(0,150);
  $('liveActivity').innerHTML=rows.length?rows.map(r=>{
    const ip=pick(r,'client','client_ip','source','src','ip')||'Unknown IP';
    const domain=pick(r,'domain','name','qname','query')||'Unknown domain';
    const type=pick(r,'type','qtype','query_type')||'DNS';
    const action=pick(r,'action')||'Pass';
    const source=pick(r,'source')||'';
    const policy=pick(r,'policy')||'';
    const device=byIp.get(ip);const name=device?.name||ip;const t=traffic.get(ip);
    const ignoreButton=device?`<button class="button small danger-button" type="button" data-ignore-id="${device.id}">Ignore device</button>`:'';
    const sessionButton=device?`<button class="button small secondary-button" type="button" data-session-device-id="${device.id}">Live session</button>`:'';
    const statusClass=action.toLowerCase()==='block'?'badge alert':'badge';
    const extra=trafficSummary(t);
    return `<div class="live-row ${device?'clickable':''}" ${device?`data-session-device-id="${device.id}"`:''}><div class="live-dot"></div><div class="live-main"><div class="primary">${escapeHtml(domain)}</div><div class="secondary">${escapeHtml(name)} • ${escapeHtml(ip)}</div>${extra?`<div class="secondary wrap-text">${escapeHtml(extra)}</div>`:''}${policy?`<div class="secondary wrap-text">Policy: ${escapeHtml(policy)}</div>`:''}</div><div class="live-meta"><span class="${statusClass}">${escapeHtml(action)}</span><span class="badge">${escapeHtml(type)}</span>${source?`<span class="secondary">${escapeHtml(source)}</span>`:''}<time>${escapeHtml(age(liveTime(r)))}</time>${sessionButton}${ignoreButton}</div></div>`
  }).join(''):'<div class="empty">No matching live activity from monitored devices.</div>';
  const ignoredDevices=management.filter(x=>x.ignored).map(x=>x.device);$('ignoredSummary').innerHTML=ignoredDevices.length?ignoredDevices.map(d=>`<span class="domain-chip neutral-chip">${escapeHtml(d.name||d.lastIpAddress||'Unknown device')} <button type="button" class="chip-action" data-unignore-id="${d.id}">×</button></span>`).join(''):'<span class="muted">No ignored devices.</span>';
  if(activeSessionDeviceId)renderLiveSession(activeSessionDeviceId);
}
function renderLiveSession(id){
  const {devices,traffic}=currentMaps();
  const device=devices.find(d=>Number(d.id)===Number(id));
  if(!device)return;
  const ip=device.lastIpAddress||'';
  const t=traffic.get(ip);
  const rows=liveRows.filter(r=>pick(r,'client','client_ip','source','src','ip')===ip).sort((a,b)=>new Date(liveTime(b)||0)-new Date(liveTime(a)||0));
  const recentDomains=[];const seen=new Set();
  for(const r of rows){const domain=pick(r,'domain','name','qname','query');if(!domain)continue;const key=domain.toLowerCase();if(seen.has(key))continue;seen.add(key);recentDomains.push(r);if(recentDomains.length>=30)break;}
  const blocked=rows.filter(r=>pick(r,'action').toLowerCase()==='block').slice(0,30);
  const policies=[...new Set(blocked.map(r=>pick(r,'policy')).filter(Boolean))];
  const remotes=(t?.details||[]);
  $('liveSessionTitle').textContent=device.name||ip||'Unknown device';
  $('liveSessionIdentity').textContent=`${ip||'No IP'} • ${device.macAddress||'No MAC'} • ${device.vendor||'Unknown vendor'}`;
  const remoteHtml=remotes.length?remotes.map(r=>`<div class="session-remote-row"><div><strong>${escapeHtml(r.address||'Unknown')}</strong><div class="secondary">Current remote destination</div></div><div class="session-rate"><span>${escapeHtml(r.rate||'0')}</span><small>${escapeHtml(r.cumulative||'0')}</small></div></div>`).join(''):'<div class="empty">No active remote IPs reported by OPNsense traffic/top right now.</div>';
  const domainHtml=recentDomains.length?recentDomains.map(r=>`<div class="timeline-item"><div><strong>${escapeHtml(pick(r,'domain','name','qname','query')||'Unknown')}</strong><div class="secondary">${escapeHtml(pick(r,'type','qtype')||'DNS')} • ${escapeHtml(pick(r,'action')||'Pass')} • ${escapeHtml(pick(r,'source')||'Unknown source')}</div>${pick(r,'policy')?`<div class="secondary">Policy: ${escapeHtml(pick(r,'policy'))}</div>`:''}</div><time>${escapeHtml(age(liveTime(r))||fmtTime(liveTime(r)))}</time></div>`).join(''):'<div class="empty">No recent DNS activity for this device.</div>';
  const blockedHtml=blocked.length?blocked.map(r=>`<div class="timeline-item blocked-item"><div><strong>${escapeHtml(pick(r,'domain','name','qname','query')||'Unknown')}</strong><div class="secondary">Blocked by OPNsense${pick(r,'policy')?` • ${escapeHtml(pick(r,'policy'))}`:''}</div></div><time>${escapeHtml(age(liveTime(r))||fmtTime(liveTime(r)))}</time></div>`).join(''):'<div class="empty">No blocked DNS requests in the current Unbound window.</div>';
  $('liveSessionBody').innerHTML=`
    <div class="detail-stats session-stats">
      <div><span class="label">Current traffic</span><strong>${escapeHtml(t?.rate||'0')}</strong></div>
      <div><span class="label">Download</span><strong>${escapeHtml(t?.rate_in||'0')}</strong></div>
      <div><span class="label">Upload</span><strong>${escapeHtml(t?.rate_out||'0')}</strong></div>
    </div>
    <div class="detail-meta"><span><b>Cumulative:</b> ${escapeHtml(t?.cumulative||'0')}</span><span><b>Remote IPs:</b> ${remotes.length}</span><span><b>Recent unique domains:</b> ${recentDomains.length}</span><span><b>Blocked requests:</b> ${blocked.length}</span></div>
    <div class="detail-section"><div class="section-head"><div><div class="label">OPNsense traffic/top</div><h2>Current remote IPs</h2></div></div><div class="session-remotes">${remoteHtml}</div></div>
    <div class="detail-section"><div class="label">OPNsense policies seen</div><div class="chips">${policies.length?policies.map(p=>`<span class="domain-chip neutral-chip">${escapeHtml(p)}</span>`).join(''):'<span class="muted">No blocking policies in the current DNS window.</span>'}</div></div>
    <div class="detail-section"><div class="section-head"><div><div class="label">Recent DNS</div><h2>Recent domains</h2></div></div><div class="timeline">${domainHtml}</div></div>
    <div class="detail-section"><div class="section-head"><div><div class="label">Blocked</div><h2>Blocked requests</h2></div></div><div class="timeline">${blockedHtml}</div></div>
    <div class="detail-section"><button class="button danger-button" type="button" data-ignore-id="${device.id}">Ignore this device</button></div>`;
}
function openLiveSession(id){activeSessionDeviceId=Number(id);$('liveSessionOverlay').classList.remove('hidden');$('liveSessionOverlay').setAttribute('aria-hidden','false');document.body.classList.add('no-scroll');renderLiveSession(id)}
function closeLiveSession(){activeSessionDeviceId=null;$('liveSessionOverlay').classList.add('hidden');$('liveSessionOverlay').setAttribute('aria-hidden','true');document.body.classList.remove('no-scroll')}
async function loadDevices(){management=await json('/api/devices/management');render()}
async function loadLive(){if(livePaused)return;try{const [dns,traffic]=await Promise.all([json('/api/opnsense/unbound/queries'),json('/api/opnsense/traffic/top?interfaces=lan')]);liveRows=Array.isArray(dns)?dns:(Array.isArray(dns.rows)?dns.rows:[]);trafficRecords=Array.isArray(traffic?.lan?.records)?traffic.lan.records:[];$('liveStatus').textContent='Live • DNS + traffic';$('liveStatus').className='pill ok';render()}catch(e){$('liveStatus').textContent='Feed error';$('liveStatus').className='pill bad';$('liveActivity').innerHTML=`<div class="empty">${escapeHtml(e.message)}</div>`}}
$('pauseLiveBtn').addEventListener('click',()=>{livePaused=!livePaused;$('pauseLiveBtn').textContent=livePaused?'Resume':'Pause';$('liveStatus').textContent=livePaused?'Paused':'Live • DNS + traffic';$('liveStatus').className=livePaused?'pill neutral':'pill ok';if(!livePaused)loadLive()});
$('liveFilter').addEventListener('input',render);
$('closeLiveSessionBtn').addEventListener('click',closeLiveSession);
$('liveSessionOverlay').addEventListener('click',e=>{if(e.target===$('liveSessionOverlay'))closeLiveSession()});
document.addEventListener('keydown',e=>{if(e.key==='Escape'&&activeSessionDeviceId)closeLiveSession()});
document.addEventListener('click',e=>{const ignore=e.target.closest('[data-ignore-id]');if(ignore){e.stopPropagation();setIgnored(ignore.dataset.ignoreId,true);return}const unignore=e.target.closest('[data-unignore-id]');if(unignore){e.stopPropagation();setIgnored(unignore.dataset.unignoreId,false);return}const session=e.target.closest('[data-session-device-id]');if(session){openLiveSession(session.dataset.sessionDeviceId)}});
loadDevices();loadLive();setInterval(loadDevices,15000);setInterval(loadLive,5000);
