const byId = id => document.getElementById(id);
const HOMEWATCH_VERSION = '2.0.0-alpha.17';
let allDevices = [];
let selectedDeviceId = null;
let selectedDevice = null;
let identityDirty = false;
let savingIdentity = false;
let activitySearchTimer = null;
let sessionSearchTimer = null;
let cachedDashboardEvents = [];
let dashboardCursor = null;
let deviceActivityCursor = null;
let deviceActivityEvents = [];
let loadingOlderActivity = false;
let loadingOlderDashboard = false;

function parseServerDate(value) {
  if (value instanceof Date) return value;
  const text = String(value || '').trim();
  if (!text) return new Date(NaN);
  return new Date(/(?:Z|[+-]\d{2}:?\d{2})$/i.test(text) ? text : `${text}Z`);
}

function isEditingIdentity() {
  const active = document.activeElement;
  return identityDirty || savingIdentity || active === byId('deviceNameInput') || active === byId('deviceMacInput');
}

function rangeParams(selectId, fromId, toId, now = new Date()) {
  const value = byId(selectId)?.value || 'today';
  const params = new URLSearchParams();
  const startOfDay = date => new Date(date.getFullYear(), date.getMonth(), date.getDate());
  if (value === 'custom') {
    const from = byId(fromId)?.value, to = byId(toId)?.value;
    if (from) params.set('from', new Date(`${from}T00:00:00`).toISOString());
    if (to) { const exclusive = new Date(`${to}T00:00:00`); exclusive.setDate(exclusive.getDate() + 1); params.set('to', exclusive.toISOString()); }
  } else if (value === 'hour') {
    params.set('from', new Date(now.getTime() - 3600000).toISOString());
  } else if (value === 'today') {
    params.set('from', startOfDay(now).toISOString());
  } else if (value === 'yesterday') {
    const to = startOfDay(now), from = new Date(to); from.setDate(from.getDate() - 1);
    params.set('from', from.toISOString()); params.set('to', to.toISOString());
  } else if (value !== 'all') {
    params.set('from', new Date(now.getTime() - Number(value) * 86400000).toISOString());
  }
  return params;
}

async function loadDashboard(append = false) {
  if (loadingOlderDashboard || (append && !dashboardCursor)) return;
  loadingOlderDashboard = true;
  byId('refresh').disabled = true;
  if (!append) dashboardCursor = null;
  try {
    const params = rangeParams('hwRange', 'hwFrom', 'hwTo');
    const filtered = new URLSearchParams(params);
    const deviceId = byId('hwDevice')?.value, category = byId('hwCategory')?.value, search = byId('hwDomainSearch')?.value.trim();
    if (deviceId) filtered.set('deviceId', deviceId);
    if (category && category !== 'all') filtered.set('category', category);
    if (search) filtered.set('search', search);
    filtered.set('pageSize', '100');
    if (append) filtered.set('cursor', dashboardCursor);
    const [statusResponse, dashboardResponse, activityResponse, summaryResponse] = await Promise.all([
      fetch('/api/status', { cache: 'no-store' }), fetch(`/api/dashboard?${params}`, { cache: 'no-store' }),
      fetch(`/api/activity?${filtered}`, { cache: 'no-store' }), fetch(`/api/activity/summary?${filtered}`, { cache: 'no-store' })
    ]);
    if (![statusResponse,dashboardResponse,activityResponse,summaryResponse].every(x => x.ok)) throw new Error('HomeWatch API did not respond correctly.');
    const status = await statusResponse.json(), dashboard = await dashboardResponse.json();
    const activity = await activityResponse.json(), summary = await summaryResponse.json();
    const incoming = activity.events || [];
    if (append) { const ids = new Set(cachedDashboardEvents.map(event => String(event.id))); for (const event of incoming) if (!ids.has(String(event.id))) { ids.add(String(event.id)); cachedDashboardEvents.push(event); } }
    else cachedDashboardEvents = incoming;
    dashboardCursor = activity.page?.nextCursor || null;
    byId('loadOlderDashboard').hidden = !activity.page?.hasMore;
    if (typeof hw1CachedEvents !== 'undefined') hw1CachedEvents = cachedDashboardEvents;
    byId('version').textContent = `v${status.version || HOMEWATCH_VERSION}`;
    byId('deviceCount').textContent = dashboard.summary.deviceCount;
    byId('activeDevices').textContent = summary.activeDevices;
    byId('alertCount').textContent = dashboard.summary.alertCount;
    byId('eventCount').textContent = summary.eventCount;
    byId('updated').textContent = `Updated ${parseServerDate(activity.generatedAt).toLocaleTimeString()}`;
    renderLiveActivity(cachedDashboardEvents, activity.generatedAt);
    byId('events').innerHTML = cachedDashboardEvents.length ? cachedDashboardEvents.map(event => eventRow(event, true)).join('') : '<div class="empty">No activity matches this range.</div>';
    renderEvidenceTable(cachedDashboardEvents);
  } catch (error) {
    byId('events').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`;
    byId('liveDevices').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`;
  } finally { loadingOlderDashboard = false; byId('refresh').disabled = false; }
}

