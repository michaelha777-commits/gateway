(() => {
  'use strict';

  const DEVICE_MARKER_SUFFIX = '.homewatch-device.local';
  const originalFetch = window.fetch.bind(window);
  const ignoredExact = new Set();
  const ignoredFamilies = new Set();
  const ignoredDevices = new Set();
  let ignoredReady = refreshIgnored();
  let lastUserScrollY = window.scrollY;
  let restoringScroll = false;

  function normalize(value) {
    return String(value || '')
      .trim()
      .toLowerCase()
      .replace(/^https?:\/\//, '')
      .split('/')[0]
      .replace(/^\.+|\.+$/g, '');
  }

  function deviceIdFromMarker(value) {
    const text = normalize(value);
    if (!text.startsWith('device-') || !text.endsWith(DEVICE_MARKER_SUFFIX)) return '';
    return text.slice(7, -DEVICE_MARKER_SUFFIX.length);
  }

  function isIgnoredDomain(value) {
    const domain = normalize(value);
    if (!domain) return false;
    if (ignoredExact.has(domain)) return true;
    for (const family of ignoredFamilies) {
      if (domain === family || domain.endsWith('.' + family)) return true;
    }
    return false;
  }

  function isIgnoredEvent(event) {
    const deviceId = String(event?.deviceId || '').toLowerCase();
    return (deviceId && ignoredDevices.has(deviceId)) || isIgnoredDomain(event?.domain);
  }

  async function refreshIgnored() {
    try {
      const response = await originalFetch('/api/ignored-domains', { cache: 'no-store' });
      if (!response.ok) return;
      const data = await response.json();
      ignoredExact.clear();
      ignoredFamilies.clear();
      ignoredDevices.clear();

      for (const value of data.exact || []) {
        const normalized = normalize(value);
        const deviceId = deviceIdFromMarker(normalized);
        if (deviceId) ignoredDevices.add(deviceId);
        else if (normalized) ignoredExact.add(normalized);
      }
      for (const value of data.families || []) {
        const normalized = normalize(value);
        if (normalized) ignoredFamilies.add(normalized);
      }
    } catch (error) {
      console.warn('Could not load investigation ignore preferences:', error);
    }
  }

  function isActivityRequest(input) {
    const raw = typeof input === 'string' ? input : input?.url;
    if (!raw) return false;
    try {
      return new URL(raw, location.href).pathname === '/api/activity';
    } catch {
      return false;
    }
  }

  function isIgnoredDomainWrite(input, init) {
    const raw = typeof input === 'string' ? input : input?.url;
    if (!raw || String(init?.method || 'GET').toUpperCase() !== 'POST') return false;
    try {
      return new URL(raw, location.href).pathname === '/api/ignored-domains';
    } catch {
      return false;
    }
  }

  window.fetch = async (input, init) => {
    let effectiveInit = init;

    // Investigation cards represent an entire root-domain family. Saving those as
    // exact ignores only hid the root name while its subdomains immediately rebuilt
    // the same investigation. Convert card-originated root ignores to family ignores.
    if (isIgnoredDomainWrite(input, init) && typeof init?.body === 'string') {
      try {
        const payload = JSON.parse(init.body);
        const domain = normalize(payload?.domain);
        if (payload?.mode === 'exact' && domain && !domain.endsWith(DEVICE_MARKER_SUFFIX)) {
          effectiveInit = { ...init, body: JSON.stringify({ ...payload, domain, mode: 'family' }) };
        }
      } catch {}
    }

    const response = await originalFetch(input, effectiveInit);

    if (isIgnoredDomainWrite(input, effectiveInit) && response.ok) {
      ignoredReady = refreshIgnored();
      await ignoredReady;
    }

    if (!isActivityRequest(input) || !response.ok) return response;

    try {
      await ignoredReady;
      const data = await response.clone().json();
      if (!Array.isArray(data?.events)) return response;
      data.events = data.events.filter(event => !isIgnoredEvent(event));

      const headers = new Headers(response.headers);
      headers.set('content-type', 'application/json; charset=utf-8');
      return new Response(JSON.stringify(data), {
        status: response.status,
        statusText: response.statusText,
        headers
      });
    } catch (error) {
      console.warn('Investigation ignore filter skipped:', error);
      return response;
    }
  };

  window.addEventListener('scroll', () => {
    if (!restoringScroll) lastUserScrollY = window.scrollY;
  }, { passive: true });

  function sessionsAreActive() {
    return document.getElementById('sessionsView')?.classList.contains('active');
  }

  // device-report-ignore.js performs a synthetic refresh every 30 seconds. Prevent
  // that refresh while the user is reading older results; manual Refresh still works.
  document.addEventListener('click', event => {
    const refresh = event.target?.closest?.('#refreshSessions');
    if (!refresh || event.isTrusted || !sessionsAreActive()) return;
    const listTop = document.getElementById('sessionList')?.getBoundingClientRect().top ?? 0;
    if (window.scrollY > 250 || listTop < -150) {
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  }, true);

  // Loading another history page rebuilds the investigation list. Preserve the
  // reader's viewport so appending older entries never throws them back to the top.
  function installScrollKeeper() {
    const list = document.getElementById('sessionList');
    if (!list) return;
    new MutationObserver(() => {
      if (!sessionsAreActive() || lastUserScrollY < 250) return;
      const target = lastUserScrollY;
      restoringScroll = true;
      requestAnimationFrame(() => {
        window.scrollTo({ top: target, left: 0, behavior: 'auto' });
        requestAnimationFrame(() => { restoringScroll = false; });
      });
    }).observe(list, { childList: true, subtree: false });
  }

  async function start() {
    await ignoredReady;
    installScrollKeeper();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', start, { once: true });
  } else {
    start();
  }
})();
