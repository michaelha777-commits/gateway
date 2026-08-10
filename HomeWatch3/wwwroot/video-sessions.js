const $=id=>document.getElementById(id);
let management=[],dnsRows=[],trafficRecords=[];
const escapeHtml=v=>String(v??'').replace(/[&<>'\"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','\"':'&quot;'}[c]));
const pick=(o,...names)=>{for(const n of names){if(o&&o[n]!==undefined&&o[n]!==null&&String(o[n]).trim()!=='')return String(o[n])}return ''};
async function json(url){const r=await fetch(url,{cache:'no-store'});if(!r.ok)throw new Error(`${r.status} ${r.statusText}`);return r.json()}
function rowsFrom(v){if(Array.isArray(v))return v;if(Array.isArray(v?.rows))return v.rows;return []}
function ts(r){const raw=pick(r,'time','timestamp','created','date');if(!raw)return 0;if(/^\d+$/.test(raw))return Number(raw)*1000;const d=new Date(raw);return Number.isNaN(d.getTime())?0:d.getTime()}
function fmt(ms){return ms?new Date(ms).toLocaleString():'Unknown'}
function duration(ms){if(ms<60000)return '<1 min';const m=Math.max(1,Math.round(ms/60000));if(m<60)return `${m} min`;const h=Math.floor(m/60),rm=m%60;return `${h}h ${rm}m`}
function bytes(v){v=Number(v||0);if(v<1024)return `${v} B`;if(v<1048576)return `${(v/1024).toFixed(1)} KB`;if(v<1073741824)return `${(v/1048576).toFixed(1)} MB`;return `${(v/1073741824).toFixed(2)} GB`}
function streamEstimate(b){b=Number(b||0);if(!b)return 'Not enough traffic data';const bits=b*8;const low=Math.max(1,Math.round(bits/6000000/60));const high=Math.max(low,Math.round(bits/2500000/60));return `~${low}–${high} min typical video equivalent`}
const services=[
 {name:'Pornhub',adult:true,match:d=>/(^|\.)pornhub\./i.test(d)||/phncdn/i.test(d)},
 {name:'XVideos',adult:true,match:d=>/(^|\.)xvideos\./i.test(d)||/xvideos-cdn/i.test(d)},
 {name:'XNXX',adult:true,match:d=>/(^|\.)xnxx\./i.test(d)},
 {name:'YouPorn',adult:true,match:d=>/(^|\.)youporn\./i.test(d)},
 {name:'RedTube',adult:true,match:d=>/(^|\.)redtube\./i.test(d)},
 {name:'YouTube',adult:false,match:d=>/youtube\.com|googlevideo\.com|ytimg\.com/i.test(d)},
 {name:'Netflix',adult:false,match:d=>/netflix\.com|nflxvideo\.net|nflxso\.net|nrdp/i.test(d)},
 {name:'TikTok',adult:false,match:d=>/tiktok\.com|tiktokcdn|byteoversea|ibytedtos/i.test(d)},
 {name:'Twitch',adult:false,match:d=>/twitch\.tv|ttvnw\.net/i.test(d)},
 {name:'Vimeo',adult:false,match:d=>/vimeo\.com|vimeocdn\.com/i.test(d)},
 {name:'Dailymotion',adult:false,match:d=>/dailymotion\.com|dmcdn\.net/i.test(d)},
 {name:'Prime Video',adult:false,match:d=>/primevideo\.com|aiv-cdn\.net/i.test(d)},
 {name:'Disney+',adult:false,match:d=>/disneyplus\.com|dssott\.com/i.test(d)}
];
function classify(domain){return services.find(s=>s.match(domain||''))||null}
function trafficMap(){return new Map(trafficRecords.map(r=>[String(r.address||''),r]))}
function buildSessions(){
 const devices=management.map(x=>x.device),ignored=new Set(management.filter(x=>x.ignored).map(x=>String(x.device.lastIpAddress||''))),byIp=new Map(devices.map(d=>[String(d.lastIpAddress||''),d]));
 const groups=new Map();
 for(const r of dnsRows){const ip=pick(r,'client','client_ip','source','src','ip');if(!ip||ignored.has(ip))continue;const domain=pick(r,'domain','name','qname','query');const svc=classify(domain);if(!svc)continue;const key=`${ip}|${svc.name}`;const time=ts(r);let g=groups.get(key);if(!g){g={ip,service:svc.name,adult:svc.adult,first:time,last:time,hits:0,domains:new Set(),blocked:0,policies:new Set()};groups.set(key,g)}g.first=Math.min(g.first||time,time||g.first);g.last=Math.max(g.last||0,time);g.hits++;g.domains.add(domain);if(pick(r,'action').toLowerCase()==='block')g.blocked++;const p=pick(r,'policy');if(p)g.policies.add(p)}
 const traffic=trafficMap();return [...groups.values()].map(g=>{const d=byIp.get(g.ip),t=traffic.get(g.ip);return {...g,device:d,traffic:t,durationMs:Math.max(0,g.last-g.first),sampleBytes:Number(t?.cumulative_bytes||0)}}).sort((a,b)=>b.last-a.last)
}
function card(s){const d=s.device,name=d?.name||s.ip,t=s.traffic;const domainList=[...s.domains].slice(0,6);const cls=s.adult?'video-session-card adult-session':'video-session-card';return `<article class="${cls}">
 <div class="video-session-head"><div><span class="badge${s.adult?' alert':''}">${s.adult?'Adult':'Video'}</span><h3>${escapeHtml(s.service)}</h3><a class="device-link" href="${d?`/device.html?id=${d.id}`:'#'}">${escapeHtml(name)}</a><div class="secondary">${escapeHtml(s.ip)}</div></div><div class="session-now">${escapeHtml(t?.rate||'0 b')}</div></div>
 <div class="video-session-metrics"><div><span>Observed duration</span><strong>${duration(s.durationMs)}</strong></div><div><span>Current ↓ / ↑</span><strong>${escapeHtml(t?.rate_in||'0 b')} / ${escapeHtml(t?.rate_out||'0 b')}</strong></div><div><span>Traffic sample</span><strong>${bytes(s.sampleBytes)}</strong></div><div><span>Streaming estimate</span><strong>${escapeHtml(streamEstimate(s.sampleBytes))}</strong></div></div>
 <div class="session-time"><b>First seen:</b> ${escapeHtml(fmt(s.first))}<br><b>Last seen:</b> ${escapeHtml(fmt(s.last))} • ${s.hits} DNS signal${s.hits===1?'':'s'}</div>
 <div class="chips">${domainList.map(x=>`<span class="domain-chip ${s.adult?'':'neutral-chip'}">${escapeHtml(x)}</span>`).join('')}</div>
 ${s.blocked?`<div class="session-blocked"><b>${s.blocked} blocked request${s.blocked===1?'':'s'}</b>${s.policies.size?` • ${escapeHtml([...s.policies].join(', '))}`:''}</div>`:''}
 </article>`}
