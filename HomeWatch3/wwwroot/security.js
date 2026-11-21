const $=id=>document.getElementById(id);
const esc=value=>String(value??'').replace(/[&<>"']/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
let alerts=[];
let devices=[];
let ignoredRoots=[];

async function request(url,options={}){
  const response=await fetch(url,{cache:'no-store',...options});
  const text=await response.text();
  let value={};
  try{value=text?JSON.parse(text):{}}catch{value={error:text||response.statusText}}
  if(!response.ok)throw new Error(value?.error||value?.detail||`${response.status} ${response.statusText}`);
  return value;
}

function rowsFrom(value){
  if(Array.isArray(value))return value;
  if(Array.isArray(value?.rows))return value.rows;
  return [];
}

function ipKey(value){return String(value||'').trim().toLowerCase()}
function deviceFor(row){
  const src=ipKey(row.src_ip),dest=ipKey(row.dest_ip);
  return devices.find(item=>ipKey(item.device?.lastIpAddress)===src)||devices.find(item=>ipKey(item.device?.lastIpAddress)===dest)||null;
}
function evidence(row){
  const tls=row.tls&&typeof row.tls==='object'?row.tls:{};
  const quic=row.quic&&typeof row.quic==='object'?row.quic:{};
  const http=row.http&&typeof row.http==='object'?row.http:{};
  const hostname=String(tls.sni||quic.sni||http.hostname||http.host||row.dns?.rrname||row.dns?.queries?.[0]?.rrname||'').trim();
  const httpPath=String(http.url||http.path||'').trim();
  const transport=tls.sni||Object.keys(tls).length?'tls':quic.sni||Object.keys(quic).length?'quic':Object.keys(http).length?'http':'other';
  const version=String(tls.version||quic.version||'').trim();
  const certificate=String(tls.subject||'').trim();
  const issuer=String(tls.issuerdn||'').trim();
  const fingerprint=String(tls.ja4||quic.ja4||tls.ja3?.hash||quic.ja3?.hash||'').trim();
  const action=String(row.alert_action||row.action||'allowed').toLowerCase();
  const blocked=action.includes('block')||action.includes('drop');
  const device=deviceFor(row);
  return {row,hostname,httpPath,transport,version,certificate,issuer,fingerprint,action,blocked,device};
}

function parseTime(value){
  if(!value)return null;
  const date=new Date(value);
  return Number.isNaN(date.getTime())?null:date;
}
function formatTime(value){const date=parseTime(value);return date?date.toLocaleString(undefined,{timeZone:'America/Toronto',month:'short',day:'numeric',hour:'numeric',minute:'2-digit',second:'2-digit'}):String(value||'Unknown time')}
function relativeTime(value){const date=parseTime(value);if(!date)return '';const seconds=Math.max(0,Math.round((Date.now()-date.getTime())/1000));if(seconds<60)return `${seconds}s ago`;if(seconds<3600)return `${Math.floor(seconds/60)}m ago`;if(seconds<86400)return `${Math.floor(seconds/3600)}h ago`;return `${Math.floor(seconds/86400)}d ago`}

function setHealth(prefix,ok,title,detail){
  $(`${prefix}Dot`).className=`health-dot ${ok?'ok':'bad'}`;
  $(`${prefix}Status`).textContent=title;
  $(`${prefix}Detail`).textContent=detail;
}

function describeStatus(value){
  const status=String(value?.status||value?.response||'').trim();
  return status||'Unknown';
}

async function loadHealth(){
  const [idsResult,etproResult]=await Promise.allSettled([request('/api/opnsense/ids/status'),request('/api/opnsense/etpro/status')]);
  if(idsResult.status==='fulfilled'){
    const status=describeStatus(idsResult.value),running=/run|active|ok/i.test(status);
    setHealth('ids',running,status,running?'OPNsense IDS/IPS is active':'Check Services → Intrusion Detection');
  }else setHealth('ids',false,'Unavailable',idsResult.reason.message);
  if(etproResult.status==='fulfilled'){
    const value=etproResult.value,status=String(value?.sensor_status||'').trim()||'No status',active=/^(active|dormant)$/i.test(status);
    const detail=[value?.last_rule_download&&`Rules ${value.last_rule_download}`,value?.last_heartbeat&&`Heartbeat ${value.last_heartbeat}`].filter(Boolean).join(' • ')||'ET Pro Telemetry plugin responded';
    setHealth('etpro',active,status,detail);
  }else setHealth('etpro',false,'Not available',etproResult.reason.message);
}

function visibleEvidence(){
  return alerts.map(evidence).filter(item=>{
    const host=item.hostname.toLowerCase().replace(/\.$/,'');
    return !item.device?.ignored && !ignoredRoots.some(root=>host===root||host.endsWith('.'+root));
  });
}

function updateDeviceFilter(){
  const current=$('deviceFilter').value;
  const matched=new Map();
  visibleEvidence().forEach(item=>{if(item.device)matched.set(String(item.device.device.id),item.device.device)});
  $('deviceFilter').innerHTML='<option value="">All devices</option>'+[...matched.values()].sort((a,b)=>String(a.name||a.lastIpAddress).localeCompare(String(b.name||b.lastIpAddress))).map(device=>`<option value="${device.id}">${esc(device.name||device.lastIpAddress||'Unknown')} (${esc(device.lastIpAddress||'')})</option>`).join('');
  if([...matched.keys()].includes(current))$('deviceFilter').value=current;
}

function filteredEvidence(){
  const search=$('searchInput').value.trim().toLowerCase(),deviceId=$('deviceFilter').value,protocol=$('protocolFilter').value,action=$('actionFilter').value,hostnameOnly=$('hostnameOnly').checked;
  return visibleEvidence().filter(item=>{
    if(deviceId&&String(item.device?.device?.id||'')!==deviceId)return false;
    if(protocol&&item.transport!==protocol)return false;
    if(action==='blocked'&&!item.blocked)return false;
    if(action==='allowed'&&item.blocked)return false;
    if(hostnameOnly&&!item.hostname)return false;
    if(search){
      const row=item.row;
      const haystack=[item.hostname,item.httpPath,row.alert,row.alert_sid,row.src_ip,row.dest_ip,item.certificate,item.issuer,item.fingerprint,item.device?.device?.name,item.device?.device?.lastIpAddress].join(' ').toLowerCase();
      if(!haystack.includes(search))return false;
    }
    return true;
  }).sort((a,b)=>(parseTime(b.row.timestamp)?.getTime()||0)-(parseTime(a.row.timestamp)?.getTime()||0));
}

function render(){
  const enriched=visibleEvidence(),visible=filteredEvidence();
  $('alertCount').textContent=enriched.length.toLocaleString();
  $('hostnameCount').textContent=enriched.filter(item=>item.hostname).length.toLocaleString();
  $('blockedCount').textContent=enriched.filter(item=>item.blocked).length.toLocaleString();
  $('deviceCount').textContent=new Set(enriched.filter(item=>item.device).map(item=>item.device.device.id)).size.toLocaleString();
  $('resultCount').textContent=`${visible.length} result${visible.length===1?'':'s'}`;
  $('evidenceRows').innerHTML=visible.length?visible.map(item=>{
    const row=item.row,device=item.device?.device,deviceName=device?.name||device?.lastIpAddress||row.src_ip||'Unknown device';
    const deviceHtml=device?`<a class="device-link" href="/device.html?id=${device.id}">${esc(deviceName)}</a>`:esc(deviceName);
    const ports=`${esc(row.src_ip||'?')}${row.src_port?`:${esc(row.src_port)}`:''} → ${esc(row.dest_ip||'?')}${row.dest_port?`:${esc(row.dest_port)}`:''}`;
    const certificate=[item.httpPath&&`HTTP path: ${item.httpPath}`,item.certificate&&`Subject: ${item.certificate}`,item.issuer&&`Issuer: ${item.issuer}`,item.fingerprint&&`Fingerprint: ${item.fingerprint}`].filter(Boolean).join(' • ');
    return `<article class="evidence-row">
      <div class="evidence-primary">
        <h4 class="${item.hostname?'evidence-host':''}">${esc(item.hostname||row.alert||'Suricata alert')}</h4>
        <div class="evidence-signature">${item.hostname?esc(row.alert||'Suricata alert'):esc(`No hostname exposed • ${row.alert||'Suricata alert'}`)}</div>
        <div class="evidence-meta"><span class="evidence-badge transport">${esc(item.transport)}</span>${item.version?`<span class="evidence-badge">${esc(item.version)}</span>`:''}<span class="evidence-badge ${item.blocked?'blocked':'allowed'}">${esc(item.action)}</span>${row.app_proto?`<span class="evidence-badge">${esc(row.app_proto)}</span>`:''}${row.alert_sid?`<span class="evidence-badge">SID ${esc(row.alert_sid)}</span>`:''}</div>
        ${item.hostname?`<button type="button" data-ignore-domain="${esc(item.hostname)}">Ignore domain</button>`:''}
        ${device?`<button type="button" data-ignore-device="${esc(device.id)}">Ignore device</button>`:''}
      </div>
      <div class="evidence-connection"><strong>${deviceHtml}</strong><small>${ports}<br>${esc(row.in_iface||'Unknown interface')} • ${esc(row.proto||'Unknown protocol')}</small>${certificate?`<div class="evidence-certificate">${esc(certificate)}</div>`:''}</div>
      <div class="evidence-time"><time>${esc(formatTime(row.timestamp))}</time><small>${esc(relativeTime(row.timestamp))}</small></div>
    </article>`;
  }).join(''):'<div class="security-empty">No Suricata evidence matches these filters.</div>';
}

async function load(){
  $('pageStatus').textContent='Loading…';$('pageStatus').className='pill neutral';$('refreshBtn').disabled=true;
  try{
    const [deviceRows,roots]=await Promise.all([request('/api/devices/management'),request('/api/domains/ignored')]);
    ignoredRoots=roots;
    devices=Array.isArray(deviceRows)?deviceRows:[];alerts=rowsFrom(await request('/api/opnsense/ids/alerts?limit=500'));updateDeviceFilter();render();
    $('pageStatus').textContent='Ready';$('pageStatus').className='pill ok';
  }catch(error){
    alerts=[];render();$('pageStatus').textContent='Unavailable';$('pageStatus').className='pill bad';$('evidenceRows').innerHTML=`<div class="security-empty security-error">${esc(error.message)}<br><br>The HomeWatch OPNsense API user needs the IDS/IPS alert privilege.</div>`;
  }finally{$('refreshBtn').disabled=false}
  await Promise.all([loadHealth(),loadArchived().catch(err=>$('archivedAlerts').textContent=err.message)]);
}

$('evidenceRows').addEventListener('click',async event=>{
  const button=event.target.closest('[data-ignore-device],[data-ignore-domain]');
  if(!button)return;
  const {ignoreDevice,ignoreDomain}=button.dataset;
  button.disabled=true;
  try{
    await request(ignoreDevice?`/api/devices/${ignoreDevice}/ignored`:'/api/domains/ignored',{
      method:ignoreDevice?'PUT':'POST', headers:{'Content-Type':'application/json'},
      body:JSON.stringify(ignoreDevice?{ignored:true}:{domain:ignoreDomain})
    });
    await load();
  }catch(error){button.disabled=false;alert(error.message)}
});

['searchInput','deviceFilter','protocolFilter','actionFilter','hostnameOnly'].forEach(id=>$(id).addEventListener(id==='searchInput'?'input':'change',render));
$('refreshBtn').addEventListener('click',load);
load();

async function loadArchived(){
  const rows=await request('/api/security/history?search='+encodeURIComponent($('archiveTerm').value));
  const list=$('archivedAlerts');list.replaceChildren();
  for(const row of rows){
    const evidence=row.evidence;
    const host=row.domain.toLowerCase(), ips=[evidence.ip_src_saddr,evidence.ip_dst_saddr].filter(Boolean);
    if(ignoredRoots.some(root=>host===root||host.endsWith('.'+root)) || devices.some(d=>d.ignored && ips.includes(d.device?.lastIpAddress)))continue;
    const entry=document.createElement('details'), title=document.createElement('summary'), detail=document.createElement('p');
    title.textContent=[formatTime(row.time),row.source,evidence.signature||row.category||'Security observation',row.acknowledged?'Acknowledged':''].filter(Boolean).join(' · ');
    detail.textContent=[row.domain,evidence.device_name,...ips,evidence.severity?'Severity '+evidence.severity:'Severity unavailable',evidence.action||(row.blocked?'Blocked':'')].filter(Boolean).join(' · ');
    const url='/api/zenarmor/investigation/'+row.source+'/'+row.epoch+'/'+row.id;
    const notes=document.createElement('div'),input=document.createElement('input'),button=document.createElement('button');
    input.placeholder='Investigation note';input.maxLength=1000;button.textContent='Acknowledge';button.className='button small';
    entry.ontoggle=async()=>{if(entry.open){try{const history=await request(url);notes.textContent=history.map(n=>[formatTime(n.time),n.actor,n.note].join(' · ')).join(' / ');}catch(e){notes.textContent=e.message;}}};
    button.onclick=async()=>{try{await request(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({note:input.value,acknowledged:true})});await loadArchived();}catch(e){notes.textContent=e.message;}};
    entry.append(title,detail,notes,input,button);list.append(entry);
  }
  if(!list.childElementCount)list.textContent='No matching retained alerts.';
}
$('archiveSearch').onsubmit=e=>{e.preventDefault();loadArchived().catch(err=>$('archivedAlerts').textContent=err.message);};
