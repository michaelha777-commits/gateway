(() => {
  'use strict';

  const DEVICE_MARKER_SUFFIX = '.homewatch-device.local';
  const hiddenIds = new Set();
  const originalFetch = window.fetch.bind(window);
  let hiddenReady = loadHiddenIds();

  function markerFor(id) {
    return `device-${String(id || '').toLowerCase()}${DEVICE_MARKER_SUFFIX}`;
  }

  function idFromMarker(value) {
    const text = String(value || '').toLowerCase();
    if (!text.startsWith('device-') || !text.endsWith(DEVICE_MARKER_SUFFIX)) return '';
    return text.slice(7, -DEVICE_MARKER_SUFFIX.length);
  }

  async function loadHiddenIds() {
    try {
      const response = await originalFetch('/api/ignored-domains', { cache: 'no-store' });
      if (!response.ok) return;
      const data = await response.json();
      hiddenIds.clear();
      (data.exact || []).map(idFromMarker).filter(Boolean).forEach(id => hiddenIds.add(id));
    } catch (error) {
      console.warn('Could not load shared ignored devices:', error);
    }
  }

  async function setDeviceHidden(id, hidden) {
    const marker = markerFor(id);
    const response = hidden
      ? await originalFetch('/api/ignored-domains', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ domain: marker, mode: 'exact' })
        })
      : await originalFetch(`/api/ignored-domains?domain=${encodeURIComponent(marker)}&mode=exact`, { method: 'DELETE' });

    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || 'Could not update the shared device preference.');
    hiddenReady = loadHiddenIds();
    await hiddenReady;
  }

  function isDashboardRequest(input) {
    const raw = typeof input === 'string' ? input : input?.url;
    if (!raw) return false;
    try {
      return new URL(raw, window.location.href).pathname === '/api/dashboard';
    } catch {
      return false;
    }
  }

  // Filter dashboard data before the existing HomeWatch scripts build reports and sessions.
  window.fetch = async (...args) => {
    const response = await originalFetch(...args);
    if (!isDashboardRequest(args[0]) || !response.ok) return response;

    try {
      await hiddenReady;
      if (!hiddenIds.size) return response;

      const data = await response.clone().json();
      if (!Array.isArray(data?.events)) return response;

      data.events = data.events.filter(event => !hiddenIds.has(String(event.deviceId || '').toLowerCase()));
      if (data.summary && typeof data.summary === 'object') {
        data.summary.eventCount = data.events.length;
        data.summary.activeDevices = new Set(data.events.map(event => String(event.deviceId || '')).filter(Boolean)).size;
      }

      const headers = new Headers(response.headers);
      headers.set('content-type', 'application/json; charset=utf-8');
      return new Response(JSON.stringify(data), {
        status: response.status,
        statusText: response.statusText,
        headers
      });
    } catch (error) {
      console.warn('HomeWatch shared device display filter skipped:', error);
      return response;
    }
  };

  let selectedDeviceId = '';

  function selectedButton() {
    return document.querySelector('#deviceList [data-device-id].selected');
  }

  function updateSelectedDevice() {
    const selected = selectedButton();
    if (selected?.dataset?.deviceId) selectedDeviceId = String(selected.dataset.deviceId).toLowerCase();
    syncControl();
  }

  function ensureControl() {
    const deviceContent = document.getElementById('deviceContent');
    if (!deviceContent || document.getElementById('hideDeviceFromReportsSessions')) return;

    const section = document.createElement('section');
    section.id = 'deviceReportSessionVisibility';
    section.style.padding = '16px 20px';
    section.style.borderBottom = '1px solid #1d344f';
    section.innerHTML = `
      <div class="subheading">
        <h3>Reports and sessions</h3>
        <span>Shared HomeWatch preference</span>
      </div>
      <label class="check" style="display:flex;gap:10px;align-items:center;margin-top:10px">
        <input id="hideDeviceFromReportsSessions" type="checkbox">
        Hide this device from reports and sessions
      </label>
      <p class="muted" style="margin:10px 0 0">Monitoring and alerts continue. This preference is stored by HomeWatch and applies to every phone, tablet, and computer that opens this HomeWatch server.</p>
      <p id="sharedDeviceVisibilityResult" class="muted" style="margin:8px 0 0"></p>`;

    const identitySection = deviceContent.querySelector(':scope > section');
    if (identitySection) identitySection.insertAdjacentElement('afterend', section);
    else deviceContent.prepend(section);

    document.getElementById('hideDeviceFromReportsSessions')?.addEventListener('change', async event => {
      if (!selectedDeviceId) return;
      const checkbox = event.target;
      const result = document.getElementById('sharedDeviceVisibilityResult');
      checkbox.disabled = true;
      if (result) result.textContent = 'Saving shared preference…';
      try {
        await setDeviceHidden(selectedDeviceId, checkbox.checked);
        if (result) result.textContent = 'Saved across HomeWatch.';
        refreshVisibleViews();
      } catch (error) {
        checkbox.checked = !checkbox.checked;
        if (result) result.textContent = `Save failed: ${error.message}`;
      } finally {
        checkbox.disabled = false;
      }
    });
  }

  async function syncControl() {
    ensureControl();
    await hiddenReady;
    const checkbox = document.getElementById('hideDeviceFromReportsSessions');
    if (!checkbox) return;
    checkbox.disabled = !selectedDeviceId;
    checkbox.checked = Boolean(selectedDeviceId && hiddenIds.has(selectedDeviceId));
  }

  function refreshVisibleViews() {
    const dashboard = document.getElementById('dashboardView');
    const sessions = document.getElementById('sessionsView');
    if (dashboard?.classList.contains('active')) document.getElementById('refresh')?.click();
    if (sessions?.classList.contains('active')) document.getElementById('refreshSessions')?.click();
  }

  function start() {
    ensureControl();

    document.getElementById('deviceList')?.addEventListener('click', event => {
      const button = event.target.closest('[data-device-id]');
      if (!button) return;
      selectedDeviceId = String(button.dataset.deviceId || '').toLowerCase();
      setTimeout(syncControl, 0);
    });

    const deviceList = document.getElementById('deviceList');
    if (deviceList) {
      new MutationObserver(updateSelectedDevice).observe(deviceList, {
        childList: true,
        subtree: true,
        attributes: true,
        attributeFilter: ['class']
      });
    }

    setInterval(async () => {
      hiddenReady = loadHiddenIds();
      await hiddenReady;
      syncControl();
      refreshVisibleViews();
    }, 30000);

    updateSelectedDevice();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();