function renderEvidenceTable(events) {
  const table = byId('hwEvidenceRows');
  if (!table) return;
  table.innerHTML = events.length ? events.map(e => `<tr><td>${escapeHtml(formatFullTime(e.timestamp))}</td><td>${escapeHtml(e.deviceName || e.deviceIp || 'Unknown')}</td><td>${escapeHtml(e.domain)}</td><td>${escapeHtml(e.category)}</td><td>${escapeHtml(e.action)}</td></tr>`).join('') : '<tr><td colspan="5">No matching activity.</td></tr>';
}

function renderLiveActivity(events, generatedAt) {
  const recent = events.filter(event => Date.now() - parseServerDate(event.timestamp).getTime() <= 300000);
  const map = new Map();
  for (const event of recent) {
    const key = event.deviceId || event.deviceName || event.deviceIp;
    if (!key) continue;
    if (!map.has(key)) map.set(key, { deviceName: event.deviceName || event.deviceIp || 'Unknown device', deviceIp: event.deviceIp || '', lastSeen: event.timestamp, domains: [] });
    const item = map.get(key);
    if (parseServerDate(event.timestamp) > parseServerDate(item.lastSeen)) item.lastSeen = event.timestamp;
    if (event.domain && !item.domains.includes(event.domain)) item.domains.push(event.domain);
  }
  const items = [...map.values()].sort((a,b) => parseServerDate(b.lastSeen) - parseServerDate(a.lastSeen)).slice(0,12);
  byId('liveUpdated').textContent = `Updated ${parseServerDate(generatedAt).toLocaleTimeString()}`;
  byId('liveDevices').innerHTML = items.length ? items.map(item => `<article class="live-card"><div class="live-card-head"><span class="live-pulse"></span><strong>${escapeHtml(item.deviceName)}</strong><time>${escapeHtml(formatRelative(item.lastSeen))}</time></div><small>${escapeHtml(item.deviceIp)}</small><div class="live-domains">${item.domains.slice(0,4).map(domain => `<span>${escapeHtml(domain)}</span>`).join('')}</div></article>`).join('') : '<div class="empty">No device has made a DNS request in the last five minutes.</div>';
}

