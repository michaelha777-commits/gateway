(() => {
  const byId = id => document.getElementById(id);
  let currentDeviceId = null;
  let pollTimer = null;

  function ensureUi() {
    const detail = document.querySelector('#deviceContent');
    if (!detail || byId('networkIntelligence')) return;
    const identity = byId('deviceSaveResult')?.closest('section');
    const section = document.createElement('section');
    section.id = 'networkIntelligence';
    section.style.cssText = 'padding:18px 20px;border-bottom:1px solid #1d344f';
    section.innerHTML = `
      <div class="subheading"><h3>Network intelligence</h3><span id="discoveryLastScan">Not scanned</span></div>
      <div style="display:flex;gap:10px;flex-wrap:wrap;margin:12px 0">
        <button id="scanNetworkQuick" class="secondary">Quick network scan</button>
        <button id="scanNetworkFull" class="secondary">Full device fingerprint</button>
        <span id="discoveryScanStatus" class="muted"></span>
      </div>
      <div id="discoverySummary" class="mini-metrics" style="margin:10px 0 14px"></div>
      <div id="discoveryDetails" class="event-list compact"><div class="empty">Select a device and run a scan.</div></div>`;
    if (identity?.nextSibling) identity.parentNode.insertBefore(section, identity.nextSibling); else detail.prepend(section);
    byId('scanNetworkQuick').addEventListener('click', () => startScan(false));
    byId('scanNetworkFull').addEventListener('click', () => startScan(true));
  }

  async function startScan(full) {
    const status = byId('discoveryScanStatus');
    status.textContent = full ? 'Starting full fingerprint scan…' : 'Starting quick scan…';
    try {
      const response = await fetch(`/api/discovery/scan?full=${full}`, { method: 'POST' });
      const data = await response.json().catch(() => ({}));
      if (!response.ok && response.status !== 409) throw new Error(data.error || 'Could not start scan.');
      status.textContent = response.status === 409 ? 'A scan is already running.' : 'Scan started. Results will update automatically.';
      watchStatus();
    } catch (error) { status.textContent = `Scan failed: ${error.message}`; }
  }

  async function watchStatus() {
    clearInterval(pollTimer);
    const refresh = async () => {
      try {
        const response = await fetch('/api/discovery/status', { cache: 'no-store' });
        if (!response.ok) return;
        const state = await response.json();
        const parts = [];
        if (state.running) parts.push(`Scanning (${state.lastMode})`);
        else if (state.lastCompleted) parts.push(`Last scan ${new Date(state.lastCompleted).toLocaleString()}`);
        parts.push(state.nmapAvailable ? 'Nmap enabled' : 'Built-in scanner active; Nmap not detected');
        if (state.devicesScanned) parts.push(`${state.devicesScanned} devices fingerprinted`);
        if (state.lastError) parts.push(`Error: ${state.lastError}`);
        byId('discoveryScanStatus').textContent = parts.join(' · ');
        if (!state.running) {
          clearInterval(pollTimer);
          pollTimer = null;
          await loadDiscovery();
        }
      } catch { }
    };
    await refresh();
    pollTimer = setInterval(refresh, 3000);
  }

  async function loadDiscovery() {
    ensureUi();
    const selected = document.querySelector('.device-card.selected');
    const id = selected?.dataset.deviceId;
    if (!id) return;
    currentDeviceId = id;
    const details = byId('discoveryDetails');
    try {
      const response = await fetch(`/api/devices/${encodeURIComponent(id)}/discovery`, { cache: 'no-store' });
      if (response.status === 404) {
        details.innerHTML = '<div class="empty">No fingerprint yet. Run a quick or full network scan.</div>';
        return;
      }
      if (!response.ok) throw new Error('Could not load device intelligence.');
      const data = await response.json();
      if (currentDeviceId !== id) return;
      byId('discoveryLastScan').textContent = data.lastScanned ? new Date(data.lastScanned).toLocaleString() : 'Not scanned';
      const ports = Array.isArray(data.openPorts) ? data.openPorts : [];
      const services = Array.isArray(data.services) ? data.services : [];
      const web = Array.isArray(data.webInterfaces) ? data.webInterfaces : [];
      byId('discoverySummary').innerHTML = `
        <article><span>Device type</span><strong>${escapeHtml(data.deviceType || 'Unknown')}</strong></article>
        <article><span>Open ports</span><strong>${ports.length}</strong></article>
        <article><span>Operating system</span><strong>${escapeHtml(data.operatingSystem || 'Unknown')}</strong></article>`;
      const rows = [
        ['Hostname', data.hostname], ['NetBIOS name', data.netBiosName], ['Manufacturer', data.manufacturer],
        ['Device type', data.deviceType], ['Operating system', data.operatingSystem], ['Discovery sources', data.discoverySources],
        ['Open TCP ports', ports.length ? ports.join(', ') : 'None detected'],
        ['Detected services', services.length ? services.map(s => `${s.port}: ${[s.name,s.product,s.version,s.extraInfo].filter(Boolean).join(' ')}`).join(' | ') : 'None identified'],
        ['Web interfaces', web.length ? web.map(w => `${w.scheme}://device:${w.port} — ${w.title || w.server || `HTTP ${w.statusCode}`}`).join(' | ') : 'None detected']
      ];
      details.innerHTML = rows.map(([label,value]) => `<article class="event-row site-list-row"><div class="event-main"><strong>${escapeHtml(label)}</strong><span>${escapeHtml(value || 'Unknown')}</span></div></article>`).join('');
    } catch (error) { details.innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`; }
  }

  function escapeHtml(value) { return String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c])); }

  document.addEventListener('click', event => {
    const card = event.target.closest?.('.device-card');
    if (card) setTimeout(loadDiscovery, 150);
    const nav = event.target.closest?.('[data-view="devices"]');
    if (nav) setTimeout(() => { ensureUi(); loadDiscovery(); watchStatus(); }, 200);
  });
  setInterval(() => {
    if (document.querySelector('#devicesView.active')) {
      ensureUi();
      const id = document.querySelector('.device-card.selected')?.dataset.deviceId;
      if (id && id !== currentDeviceId) loadDiscovery();
    }
  }, 2000);
})();
