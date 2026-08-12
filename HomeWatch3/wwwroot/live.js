const $ = id => document.getElementById(id);
let livePaused = false;
let activities = [];
let trafficRecords = [];
let management = [];
let ignoredDomains = [];
let activeSessionDeviceId = null;

const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
const normalizeDomain = value => String(value || '').trim().toLowerCase().replace(/\.$/, '');

async function json(url, options) {
  const response = await fetch(url, {cache:'no-store', ...(options || {})});
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try { const body = await response.json(); message = body.error || message; } catch {}
    throw new Error(message);
  }
  return response.json();
}

function fmtTime(value) { const date = new Date(value); return Number.isNaN(date.getTime()) ? String(value || '') : date.toLocaleString(); }
function age(value) { const date = new Date(value); if (Number.isNaN(date.getTime())) return ''; const seconds = Math.max(0, (Date.now() - date.getTime()) / 1000); if (seconds < 60) return 'just now'; const minutes = Math.floor(seconds / 60); return minutes < 60 ? `${minutes} min ago` : `${Math.floor(minutes / 60)} hr ago`; }
function bytes(value) { let size = Number(value || 0); if (size < 1024) return `${Math.round(size)} B`; if (size < 1048576) return `${(size / 1024).toFixed(1)} KB`; if (size < 1073741824) return `${(size / 1048576).toFixed(1)} MB`; return `${(size / 1073741824).toFixed(2)} GB`; }
function visibilityLabel(value) { return ({'exact-url':'Exact URL', hostname:'Hostname / SNI', application:'Application only', ip:'IP only'})[value] || 'IP only'; }
function visibilityRank(value) { return ({'exact-url':4, hostname:3, application:2, ip:1})[value] || 1; }
function isDomainIgnored(domain) { const value = normalizeDomain(domain); return ignoredDomains.some(root => value === root || value.endsWith('.' + root)); }
function trafficByIp() { return new Map(trafficRecords.map(row => [String(row.address || ''), row])); }
function currentMaps() { const devices = management.map(item => item.device); return {devices, byId:new Map(devices.map(device => [Number(device.id), device])), traffic:trafficByIp(), ignoredIds:new Set(management.filter(item => item.ignored).map(item => Number(item.device.id))), infrastructureIds:new Set(management.filter(item => item.infrastructure).map(item => Number(item.device.id)))}; }

function trafficSummary(row) {
  if (!row) return '';
  const remotes = (row.details || []).slice(0, 3).map(item => item.address).filter(Boolean);
  const parts = [];
  if (row.rate) parts.push(`Traffic ${row.rate}`);
  if (row.rate_in || row.rate_out) parts.push(`↓ ${row.rate_in || '0'} ↑ ${row.rate_out || '0'}`);
  if (remotes.length) parts.push(`Remote ${remotes.join(', ')}`);
  return parts.join(' • ');
}

function groupSessions(rows) {
  const groups = new Map();
  for (const row of rows) {
    const deviceKey = row.deviceId ? `device:${row.deviceId}` : `ip:${row.ip || 'unknown'}`;
    const service = row.service || row.application || row.domain || row.destinationIp || 'Unknown service';
    const key = `${deviceKey}|${service}`;
    let group = groups.get(key);
    if (!group) {
      group = {deviceId:row.deviceId, device:row.device, ip:row.ip, service, rows:[], domains:new Set(), applications:new Set(), protocols:new Set(), sources:new Set(), destinations:new Set(), first:new Date(row.startedUtc || row.timestampUtc).getTime(), last:new Date(row.lastSeenUtc || row.timestampUtc).getTime(), blocked:false, background:true, encrypted:false, confidence:0, visibility:'ip', bytesDown:0, bytesUp:0};
      groups.set(key, group);
    }
    group.rows.push(row);
    (row.domains || []).forEach(value => group.domains.add(value));
    (row.applications || []).forEach(value => group.applications.add(value));
    (row.protocols || []).forEach(value => group.protocols.add(value));
    (row.sources || []).forEach(value => group.sources.add(value));
    (row.destinationIps || []).forEach(value => group.destinations.add(value));
    group.first = Math.min(group.first, new Date(row.startedUtc || row.timestampUtc).getTime());
    group.last = Math.max(group.last, new Date(row.lastSeenUtc || row.timestampUtc).getTime());
    group.blocked ||= Boolean(row.blocked);
    group.background &&= Boolean(row.background);
    group.encrypted ||= Boolean(row.encrypted);
    group.confidence = Math.max(group.confidence, Number(row.confidence || 0));
    if (visibilityRank(row.visibility) > visibilityRank(group.visibility)) group.visibility = row.visibility;
    group.bytesDown += Number(row.bytesDown || 0);
    group.bytesUp += Number(row.bytesUp || 0);
  }
  return [...groups.values()].map(group => {
    const recent = Date.now() - group.last < 120000;
    const active = !group.blocked && !group.background && recent;
    return {...group, active, level:group.blocked ? 'blocked' : active ? 'active' : 'background'};
  }).sort((a, b) => b.last - a.last);
}