async function loadDevices(preserveSelection = true, force = false) {
  if (!force && isEditingIdentity()) return;
  byId('refreshDevices').disabled = true;
  try {
    const response = await fetch(`/api/devices?${rangeParams('deviceRange', 'deviceFrom', 'deviceTo')}`, { cache: 'no-store' });
    if (!response.ok) throw new Error('Could not load devices.');
    const data = await response.json();
    allDevices = data.devices || [];
    renderDeviceList();
    if (preserveSelection && selectedDeviceId && allDevices.some(d => d.id === selectedDeviceId)) await selectDevice(selectedDeviceId, force);
    else if (allDevices.length) await selectDevice(allDevices[0].id, force);
  } catch (error) { byId('deviceList').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`; }
  finally { byId('refreshDevices').disabled = false; }
}

function renderDeviceList() {
  const search = byId('deviceSearch').value.trim().toLowerCase();
  const devices = allDevices.filter(device => !search || [device.name, device.ipAddress, device.macAddress, device.vendor].some(v => String(v || '').toLowerCase().includes(search)));
  if (!devices.length) { byId('deviceList').innerHTML = '<div class="empty">No matching devices.</div>'; return; }
  byId('deviceList').innerHTML = devices.map(device => {
    const name = friendlyDeviceName(device);
    const domains = (device.topDomains || []).slice(0,3).map(x => x.domain).join(' · ');
    const identity = [device.ipAddress, device.macAddress].filter(Boolean).join(' · ') || 'No network identity';
    return `<button class="device-card ${device.id === selectedDeviceId ? 'selected' : ''}" data-device-id="${escapeHtml(device.id)}"><span class="device-avatar">${escapeHtml(name.slice(0,1).toUpperCase())}</span><span class="device-card-main"><strong>${escapeHtml(name)}</strong><small>${escapeHtml(identity)} · ${Number(device.eventCount || 0)} requests</small><span>${escapeHtml(domains || 'No recent activity')}</span></span><span class="online-dot ${device.online ? 'online' : ''}"></span></button>`;
  }).join('');
  document.querySelectorAll('[data-device-id]').forEach(button => button.addEventListener('click', () => selectDevice(button.dataset.deviceId, true)));
}

async function selectDevice(id, forceIdentity = false) {
  if (identityDirty && id !== selectedDeviceId && !confirm('Discard the unsaved device name or MAC address?')) return;
  selectedDeviceId = id;
  selectedDevice = allDevices.find(device => device.id === id) || null;
  renderDeviceList();
  byId('deviceEmpty').hidden = true;
  byId('deviceContent').hidden = false;
  if (selectedDevice && (forceIdentity || !isEditingIdentity())) {
    byId('deviceNameInput').value = selectedDevice.name || selectedDevice.ipAddress || '';
    byId('deviceMacInput').value = selectedDevice.macAddress || '';
    byId('deviceSaveResult').textContent = '';
    identityDirty = false;
  }
  await loadDeviceActivity(id, forceIdentity);
}

async function saveDevice() {
  if (!selectedDeviceId) return;
  const button = byId('saveDevice');
  const result = byId('deviceSaveResult');
  savingIdentity = true;
  button.disabled = true;
  result.textContent = 'Saving device…';
  try {
    const response = await fetch(`/api/devices/${encodeURIComponent(selectedDeviceId)}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name: byId('deviceNameInput').value.trim(), macAddress: byId('deviceMacInput').value.trim() })
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || 'Could not save the device.');
    identityDirty = false;
    result.textContent = 'Saved. This name now appears throughout HomeWatch.';
    await loadDevices(true, true);
    await loadDashboard();
  } catch (error) { result.textContent = `Save failed: ${error.message}`; }
  finally { savingIdentity = false; button.disabled = false; }
}

