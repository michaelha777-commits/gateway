const byId = id => document.getElementById(id);
let allDevices = [];
let selectedDeviceId = null;
let activitySearchTimer = null;

async function loadDashboard() {
  byId('refresh').disabled = true;
  try {
    const [statusResponse, dashboardResponse] = await Promise.all([
      fetch('/api/status', { cache: 'no-store' }),
      fetch('/api/dashboard?hours=24', { cache: 'no-store' })
    ]);

    if (!statusResponse.ok || !dashboardResponse.ok) throw new Error('HomeWatch API did not respond correctly.');

    const status = await statusResponse.json();
    const dashboard = await dashboardResponse.json();
    byId('version').textContent = `v${status.version}`;
    byId('deviceCount').textContent = dashboard.summary.deviceCount;
    byId('activeDevices').textContent = dashboard.summary.activeDevices;
    byId('alertCount').textContent = dashboard.summary.alertCount;
    byId('eventCount').textContent = dashboard.summary.eventCount;
    byId('updated').textContent = `Updated ${new Date(dashboard.generatedAt).toLocaleTimeString()}`;

    const container = byId('events');
    if (!dashboard.events.length) {
      container.innerHTML = '<div class="empty">No activity has been imported yet.</div>';
      return;
    }

    container.innerHTML = dashboard.events.map(event => eventRow(event, true)).join('');
  } catch (error) {
    byId('events').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`;
  } finally {
    byId('refresh').disabled = false;
  }
}

async function loadDevices(preserveSelection = true) {
  byId('refreshDevices').disabled = true;
  const hours = byId('deviceHours').value;
  try {
    const response = await fetch(`/api/devices?hours=${encodeURIComponent(hours)}`, { cache: 'no-store' });
    if (!response.ok) throw new Error('Could not load devices.');
    const data = await response.json();
    allDevices = data.devices;
    renderDeviceList();

    if (preserveSelection && selectedDeviceId && allDevices.some(device => device.id === selectedDeviceId)) {
      await loadDeviceActivity(selectedDeviceId);
    } else if (allDevices.length) {
      await selectDevice(allDevices[0].id);
    }
  } catch (error) {
    byId('deviceList').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`;
  } finally {
    byId('refreshDevices').disabled = false;
  }
}

function renderDeviceList() {
  const search = byId('deviceSearch').value.trim().toLowerCase();
  const devices = allDevices.filter(device =>
    !search || [device.name, device.ipAddress, device.vendor].some(value => String(value || '').toLowerCase().includes(search))
  );

  if (!devices.length) {
    byId('deviceList').innerHTML = '<div class="empty">No matching devices.</div>';
    return;
  }

  byId('deviceList').innerHTML = devices.map(device => {
    const name = friendlyDeviceName(device);
    const domains = (device.topDomains || []).slice(0, 3).map(item => item.domain).join(' · ');
    return `
      <button class="device-card ${device.id === selectedDeviceId ? 'selected' : ''}" data-device-id="${escapeHtml(device.id)}">
        <span class="device-avatar">${escapeHtml(name.slice(0, 1).toUpperCase())}</span>
        <span class="device-card-main">
          <strong>${escapeHtml(name)}</strong>
          <small>${escapeHtml(device.ipAddress || 'No IP')} · ${Number(device.eventCount || 0)} requests</small>
          <span>${escapeHtml(domains || 'No recent activity')}</span>
        </span>
        <span class="online-dot ${device.online ? 'online' : ''}" title="${device.online ? 'Online' : 'Offline'}"></span>
      </button>`;
  }).join('');

  document.querySelectorAll('[data-device-id]').forEach(button => {
    button.addEventListener('click', () => selectDevice(button.dataset.deviceId));
  });
}

async function selectDevice(id) {
  selectedDeviceId = id;
  renderDeviceList();
  byId('deviceEmpty').hidden = true;
  byId('deviceContent').hidden = false;
  await loadDeviceActivity(id);
}

