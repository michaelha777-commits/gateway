const byId = id => document.getElementById(id);
const HOMEWATCH_VERSION = '2.0.0-alpha.8';
let allDevices = [];
let selectedDeviceId = null;
let activitySearchTimer = null;
let sessionSearchTimer = null;
let cachedDashboardEvents = [];

function parseServerDate(value) {
  if (value instanceof Date) return value;
  const text = String(value || '');
  if (!text) return new Date(NaN);
  const hasTimezone = /(?:Z|[+-]\d{2}:?\d{2})$/i.test(text);
  return new Date(hasTimezone ? text : `${text}Z`);
}

async function loadDashboard() {
  byId('refresh').disabled = true;
  try {
    const [statusResponse, dashboardResponse] = await Promise.all([
      fetch('/api/status', { cache: 'no-store' }),
      fetch('/api/dashboard?hours=24', { cache: 'no-store' })
    ]);
    if (!statusResponse.ok || !dashboardResponse.ok) throw new Error('HomeWatch API did not respond correctly.');
    const dashboard = await dashboardResponse.json();
    cachedDashboardEvents = dashboard.events || [];
    byId('version').textContent = `v${HOMEWATCH_VERSION}`;
    byId('deviceCount').textContent = dashboard.summary.deviceCount;
    byId('activeDevices').textContent = dashboard.summary.activeDevices;
    byId('alertCount').textContent = dashboard.summary.alertCount;
    byId('eventCount').textContent = dashboard.summary.eventCount;
    byId('updated').textContent = `Updated ${parseServerDate(dashboard.generatedAt).toLocaleTimeString()}`;
    renderLiveActivity(cachedDashboardEvents, dashboard.generatedAt);
    byId('events').innerHTML = cachedDashboardEvents.length
      ? cachedDashboardEvents.map(event => eventRow(event, true)).join('')
      : '<div class="empty">No activity has been imported yet.</div>';
  } catch (error) {
    byId('events').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`;
    byId('liveDevices').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`;
  } finally { byId('refresh').disabled = false; }
}

function renderLiveActivity(events, generatedAt) {
  const now = Date.now();
  const recent = events.filter(event => now - parseServerDate(event.timestamp).getTime() <= 5 * 60 * 1000);
  const byDevice = new Map();
  for (const event of recent) {
    const key = event.deviceId || event.deviceName || event.deviceIp;
    if (!key) continue;
    let item = byDevice.get(key);
    if (!item) {
      item = { deviceName: event.deviceName || event.deviceIp || 'Unknown device', deviceIp: event.deviceIp || '', lastSeen: event.timestamp, domains: [] };
      byDevice.set(key, item);
    }
    if (parseServerDate(event.timestamp) > parseServerDate(item.lastSeen)) item.lastSeen = event.timestamp;
    if (event.domain && !item.domains.includes(event.domain)) item.domains.push(event.domain);
  }
  const items = Array.from(byDevice.values()).sort((a,b) => parseServerDate(b.lastSeen) - parseServerDate(a.lastSeen)).slice(0,12);
  byId('liveUpdated').textContent = `Updated ${parseServerDate(generatedAt).toLocaleTimeString()}`;
  byId('liveDevices').innerHTML = items.length ? items.map(item => `
    <article class="live-card"><div class="live-card-head"><span class="live-pulse"></span><strong>${escapeHtml(item.deviceName)}</strong><time>${escapeHtml(formatRelative(item.lastSeen))}</time></div><small>${escapeHtml(item.deviceIp)}</small><div class="live-domains">${item.domains.slice(0,4).map(domain => `<span>${escapeHtml(domain)}</span>`).join('')}</div></article>`).join('')
    : '<div class="empty">No device has made a DNS request in the last five minutes.</div>';
}

