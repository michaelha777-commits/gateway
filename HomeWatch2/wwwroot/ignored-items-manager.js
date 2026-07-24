(() => {
  'use strict';

  const DEVICE_MARKER_SUFFIX = '.homewatch-device.local';
  const byId = id => document.getElementById(id);
  const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));

  function deviceIdFromMarker(value) {
    const text = String(value || '').toLowerCase();
    if (!text.startsWith('device-') || !text.endsWith(DEVICE_MARKER_SUFFIX)) return '';
    return text.slice(7, -DEVICE_MARKER_SUFFIX.length);
  }

  async function api(path, options = {}) {
    const response = await fetch(path, { cache: 'no-store', ...options });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || `Request failed (${response.status})`);
    return data;
  }

  function ensurePanels() {
    const view = byId('settingsView');
    if (!view || byId('ignoredItemsManager')) return;

    const section = document.createElement('section');
    section.id = 'ignoredItemsManager';
    section.innerHTML = `
      <section class="panel" style="margin-top:18px;padding:22px">
        <div class="subheading"><h3>Ignored devices</h3><span>Restore visibility at any time</span></div>
        <p class="muted">These devices are hidden from reports and sessions. Monitoring and alerts continue.</p>
        <div id="ignoredDevicesList"><p class="muted">Loading ignored devices…</p></div>
      </section>
      <section class="panel" style="margin-top:18px;padding:22px">
        <div class="subheading"><h3>Ignored domains</h3><span>Shared across HomeWatch</span></div>
        <p class="muted">These exact domains or domain families are hidden from the dashboard, alerts, sessions and device activity.</p>
        <div id="ignoredDomainsManagerList"><p class="muted">Loading ignored domains…</p></div>
      </section>`;

    view.appendChild(section);
  }

  function row(title, subtitle, value, mode, type) {
    return `<div style="display:flex;justify-content:space-between;gap:12px;align-items:center;padding:11px 0;border-bottom:1px solid #1d344f">
      <div><strong>${escapeHtml(title)}</strong><br><small>${escapeHtml(subtitle)}</small></div>
      <button type="button" data-restore-ignored="${escapeHtml(value)}" data-restore-mode="${escapeHtml(mode)}" data-restore-type="${escapeHtml(type)}">Restore</button>
    </div>`;
  }

  async function loadManager() {
    ensurePanels();
    const devicesList = byId('ignoredDevicesList');
    const domainsList = byId('ignoredDomainsManagerList');
    if (!devicesList || !domainsList) return;

    try {
      const [ignored, deviceData] = await Promise.all([
        api('/api/ignored-domains'),
        api('/api/devices?hours=720').catch(() => ({ devices: [] }))
      ]);

      const devices = Array.isArray(deviceData.devices) ? deviceData.devices : [];
      const devicesById = new Map(devices.map(device => [String(device.id || '').toLowerCase(), device]));
      const exact = Array.isArray(ignored.exact) ? ignored.exact : [];
      const families = Array.isArray(ignored.families) ? ignored.families : [];

      const deviceMarkers = exact
        .map(value => ({ value, id: deviceIdFromMarker(value) }))
        .filter(item => item.id);

      const domainRows = [
        ...exact.filter(value => !deviceIdFromMarker(value)).map(value => ({ value, mode: 'exact' })),
        ...families.map(value => ({ value, mode: 'family' }))
      ];

      devicesList.innerHTML = deviceMarkers.length
        ? deviceMarkers.map(item => {
            const device = devicesById.get(item.id);
            const title = device?.name || `Device ${item.id.slice(0, 8)}`;
            const details = [device?.ipAddress, device?.macAddress].filter(Boolean).join(' · ') || `Device ID: ${item.id}`;
            return row(title, details, item.value, 'exact', 'device');
          }).join('')
        : '<p class="muted">No ignored devices.</p>';

      domainsList.innerHTML = domainRows.length
        ? domainRows.map(item => row(item.value, item.mode === 'family' ? 'Entire domain family' : 'Exact domain', item.value, item.mode, 'domain')).join('')
        : '<p class="muted">No ignored domains.</p>';
    } catch (error) {
      devicesList.innerHTML = `<p class="error">${escapeHtml(error.message)}</p>`;
      domainsList.innerHTML = `<p class="error">${escapeHtml(error.message)}</p>`;
    }
  }

  async function restore(button) {
    const value = button.dataset.restoreIgnored;
    const mode = button.dataset.restoreMode || 'exact';
    const type = button.dataset.restoreType || 'item';
    button.disabled = true;
    button.textContent = 'Restoring…';
    try {
      await api(`/api/ignored-domains?domain=${encodeURIComponent(value)}&mode=${encodeURIComponent(mode)}`, { method: 'DELETE' });
      await loadManager();
      byId('refresh')?.click();
      byId('refreshSessions')?.click();
      if (type === 'device') byId('refreshDevices')?.click();
    } catch (error) {
      button.disabled = false;
      button.textContent = 'Restore';
      alert(error.message);
    }
  }

  async function synchronizeVersion() {
    try {
      const status = await api('/api/status');
      const version = status.version || 'unknown';
      if (byId('version')) byId('version').textContent = `v${version}`;
      document.title = `HomeWatch ${version}`;
    } catch {}
    byId('homewatchVersion')?.remove();
  }

  function start() {
    ensurePanels();
    synchronizeVersion();

    document.addEventListener('click', event => {
      const restoreButton = event.target.closest('[data-restore-ignored]');
      if (restoreButton) restore(restoreButton);
    });

    document.querySelector('.nav [data-view="settings"]')?.addEventListener('click', () => setTimeout(loadManager, 0));

    const observer = new MutationObserver(() => byId('homewatchVersion')?.remove());
    observer.observe(document.body, { childList: true, subtree: true });

    setInterval(() => {
      synchronizeVersion();
      if (byId('settingsView')?.classList.contains('active')) loadManager();
    }, 30000);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();