function sessionDuration(session) {
  const seconds = Math.max(0, Math.round((session.last - session.first) / 1000));
  if (seconds < 60) return seconds ? `${seconds} sec window` : 'single observation';
  const minutes = Math.max(1, Math.round(seconds / 60));
  return minutes < 60 ? `${minutes} min window` : `${Math.floor(minutes / 60)}h ${minutes % 60}m window`;
}

function renderIgnoredDomains() {
  $('ignoredDomainCount').textContent = `${ignoredDomains.length} ignored`;
  $('ignoredDomains').innerHTML = ignoredDomains.length ? ignoredDomains.map(domain => `<span class="domain-chip neutral-chip">${escapeHtml(domain)} <button type="button" class="chip-action" data-unignore-domain="${escapeHtml(domain)}" aria-label="Remove ${escapeHtml(domain)}">×</button></span>`).join('') : '<span class="muted">No manually ignored domains.</span>';
}

function render() {
  const maps = currentMaps();
  const query = ($('liveFilter').value || '').trim().toLowerCase();
  const showInfrastructure = $('showInfrastructure').checked;
  const showBackground = $('showBackground').checked;
  const level = $('activityLevel').value;
  const visibility = $('visibilityLevel').value;
  const visibleRows = activities.filter(row => {
    if (row.deviceId && maps.ignoredIds.has(Number(row.deviceId))) return false;
    if (!showInfrastructure && row.deviceId && maps.infrastructureIds.has(Number(row.deviceId))) return false;
    if (row.domain && isDomainIgnored(row.domain)) return false;
    return true;
  });
  const sessions = groupSessions(visibleRows).filter(session => {
    const haystack = `${session.device || ''} ${session.ip || ''} ${session.service} ${[...session.domains].join(' ')} ${[...session.applications].join(' ')} ${[...session.destinations].join(' ')}`.toLowerCase();
    return (!query || haystack.includes(query)) && (level === 'all' || session.level === level) && (visibility === 'all' || session.visibility === visibility) && (showBackground || session.level !== 'background' || level === 'background');
  }).slice(0, 75);
  const active = sessions.filter(session => session.active);
  $('liveSummary').innerHTML = `<div><span class="label">Active now</span><strong>${active.length}</strong></div><div><span class="label">Devices</span><strong>${new Set(sessions.map(session => session.deviceId || session.ip)).size}</strong></div><div><span class="label">Correlated activities</span><strong>${sessions.reduce((count, session) => count + session.rows.length, 0)}</strong></div>`;
  $('liveActivity').innerHTML = sessions.length ? sessions.map(session => {
    const traffic = maps.traffic.get(session.ip || '');
    const domains = [...session.domains];
    const applications = [...session.applications];
    const protocols = [...session.protocols];
    const sources = [...session.sources];
    const totalBytes = session.bytesDown + session.bytesUp;
    const deviceLink = session.deviceId ? `<a class="device-link" href="/device.html?id=${session.deviceId}">${escapeHtml(session.device || session.ip || 'Unknown device')}</a>` : escapeHtml(session.device || session.ip || 'Unknown device');
    const label = session.blocked ? 'Blocked' : session.active ? 'Likely active' : 'Background contact';
    const badge = session.blocked ? 'badge alert' : session.active ? 'badge confidence-active' : 'badge confidence-background';
    return `<article class="live-row session-row"><div class="live-dot"></div><div class="live-main"><div class="primary">${escapeHtml(session.service)}</div><div class="secondary">${deviceLink}${session.ip ? ` • ${escapeHtml(session.ip)}` : ''} • ${escapeHtml(sessionDuration(session))}</div><div class="secondary">${domains.length ? `Hostname: ${escapeHtml(domains[0])}` : applications.length ? `Application: ${escapeHtml(applications[0])}` : `Remote IP: ${escapeHtml([...session.destinations][0] || 'Unknown')}`} • last seen ${escapeHtml(age(new Date(session.last).toISOString()))}</div>${trafficSummary(traffic) ? `<div class="secondary wrap-text">${escapeHtml(trafficSummary(traffic))}</div>` : ''}<details class="session-evidence"><summary>Correlated evidence (${session.rows.length} activit${session.rows.length === 1 ? 'y' : 'ies'})</summary><div class="live-evidence-grid"><span><b>Hostnames:</b> ${escapeHtml(domains.join(', ') || 'Not observed')}</span><span><b>Applications:</b> ${escapeHtml(applications.join(', ') || 'Not identified')}</span><span><b>Protocols:</b> ${escapeHtml(protocols.join(', ') || 'Unknown')}</span><span><b>Sources:</b> ${escapeHtml(sources.join(' + ') || 'Unknown')}</span><span><b>Traffic:</b> ↓ ${escapeHtml(bytes(session.bytesDown))} / ↑ ${escapeHtml(bytes(session.bytesUp))}</span></div></details></div><div class="live-meta"><span class="${badge}">${label}</span><span class="badge visibility-${escapeHtml(session.visibility)}">${escapeHtml(visibilityLabel(session.visibility))}</span>${session.encrypted ? '<span class="badge encrypted-badge">Encrypted</span>' : ''}${totalBytes ? `<span class="badge">${escapeHtml(bytes(totalBytes))}</span>` : ''}<span class="badge">${session.confidence}% confidence</span>${session.deviceId ? `<button class="button small secondary-button" type="button" data-session-device-id="${session.deviceId}">Details</button><details class="manage-menu"><summary>Manage</summary><div class="manage-panel"><button class="button small secondary-button" type="button" data-ignore-id="${session.deviceId}">Hide all activity from this device</button><div class="secondary">This only hides activity. It does not block the device.</div></div></details>` : ''}</div></article>`;
  }).join('') : '<div class="empty">No matching activity sessions. Turn on “Show background activity” to include routine cloud and update contacts.</div>';
  const ignored = management.filter(item => item.ignored).map(item => item.device);
  $('ignoredSummary').innerHTML = ignored.length ? ignored.map(device => `<span class="domain-chip neutral-chip"><a class="device-link" href="/device.html?id=${device.id}">${escapeHtml(device.name || device.lastIpAddress || 'Unknown device')}</a> <button type="button" class="chip-action" data-unignore-id="${device.id}">×</button></span>`).join('') : '<span class="muted">No ignored devices.</span>';
  renderIgnoredDomains();
  if (activeSessionDeviceId) renderLiveSession(activeSessionDeviceId);
}

