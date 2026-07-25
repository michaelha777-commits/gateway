(() => {
  const byId = id => document.getElementById(id);
  let currentDeviceId = null;
  let pollTimer = null;

  function ensureUi() {
    const detail = byId('deviceContent');
    if (!detail || byId('networkIntelligence')) return;
    const identity = byId('deviceSaveResult')?.closest('section');
    const section = document.createElement('section');
    section.id = 'networkIntelligence';
    section.className = 'device-intelligence';
    section.innerHTML = `
      <div class="mobile-device-hero">
        <div class="mobile-device-icon" id="mobileDeviceIcon">⌘</div>
        <div class="mobile-device-main">
          <strong id="mobileDeviceName">Device</strong>
          <span id="mobileDeviceType">Network device</span>
          <div class="mobile-device-status"><span class="status-badge" id="mobileDeviceStatus">Offline</span><span id="mobileDeviceSeen">Last seen unknown</span></div>
        </div>
      </div>
      <div class="device-tabs" role="tablist">
        <button class="active" type="button">Overview</button><button type="button" data-device-tab="activity">Activity</button><button type="button" data-device-tab="sessions">Sessions</button><button type="button">Security</button><button type="button">Timeline</button>
      </div>
      <section class="device-info-card" id="identityCard">
        <h3><span>♙</span> Identity</h3>
        <div class="device-kv" id="deviceIdentityRows"><div class="empty">Loading identity…</div></div>
      </section>
      <section class="device-info-card">
        <h3><span>⌘</span> Network</h3>
        <div class="device-kv" id="deviceNetworkRows"><div class="empty">Loading network details…</div></div>
        <div class="device-scan-actions">
          <button id="scanNetworkQuick" class="secondary">◉ Quick network scan</button>
          <button id="scanNetworkFull" class="secondary">◉ Full device fingerprint</button>
        </div>
        <div id="discoveryScanStatus" class="scan-status"></div>
      </section>
      <section class="device-info-card">
        <h3><span>▣</span> Operating System</h3>
        <div class="device-kv" id="deviceOsRows"><div class="empty">Loading operating system…</div></div>
      </section>
      <section class="device-info-card">
        <h3><span>⌕</span> Discovery</h3>
        <div id="discoveryDetails" class="device-discovery-list"><div class="empty">Select a device and run a scan.</div></div>
      </section>`;
    if (identity) identity.insertAdjacentElement('beforebegin', section); else detail.prepend(section);
    byId('scanNetworkQuick').addEventListener('click', () => startScan(false));
    byId('scanNetworkFull').addEventListener('click', () => startScan(true));
    section.querySelector('[data-device-tab="activity"]')?.addEventListener('click', () => byId('activitySearch')?.scrollIntoView({ behavior:'smooth' }));
    section.querySelector('[data-device-tab="sessions"]')?.addEventListener('click', () => document.querySelector('[data-view="sessions"]')?.click());
  }

  function row(label, value, extra = '') {
    return `<div class="device-kv-row"><span>${escapeHtml(label)}</span><strong>${escapeHtml(value || 'Unknown')}</strong>${extra}</div>`;
  }

  async function startScan(full) {
    const status = byId('discoveryScanStatus');
    status.className = 'scan-status working';
    status.textContent = full ? 'Starting full fingerprint scan…' : 'Starting quick scan…';
    try {
      const response = await fetch(`/api/discovery/scan?full=${full}`, { method:'POST' });
      const data = await response.json().catch(() => ({}));
      if (!response.ok && response.status !== 409) throw new Error(data.error || 'Could not start scan.');
      status.textContent = response.status === 409 ? 'A scan is already running.' : 'Scan started. Results will update automatically.';
      watchStatus();
    } catch (error) {
      status.className = 'scan-status error-card';
      status.textContent = `Scan failed: ${error.message}`;
    }
  }

  async function watchStatus() {
    clearInterval(pollTimer);
    const refresh = async () => {
      try {
        const response = await fetch('/api/discovery/status', { cache:'no-store' });
        if (!response.ok) return;
        const state = await response.json();
        const status = byId('discoveryScanStatus');
        const parts = [];
        if (state.running) parts.push(`Scanning (${state.lastMode})`);
        else if (state.lastCompleted) parts.push(`Last scan ${new Date(state.lastCompleted).toLocaleString()}`);
        parts.push(state.nmapAvailable ? 'Nmap enabled' : 'Built-in scanner active');
        if (state.devicesScanned) parts.push(`${state.devicesScanned} devices fingerprinted`);
        if (state.lastError) {
          status.className = 'scan-status error-card';
          parts.push(`Error: ${state.lastError}`);
        } else status.className = 'scan-status';
        status.textContent = parts.join(' · ');
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
    const selectedDevice = window.allDevices?.find?.(d => d.id === id) || null;
    const name = selectedDevice?.name || selectedDevice?.ipAddress || 'Device';
    byId('mobileDeviceName').textContent = name;
    byId('mobileDeviceStatus').textContent = selectedDevice?.online ? '● Online' : 'Offline';
    byId('mobileDeviceStatus').classList.toggle('online', Boolean(selectedDevice?.online));
    byId('mobileDeviceSeen').textContent = selectedDevice?.online ? 'Seen now' : (selectedDevice?.lastSeen ? `Last seen ${new Date(selectedDevice.lastSeen).toLocaleString()}` : 'Last seen unknown');
    try {
      const response = await fetch(`/api/devices/${encodeURIComponent(id)}/discovery`, { cache:'no-store' });
      if (response.status === 404) {
        byId('deviceIdentityRows').innerHTML = [row('Device name', name), row('IP address', selectedDevice?.ipAddress), row('MAC address', selectedDevice?.macAddress), row('Vendor', selectedDevice?.vendor)].join('');
        byId('deviceNetworkRows').innerHTML = row('Device type', 'Unknown') + row('Open ports', '0');
        byId('deviceOsRows').innerHTML = row('OS', 'Unknown');
        details.innerHTML = '<div class="empty">No fingerprint yet. Run a quick or full network scan.</div>';
        return;
      }
      if (!response.ok) throw new Error('Could not load device intelligence.');
      const data = await response.json();
      if (currentDeviceId !== id) return;
      const ports = Array.isArray(data.openPorts) ? data.openPorts : [];
      const services = Array.isArray(data.services) ? data.services : [];
      const web = Array.isArray(data.webInterfaces) ? data.webInterfaces : [];
      byId('mobileDeviceType').textContent = data.deviceType || 'Network device';
      byId('mobileDeviceIcon').textContent = /windows/i.test(data.operatingSystem || '') ? '▣' : /iphone|ipad|apple/i.test(`${data.deviceType} ${data.operatingSystem}`) ? '▯' : '⌘';
      byId('deviceIdentityRows').innerHTML = [
        row('Device name', name), row('Hostname', data.hostname), row('NetBIOS name', data.netBiosName),
        row('IP address', selectedDevice?.ipAddress), row('MAC address', selectedDevice?.macAddress), row('Vendor', data.manufacturer || selectedDevice?.vendor)
      ].join('');
      byId('deviceNetworkRows').innerHTML = [
        row('Device type', data.deviceType), row('Open ports', String(ports.length)),
        row('First seen', selectedDevice?.firstSeen ? new Date(selectedDevice.firstSeen).toLocaleString() : ''),
        row('Last seen', selectedDevice?.lastSeen ? new Date(selectedDevice.lastSeen).toLocaleString() : ''),
        row('Nmap enabled', 'Yes'), row('Last scan', data.lastScanned ? new Date(data.lastScanned).toLocaleString() : '')
      ].join('');
      const osText = data.operatingSystem || 'Unknown';
      const build = (osText.match(/\b\d+\.\d+\.\d+(?:\.\d+)?\b/) || [])[0] || '';
      byId('deviceOsRows').innerHTML = [row('OS', osText), row('OS build', build), row('Platform', /windows/i.test(osText) ? 'Windows' : 'Unknown'), row('Architecture', data.architecture || 'Unknown'), row('Last updated', data.lastScanned ? new Date(data.lastScanned).toLocaleString() : '')].join('');
      const groups = [
        ['Discovery sources', data.discoverySources],
        ['Open TCP ports', ports.length ? ports.join(', ') : 'None detected'],
        ['Detected services', services.length ? services.map(s => `${s.port}: ${[s.name,s.product,s.version,s.extraInfo].filter(Boolean).join(' ')}`).join(' | ') : 'None identified'],
        ['Web interfaces', web.length ? web.map(w => `${w.scheme}://device:${w.port} — ${w.title || w.server || `HTTP ${w.statusCode}`}`).join(' | ') : 'None detected']
      ];
      details.innerHTML = groups.map(([label,value]) => `<article><strong>${escapeHtml(label)}</strong><span>${escapeHtml(value || 'Unknown')}</span></article>`).join('');
    } catch (error) {
      details.innerHTML = `<div class="scan-status error-card">${escapeHtml(error.message)}</div>`;
    }
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