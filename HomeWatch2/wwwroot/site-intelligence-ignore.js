(() => {
  'use strict';

  const STORAGE_KEY = 'homewatch.hiddenReportSessionDomains.v1';

  function normalizeDomain(value) {
    return String(value || '').trim().toLowerCase().replace(/^https?:\/\//, '').split('/')[0].replace(/\.$/, '');
  }

  function readHiddenDomains() {
    try {
      const value = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
      return new Set(Array.isArray(value) ? value.map(normalizeDomain).filter(Boolean) : []);
    } catch {
      return new Set();
    }
  }

  function writeHiddenDomains(domains) {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify([...domains].sort()));
    } catch {
      // A browser preference must never stop HomeWatch.
    }
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

  const originalFetch = window.fetch.bind(window);
  window.fetch = async (...args) => {
    const response = await originalFetch(...args);
    if (!isDashboardRequest(args[0]) || !response.ok) return response;

    try {
      const hidden = readHiddenDomains();
      if (!hidden.size) return response;
      const data = await response.clone().json();
      if (!Array.isArray(data?.events)) return response;

      data.events = data.events.filter(event => !hidden.has(normalizeDomain(event.domain)));
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
      console.warn('HomeWatch site display filter skipped:', error);
      return response;
    }
  };

  const intelligenceRules = [
    [/^(?:www\.)?youtube\.com$|googlevideo\.com$|youtubei\.googleapis\.com$/, ['YouTube', 'Video streaming, playback, thumbnails, or application API']],
    [/netflix\.com$|nflxvideo\.net$|nflximg\.net$/, ['Netflix', 'Video-streaming service or Netflix content-delivery infrastructure']],
    [/facebook\.com$|fbcdn\.net$|instagram\.com$/, ['Meta service', 'Facebook or Instagram application, media, messaging, or tracking infrastructure']],
    [/whatsapp\.com$|whatsapp\.net$/, ['WhatsApp', 'Messaging, calling, media, or connection service']],
    [/googleapis\.com$|gstatic\.com$|google\.com$|gvt\d?\.com$/, ['Google service', 'Google application, content-delivery, fonts, updates, or infrastructure']],
    [/microsoft\.com$|windowsupdate\.com$|office\.com$|live\.com$|msedge\.net$/, ['Microsoft service', 'Windows, Microsoft 365, Edge, updates, identity, or cloud infrastructure']],
    [/apple\.com$|icloud\.com$|cdn-apple\.com$/, ['Apple service', 'Apple device, iCloud, software-update, or content-delivery service']],
    [/amazonaws\.com$|cloudfront\.net$|amazon\.com$/, ['Amazon service', 'Amazon retail, cloud hosting, or content-delivery infrastructure']],
    [/spotify\.com$|scdn\.co$/, ['Spotify', 'Music streaming, artwork, application, or content-delivery service']],
    [/tiktok\.com$|tiktokcdn\.com$|byteoversea\.com$/, ['TikTok', 'Social-video application or content-delivery infrastructure']],
    [/github\.com$|githubusercontent\.com$/, ['GitHub', 'Software-development, repository, release, or content service']],
    [/openai\.com$|chatgpt\.com$/, ['OpenAI / ChatGPT', 'Artificial-intelligence application or service infrastructure']],
    [/anysex\.com$|xnxx|xvideos|pornhub|xhamster|redtube|youporn|spankbang|tube8|brazzers|erome|jerkmate|chaturbate|stripchat|livejasmin|bongacams|myfreecams|onlyfans|nhentai|hentai|rule34|literotica/, ['Adult-content service', 'Adult website, media host, streaming service, or supporting content-delivery domain']],
    [/doubleclick\.net$|googlesyndication\.com$|googleadservices\.com$/, ['Advertising / tracking', 'Advertising delivery, measurement, attribution, or tracking infrastructure']],
    [/cloudflare\.com$|cloudflare-dns\.com$/, ['Cloudflare infrastructure', 'Content delivery, security, DNS, or website protection service']]
  ];

  function describeDomain(domain) {
    const normalized = normalizeDomain(domain);
    const match = intelligenceRules.find(([pattern]) => pattern.test(normalized));
    if (match) return { title: match[1][0], description: match[1][1] };
    const parts = normalized.split('.').filter(Boolean);
    const root = parts.length > 1 ? parts.slice(-2).join('.') : normalized;
    return { title: root || 'Unknown site', description: 'Internet domain contacted by this device. HomeWatch currently has no cached reputation details for it.' };
  }

  function refreshVisibleViews() {
    if (document.getElementById('dashboardView')?.classList.contains('active')) document.getElementById('refresh')?.click();
    if (document.getElementById('sessionsView')?.classList.contains('active')) document.getElementById('refreshSessions')?.click();
    updateHiddenSummary();
  }

  function hideDomain(domain) {
    const normalized = normalizeDomain(domain);
    if (!normalized) return;
    const hidden = readHiddenDomains();
    hidden.add(normalized);
    writeHiddenDomains(hidden);
    refreshVisibleViews();
  }

  function ensureSummary() {
    const toolbar = document.querySelector('#sessionsView .session-toolbar');
    if (!toolbar || document.getElementById('ignoredSitesSummary')) return;
    const summary = document.createElement('div');
    summary.id = 'ignoredSitesSummary';
    summary.style.display = 'flex';
    summary.style.alignItems = 'center';
    summary.style.gap = '10px';
    summary.style.flexWrap = 'wrap';
    summary.innerHTML = '<span class="muted" id="ignoredSitesCount">No ignored sites</span><button id="restoreIgnoredSites" type="button" style="display:none">Restore all ignored sites</button>';
    toolbar.appendChild(summary);
    document.getElementById('restoreIgnoredSites')?.addEventListener('click', () => {
      writeHiddenDomains(new Set());
      refreshVisibleViews();
    });
    updateHiddenSummary();
  }

  function updateHiddenSummary() {
    ensureSummary();
    const hidden = readHiddenDomains();
    const count = document.getElementById('ignoredSitesCount');
    const restore = document.getElementById('restoreIgnoredSites');
    if (count) count.textContent = hidden.size ? `${hidden.size} ignored site${hidden.size === 1 ? '' : 's'}` : 'No ignored sites';
    if (restore) restore.style.display = hidden.size ? '' : 'none';
  }

  function decorateSessionCards() {
    ensureSummary();
    document.querySelectorAll('#sessionList .session-card').forEach(card => {
      if (card.dataset.siteIntelligenceApplied === '1') return;
      const domainElement = card.querySelector('.session-domain strong');
      const domain = normalizeDomain(domainElement?.textContent);
      if (!domain) return;

      const details = describeDomain(domain);
      const area = card.querySelector('.session-domain');
      if (!area) return;
      const detail = document.createElement('small');
      detail.className = 'site-intelligence-detail';
      detail.style.display = 'block';
      detail.style.marginTop = '6px';
      detail.innerHTML = `<strong style="display:block">${escapeText(details.title)}</strong><span>${escapeText(details.description)}</span>`;
      area.appendChild(detail);

      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'ignore-site-button';
      button.style.marginTop = '8px';
      button.textContent = 'Ignore this site';
      button.title = `Hide ${domain} from dashboard reports and sessions in this browser`;
      button.addEventListener('click', event => {
        event.preventDefault();
        event.stopPropagation();
        hideDomain(domain);
      });
      area.appendChild(button);
      card.dataset.siteIntelligenceApplied = '1';
    });
  }

  function escapeText(value) {
    return String(value ?? '').replace(/[&<>'"]/g, character => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', "'":'&#39;', '"':'&quot;' }[character]));
  }

  function start() {
    ensureSummary();
    const list = document.getElementById('sessionList');
    if (list) new MutationObserver(decorateSessionCards).observe(list, { childList:true, subtree:true });
    decorateSessionCards();
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once:true });
  else start();
})();