async function loadDeviceActivity(id = selectedDeviceId) {
  if (!id) return;
  const hours = byId('activityHours').value;
  const search = byId('activitySearch').value.trim();
  byId('deviceEvents').innerHTML = '<div class="empty">Loading activity…</div>';

  try {
    const url = `/api/devices/${encodeURIComponent(id)}/activity?hours=${encodeURIComponent(hours)}&limit=1000&search=${encodeURIComponent(search)}`;
    const response = await fetch(url, { cache: 'no-store' });
    if (!response.ok) throw new Error(response.status === 404 ? 'Device no longer exists.' : 'Could not load device activity.');
    const data = await response.json();
    const device = data.device;

    byId('selectedDeviceName').textContent = friendlyDeviceName(device);
    byId('selectedDeviceMeta').textContent = [device.ipAddress, device.vendor, `Last seen ${formatRelative(device.lastSeen)}`].filter(Boolean).join(' · ');
    byId('deviceStatus').textContent = device.online ? 'Online' : 'Offline';
    byId('deviceStatus').classList.toggle('online', device.online);
    byId('selectedEventCount').textContent = data.summary.eventCount;
    byId('selectedDomainCount').textContent = data.summary.uniqueDomains;
    byId('activityUpdated').textContent = `Updated ${new Date(data.generatedAt).toLocaleTimeString()}`;

    byId('topDomains').innerHTML = data.topDomains.length
      ? data.topDomains.map(item => `<button class="domain-chip" data-domain="${escapeHtml(item.domain)}"><span>${escapeHtml(item.domain)}</span><strong>${item.count}</strong></button>`).join('')
      : '<span class="muted">No domains for this period.</span>';

    document.querySelectorAll('[data-domain]').forEach(button => {
      button.addEventListener('click', () => {
        byId('activitySearch').value = button.dataset.domain;
        loadDeviceActivity();
      });
    });

    byId('deviceEvents').innerHTML = data.events.length
      ? data.events.map(event => eventRow({ ...event, deviceName: friendlyDeviceName(device) }, false)).join('')
      : '<div class="empty">No website activity matches this filter.</div>';
  } catch (error) {
    byId('deviceEvents').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`;
  }
}

function eventRow(event, showDevice) {
  return `
    <article class="event-row">
      <div class="event-icon">${escapeHtml((event.deviceName || '?').slice(0, 1).toUpperCase())}</div>
      <div class="event-main">
        ${showDevice ? `<strong>${escapeHtml(event.deviceName)}</strong>` : ''}
        <span class="domain-name">${escapeHtml(event.domain)}</span>
      </div>
      <div class="event-meta">
        <span class="pill">${escapeHtml(event.category || 'dns')}</span>
        <time title="${escapeHtml(new Date(event.timestamp).toLocaleString())}">${formatEventTime(event.timestamp)}</time>
      </div>
    </article>`;
}

function friendlyDeviceName(device) {
  const name = String(device.name || '').trim();
  if (name && name !== device.ipAddress) return name;
  return device.ipAddress || 'Unknown device';
}

function formatEventTime(value) {
  const date = new Date(value);
  const today = new Date();
  return date.toDateString() === today.toDateString()
    ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
    : date.toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

function formatRelative(value) {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return new Date(value).toLocaleString();
}

function switchView(name) {
  document.querySelectorAll('.view').forEach(view => view.classList.toggle('active', view.id === `${name}View`));
  document.querySelectorAll('.nav button').forEach(button => button.classList.toggle('active', button.dataset.view === name));
  if (name === 'devices') loadDevices();
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, char => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;'
  })[char]);
}

document.querySelectorAll('.nav button').forEach(button => button.addEventListener('click', () => switchView(button.dataset.view)));
byId('refresh').addEventListener('click', loadDashboard);
byId('refreshDevices').addEventListener('click', () => loadDevices(true));
byId('deviceSearch').addEventListener('input', renderDeviceList);
byId('deviceHours').addEventListener('change', () => loadDevices(true));
byId('activityHours').addEventListener('change', () => loadDeviceActivity());
byId('activitySearch').addEventListener('input', () => {
  clearTimeout(activitySearchTimer);
  activitySearchTimer = setTimeout(() => loadDeviceActivity(), 300);
});

loadDashboard();
setInterval(() => {
  if (byId('dashboardView').classList.contains('active')) loadDashboard();
  if (byId('devicesView').classList.contains('active')) loadDevices(true);
}, 10000);