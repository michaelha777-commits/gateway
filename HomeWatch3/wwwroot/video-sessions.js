const $ = id => document.getElementById(id);
const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));

async function json(url) {
  const response = await fetch(url, {cache: 'no-store'});
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  return response.json();
}

function bytes(value) {
  let size = Number(value || 0);
  if (size < 1024) return `${Math.round(size)} B`;
  if (size < 1048576) return `${(size / 1024).toFixed(1)} KB`;
  if (size < 1073741824) return `${(size / 1048576).toFixed(1)} MB`;
  return `${(size / 1073741824).toFixed(2)} GB`;
}

function rate(bits) {
  const value = Number(bits || 0);
  if (value < 1000) return `${Math.round(value)} bps`;
  if (value < 1000000) return `${(value / 1000).toFixed(1)} Kbps`;
  return `${(value / 1000000).toFixed(2)} Mbps`;
}

function when(value) {
  return value ? new Date(value).toLocaleString() : '—';
}

function duration(start, end) {
  const minutes = Math.max(0, Math.round((new Date(end) - new Date(start)) / 60000));
  if (minutes < 60) return `${Math.max(1, minutes)} min`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}

function visibilityLabel(value) {
  return ({'exact-url':'Exact URL', hostname:'Hostname / SNI', application:'Application only', ip:'IP only'})[value] || 'IP only';
}

function sessionState(session) {
  if (!session.active) return {label: 'ENDED', cls: 'neutral'};
  const idleSeconds = Math.max(0, (Date.now() - new Date(session.lastSeenUtc).getTime()) / 1000);
  return idleSeconds > 60 ? {label: 'WAITING TO CLOSE', cls: 'warn'} : {label: 'ACTIVE', cls: 'ok'};
}

function renderCard(session, trafficByDevice) {
  const state = sessionState(session);
  const traffic = trafficByDevice.get(Number(session.deviceId));
  const applications = session.applications || [];
  const protocols = session.protocols || [];
  const sources = session.telemetrySources || [];
  const domains = session.domains || [];
  const correlated = Number(session.attributedBytesDown || 0) + Number(session.attributedBytesUp || 0);
  const estimated = Number(session.bytesDown || 0) + Number(session.bytesUp || 0);
  const total = correlated || estimated;
  const evidenceCount = (session.evidence || []).length + (session.flowEvidence || []).length;
  const hostnamePreview = domains.slice(0, 3).join(', ') || 'No hostname observed';
  const appPreview = applications.join(', ') || 'Application not identified';
  const protocolPreview = protocols.join(', ') || 'DNS only';
  const liveRate = traffic ? `↓ ${rate(traffic.averageBitsIn)} / ↑ ${rate(traffic.averageBitsOut)}` : 'No current sample';

  return `<article class="video-session-card ${session.adult ? 'adult-session' : ''}">
    <div class="video-session-head">
      <div><span class="badge ${session.adult ? 'alert' : ''}">${session.adult ? 'Adult' : 'Video'}</span><h3>${esc(session.service)}</h3><a class="device-link" href="/device.html?id=${session.deviceId}">${esc(session.deviceName || session.ip)}</a><div class="secondary">${esc(session.ip)}</div></div>
      <span class="pill ${state.cls}">${state.label}</span>
    </div>
    <div class="alert-chips session-telemetry">
      <span class="badge visibility-${esc(session.visibility || 'ip')}">${esc(visibilityLabel(session.visibility))}</span>
      ${(session.flowEvidence || []).some(flow => flow.encrypted) ? '<span class="badge encrypted-badge">Encrypted</span>' : ''}
      ${sources.map(source => `<span class="badge">${esc(source)}</span>`).join('')}
    </div>
    <div class="video-session-metrics">
      <div><span>Correlated traffic</span><strong>${esc(total ? bytes(total) : 'Pending')}</strong></div>
      <div><span>Current device rate</span><strong>${esc(liveRate)}</strong></div>
      <div><span>Application</span><strong>${esc(appPreview)}</strong></div>
      <div><span>Protocol</span><strong>${esc(protocolPreview)}</strong></div>
    </div>
    <div class="session-time"><b>Observed hostnames:</b> ${esc(hostnamePreview)}<br><b>Started:</b> ${esc(when(session.startedUtc))}<br><b>Last signal:</b> ${esc(when(session.lastSeenUtc))}<br><b>Duration:</b> ${esc(duration(session.startedUtc, session.endedUtc || session.lastSeenUtc))}</div>
    ${session.blockedRequests ? `<div class="session-blocked">${Number(session.blockedRequests)} blocked DNS request${Number(session.blockedRequests) === 1 ? '' : 's'} in this session</div>` : ''}
    <div class="window-summary">${evidenceCount} evidence signal${evidenceCount === 1 ? '' : 's'} • ${Number(session.attributionConfidence || 0)}% traffic attribution confidence. Hostname/SNI does not reveal an encrypted HTTPS path.</div>
    <a class="button secondary-button session-evidence-link" href="/session.html?id=${encodeURIComponent(session.id)}">View DNS + flow evidence</a>
  </article>`;
}