function renderLiveSession(id) {
  const maps = currentMaps();
  const device = maps.byId.get(Number(id));
  if (!device) return;
  const rows = activities.filter(row => Number(row.deviceId) === Number(id)).sort((a,b) => new Date(b.lastSeenUtc || b.timestampUtc) - new Date(a.lastSeenUtc || a.timestampUtc));
  const domains = [...new Set(rows.flatMap(row => row.domains || []).filter(Boolean))];
  const applications = [...new Set(rows.flatMap(row => row.applications || []).filter(Boolean))];
  const protocols = [...new Set(rows.flatMap(row => row.protocols || []).filter(Boolean))];
  const sources = [...new Set(rows.flatMap(row => row.sources || []).filter(Boolean))];
  const destinations = [];
  const seen = new Set();
  for (const row of rows) for (const ip of row.destinationIps || []) { if (!seen.has(ip)) { seen.add(ip); destinations.push({ip, port:row.destinationPort, country:row.country, application:row.application, protocol:row.protocol}); } }
  const traffic = maps.traffic.get(device.lastIpAddress || '');
  $('liveSessionTitle').innerHTML = `<a class="device-link" href="/device.html?id=${device.id}">${escapeHtml(device.name || device.lastIpAddress || 'Unknown device')}</a>`;
  $('liveSessionIdentity').textContent = `${device.lastIpAddress || 'No IP'} • ${device.macAddress || 'No MAC'} • ${device.vendor || 'Unknown vendor'}`;
  const remoteHtml = destinations.length ? destinations.slice(0, 40).map(item => `<div class="session-remote-row"><div><strong>${escapeHtml(item.ip)}${item.port ? `:${item.port}` : ''}</strong><div class="secondary">${escapeHtml(item.application || item.protocol || 'Remote destination')}${item.country ? ` • ${escapeHtml(item.country)}` : ''}</div></div></div>`).join('') : '<div class="empty">No ntopng remote destinations observed in this window.</div>';
  const domainHtml = domains.length ? domains.slice(0, 40).map(domain => `<div class="timeline-item"><div><strong>${escapeHtml(domain)}</strong><div class="secondary">Observed hostname / SNI</div><details class="manage-menu"><summary>Manage</summary><div class="manage-panel"><button class="button small secondary-button" type="button" data-ignore-domain="${escapeHtml(domain)}">Hide this domain from Live Activity</button><div class="secondary">This only hides the domain. It does not block it.</div></div></details></div></div>`).join('') : '<div class="empty">No hostname was observable; check the application and IP evidence below.</div>';
  const blocked = rows.filter(row => row.blocked);
  $('liveSessionBody').innerHTML = `<div class="detail-stats session-stats"><div><span class="label">Current traffic</span><strong>${escapeHtml(traffic?.rate || '0')}</strong></div><div><span class="label">Activities</span><strong>${rows.length}</strong></div><div><span class="label">Visibility</span><strong>${escapeHtml(visibilityLabel(rows.sort((a,b) => visibilityRank(b.visibility) - visibilityRank(a.visibility))[0]?.visibility || 'ip'))}</strong></div></div><div class="detail-meta"><span><b>Applications:</b> ${escapeHtml(applications.join(', ') || 'Not identified')}</span><span><b>Protocols:</b> ${escapeHtml(protocols.join(', ') || 'Unknown')}</span><span><b>Sources:</b> ${escapeHtml(sources.join(' + ') || 'Unknown')}</span><span><b>Blocked:</b> ${blocked.length}</span></div><div class="detail-section"><a class="button secondary-button" href="/device.html?id=${device.id}">Open full device details</a></div><div class="detail-section"><div class="section-head"><div><div class="label">ntopng flow telemetry</div><h2>Remote destinations</h2></div></div><div class="session-remotes">${remoteHtml}</div></div><div class="detail-section"><div class="section-head"><div><div class="label">DNS + flow names</div><h2>Observed hostnames</h2></div></div><div class="timeline">${domainHtml}</div></div><div class="detail-section"><details class="manage-menu"><summary>Manage device visibility</summary><div class="manage-panel"><button class="button secondary-button" type="button" data-ignore-id="${device.id}">Hide all activity from this device</button><div class="secondary">This only hides activity. It does not block the device.</div></div></details></div>`;
}

