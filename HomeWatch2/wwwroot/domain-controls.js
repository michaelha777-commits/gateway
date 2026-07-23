(() => {
  'use strict';

  const byId = id => document.getElementById(id);
  const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  const ignoredExact = new Set();
  const ignoredFamilies = new Set();
  const originalFetch = window.fetch.bind(window);
  const DEVICE_MARKER_SUFFIX = '.homewatch-device.local';
  let ignoredReady = loadIgnoredDomains();

  function normalize(value) { return String(value || '').trim().toLowerCase().replace(/^https?:\/\//, '').split('/')[0].replace(/^\.+|\.+$/g, ''); }
  function family(domain) { const parts = normalize(domain).split('.').filter(Boolean); return parts.length > 1 ? parts.slice(-2).join('.') : normalize(domain); }
  function ignored(domain) {
    const d = normalize(domain); if (!d) return false;
    if (ignoredExact.has(d)) return true;
    return [...ignoredFamilies].some(root => d === root || d.endsWith('.' + root));
  }
  function domainFromAlert(alert) {
    const text = String(alert?.detail || '');
    const match = text.match(/(?:domain|website):\s*([^\s]+)/i);
    return normalize(match?.[1] || '');
  }

  async function loadIgnoredDomains() {
    try {
      const response = await originalFetch('/api/ignored-domains', { cache: 'no-store' });
      if (!response.ok) return;
      const data = await response.json();
      ignoredExact.clear(); ignoredFamilies.clear();
      (data.exact || []).map(normalize).filter(Boolean).forEach(x => ignoredExact.add(x));
      (data.families || []).map(normalize).filter(Boolean).forEach(x => ignoredFamilies.add(x));
    } catch {}
  }

  window.fetch = async (...args) => {
    const response = await originalFetch(...args);
    const raw = typeof args[0] === 'string' ? args[0] : args[0]?.url;
    if (!raw || !response.ok) return response;
    let url;
    try { url = new URL(raw, location.href); } catch { return response; }
    const filterEvents = ['/api/dashboard'].includes(url.pathname) || /^\/api\/devices\/[^/]+\/activity$/.test(url.pathname);
    const filterAlerts = url.pathname === '/api/alerts';
    if (!filterEvents && !filterAlerts) return response;
    try {
      await ignoredReady;
      const data = await response.clone().json();
      if (filterEvents && Array.isArray(data?.events)) {
        data.events = data.events.filter(event => !ignored(event.domain));
        if (data.summary) {
          if ('eventCount' in data.summary) data.summary.eventCount = data.events.length;
          if ('uniqueDomains' in data.summary) data.summary.uniqueDomains = new Set(data.events.map(x => normalize(x.domain))).size;
        }
        if (Array.isArray(data.topDomains)) data.topDomains = data.topDomains.filter(x => !ignored(x.domain));
      }
      if (filterAlerts && Array.isArray(data?.alerts)) data.alerts = data.alerts.filter(alert => !ignored(domainFromAlert(alert)));
      const headers = new Headers(response.headers); headers.set('content-type', 'application/json; charset=utf-8');
      return new Response(JSON.stringify(data), { status: response.status, statusText: response.statusText, headers });
    } catch { return response; }
  };

  async function ignoreDomain(domain, mode) {
    const d = normalize(domain); if (!d) return;
    const selectedMode = mode === 'family' ? 'family' : 'exact';
    try {
      const response = await originalFetch('/api/ignored-domains', {
        method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ domain: d, mode: selectedMode })
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error || 'Could not ignore this domain.');
      ignoredReady = loadIgnoredDomains(); await ignoredReady;
      refreshViews(); renderIgnoredManager();
    } catch (error) { alert(error.message); }
  }

  async function ignoreSessionDevice(button) {
    const card = button.closest('.session-card');
    if (!card) return;
    const deviceIp = String(card.querySelector('.session-device small')?.textContent || '').trim();
    const deviceName = String(card.querySelector('.session-device strong')?.textContent || '').trim();
    button.disabled = true;
    button.textContent = 'Ignoring…';
    try {
      const response = await originalFetch('/api/devices?hours=720', { cache: 'no-store' });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error || 'Could not load devices.');
      const devices = Array.isArray(data.devices) ? data.devices : [];
      const device = devices.find(x => deviceIp && String(x.ipAddress || '').trim() === deviceIp)
        || devices.find(x => deviceName && String(x.name || '').trim() === deviceName);
      if (!device?.id) throw new Error('HomeWatch could not identify this device.');

      const marker = `device-${String(device.id).toLowerCase()}${DEVICE_MARKER_SUFFIX}`;
      const save = await originalFetch('/api/ignored-domains', {
        method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ domain: marker, mode: 'exact' })
      });
      const saved = await save.json().catch(() => ({}));
      if (!save.ok) throw new Error(saved.error || 'Could not ignore this device.');
      document.getElementById('refreshSessions')?.click();
      document.getElementById('refresh')?.click();
    } catch (error) {
      button.disabled = false;
      button.textContent = 'Ignore device';
      alert(error.message);
    }
  }

  async function restoreDomain(value, mode) {
    try {
      const response = await originalFetch(`/api/ignored-domains?domain=${encodeURIComponent(value)}&mode=${encodeURIComponent(mode)}`, { method: 'DELETE' });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error || 'Could not restore this domain.');
      ignoredReady = loadIgnoredDomains(); await ignoredReady;
      refreshViews(); renderIgnoredManager();
    } catch (error) { alert(error.message); }
  }
  function refreshViews() {
    if (byId('dashboardView')?.classList.contains('active')) byId('refresh')?.click();
    if (byId('sessionsView')?.classList.contains('active')) byId('refreshSessions')?.click();
    if (byId('devicesView')?.classList.contains('active')) byId('activityHours')?.dispatchEvent(new Event('change'));
    if (byId('alertsView')?.classList.contains('active')) document.querySelector('[data-view="alerts"]')?.click();
  }

  function domainFromCard(card) {
    const candidates = [...card.querySelectorAll('strong,.domain-name')].map(x => normalize(x.textContent));
    return candidates.find(x => x.includes('.')) || '';
  }

  function addControls() {
    document.querySelectorAll('#sessionList .session-card, #deviceEvents .site-list-row').forEach(card => {
      if (card.dataset.domainControls === '1') return;
      const domain = domainFromCard(card); if (!domain) return;
      card.dataset.domainControls = '1';
      const controls = document.createElement('div');
      controls.className = 'domain-controls';
      controls.style.cssText = 'display:flex;gap:8px;flex-wrap:wrap;margin-top:9px';
      const ignoreDevice = card.matches('#sessionList .session-card') ? '<button type="button" data-ignore-session-device>Ignore device</button>' : '';
      controls.innerHTML = `<button type="button" data-domain-details="${escapeHtml(domain)}">Details</button><button type="button" data-ignore-exact="${escapeHtml(domain)}">Ignore domain</button>${ignoreDevice}<button type="button" data-ignore-family="${escapeHtml(domain)}">Ignore ${escapeHtml(family(domain))}</button>`;
      (card.querySelector('.session-domain,.event-main') || card).appendChild(controls);
    });
  }

  async function showDetails(domain) {
    ensureDrawer();
    const drawer = byId('domainDetailsDrawer');
    drawer.hidden = false;
    byId('domainDetailsBody').innerHTML = `<p class="muted">Loading intelligence for ${escapeHtml(domain)}…</p>`;
    try {
      const response = await originalFetch(`/api/domain-intelligence?domain=${encodeURIComponent(domain)}`, { cache:'no-store' });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error || 'Intelligence lookup failed.');
      const vt = data.virusTotal || {}; const scan = data.urlscan || {};
      const risk = Number(vt.malicious || 0) > 0 ? 'Potentially malicious' : Number(vt.suspicious || 0) > 0 ? 'Suspicious' : vt.available ? 'No malicious detections' : 'Not yet rated';
      byId('domainDetailsBody').innerHTML = `
        <h2 style="margin-top:0">${escapeHtml(domain)}</h2>
        <p><strong>Domain family:</strong> ${escapeHtml(family(domain))}</p>
        <p><strong>Risk:</strong> ${escapeHtml(risk)}</p>
        <p><strong>VirusTotal:</strong> ${vt.available ? `${Number(vt.malicious||0)} malicious · ${Number(vt.suspicious||0)} suspicious · ${Number(vt.harmless||0)} harmless` : 'Unavailable or API key not configured'}</p>
        <p><strong>Categories:</strong> ${escapeHtml(Array.isArray(vt.categories) && vt.categories.length ? vt.categories.join(', ') : 'None reported')}</p>
        <p><strong>urlscan title:</strong> ${escapeHtml(scan.title || 'No previous scan found')}</p>
        <p><strong>Hosting:</strong> ${escapeHtml([scan.ip, scan.server, scan.country].filter(Boolean).join(' · ') || 'Not reported')}</p>
        <div style="display:flex;gap:8px;flex-wrap:wrap;margin-top:16px"><button data-ignore-exact="${escapeHtml(domain)}">Ignore domain</button><button data-ignore-family="${escapeHtml(domain)}">Ignore ${escapeHtml(family(domain))}</button></div>`;
    } catch (error) { byId('domainDetailsBody').innerHTML = `<p class="error">${escapeHtml(error.message)}</p>`; }
  }

  function ensureDrawer() {
    if (byId('domainDetailsDrawer')) return;
    const drawer = document.createElement('aside'); drawer.id = 'domainDetailsDrawer'; drawer.hidden = true;
    drawer.style.cssText = 'position:fixed;right:0;top:0;bottom:0;width:min(460px,92vw);z-index:1000;background:#0b1726;border-left:1px solid #29435f;padding:22px;overflow:auto;box-shadow:-10px 0 30px rgba(0,0,0,.35)';
    drawer.innerHTML = `<button id="closeDomainDetails" type="button" style="float:right">Close</button><div id="domainDetailsBody" style="clear:both;padding-top:15px"></div>`;
    document.body.appendChild(drawer); byId('closeDomainDetails').addEventListener('click', () => drawer.hidden = true);
  }

  function ensureIgnoredManager() {
    const view = byId('settingsView'); if (!view || byId('ignoredDomainsPanel')) return;
    const panel = document.createElement('section'); panel.id = 'ignoredDomainsPanel'; panel.className = 'panel'; panel.style.cssText = 'margin-top:18px;padding:22px';
    panel.innerHTML = `<div class="subheading"><h3>Ignored domains</h3><span>Shared across every HomeWatch device</span></div><p class="muted">Ignored domains are stored by HomeWatch and hidden from dashboard, alerts, sessions and device activity on every browser.</p><div id="ignoredDomainsList"></div>`;
    view.appendChild(panel); renderIgnoredManager();
  }
  function renderIgnoredManager() {
    const list = byId('ignoredDomainsList'); if (!list) return;
    const exact = [...ignoredExact].filter(value => !value.endsWith(DEVICE_MARKER_SUFFIX)).map(value => ({value,mode:'exact'}));
    const families = [...ignoredFamilies].map(value => ({value,mode:'family'}));
    const rows = [...exact,...families];
    list.innerHTML = rows.length ? rows.map(x => `<div style="display:flex;justify-content:space-between;gap:12px;align-items:center;padding:10px 0;border-bottom:1px solid #1d344f"><div><strong>${escapeHtml(x.value)}</strong><br><small>${x.mode === 'family' ? 'Entire domain family' : 'Exact domain'}</small></div><button data-restore-domain="${escapeHtml(x.value)}" data-restore-mode="${x.mode}">Restore</button></div>`).join('') : '<p class="muted">No ignored domains.</p>';
  }

  async function showVersion() {
    try {
      const response = await originalFetch('/api/status', { cache: 'no-store' });
      const data = await response.json();
      const badge = document.createElement('div');
      badge.id = 'homewatchVersion';
      badge.textContent = `HomeWatch ${data.version || 'unknown'} · 8a6e5a9`;
      badge.title = 'Running application version and Git commit';
      badge.style.cssText = 'position:fixed;top:8px;right:10px;z-index:900;padding:5px 9px;border-radius:999px;background:#10263d;border:1px solid #315776;color:#b9d4ea;font:600 11px/1.2 system-ui;box-shadow:0 2px 10px rgba(0,0,0,.2)';
      document.body.appendChild(badge);
    } catch {}
  }

  document.addEventListener('click', event => {
    const details = event.target.closest('[data-domain-details]'); if (details) { showDetails(details.dataset.domainDetails); return; }
    const ignoreDevice = event.target.closest('[data-ignore-session-device]'); if (ignoreDevice) { ignoreSessionDevice(ignoreDevice); return; }
    const exact = event.target.closest('[data-ignore-exact]'); if (exact) { ignoreDomain(exact.dataset.ignoreExact, 'exact'); return; }
    const familyButton = event.target.closest('[data-ignore-family]'); if (familyButton) { ignoreDomain(familyButton.dataset.ignoreFamily, 'family'); return; }
    const restore = event.target.closest('[data-restore-domain]'); if (restore) restoreDomain(restore.dataset.restoreDomain, restore.dataset.restoreMode);
  });

  async function start() {
    await ignoredReady;
    ensureDrawer(); ensureIgnoredManager(); addControls(); renderIgnoredManager(); showVersion();
    ['sessionList','deviceEvents'].forEach(id => { const node = byId(id); if (node) new MutationObserver(() => setTimeout(addControls,0)).observe(node,{childList:true,subtree:true}); });
    document.querySelector('.nav [data-view="settings"]')?.addEventListener('click', () => setTimeout(() => { ensureIgnoredManager(); renderIgnoredManager(); }, 0));
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, {once:true}); else start();
})();