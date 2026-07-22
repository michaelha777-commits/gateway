const byId = id => document.getElementById(id);

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
      container.innerHTML = '<div class="empty">No activity has been imported yet. The AdGuard worker is the next build step.</div>';
      return;
    }

    container.innerHTML = dashboard.events.map(event => `
      <article class="event-row">
        <div class="event-icon">${escapeHtml((event.deviceName || '?').slice(0, 1).toUpperCase())}</div>
        <div class="event-main">
          <strong>${escapeHtml(event.deviceName)}</strong>
          <span>${escapeHtml(event.domain)}</span>
        </div>
        <div class="event-meta">
          <span class="pill">${escapeHtml(event.category)}</span>
          <time>${new Date(event.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</time>
        </div>
      </article>`).join('');
  } catch (error) {
    byId('events').innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`;
  } finally {
    byId('refresh').disabled = false;
  }
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, char => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;'
  })[char]);
}

byId('refresh').addEventListener('click', loadDashboard);
loadDashboard();
