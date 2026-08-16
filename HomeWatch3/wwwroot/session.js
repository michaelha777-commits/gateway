const $ = id => document.getElementById(id);
const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
async function json(url) { const response = await fetch(url, {cache:'no-store'}); if (!response.ok) throw new Error(`${response.status} ${response.statusText}`); return response.json(); }
function fmt(value) { return value ? new Date(value).toLocaleString() : '—'; }
function bytes(value) { let size = Number(value || 0); if (size < 1024) return `${Math.round(size)} B`; if (size < 1048576) return `${(size / 1024).toFixed(1)} KB`; if (size < 1073741824) return `${(size / 1048576).toFixed(1)} MB`; return `${(size / 1073741824).toFixed(2)} GB`; }
function duration(start, end) { const minutes = Math.max(0, Math.round((new Date(end) - new Date(start)) / 60000)); return minutes < 60 ? `${Math.max(1, minutes)} min` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`; }
function visibilityLabel(value) { return ({'exact-url':'Exact URL', hostname:'Hostname / SNI', application:'Application only', ip:'IP only'})[value] || 'IP only'; }
function sessionState(session) { if (!session.active) return {label:'ENDED', cls:'neutral', detail:''}; const idle = Math.max(0, (Date.now() - new Date(session.lastSeenUtc).getTime()) / 1000); if (idle > 60) { const left = Math.max(0, Math.ceil((600 - idle) / 60)); return {label:'WAITING TO CLOSE', cls:'warn', detail:left ? `No new service signal for ${Math.floor(idle / 60)} min; closes in about ${left} min.` : 'Closing shortly…'}; } return {label:'ACTIVE', cls:'ok', detail:'Recent DNS or ntopng flow activity is still being observed.'}; }
const id = new URLSearchParams(location.search).get('id');

function renderFlowEvidence(session) {
  const rows = Array.isArray(session.flowEvidence) ? [...session.flowEvidence].sort((a,b) => new Date(a.startedUtc) - new Date(b.startedUtc)) : [];
  $('flowTimeline').innerHTML = rows.length ? rows.map(row => {
    const target = row.hostname || row.application || row.remoteIp || 'Unknown destination';
    const total = Number(row.bytesDown || 0) + Number(row.bytesUp || 0);
    return `<div class="timeline-item"><div><b>${esc(target)}</b><div class="secondary">${esc(fmt(row.startedUtc))} – ${esc(fmt(row.lastSeenUtc))}</div><div class="secondary">${esc(row.application || 'Application not identified')} • ${esc(row.protocol || 'Unknown protocol')} • ${esc(row.remoteIp || '')}${row.remotePort ? `:${row.remotePort}` : ''}${row.country ? ` • ${esc(row.country)}` : ''}</div><div class="alert-chips"><span class="badge">${esc(visibilityLabel(row.visibility))}</span>${row.encrypted ? '<span class="badge encrypted-badge">Encrypted</span>' : ''}${total ? `<span class="badge">↓ ${esc(bytes(row.bytesDown))} / ↑ ${esc(bytes(row.bytesUp))}</span>` : ''}<span class="badge">${Number(row.confidence || 0)}% confidence</span></div></div><div class="secondary">ntopng</div></div>`;
  }).join('') : '<div class="empty">No ntopng flow evidence was retained for this session. DNS evidence may still identify the service hostname.</div>';
}

function renderDnsEvidence(session) {
  const rows = Array.isArray(session.evidence) ? [...session.evidence].sort((a,b) => new Date(a.timestampUtc) - new Date(b.timestampUtc)) : [];
  $('timeline').innerHTML = rows.length ? rows.map(row => `<div class="timeline-item"><div><b>${esc(row.domain || 'DNS event')}</b><div class="secondary">${esc(fmt(row.timestampUtc))} • ${esc(row.queryType || 'DNS')} • ${esc(row.source || 'OPNsense')}</div><div class="secondary">${esc(row.evidence || '')}</div>${row.policy ? `<div class="secondary">Policy: ${esc(row.policy)}</div>` : ''}</div><div><span class="badge ${String(row.action || '').toLowerCase() === 'block' ? 'alert' : ''}">${esc(row.action || 'Pass')}</span> ${row.confidence ? `<span class="secondary">${row.confidence}%</span>` : ''}</div></div>`).join('') : '<div class="empty">No DNS evidence is attached. This can happen when ntopng identified the application or hostname directly from an active flow.</div>';
}

async function load() {
  try {
    const all = await json('/api/video-sessions?minutes=43200');
    const session = (all || []).find(item => String(item.id).toLowerCase() === String(id || '').toLowerCase());
    if (!session) throw new Error('Session not found in the retained session window.');
    const state = sessionState(session);
    const applications = session.applications || [];
    const protocols = session.protocols || [];
    const sources = session.telemetrySources || [];
    const attributed = Number(session.attributedBytesDown || 0) + Number(session.attributedBytesUp || 0);
    $('serviceName').textContent = session.service;
    $('sessionSummary').innerHTML = `<a class="device-link" href="/device.html?id=${session.deviceId}">${esc(session.deviceName || session.ip)}</a> • ${esc(session.ip)} • ${session.adult ? 'Adult' : 'Video'} session`;
    $('duration').textContent = duration(session.startedUtc, session.endedUtc || session.lastSeenUtc);
    $('correlatedTraffic').textContent = attributed ? bytes(attributed) : 'Pending';
    $('visibility').textContent = visibilityLabel(session.visibility);
    $('sessionState').textContent = state.label;
    $('sessionState').className = `pill ${state.cls}`;
    $('sessionFacts').innerHTML = `<span><b>Started:</b> ${esc(fmt(session.startedUtc))}</span><span><b>Last signal:</b> ${esc(fmt(session.lastSeenUtc))}</span>${session.endedUtc ? `<span><b>Ended:</b> ${esc(fmt(session.endedUtc))}</span>` : ''}<span><b>Applications:</b> ${esc(applications.join(', ') || 'Not identified')}</span><span><b>Protocols:</b> ${esc(protocols.join(', ') || 'DNS only')}</span><span><b>Telemetry sources:</b> ${esc(sources.join(' + ') || 'Unknown')}</span><span><b>Correlated traffic:</b> ${attributed ? `${esc(bytes(attributed))} (${Number(session.attributionConfidence || 0)}% confidence)` : 'Not enough flow/IP correlation yet'}</span><span><b>Matched remote IPs:</b> ${esc((session.matchedRemoteIps || []).join(', ') || 'None matched')}</span><span><b>Blocked requests:</b> ${Number(session.blockedRequests || 0)}</span>${state.detail ? `<span><b>Session state:</b> ${esc(state.detail)}</span>` : ''}`;
    $('visibilitySummary').innerHTML = `<b>Best visibility:</b> ${esc(visibilityLabel(session.visibility))}<br><b>Applications:</b> ${esc(applications.join(', ') || 'Not identified')}<br><b>Protocols:</b> ${esc(protocols.join(', ') || 'DNS only')}<br><b>Limit:</b> “Hostname / SNI” identifies the service host but does not reveal the encrypted HTTPS path, search terms, video title, or page content.`;
    $('domains').innerHTML = (session.domains || []).map(domain => `<span class="domain-chip ${session.adult ? '' : 'neutral-chip'}">${esc(domain)}</span>`).join('') || '<span class="muted">No hostname was observable for this session.</span>';
    $('policyEvidence').innerHTML = `<b>OPNsense policies:</b> ${esc((session.policies || []).join(', ') || 'No named policy recorded')}<br><b>Interpretation:</b> ${session.adult ? 'Adult-service classification was corroborated by HomeWatch intelligence.' : 'The service was recognized from correlated DNS and/or ntopng application telemetry.'}<br><b>Traffic accounting:</b> Correlated traffic is based on matching active ntopng flows and remote IPs. It is stronger than device-wide sampling, but still does not expose encrypted content.`;
    renderFlowEvidence(session);
    renderDnsEvidence(session);
    const discovery = await json(`/api/devices/${session.deviceId}/discovery`).catch(() => null);
    $('identity').innerHTML = discovery ? `<span><b>Reverse DNS:</b> ${esc(discovery.reverseDns || 'Unknown')}</span><span><b>Device type:</b> ${esc(discovery.deviceType || 'Unknown')}</span><span><b>OS:</b> ${esc(discovery.operatingSystem || 'Unknown')}</span><span><b>Identity confidence:</b> ${Number(discovery.identityConfidence || 0)}%</span><span><b>Services:</b> ${esc((discovery.services || []).join(', ') || 'None discovered')}</span><span><b>Open ports:</b> ${esc((discovery.openPorts || []).join(', ') || 'None discovered')}</span>` : '<span>No discovery fingerprint exists yet. Open the device page and run Refresh discovery.</span>';
  } catch (error) { $('serviceName').textContent = 'Unable to load session'; $('timeline').innerHTML = `<div class="empty">${esc(error.message)}</div>`; $('flowTimeline').innerHTML = `<div class="empty">${esc(error.message)}</div>`; }
}
load(); setInterval(load, 15000);
