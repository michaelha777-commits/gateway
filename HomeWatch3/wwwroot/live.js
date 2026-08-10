const $=id=>document.getElementById(id);
let livePaused=false,liveRows=[],trafficRecords=[],management=[];
const escapeHtml=v=>String(v??'').replace(/[&<>'\"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','\"':'&quot;'}[c]));
const pick=(o,...names)=>{for(const n of names){if(o&&o[n]!==undefined&&o[n]!==null&&String(o[n]).trim()!=='')return String(o[n])}return ''};
const age=v=>{if(!v)return '';const d=new Date(v);if(Number.isNaN(d.getTime()))return '';const ms=Date.now()-d.getTime();if(ms<60000)return 'just now';const m=Math.floor(ms/60000);if(m<60)return `${m} min ago`;return `${Math.floor(m/60)} hr ago`};
async function json(url,options){const r=await fetch(url,{cache:'no-store',...(options||{})});if(!r.ok){let msg=`${r.status} ${r.statusText}`;try{const b=await r.json();if(b.error)msg=b.error}catch{}throw new Error(msg)}return r.json()}
function liveTime(r){const raw=pick(r,'time','timestamp','created','date');if(!raw)return '';if(/^\d+$/.test(raw)){const n=Number(raw);return new Date(raw.length<=10?n*1000:n).toISOString()}return raw}
async function setIgnored(id,ignored){try{await json(`/api/devices/${id}/ignored`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({ignored})});await loadDevices()}catch(e){alert(`Unable to update device: ${e.message}`)}}
function trafficByIp(){return new Map(trafficRecords.map(r=>[String(r.address||''),r]))}
function trafficSummary(t){if(!t)return '';const remotes=(t.details||[]).slice(0,3).map(x=>x.address).filter(Boolean);const rate=t.rate||'';const inRate=t.rate_in||'';const outRate=t.rate_out||'';const bits=[];if(rate)bits.push(`Traffic ${rate}`);if(inRate||outRate)bits.push(`↓ ${inRate||'0'}  ↑ ${outRate||'0'}`);if(remotes.length)bits.push(`Remote ${remotes.join(', ')}`);return bits.join(' • ')}
function render(){
  const ignoredIds=new Set(management.filter(x=>x.ignored).map(x=>Number(x.device.id)));
  const devices=management.map(x=>x.device),byIp=new Map(devices.filter(d=>d.lastIpAddress).map(d=>[d.lastIpAddress,d])),traffic=trafficByIp();
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
    const statusClass=action.toLowerCase()==='block'?'badge alert':'badge';
    const extra=trafficSummary(t);
    return `<div class="live-row"><div class="live-dot"></div><div class="live-main"><div class="primary">${escapeHtml(domain)}</div><div class="secondary">${escapeHtml(name)} • ${escapeHtml(ip)}</div>${extra?`<div class="secondary wrap-text">${escapeHtml(extra)}</div>`:''}${policy?`<div class="secondary wrap-text">Policy: ${escapeHtml(policy)}</div>`:''}</div><div class="live-meta"><span class="${statusClass}">${escapeHtml(action)}</span><span class="badge">${escapeHtml(type)}</span>${source?`<span class="secondary">${escapeHtml(source)}</span>`:''}<time>${escapeHtml(age(liveTime(r)))}</time>${ignoreButton}</div></div>`
  }).join(''):'<div class="empty">No matching live activity from monitored devices.</div>';
  const ignoredDevices=management.filter(x=>x.ignored).map(x=>x.device);$('ignoredSummary').innerHTML=ignoredDevices.length?ignoredDevices.map(d=>`<span class="domain-chip neutral-chip">${escapeHtml(d.name||d.lastIpAddress||'Unknown device')} <button type="button" class="chip-action" data-unignore-id="${d.id}">×</button></span>`).join(''):'<span class="muted">No ignored devices.</span>';
}
async function loadDevices(){management=await json('/api/devices/management');render()}
async function loadLive(){if(livePaused)return;try{const [dns,traffic]=await Promise.all([json('/api/opnsense/unbound/queries'),json('/api/opnsense/traffic/top?interfaces=lan')]);liveRows=Array.isArray(dns)?dns:(Array.isArray(dns.rows)?dns.rows:[]);trafficRecords=Array.isArray(traffic?.lan?.records)?traffic.lan.records:[];$('liveStatus').textContent='Live • DNS + traffic';$('liveStatus').className='pill ok';render()}catch(e){$('liveStatus').textContent='Feed error';$('liveStatus').className='pill bad';$('liveActivity').innerHTML=`<div class="empty">${escapeHtml(e.message)}</div>`}}
$('pauseLiveBtn').addEventListener('click',()=>{livePaused=!livePaused;$('pauseLiveBtn').textContent=livePaused?'Resume':'Pause';$('liveStatus').textContent=livePaused?'Paused':'Live • DNS + traffic';$('liveStatus').className=livePaused?'pill neutral':'pill ok';if(!livePaused)loadLive()});
$('liveFilter').addEventListener('input',render);
document.addEventListener('click',e=>{const ignore=e.target.closest('[data-ignore-id]');if(ignore){setIgnored(ignore.dataset.ignoreId,true);return}const unignore=e.target.closest('[data-unignore-id]');if(unignore){setIgnored(unignore.dataset.unignoreId,false)}});
loadDevices();loadLive();setInterval(loadDevices,15000);setInterval(loadLive,5000);