function render(){const q=($('sessionFilter').value||'').trim().toLowerCase();let sessions=buildSessions();if(q)sessions=sessions.filter(s=>`${s.device?.name||''} ${s.ip} ${s.service} ${[...s.domains].join(' ')}`.toLowerCase().includes(q));$('sessionCount').textContent=sessions.length;$('adultSessionCount').textContent=sessions.filter(s=>s.adult).length;$('sessionDeviceCount').textContent=new Set(sessions.map(s=>s.ip)).size;$('videoSessions').innerHTML=sessions.length?sessions.map(card).join(''):'<div class="empty">No matching video sessions detected in the current OPNsense DNS window.</div>'}
async function load(){try{const [m,dns,t]=await Promise.all([json('/api/devices/management'),json('/api/opnsense/unbound/queries'),json('/api/opnsense/traffic/top?interfaces=lan')]);management=m;dnsRows=rowsFrom(dns);trafficRecords=Array.isArray(t?.lan?.records)?t.lan.records:[];$('sessionStatus').textContent='Live • 5s';$('sessionStatus').className='pill ok';render()}catch(e){$('sessionStatus').textContent='Error';$('sessionStatus').className='pill bad';$('videoSessions').innerHTML=`<div class="empty">${escapeHtml(e.message)}</div>`}}
$('sessionFilter').addEventListener('input',render);load();setInterval(load,5000);
