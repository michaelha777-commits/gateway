const $ = id => document.getElementById(id);
const esc = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));

async function json(url, options) {
  const response = await fetch(url, {cache: 'no-store', ...(options || {})});
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try { const body = await response.json(); message = body.error || message; } catch {}
    throw new Error(message);
  }
  return response.json();
}

function utcDate(value) {
  if (!value) return null;
  let raw = String(value).trim();
  if (/^\d{4}-\d{2}-\d{2}T/.test(raw) && !/[zZ]|[+-]\d\d:?\d\d$/.test(raw)) raw += 'Z';
  const date = new Date(raw);
  return Number.isNaN(date.getTime()) ? null : date;
}

function fmt(value) {
  const date = utcDate(value);
  return date ? date.toLocaleString(undefined, {month:'short', day:'numeric', hour:'numeric', minute:'2-digit', second:'2-digit'}) : String(value ?? '');
}

function bytes(value) {
  let size = Number(value || 0);
  if (size < 1024) return `${Math.round(size)} B`;
  if (size < 1048576) return `${(size / 1024).toFixed(1)} KB`;
  if (size < 1073741824) return `${(size / 1048576).toFixed(1)} MB`;
  return `${(size / 1073741824).toFixed(2)} GB`;
}

function duration(seconds) {
  const value = Math.max(0, Number(seconds || 0));
  if (value < 1) return '';
  if (value < 60) return `${Math.round(value)} sec`;
  const minutes = Math.round(value / 60);
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}

function visibilityLabel(value) {
  return ({'exact-url':'Exact URL', hostname:'Hostname / SNI', application:'Application only', ip:'IP only'})[value] || 'IP only';
}

function queryString(includeLimit = true) {
  const params = new URLSearchParams({
    minutes: $('range').value,
    category: $('category').value,
    visibility: $('visibility').value,
    source: $('source').value,
    activity: $('activity').value
  });
  if (includeLimit) params.set('limit', '500');
  if ($('device').value) params.set('deviceId', $('device').value);
  if ($('search').value.trim()) params.set('search', $('search').value.trim());
  return params.toString();
}

async function loadDevices() {
  const rows = await json('/api/devices/management');
  $('device').innerHTML = '<option value="">All devices</option>' + rows.map(item => {
    const device = item.device;
    return `<option value="${device.id}">${esc(device.name || device.lastIpAddress || 'Unknown')} (${esc(device.lastIpAddress || '')})</option>`;
  }).join('');
}

async function markSafe(domain) {
  if (!confirm(`Mark ${domain} as not adult?`)) return;
  await json('/api/intelligence/adult/safe', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({domain})});
  await load();
}

async function removeSafe(domain) {
  await fetch(`/api/intelligence/adult/safe?domain=${encodeURIComponent(domain)}`, {method:'DELETE'});
  await Promise.all([loadIntel(), loadHistory()]);
}