async function loadDeviceActivity(id = selectedDeviceId, forceIdentity = false, append = false) {
  if (!id || loadingOlderActivity) return;
  loadingOlderActivity = true;
  const search = byId('activitySearch').value.trim();
  if (!append) { deviceActivityCursor = null; deviceActivityEvents = []; byId('deviceEvents').innerHTML = '<div class="empty">Loading visited sites…</div>'; }
  try {
    const params = rangeParams('activityRange', 'activityFrom', 'activityTo');
    params.set('deviceId', id); params.set('pageSize', '100'); if (search) params.set('search', search);
    if (append && deviceActivityCursor) params.set('cursor', deviceActivityCursor);
    const summaryParams = new URLSearchParams(params); summaryParams.delete('pageSize'); summaryParams.delete('cursor');
    const requests = [fetch(`/api/activity?${params}`, { cache: 'no-store' })];
    if (!append) requests.push(fetch(`/api/activity/summary?${summaryParams}`, { cache: 'no-store' }));
    const responses = await Promise.all(requests);
    if (!responses.every(x => x.ok)) throw new Error(responses[0].status === 404 ? 'Device no longer exists.' : 'Could not load device activity.');
    const data = await responses[0].json();
    const knownIds = new Set(deviceActivityEvents.map(event => String(event.id)));
    for (const event of data.events || []) if (!knownIds.has(String(event.id))) { knownIds.add(String(event.id)); deviceActivityEvents.push(event); }
    deviceActivityCursor = data.page?.nextCursor || null;
    if (!append) {
      const summary = await responses[1].json();
      const device = selectedDevice || allDevices.find(x => x.id === id);
      byId('selectedDeviceName').textContent = friendlyDeviceName(device);
      byId('selectedDeviceMeta').textContent = [device.ipAddress, device.macAddress, device.vendor, `Last seen ${formatRelative(device.lastSeen)}`].filter(Boolean).join(' · ');
      if (forceIdentity || !isEditingIdentity()) { byId('deviceNameInput').value = device.name || device.ipAddress || ''; byId('deviceMacInput').value = device.macAddress || ''; }
      byId('deviceStatus').textContent = device.online ? 'Online' : 'Offline'; byId('deviceStatus').classList.toggle('online', device.online);
      byId('selectedEventCount').textContent = summary.eventCount; byId('selectedDomainCount').textContent = summary.uniqueDomains;
      byId('topDomains').innerHTML = '<span class="muted">Domain totals are computed across the full selected range.</span>';
    }
    byId('activityUpdated').textContent = `${deviceActivityEvents.length.toLocaleString()} loaded · ${data.page?.hasMore ? 'more available' : 'end of history'}`;
    byId('loadOlderActivity').hidden = !data.page?.hasMore;
    renderVisitedSites(deviceActivityEvents);
  } catch (error) { if (!append) byId('deviceEvents').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`; }
  finally { loadingOlderActivity = false; }
}

function renderVisitedSites(events) {
  const sites = new Map();
  for (const event of events) {
    const domain = String(event.domain || 'Unknown').toLowerCase();
    if (!sites.has(domain)) sites.set(domain, { domain, category: event.category || 'dns', first: event.timestamp, last: event.timestamp, requests: 0, action: event.action || 'observed' });
    const site = sites.get(domain);
    site.requests++;
    if (parseServerDate(event.timestamp) < parseServerDate(site.first)) site.first = event.timestamp;
    if (parseServerDate(event.timestamp) > parseServerDate(site.last)) site.last = event.timestamp;
    if (event.category && event.category !== 'dns') site.category = event.category;
  }
  const ordered = [...sites.values()].sort((a,b) => parseServerDate(b.last) - parseServerDate(a.last));
  byId('deviceEvents').innerHTML = ordered.length ? ordered.map(site => visitedSiteRow(site)).join('') : '<div class="empty">No sites match this period or search.</div>';
}

function visitedSiteRow(site) {
  const description = describeSite(site.domain, site.category);
  return `<article class="event-row site-list-row"><div class="event-icon">${escapeHtml(site.domain.slice(0,1).toUpperCase())}</div><div class="event-main"><strong>${escapeHtml(site.domain)}</strong><span>${escapeHtml(description)}</span><small>${site.requests} DNS request${site.requests === 1 ? '' : 's'} · First ${escapeHtml(formatFullTime(site.first))} · Last ${escapeHtml(formatFullTime(site.last))}</small></div><div class="event-meta"><span class="pill">${escapeHtml(site.category || 'dns')}</span><span>${escapeHtml(site.action)}</span></div></article>`;
}

function describeSite(domain, category) {
  const d = domain.toLowerCase();
  const known = [
    [/chatgpt|openai/, 'OpenAI / ChatGPT artificial-intelligence service'],
    [/youtube|googlevideo/, 'YouTube video and streaming service'],
    [/netflix|nflx/, 'Netflix video-streaming service'],
    [/whatsapp/, 'WhatsApp messaging and calling service'],
    [/facebook|fbcdn|instagram/, 'Meta social-media service'],
    [/amazon|cloudfront|amazonaws/, 'Amazon retail, cloud, or content-delivery service'],
    [/microsoft|office|outlook|teams|live\.com/, 'Microsoft 365, Windows, or Teams service'],
    [/google|gstatic|googleapis|gvt/, 'Google search, application, or infrastructure service'],
    [/apple|icloud/, 'Apple or iCloud service'],
    [/spotify/, 'Spotify music-streaming service'],
    [/tiktok/, 'TikTok social-video service'],
    [/github/, 'GitHub software-development service']
  ];
  const match = known.find(([pattern]) => pattern.test(d));
  if (match) return match[1];
  if (category === 'adult') return 'Adult-content domain detected by HomeWatch';
  return `Internet domain contacted by this device (${rootDomain(domain)})`;
}

function rootDomain(domain) { const parts = String(domain).split('.').filter(Boolean); return parts.length > 1 ? parts.slice(-2).join('.') : domain; }


function eventRow(event, showDevice) {
  const date = parseServerDate(event.timestamp);
  return `<article class="event-row"><div class="event-icon">${escapeHtml((event.deviceName || '?').slice(0,1).toUpperCase())}</div><div class="event-main">${showDevice ? `<strong>${escapeHtml(event.deviceName)}</strong>` : ''}<span class="domain-name">${escapeHtml(event.domain)}</span></div><div class="event-meta"><span class="pill">${escapeHtml(event.category || 'dns')}</span><time title="${escapeHtml(date.toLocaleString())}">${formatEventTime(event.timestamp)}</time></div></article>`;
}

function friendlyDeviceName(device) { const name = String(device.name || '').trim(); return name && name !== device.ipAddress ? name : (device.ipAddress || 'Unknown device'); }
function formatEventTime(value) { const date = parseServerDate(value), today = new Date(); return date.toDateString() === today.toDateString() ? date.toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'}) : date.toLocaleString([], {month:'short',day:'numeric',hour:'2-digit',minute:'2-digit'}); }
function formatFullTime(value) { return parseServerDate(value).toLocaleString([], {year:'numeric',month:'short',day:'numeric',hour:'2-digit',minute:'2-digit',second:'2-digit'}); }
function formatRelative(value) { const seconds = Math.max(0, Math.floor((Date.now() - parseServerDate(value).getTime()) / 1000)); if (seconds < 60) return `${seconds}s ago`; if (seconds < 3600) return `${Math.floor(seconds/60)}m ago`; if (seconds < 86400) return `${Math.floor(seconds/3600)}h ago`; return parseServerDate(value).toLocaleString(); }
function formatDuration(seconds) { if (seconds < 60) return seconds <= 5 ? 'Single visit' : `${seconds}s`; if (seconds < 3600) return `${Math.max(1,Math.round(seconds/60))} min`; return `${Math.floor(seconds/3600)}h ${Math.round((seconds%3600)/60)}m`; }

async function loadSettings() {
  byId('browserTimezone').textContent = Intl.DateTimeFormat().resolvedOptions().timeZone || 'Browser local time';
  byId('ntfyStatus').textContent = 'Checking…';
  try {
    const response = await fetch('/api/status', { cache:'no-store' });
    if (!response.ok) throw new Error('Could not read notification status.');
    const status = await response.json();
    const configured = Boolean(status.notifications && status.notifications.configured);
    byId('ntfyStatus').textContent = configured ? 'Configured' : 'Not configured';
    byId('ntfyDetails').textContent = configured ? 'HomeWatch is ready to send ntfy phone alerts.' : 'Restart HomeWatch and enter the exact ntfy topic subscribed on your phone.';
    byId('testNtfy').disabled = !configured;
  } catch (error) { byId('ntfyStatus').textContent = 'Unavailable'; byId('ntfyDetails').textContent = error.message; byId('testNtfy').disabled = true; }
}

async function testNtfy() {
  const button = byId('testNtfy'), result = byId('ntfyTestResult');
  button.disabled = true; result.textContent = 'Sending test notification…';
  try { const response = await fetch('/api/notifications/test', {method:'POST'}); const data = await response.json().catch(() => ({})); if (!response.ok) throw new Error(data.error || 'The test notification failed.'); result.textContent = 'Test sent. Check the ntfy app on your phone.'; }
  catch (error) { result.textContent = `Test failed: ${error.message}`; }
  finally { button.disabled = false; }
}

function switchView(name) {
  document.querySelectorAll('.view').forEach(view => view.classList.toggle('active', view.id === `${name}View`));
  document.querySelectorAll('.nav button').forEach(button => button.classList.toggle('active', button.dataset.view === name));
  if (name === 'dashboard') loadDashboard();
  if (name === 'devices') loadDevices();
  if (name === 'sessions') loadSessions();
  if (name === 'settings') loadSettings();
}

function escapeHtml(value) { return String(value ?? '').replace(/[&<>'"]/g, character => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[character])); }

document.querySelectorAll('.nav button').forEach(button => button.addEventListener('click', () => switchView(button.dataset.view)));
byId('refresh').addEventListener('click', loadDashboard);
byId('refreshDevices').addEventListener('click', () => loadDevices(true, true));
byId('refreshSessions').addEventListener('click', loadSessions);
byId('saveDevice').addEventListener('click', saveDevice);
byId('testNtfy').addEventListener('click', testNtfy);
byId('deviceNameInput').addEventListener('input', () => { identityDirty = true; byId('deviceSaveResult').textContent = 'Unsaved changes'; });
byId('deviceMacInput').addEventListener('input', () => { identityDirty = true; byId('deviceSaveResult').textContent = 'Unsaved changes'; });
byId('deviceSearch').addEventListener('input', renderDeviceList);
byId('deviceRange').addEventListener('change', () => loadDevices(true, true));
byId('activityRange').addEventListener('change', () => loadDeviceActivity());
byId('loadOlderDashboard').addEventListener('click', () => loadDashboard(true));
byId('loadOlderActivity').addEventListener('click', () => loadDeviceActivity(selectedDeviceId, false, true));
byId('hwRefresh').addEventListener('click', loadDashboard);
byId('hwRange').addEventListener('change', loadDashboard);
byId('hwFrom').addEventListener('change', loadDashboard);
byId('hwTo').addEventListener('change', loadDashboard);
byId('deviceFrom').addEventListener('change', () => loadDevices(true, true));
byId('deviceTo').addEventListener('change', () => loadDevices(true, true));
byId('activityFrom').addEventListener('change', () => loadDeviceActivity());
byId('activityTo').addEventListener('change', () => loadDeviceActivity());
byId('activitySearch').addEventListener('input', () => { clearTimeout(activitySearchTimer); activitySearchTimer = setTimeout(() => loadDeviceActivity(),300); });
loadDashboard();
setInterval(() => {
  if (byId('dashboardView').classList.contains('active')) loadDashboard();
  if (byId('devicesView').classList.contains('active') && !isEditingIdentity()) loadDevices(true);
  if (byId('sessionsView').classList.contains('active')) loadSessions();
}, 10000);

const activityObserver = new IntersectionObserver(entries => { if (entries[0].isIntersecting && deviceActivityCursor) loadDeviceActivity(selectedDeviceId, false, true); }, { rootMargin: '250px' });
activityObserver.observe(byId('activityScrollSentinel'));
const dashboardObserver = new IntersectionObserver(entries => { if (entries[0].isIntersecting && dashboardCursor) loadDashboard(true); }, { rootMargin: '250px' });
dashboardObserver.observe(byId('dashboardScrollSentinel'));
