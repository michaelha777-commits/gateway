(() => {
  'use strict';

  const originalFetch = window.fetch.bind(window);
  const relayPattern = /(^|\.)(mask(?:-[a-z0-9]+)?|[a-z0-9-]*-mask)(\.|$)/i;

  function isRelayEvent(event) {
    const domain = String(event?.domain || '').toLowerCase();
    return event?.category === 'privacy-proxy' ||
      ((domain.endsWith('.icloud.com') || domain.endsWith('.apple-dns.net')) && relayPattern.test(domain));
  }

  function isBlocked(event) {
    const action = String(event?.action || '').toLowerCase();
    return action.includes('block') || action.includes('filter') || action.includes('deny') || action.includes('refus');
  }

  function ensurePanel() {
    const content = document.getElementById('deviceContent');
    if (!content || document.getElementById('privateRelayStatusPanel')) return null;

    const panel = document.createElement('section');
    panel.id = 'privateRelayStatusPanel';
    panel.style.cssText = 'padding:18px 20px;border-bottom:1px solid #1d344f';
    panel.innerHTML = `
      <div class="subheading"><h3>Apple Private Relay</h3><span id="privateRelayState">No attempts detected</span></div>
      <p id="privateRelaySummary" class="muted" style="margin:0">HomeWatch will show whether this device's relay requests were blocked or allowed.</p>
      <div id="privateRelayDomains" class="domain-chips" style="margin-top:12px"></div>`;

    const header = content.querySelector('.device-header');
    if (header?.nextSibling) content.insertBefore(panel, header.nextSibling);
    else content.prepend(panel);
    return panel;
  }

  function render(events) {
    ensurePanel();
    const state = document.getElementById('privateRelayState');
    const summary = document.getElementById('privateRelaySummary');
    const domains = document.getElementById('privateRelayDomains');
    if (!state || !summary || !domains) return;

    const relayEvents = (events || []).filter(isRelayEvent);
    if (!relayEvents.length) {
      state.textContent = 'No attempts detected';
      state.style.color = '';
      summary.textContent = 'No Apple Private Relay DNS requests were found for this device in the selected period.';
      domains.innerHTML = '';
      return;
    }

    const blocked = relayEvents.filter(isBlocked);
    const allowed = relayEvents.length - blocked.length;
    const uniqueDomains = [...new Set(relayEvents.map(event => String(event.domain || '').toLowerCase()).filter(Boolean))];

    if (allowed === 0) {
      state.textContent = 'Blocked';
      state.style.color = '#83e6b4';
      summary.textContent = `${blocked.length} Private Relay request${blocked.length === 1 ? '' : 's'} blocked. The device could not establish the Apple privacy proxy through these DNS requests.`;
    } else {
      state.textContent = 'Active or not fully blocked';
      state.style.color = '#ffb4b4';
      summary.textContent = `${allowed} Private Relay request${allowed === 1 ? '' : 's'} were processed instead of blocked${blocked.length ? `; ${blocked.length} were blocked` : ''}. Review the AdGuard rules for this device.`;
    }

    domains.innerHTML = uniqueDomains.slice(0, 12)
      .map(domain => `<span class="domain-chip" style="cursor:default"><span>${escapeHtml(domain)}</span></span>`)
      .join('');
  }

  function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>'"]/g, character => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;'
    }[character]));
  }

  window.fetch = async (...args) => {
    const response = await originalFetch(...args);
    const raw = typeof args[0] === 'string' ? args[0] : args[0]?.url;
    if (!raw || !response.ok) return response;

    try {
      const url = new URL(raw, window.location.href);
      if (!/^\/api\/devices\/[^/]+\/activity$/.test(url.pathname)) return response;
      const data = await response.clone().json();
      render(data.events || []);
    } catch {
      // Relay status is supplemental and must never interfere with normal activity loading.
    }
    return response;
  };

  function start() {
    ensurePanel();
    document.querySelector('[data-view="devices"]')?.addEventListener('click', () => setTimeout(ensurePanel, 0));
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();