function openLiveSession(id) { activeSessionDeviceId = Number(id); $('liveSessionOverlay').classList.remove('hidden'); $('liveSessionOverlay').setAttribute('aria-hidden','false'); document.body.classList.add('no-scroll'); renderLiveSession(id); }
function closeLiveSession() { activeSessionDeviceId = null; $('liveSessionOverlay').classList.add('hidden'); $('liveSessionOverlay').setAttribute('aria-hidden','true'); document.body.classList.remove('no-scroll'); }
async function setIgnored(id, ignored) { try { await json(`/api/devices/${id}/ignored`, {method:'PUT', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ignored})}); if (activeSessionDeviceId === Number(id) && ignored) closeLiveSession(); await loadDevices(); } catch (error) { alert(`Unable to update device: ${error.message}`); } }
async function addIgnoredDomain(domain) { const value = normalizeDomain(domain); if (!value) return; try { await json('/api/domains/ignored', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({domain:value})}); $('ignoredDomainInput').value = ''; await loadIgnoredDomains(); } catch (error) { alert(`Unable to ignore domain: ${error.message}`); } }
async function removeIgnoredDomain(domain) { try { await json(`/api/domains/ignored?domain=${encodeURIComponent(domain)}`, {method:'DELETE'}); await loadIgnoredDomains(); } catch (error) { alert(`Unable to remove ignored domain: ${error.message}`); } }
async function loadDevices() { management = await json('/api/devices/management'); render(); }
async function loadIgnoredDomains() { ignoredDomains = (await json('/api/domains/ignored')).map(normalizeDomain).filter(Boolean); render(); }