let sessions = [];
let trafficRows = [];

function render() {
  const query = $('sessionFilter').value.trim().toLowerCase();
  const filtered = sessions.filter(session => !query || [
    session.service, session.deviceName, session.ip,
    ...(session.domains || []), ...(session.applications || []), ...(session.protocols || [])
  ].some(value => String(value || '').toLowerCase().includes(query)));
  const trafficByDevice = new Map(trafficRows.map(row => [Number(row.deviceId), row]));

  $('sessionCount').textContent = filtered.length;
  $('adultSessionCount').textContent = filtered.filter(session => session.adult).length;
  $('sessionDeviceCount').textContent = new Set(filtered.map(session => session.deviceId)).size;
  $('videoSessions').innerHTML = filtered.length
    ? filtered.map(session => renderCard(session, trafficByDevice)).join('')
    : '<div class="empty">No matching sessions were observed in this window.</div>';
}

async function load() {
  try {
    const minutes = Number($('historyWindow').value || 1440);
    const seconds = Number($('bandwidthWindow').value || 60);
    [sessions, trafficRows] = await Promise.all([
      json(`/api/video-sessions?minutes=${minutes}`),
      json(`/api/traffic/window?seconds=${seconds}`).catch(() => [])
    ]);
    $('sessionStatus').textContent = `${sessions.length} retained`;
    $('sessionStatus').className = 'pill ok';
    render();
  } catch (error) {
    $('sessionStatus').textContent = 'Unavailable';
    $('sessionStatus').className = 'pill bad';
    $('videoSessions').innerHTML = `<div class="empty">Unable to load sessions: ${esc(error.message)}</div>`;
  }
}

async function exportAdultHistory() {
  const button = $('exportAdult');
  const original = button.textContent;
  button.disabled = true;
  button.textContent = 'Preparing ZIP…';
  try {
    const params = new URLSearchParams({
      minutes: $('historyWindow').value,
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
    });
    const response = await fetch('/api/exports/adult-history?' + params.toString(), {cache:'no-store'});
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    const disposition = response.headers.get('Content-Disposition') || '';
    const match = disposition.match(/filename\*?=(?:UTF-8''|\")?([^\";]+)/i);
    const fileName = match ? decodeURIComponent(match[1].replace(/\"$/,'')) : 'homewatch-adult-history.zip';
    const url = URL.createObjectURL(await response.blob());
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    button.textContent = 'Export downloaded';
    setTimeout(() => { button.textContent = original; }, 1800);
  } catch (error) {
    alert(`Unable to export adult history: ${error.message}`);
    button.textContent = original;
  } finally {
    button.disabled = false;
  }
}

$('historyWindow').addEventListener('change', load);
$('bandwidthWindow').addEventListener('change', load);
$('sessionFilter').addEventListener('input', render);
$('exportAdult').addEventListener('click', exportAdultHistory);
load();
setInterval(load, 5000);
