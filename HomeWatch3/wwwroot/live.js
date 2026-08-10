const $=id=>document.getElementById(id);
const IGNORE_KEY='homewatch3.ignoredDeviceIds';
let livePaused=false,liveRows=[],devices=[];
const ignoredIds=()=>{try{return new Set(JSON.parse(localStorage.getItem(IGNORE_KEY)||'[]').map(Number))}catch{return new Set()}};
const escapeHtml=v=>String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
const pick=(o,...names)=>{for(const n of names){if(o&&o[n]!==undefined&&o[n]!==null&&String(o[n]).trim()!=='')return String(o[n])}return ''};
const age=v=>{if(!v)return '';const d=new Date(v);if(Number.isNaN(d.getTime()))return '';const ms=Date.now()-d.getTime();if(ms<60000)return 'just now';const m=Math.floor(ms/60000);if(m<60)return `${m} min ago`;return `${Math.floor(m/60)} hr ago`};
async function json(url){const r=await fetch(url,{cache:'no-store'});if(!r.ok)throw new Error(`${r.status} ${r.statusText}`);return r.json()}
function liveTime(r){const raw=pick(r,'time','timestamp','created','date');if(!raw)return '';if(/^\d+$/.test(raw)){const n=Number(raw);return new Date(raw.length<=10?n*1000:n).toISOString()}return raw}
function render(){
  const ignored=ignoredIds();const byIp=new Map(devices.filter(d=>d.lastIpAddress).map(d=>[d.lastIpAddress,d]));const q=($('liveFilter').value||'').trim().toLowerCase();
  const rows=liveRows.filter(r=>{const ip=pick(r,'client','client_ip','source','src','ip');const device=byIp.get(ip);if(device&&ignored.has(Number(device.id)))return false;const domain=pick(r,'domain','name','qname','query');const hay=`${device?.name||''} ${ip} ${domain}`.toLowerCase();return !q||hay.includes(q)}).slice(0,150);
  $('liveActivity').innerHTML=rows.length?rows.map(r=>{const ip=pick(r,'client','client_ip','source','src','ip')||'Unknown IP';const domain=pick(r,'domain','name','qname','query')||'Unknown domain';const type=pick(r,'type','qtype','query_type')||'DNS';const result=pick(r,'return_code','rcode','status','answer')||'';const device=byIp.get(ip);const name=device?.name||ip;return `<div class="live-row"><div class="live-dot"></div><div class="live-main"><div class="primary">${escapeHtml(domain)}</div><div class="secondary">${escapeHtml(name)} • ${escapeHtml(ip)}</div></div><div class="live-meta"><span class="badge">${escapeHtml(type)}</span>${result?`<span class="secondary">${escapeHtml(result)}</span>`:''}<time>${escapeHtml(age(liveTime(r)))}</time></div></div>`}).join(''):'<div class="empty">No matching live activity from monitored devices.</div>';
  const ignoredDevices=devices.filter(d=>ignored.has(Number(d.id)));$('ignoredSummary').innerHTML=ignoredDevices.length?ignoredDevices.map(d=>`<span class="domain-chip neutral-chip">${escapeHtml(d.name||d.lastIpAddress||'Unknown device')}</span>`).join(''):'<span class="muted">No ignored devices.</span>';
}
async function loadDevices(){devices=await json('/api/devices');render()}
async function loadLive(){if(livePaused)return;try{const payload=await json('/api/opnsense/unbound/queries');liveRows=Array.isArray(payload)?payload:(Array.isArray(payload.rows)?payload.rows:[]);$('liveStatus').textContent='Live • 5s';$('liveStatus').className='pill ok';render()}catch(e){$('liveStatus').textContent='Feed error';$('liveStatus').className='pill bad';$('liveActivity').innerHTML=`<div class="empty">${escapeHtml(e.message)}</div>`}}
$('pauseLiveBtn').addEventListener('click',()=>{livePaused=!livePaused;$('pauseLiveBtn').textContent=livePaused?'Resume':'Pause';$('liveStatus').textContent=livePaused?'Paused':'Live • 5s';$('liveStatus').className=livePaused?'pill neutral':'pill ok';if(!livePaused)loadLive()});
$('liveFilter').addEventListener('input',render);
loadDevices();loadLive();setInterval(loadDevices,15000);setInterval(loadLive,5000);