async function loadLive() {
  if (livePaused) return;
  try {
    const [history, traffic, telemetry] = await Promise.all([json('/api/history?minutes=20&limit=500'), json('/api/opnsense/traffic/top?interfaces=lan'), json('/api/telemetry/status')]);
    activities = Array.isArray(history) ? history : [];
    trafficRecords = Array.isArray(traffic?.lan?.records) ? traffic.lan.records : [];
    $('liveStatus').textContent = telemetry.available ? 'Live • DNS + ntopng' : 'Live • DNS only';
    $('liveStatus').className = telemetry.available ? 'pill ok' : 'pill neutral';
    render();
  } catch (error) { $('liveStatus').textContent = 'Feed error'; $('liveStatus').className = 'pill bad'; $('liveActivity').innerHTML = `<div class="empty">${escapeHtml(error.message)}</div>`; }
}

$('pauseLiveBtn').addEventListener('click', () => { livePaused = !livePaused; $('pauseLiveBtn').textContent = livePaused ? 'Resume' : 'Pause'; $('liveStatus').textContent = livePaused ? 'Paused' : 'Connecting…'; $('liveStatus').className = 'pill neutral'; if (!livePaused) loadLive(); });
['liveFilter'].forEach(id => $(id).addEventListener('input', render));
['activityLevel','visibilityLevel','showBackground','showInfrastructure'].forEach(id => $(id).addEventListener('change', render));
$('addIgnoredDomainBtn').addEventListener('click', () => addIgnoredDomain($('ignoredDomainInput').value));
$('ignoredDomainInput').addEventListener('keydown', event => { if (event.key === 'Enter') addIgnoredDomain(event.currentTarget.value); });
$('closeLiveSessionBtn').addEventListener('click', closeLiveSession);
$('liveSessionOverlay').addEventListener('click', event => { if (event.target === $('liveSessionOverlay')) closeLiveSession(); });
document.addEventListener('keydown', event => { if (event.key === 'Escape' && activeSessionDeviceId) closeLiveSession(); });
document.addEventListener('click', event => { const ignore = event.target.closest('[data-ignore-id]'); if (ignore) { event.stopPropagation(); setIgnored(ignore.dataset.ignoreId, true); return; } const unignore = event.target.closest('[data-unignore-id]'); if (unignore) { event.stopPropagation(); setIgnored(unignore.dataset.unignoreId, false); return; } const ignoreDomain = event.target.closest('[data-ignore-domain]'); if (ignoreDomain) { event.stopPropagation(); addIgnoredDomain(ignoreDomain.dataset.ignoreDomain); return; } const unignoreDomain = event.target.closest('[data-unignore-domain]'); if (unignoreDomain) { event.stopPropagation(); removeIgnoredDomain(unignoreDomain.dataset.unignoreDomain); return; } const session = event.target.closest('[data-session-device-id]'); if (session) { event.stopPropagation(); openLiveSession(session.dataset.sessionDeviceId); } });
Promise.all([loadDevices(), loadIgnoredDomains()]).then(loadLive);
setInterval(loadDevices, 15000); setInterval(loadIgnoredDomains, 15000); setInterval(loadLive, 5000);