async function loadDevices(preserveSelection = true) {
  byId('refreshDevices').disabled = true;
  try {
    const hours = byId('deviceHours').value;
    const response = await fetch(`/api/dashboard?hours=${encodeURIComponent(hours)}`, { cache:'no-store' });
    if (!response.ok) throw new Error('Could not load devices.');
    const data = await response.json();
    const deviceMap = new Map();
    for (const event of data.events || []) {
      if (!event.deviceId) continue;
      let device = deviceMap.get(event.deviceId);
      if (!device) {
        device = { id:event.deviceId, name:event.deviceName || event.deviceIp || 'Unknown device', ipAddress:event.deviceIp || null, vendor:null, lastSeen:event.timestamp, online:(Date.now()-parseServerDate(event.timestamp).getTime())<=300000, eventCount:0, domainCounts:new Map() };
        deviceMap.set(event.deviceId, device);
      }
      device.eventCount++;
      if (parseServerDate(event.timestamp) > parseServerDate(device.lastSeen)) device.lastSeen = event.timestamp;
      device.domainCounts.set(event.domain, (device.domainCounts.get(event.domain)||0)+1);
    }
    allDevices = Array.from(deviceMap.values()).map(device => ({...device, topDomains:Array.from(device.domainCounts.entries()).map(([domain,count])=>({domain,count})).sort((a,b)=>b.count-a.count).slice(0,5)})).sort((a,b)=>parseServerDate(b.lastSeen)-parseServerDate(a.lastSeen));
    renderDeviceList();
    if (preserveSelection && selectedDeviceId && allDevices.some(d=>d.id===selectedDeviceId)) await loadDeviceActivity(selectedDeviceId);
    else if (allDevices.length) await selectDevice(allDevices[0].id);
  } catch (error) { byId('deviceList').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`; }
  finally { byId('refreshDevices').disabled = false; }
}

function renderDeviceList() {
  const search = byId('deviceSearch').value.trim().toLowerCase();
  const devices = allDevices.filter(device => !search || [device.name,device.ipAddress,device.vendor].some(v=>String(v||'').toLowerCase().includes(search)));
  if (!devices.length) { byId('deviceList').innerHTML='<div class="empty">No matching devices.</div>'; return; }
  byId('deviceList').innerHTML = devices.map(device => {
    const name=friendlyDeviceName(device), domains=(device.topDomains||[]).slice(0,3).map(x=>x.domain).join(' · ');
    return `<button class="device-card ${device.id===selectedDeviceId?'selected':''}" data-device-id="${escapeHtml(device.id)}"><span class="device-avatar">${escapeHtml(name.slice(0,1).toUpperCase())}</span><span class="device-card-main"><strong>${escapeHtml(name)}</strong><small>${escapeHtml(device.ipAddress||'No IP')} · ${Number(device.eventCount||0)} requests</small><span>${escapeHtml(domains||'No recent activity')}</span></span><span class="online-dot ${device.online?'online':''}"></span></button>`;
  }).join('');
  document.querySelectorAll('[data-device-id]').forEach(button=>button.addEventListener('click',()=>selectDevice(button.dataset.deviceId)));
}

async function selectDevice(id) { selectedDeviceId=id; renderDeviceList(); byId('deviceEmpty').hidden=true; byId('deviceContent').hidden=false; await loadDeviceActivity(id); }

async function loadDeviceActivity(id=selectedDeviceId) {
  if (!id) return;
  const hours=byId('activityHours').value, search=byId('activitySearch').value.trim();
  byId('deviceEvents').innerHTML='<div class="empty">Loading activity…</div>';
  try {
    const response=await fetch(`/api/devices/${encodeURIComponent(id)}/activity?hours=${encodeURIComponent(hours)}&limit=1000&search=${encodeURIComponent(search)}`,{cache:'no-store'});
    if (!response.ok) throw new Error(response.status===404?'Device no longer exists.':'Could not load device activity.');
    const data=await response.json(), device=data.device;
    byId('selectedDeviceName').textContent=friendlyDeviceName(device);
    byId('selectedDeviceMeta').textContent=[device.ipAddress,device.vendor,`Last seen ${formatRelative(device.lastSeen)}`].filter(Boolean).join(' · ');
    byId('deviceStatus').textContent=device.online?'Online':'Offline';
    byId('deviceStatus').classList.toggle('online',device.online);
    byId('selectedEventCount').textContent=data.summary.eventCount;
    byId('selectedDomainCount').textContent=data.summary.uniqueDomains;
    byId('activityUpdated').textContent=`Updated ${parseServerDate(data.generatedAt).toLocaleTimeString()}`;
    byId('topDomains').innerHTML=data.topDomains.length?data.topDomains.map(item=>`<button class="domain-chip" data-domain="${escapeHtml(item.domain)}"><span>${escapeHtml(item.domain)}</span><strong>${item.count}</strong></button>`).join(''):'<span class="muted">No domains for this period.</span>';
    document.querySelectorAll('[data-domain]').forEach(button=>button.addEventListener('click',()=>{byId('activitySearch').value=button.dataset.domain;loadDeviceActivity();}));
    byId('deviceEvents').innerHTML=data.events.length?data.events.map(event=>eventRow({...event,deviceName:friendlyDeviceName(device)},false)).join(''):'<div class="empty">No website activity matches this filter.</div>';
  } catch(error) { byId('deviceEvents').innerHTML=`<div class="empty error">${escapeHtml(error.message)}</div>`; }
}

async function loadSessions() {
  byId('refreshSessions').disabled=true;
  const hours=byId('sessionHours').value, search=byId('sessionSearch').value.trim().toLowerCase();
  byId('sessionList').innerHTML='<div class="panel empty">Loading sessions…</div>';
  try {
    const response=await fetch(`/api/dashboard?hours=${encodeURIComponent(hours)}`,{cache:'no-store'});
    if(!response.ok) throw new Error('Could not load sessions.');
    const data=await response.json();
    const sessions=buildSessions(data.events||[]).filter(s=>!search||[s.deviceName,s.deviceIp,s.domain].some(v=>String(v||'').toLowerCase().includes(search)));
    byId('sessionList').innerHTML=sessions.length?sessions.map(sessionCard).join(''):'<div class="panel empty">No sessions match this period or search.</div>';
  } catch(error) { byId('sessionList').innerHTML=`<div class="panel empty error">${escapeHtml(error.message)}</div>`; }
  finally { byId('refreshSessions').disabled=false; }
}

function buildSessions(events) {
  const sorted=[...events].sort((a,b)=>parseServerDate(a.timestamp)-parseServerDate(b.timestamp)), sessions=[], open=new Map(), gapMs=600000;
  for(const event of sorted){const key=`${event.deviceId||event.deviceName}|${event.domain}`,timestamp=parseServerDate(event.timestamp);let session=open.get(key);if(!session||timestamp-parseServerDate(session.end)>gapMs){session={deviceName:event.deviceName||event.deviceIp||'Unknown device',deviceIp:event.deviceIp||'',domain:event.domain,start:event.timestamp,end:event.timestamp,requests:1};sessions.push(session);open.set(key,session);}else{session.end=event.timestamp;session.requests++;}}
  return sessions.sort((a,b)=>parseServerDate(b.end)-parseServerDate(a.end));
}

function sessionCard(session){const start=parseServerDate(session.start),end=parseServerDate(session.end),durationSeconds=Math.max(0,Math.round((end-start)/1000));return `<article class="panel session-card"><div class="session-device"><span class="device-avatar">${escapeHtml(session.deviceName.slice(0,1).toUpperCase())}</span><div><strong>${escapeHtml(session.deviceName)}</strong><small>${escapeHtml(session.deviceIp)}</small></div></div><div class="session-domain"><strong>${escapeHtml(session.domain)}</strong><span>${session.requests} DNS request${session.requests===1?'':'s'}</span></div><div class="session-time"><strong>${escapeHtml(formatDuration(durationSeconds))}</strong><span>${escapeHtml(formatEventTime(session.start))} – ${escapeHtml(formatEventTime(session.end))}</span></div></article>`;}

function eventRow(event,showDevice){const date=parseServerDate(event.timestamp);return `<article class="event-row"><div class="event-icon">${escapeHtml((event.deviceName||'?').slice(0,1).toUpperCase())}</div><div class="event-main">${showDevice?`<strong>${escapeHtml(event.deviceName)}</strong>`:''}<span class="domain-name">${escapeHtml(event.domain)}</span></div><div class="event-meta"><span class="pill">${escapeHtml(event.category||'dns')}</span><time title="${escapeHtml(date.toLocaleString())}">${formatEventTime(event.timestamp)}</time></div></article>`;}
function friendlyDeviceName(device){const name=String(device.name||'').trim();return name&&name!==device.ipAddress?name:(device.ipAddress||'Unknown device');}
function formatEventTime(value){const date=parseServerDate(value),today=new Date();return date.toDateString()===today.toDateString()?date.toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',second:'2-digit'}):date.toLocaleString([],{month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'});}
function formatRelative(value){const seconds=Math.max(0,Math.floor((Date.now()-parseServerDate(value).getTime())/1000));if(seconds<60)return `${seconds}s ago`;if(seconds<3600)return `${Math.floor(seconds/60)}m ago`;if(seconds<86400)return `${Math.floor(seconds/3600)}h ago`;return parseServerDate(value).toLocaleString();}
function formatDuration(seconds){if(seconds<60)return seconds<=5?'Single visit':`${seconds}s`;if(seconds<3600)return `${Math.max(1,Math.round(seconds/60))} min`;const hours=Math.floor(seconds/3600),minutes=Math.round((seconds%3600)/60);return `${hours}h ${minutes}m`;}

async function loadSettings(){
  byId('ntfyStatus').textContent='Checking…';byId('ntfyDetails').textContent='HomeWatch is checking the notification configuration.';
  try{const response=await fetch('/api/status',{cache:'no-store'});if(!response.ok)throw new Error('Could not read notification status.');const status=await response.json();const configured=Boolean(status.notifications&&status.notifications.configured);byId('ntfyStatus').textContent=configured?'Configured':'Not configured';byId('ntfyDetails').textContent=configured?'HomeWatch has an ntfy topic configured and is ready to send phone alerts.':'Restart HomeWatch and enter the exact ntfy topic subscribed on your phone.';byId('testNtfy').disabled=!configured;}catch(error){byId('ntfyStatus').textContent='Unavailable';byId('ntfyDetails').textContent=error.message;byId('testNtfy').disabled=true;}}

async function testNtfy(){const button=byId('testNtfy'),result=byId('ntfyTestResult');button.disabled=true;result.textContent='Sending test notification…';try{const response=await fetch('/api/notifications/test',{method:'POST'});const data=await response.json().catch(()=>({}));if(!response.ok)throw new Error(data.error||'The test notification failed.');result.textContent='Test sent. Check the ntfy app on your phone.';}catch(error){result.textContent=`Test failed: ${error.message}`;}finally{button.disabled=false;}}

function switchView(name){document.querySelectorAll('.view').forEach(view=>view.classList.toggle('active',view.id===`${name}View`));document.querySelectorAll('.nav button').forEach(button=>button.classList.toggle('active',button.dataset.view===name));if(name==='dashboard')loadDashboard();if(name==='devices')loadDevices();if(name==='sessions')loadSessions();if(name==='settings')loadSettings();}
function escapeHtml(value){return String(value??'').replace(/[&<>'"]/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[char]));}

document.querySelectorAll('.nav button').forEach(button=>button.addEventListener('click',()=>switchView(button.dataset.view)));
byId('refresh').addEventListener('click',loadDashboard);
byId('refreshDevices').addEventListener('click',()=>loadDevices(true));
byId('refreshSessions').addEventListener('click',loadSessions);
byId('testNtfy').addEventListener('click',testNtfy);
byId('deviceSearch').addEventListener('input',renderDeviceList);
byId('deviceHours').addEventListener('change',()=>loadDevices(true));
byId('activityHours').addEventListener('change',()=>loadDeviceActivity());
byId('sessionHours').addEventListener('change',loadSessions);
byId('sessionSearch').addEventListener('input',()=>{clearTimeout(sessionSearchTimer);sessionSearchTimer=setTimeout(loadSessions,250);});
byId('activitySearch').addEventListener('input',()=>{clearTimeout(activitySearchTimer);activitySearchTimer=setTimeout(()=>loadDeviceActivity(),300);});
loadDashboard();
setInterval(()=>{if(byId('dashboardView').classList.contains('active'))loadDashboard();if(byId('devicesView').classList.contains('active'))loadDevices(true);if(byId('sessionsView').classList.contains('active'))loadSessions();},10000);