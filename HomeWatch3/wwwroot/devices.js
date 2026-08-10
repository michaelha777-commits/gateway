const $=id=>document.getElementById(id);
let management=[],trafficRecords=[];
const escapeHtml=v=>String(v??'').replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
async function json(url,options){const r=await fetch(url,{cache:'no-store',...(options||{})});if(!r.ok){let msg=`${r.status} ${r.statusText}`;try{const b=await r.json();if(b.error)msg=b.error}catch{}throw new Error(msg)}return r.json()}
function trafficByIp(){return new Map(trafficRecords.map(r=>[String(r.address||''),r]))}
function bits(v){const n=Number(v);return Number.isFinite(n)?n:0}
function active(t){return !!t && (bits(t.rate_bits)>0 || bits(t.rate_bits_in)>0 || bits(t.rate_bits_out)>0)}
function stamp(t){const raw=t?.timestamp||t?.time||'';if(!raw)return '';const d=new Date(raw);return Number.isNaN(d.getTime())?'':d.toLocaleTimeString([], {hour:'numeric',minute:'2-digit',second:'2-digit'})}
function rateText(t,key,label){const value=t?.[key]||'0 b';return `${label} ${value}`}
function row(item){
  const d=item.device,t=trafficByIp().get(d.lastIpAddress||'');
  const down=t?.rate_in||'0 b',up=t?.rate_out||'0 b',total=t?.rate||'0 b';
  const cin=t?.cumulative_in||t?.cumulative||'0 b',cout=t?.cumulative_out||'0 b';
  const remoteCount=Array.isArray(t?.details)?t.details.length:0;
  const state=active(t)?'<span class="badge live-badge">ACTIVE</span>':'<span class="badge">IDLE</span>';
  return `<div class="row device-manage-row"><div class="device-main"><div><a class="device-link primary" href="/device.html?id=${d.id}">${escapeHtml(d.name||d.lastIpAddress||'Unknown device')}</a><div class="secondary">${escapeHtml(d.vendor||'Unknown vendor')} • ${escapeHtml(d.lastIpAddress||'No IP')}</div><div class="bandwidth-line">${state}<span><b>↓ ${escapeHtml(down)}</b></span><span><b>↑ ${escapeHtml(up)}</b></span><span>Total ${escapeHtml(total)}</span></div></div><div class="middle"><div class="secondary">MAC ${escapeHtml(d.macAddress||'Unknown')}</div><div class="secondary">Window ↓ ${escapeHtml(cin)} • ↑ ${escapeHtml(cout)} • ${remoteCount} remote${remoteCount===1?'':'s'}${stamp(t)?` • ${escapeHtml(stamp(t))}`:''}</div></div></div><button class="button small ${item.ignored?'secondary-button':'danger-button'}" data-id="${d.id}" data-action="${item.ignored?'remove':'ignore'}" type="button">${item.ignored?'Remove':'Ignore'}</button></div>`
}
function renderTopTalkers(){
  const byIp=trafficByIp();
  const monitored=management.filter(x=>!x.ignored).map(x=>x.device);
  const ranked=monitored.map(d=>({d,t:byIp.get(d.lastIpAddress||'')})).filter(x=>x.t).sort((a,b)=>bits(b.t.rate_bits)-bits(a.t.rate_bits)).slice(0,10);
  $('topTalkers').innerHTML=ranked.length?ranked.map((x,i)=>{const d=x.d,t=x.t;const remotes=Array.isArray(t.details)?t.details.length:0;return `<div class="top-talker-row"><div class="talker-rank">${i+1}</div><div class="talker-device"><a class="device-link primary" href="/device.html?id=${d.id}">${escapeHtml(d.name||d.lastIpAddress||'Unknown device')}</a><div class="secondary">${escapeHtml(d.lastIpAddress||'No IP')} • ${escapeHtml(d.vendor||'Unknown vendor')} • ${remotes} remote${remotes===1?'':'s'}</div></div><div class="talker-rate"><strong>↓ ${escapeHtml(t.rate_in||'0 b')}</strong><span>↑ ${escapeHtml(t.rate_out||'0 b')}</span><small>${escapeHtml(t.rate||'0 b')} total</small></div></div>`}).join(''):'<div class="empty">No current LAN traffic reported for monitored devices.</div>';
}
function render(){
  const q=($('deviceSearch').value||'').trim().toLowerCase();
  const filtered=management.filter(x=>{const d=x.device;return !q||`${d.name||''} ${d.lastIpAddress||''} ${d.macAddress||''} ${d.vendor||''}`.toLowerCase().includes(q)});
  const ignored=filtered.filter(x=>x.ignored),monitored=filtered.filter(x=>!x.ignored);
  $('ignoredDevices').innerHTML=ignored.length?ignored.map(row).join(''):'<div class="empty">No ignored devices match this search.</div>';
  $('monitoredDevices').innerHTML=monitored.length?monitored.map(row).join(''):'<div class="empty">No monitored devices match this search.</div>';
  const allIgnored=management.filter(x=>x.ignored).length;
  $('ignoredCount').textContent=`${allIgnored} ignored`;$('monitoredCount').textContent=`${management.length-allIgnored} monitored`;
  renderTopTalkers();
}
async function load(){try{const [m,t]=await Promise.all([json('/api/devices/management'),json('/api/opnsense/traffic/top?interfaces=lan')]);management=m;trafficRecords=Array.isArray(t?.lan?.records)?t.lan.records:[];$('managementStatus').textContent='Live • 5s';$('managementStatus').className='pill ok';render()}catch(e){$('managementStatus').textContent='Error';$('managementStatus').className='pill bad';$('topTalkers').innerHTML=`<div class="empty">${escapeHtml(e.message)}</div>`;$('ignoredDevices').innerHTML=`<div class="empty">${escapeHtml(e.message)}</div>`;$('monitoredDevices').innerHTML=''}}
async function setIgnored(id,ignored,button){button.disabled=true;try{await json(`/api/devices/${id}/ignored`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({ignored})});await load()}catch(e){alert(e.message)}finally{button.disabled=false}}
document.addEventListener('click',e=>{const b=e.target.closest('[data-id][data-action]');if(b)setIgnored(b.dataset.id,b.dataset.action==='ignore',b)});$('deviceSearch').addEventListener('input',render);load();setInterval(load,5000);