async function exportAdultHistory() {
  const button = $('exportAdult');
  const original = button.textContent;
  button.disabled = true;
  button.textContent = 'Preparing ZIP…';
  try {
    const params = new URLSearchParams({
      minutes: $('range').value,
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
    });
    if ($('device').value) params.set('deviceId', $('device').value);
    const response = await fetch('/api/exports/adult-history?' + params.toString(), {cache:'no-store'});
    if (!response.ok) {
      let message = `${response.status} ${response.statusText}`;
      try { const body = await response.json(); message = body.error || body.detail || message; } catch {}
      throw new Error(message);
    }
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

async function loadIntel() {
  try {
    const [status, safe] = await Promise.all([json('/api/intelligence/adult/status'), json('/api/intelligence/adult/safe')]);
    $('intelStatus').innerHTML = `<span><b>Known intelligence domains:</b> ${Number(status.domainCount || 0).toLocaleString()}</span><span><b>User safe domains:</b> ${status.safeDomainCount || 0}</span><span><b>Last feed update:</b> ${status.lastUpdatedUtc ? esc(fmt(status.lastUpdatedUtc)) : 'Waiting for first refresh'}</span>${status.lastError ? `<span><b>Feed warning:</b> ${esc(status.lastError)}</span>` : ''}`;
    $('safeDomains').innerHTML = safe.length ? safe.map(domain => `<span class="domain-chip neutral-chip">${esc(domain)} <button class="chip-action" data-remove-safe="${esc(domain)}">×</button></span>`).join('') : '<span class="muted">No domains marked safe by you.</span>';
  } catch (error) { $('intelStatus').textContent = error.message; }
}

function activityRow(row) {
  const primary = row.exactUrl || row.domain || row.application || row.destinationIp || 'No hostname available';
  const primaryLabel = row.exactUrl ? 'Exact URL' : row.domain ? 'Observed hostname' : row.application ? 'Detected application' : 'Remote IP';
  const device = row.deviceId ? `<a class="device-link" href="/device.html?id=${row.deviceId}">${esc(row.device || row.ip || 'Unknown')}</a>` : esc(row.device || row.ip || 'Unknown');
  const destination = row.destinationIp ? `${row.destinationIp}${row.destinationPort ? `:${row.destinationPort}` : ''}${row.country ? ` • ${row.country}` : ''}` : '';
  const totalBytes = Number(row.bytesDown || 0) + Number(row.bytesUp || 0);
  const sources = Array.isArray(row.sources) ? row.sources : [];
  const protocols = Array.isArray(row.protocols) ? row.protocols : [];
  const domains = Array.isArray(row.domains) ? row.domains : [];
  const canMarkSafe = row.category === 'Adult' && row.domain;
  return `<article class="timeline-item history-item">
    <div class="history-main">
      <div class="history-target-label">${esc(primaryLabel)}</div>
      <div class="primary history-target">${esc(primary)}</div>
      <div class="secondary">${device} • ${esc(row.ip || '')}${row.service && row.service !== primary ? ` • ${esc(row.service)}` : ''}${row.application ? ` • App: ${esc(row.application)}` : ''}</div>
      ${destination ? `<div class="secondary">Destination ${esc(destination)}${row.protocol ? ` • ${esc(row.protocol)}` : ''}</div>` : ''}
      <div class="alert-chips">
        <span class="badge visibility-${esc(row.visibility)}">${esc(visibilityLabel(row.visibility))}</span>
        <span class="badge${row.category === 'Adult' ? ' alert' : ''}">${esc(row.category || 'Other')}</span>
        ${row.encrypted ? '<span class="badge encrypted-badge">Encrypted</span>' : ''}
        ${row.blocked ? '<span class="badge alert">Blocked</span>' : ''}
        ${row.background ? '<span class="badge">Background service</span>' : '<span class="badge confidence-active">Likely user activity</span>'}
        ${row.eventCount > 1 ? `<span class="badge">${row.eventCount} signals merged</span>` : ''}
        ${duration(row.durationSeconds) ? `<span class="badge">${esc(duration(row.durationSeconds))}</span>` : ''}
        ${totalBytes ? `<span class="badge">${esc(bytes(totalBytes))}</span>` : ''}
        <span class="badge">Confidence ${Number(row.confidence || 0)}%</span>
        ${canMarkSafe ? `<button class="button small secondary-button" data-safe="${esc(row.domain)}">Mark not adult</button>` : ''}
      </div>
      <details class="history-evidence"><summary>Telemetry evidence</summary><div class="history-evidence-grid"><span><b>Sources:</b> ${esc(sources.join(' + ') || 'Unknown')}</span><span><b>Protocols:</b> ${esc(protocols.join(', ') || row.protocol || 'Unknown')}</span><span><b>Hostnames:</b> ${esc(domains.join(', ') || 'Not observed')}</span><span><b>Traffic:</b> ↓ ${esc(bytes(row.bytesDown))} / ↑ ${esc(bytes(row.bytesUp))}</span></div></details>
    </div>
    <time title="${esc(String(row.timestampUtc || ''))}">${esc(fmt(row.timestampUtc))}</time>
  </article>`;
}

async function loadHistory() {
  try {
    $('historyStatus').textContent = 'Correlating…';
    $('historyStatus').className = 'pill neutral';
    const [rows, summary] = await Promise.all([
      json('/api/history?' + queryString(true)),
      json('/api/history/summary?' + queryString(false))
    ]);
    $('eventCount').textContent = Number(summary.eventCount || 0).toLocaleString();
    $('signalCount').textContent = Number(summary.rawSignalCount || 0).toLocaleString();
    $('domainCount').textContent = Number(summary.uniqueDomains || 0).toLocaleString();
    $('encryptedCount').textContent = Number(summary.encryptedCount || 0).toLocaleString();
    $('deviceCount').textContent = Number(summary.activeDevices || 0).toLocaleString();
    $('resultCount').textContent = `${rows.length} results`;
    $('historyRows').innerHTML = rows.length ? rows.map(activityRow).join('') : '<div class="empty">No correlated activity for this filter.</div>';
    $('historyStatus').textContent = 'DNS + flows';
    $('historyStatus').className = 'pill ok';
  } catch (error) {
    $('historyStatus').textContent = 'Error';
    $('historyStatus').className = 'pill bad';
    $('historyRows').innerHTML = `<div class="empty">${esc(error.message)}</div>`;
  }
}

async function load() { await Promise.all([loadHistory(), loadIntel()]); }
document.addEventListener('click', event => {
  const safe = event.target.closest('[data-safe]');
  if (safe) { markSafe(safe.dataset.safe); return; }
  const remove = event.target.closest('[data-remove-safe]');
  if (remove) removeSafe(remove.dataset.removeSafe);
});
$('refresh').addEventListener('click', load);
$('exportAdult').addEventListener('click', exportAdultHistory);
['range','category','device','visibility','source','activity'].forEach(id => $(id).addEventListener('change', loadHistory));
$('search').addEventListener('keydown', event => { if (event.key === 'Enter') loadHistory(); });
loadDevices().then(load);
