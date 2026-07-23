(() => {
  'use strict';

  const STORAGE_KEY = 'homewatch.hiddenReportSessionDeviceIds.v1';

  function readHiddenIds() {
    try {
      const value = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
      return new Set(Array.isArray(value) ? value.map(String) : []);
    } catch {
      return new Set();
    }
  }

  function writeHiddenIds(ids) {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify([...ids]));
    } catch {
      // Local-storage failure must never stop HomeWatch.
    }
  }

  function isDashboardRequest(input) {
    const raw = typeof input === 'string' ? input : input?.url;
    if (!raw) return false;
    try {
      const url = new URL(raw, window.location.href);
      return url.pathname === '/api/dashboard';
    } catch {
      return false;
    }
  }

  // Filter dashboard data before the existing HomeWatch scripts build reports and sessions.
  const originalFetch = window.fetch.bind(window);
  window.fetch = async (...args) => {
    const response = await originalFetch(...args);
    if (!isDashboardRequest(args[0]) || !response.ok) return response;

    try {
      const hiddenIds = readHiddenIds();
      if (!hiddenIds.size) return response;

      const data = await response.clone().json();
      if (!Array.isArray(data?.events)) return response;

      data.events = data.events.filter(event => !hiddenIds.has(String(event.deviceId || '')));
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
      console.warn('HomeWatch device display filter skipped:', error);
      return response;
    }
  };

  let selectedDeviceId = '';

  function selectedButton() {
    return document.querySelector('#deviceList [data-device-id].selected');
  }

  function updateSelectedDevice() {
    const selected = selectedButton();
    if (selected?.dataset?.deviceId) selectedDeviceId = String(selected.dataset.deviceId);
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
        <span>Browser-only display preference</span>
      </div>
      <label class="check" style="display:flex;gap:10px;align-items:center;margin-top:10px">
        <input id="hideDeviceFromReportsSessions" type="checkbox">
        Hide this device from reports and sessions
      </label>
      <p class="muted" style="margin:10px 0 0">Monitoring and alerts continue. This setting only removes the device from dashboard reports and grouped sessions in this browser.</p>`;

    const identitySection = deviceContent.querySelector(':scope > section');
    if (identitySection) identitySection.insertAdjacentElement('afterend', section);
    else deviceContent.prepend(section);

    document.getElementById('hideDeviceFromReportsSessions')?.addEventListener('change', event => {
      if (!selectedDeviceId) return;
      const hiddenIds = readHiddenIds();
      if (event.target.checked) hiddenIds.add(selectedDeviceId);
      else hiddenIds.delete(selectedDeviceId);
      writeHiddenIds(hiddenIds);
      refreshVisibleViews();
    });
  }

  function syncControl() {
    ensureControl();
    const checkbox = document.getElementById('hideDeviceFromReportsSessions');
    if (!checkbox) return;
    checkbox.disabled = !selectedDeviceId;
    checkbox.checked = Boolean(selectedDeviceId && readHiddenIds().has(selectedDeviceId));
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
      selectedDeviceId = String(button.dataset.deviceId || '');
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

    updateSelectedDevice();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();
