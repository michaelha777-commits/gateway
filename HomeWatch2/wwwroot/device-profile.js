(() => {
  'use strict';

  const byId = id => document.getElementById(id);
  const escapeHtml = value => String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  let lastDeviceId = '';
  let requestNumber = 0;

  function ensureUi() {
    const content = byId('deviceContent');
    if (!content || byId('deviceProfileSummary')) return;
    const section = document.createElement('section');
    section.id = 'deviceProfileSummary';
    section.style.cssText = 'padding:18px 20px;border-bottom:1px solid #1d344f';
    section.innerHTML = '<div class="subheading"><h3>Device profile</h3><span id="deviceProfileConfidence">Building profile…</span></div><div id="deviceProfileBody"><div class="empty" style="padding:24px">Select a device to build its profile.</div></div>';
    const relay = byId('privateRelayStatus');
    const identity = byId('deviceSaveResult')?.closest('section');
    if (relay) relay.insertAdjacentElement('afterend', section);
    else if (identity) identity.insertAdjacentElement('beforebegin', section);
    else content.prepend(section);
  }

  function category(domain, stored) {
    const d = String(domain || '').toLowerCase();
    if (stored && !['dns','unknown','routine'].includes(stored)) return stored;
    if (/mask.*(?:icloud\.com|apple-dns\.net)/.test(d)) return 'privacy-proxy';
    if (/youtube|googlevideo|netflix|nflx|spotify|twitch|primevideo|amazonvideo/.test(d)) return 'streaming';
    if (/facebook|fbcdn|instagram|tiktok|snapchat|reddit|twitter|x\.com$|pinterest/.test(d)) return 'social';
    if (/amazon|ebay|walmart|bestbuy|etsy|shopify/.test(d)) return 'shopping';
    if (/steam|xbox|playstation|epicgames|roblox|fortnite/.test(d)) return 'gaming';
    if (/vpn|proxy|torproject|protonvpn|nordvpn|expressvpn|surfshark|nextdns|cloudflare-dns|dns\.google/.test(d)) return 'bypass';
    if (/porn|xvideos|xnxx|xhamster|redtube|youporn|spankbang|onlyfans|chaturbate|stripchat|livejasmin|erome|jerkmate|anysex/.test(d)) return 'adult';
    return 'routine';
  }

  function actionState(action) {
    const value = String(action || '').trim().toLowerCase();
    if (!value || value === 'observed') return 'allowed';
    if (value.startsWith('notfiltered') || value.includes('whitelist') || value.includes('allowed')) return 'allowed';
    if (value.includes('rewrite')) return 'rewritten';
    if (value.includes('cache')) return 'cached';
    if (value.includes('block') || value.includes('deny') || value.includes('refus') || value.startsWith('filtered')) return 'blocked';
    return 'allowed';
  }

  function inferDevice(device, events) {
    const text = `${device.name || ''} ${device.vendor || ''} ${events.map(x => x.domain).join(' ')}`.toLowerCase();
    const scores = [
      { label:'Apple mobile device', icon:'📱', score:0, reasons:[] },
      { label:'Windows computer', icon:'💻', score:0, reasons:[] },
      { label:'Android device', icon:'📱', score:0, reasons:[] },
      { label:'Smart TV / streaming device', icon:'📺', score:0, reasons:[] },
      { label:'Game console', icon:'🎮', score:0, reasons:[] }
    ];
    const add = (i, points, reason) => { scores[i].score += points; scores[i].reasons.push(reason); };
    if (/iphone|ipad|apple/.test(text)) add(0, 45, 'Apple name, vendor, or service pattern');
    if (/icloud|apple-dns|mzstatic|cdn-apple|itunes|apple\.com/.test(text)) add(0, 35, 'Apple service activity');
    if (/mask.*(?:icloud|apple-dns)/.test(text)) add(0, 20, 'Apple Private Relay activity');
    if (/windows|microsoft|office|outlook|teams|windowsupdate|msft/.test(text)) add(1, 55, 'Microsoft and Windows service activity');
    if (/android|googleapis|gstatic|googleplay|firebase/.test(text)) add(2, 35, 'Android or Google application activity');
    if (/roku|chromecast|smarttv|webos|tizen|netflix|nflx|youtube\.com\/tv/.test(text)) add(3, 55, 'TV or streaming service pattern');
    if (/playstation|xbox|nintendo|steam|epicgames|fortnite/.test(text)) add(4, 70, 'Gaming platform activity');
    const best = scores.sort((a,b) => b.score - a.score)[0];
    if (best.score === 0) return { label:'Network device', icon:'🌐', confidence:35, reasons:['Limited identifying evidence'] };
    return { ...best, confidence:Math.min(98, Math.max(55, best.score)) };
  }

  function health(events) {
    const counts = { adult:0, bypass:0, privacy:0, blocked:0 };
    for (const event of events) {
      const cat = category(event.domain, event.category);
      if (cat === 'adult') counts.adult++;
      if (cat === 'bypass') counts.bypass++;
      if (cat === 'privacy-proxy') counts.privacy++;
      if (actionState(event.action) === 'blocked') counts.blocked++;
    }
    let score = 100;
    score -= Math.min(30, counts.adult * 3);
    score -= Math.min(25, counts.bypass * 4);
    if (counts.privacy > 0 && counts.blocked < counts.privacy) score -= 12;
    score = Math.max(0, score);
    const label = score >= 90 ? 'Healthy' : score >= 70 ? 'Review' : 'Attention needed';
    return { score, label, counts };
  }

  function topCategories(events) {
    const map = new Map();
    for (const event of events) {
      const value = category(event.domain, event.category);
      map.set(value, (map.get(value) || 0) + 1);
    }
    return [...map.entries()].sort((a,b) => b[1] - a[1]).slice(0,5);
  }

  function render(device, events) {
    const inferred = inferDevice(device, events);
    const status = health(events);
    const unique = new Set(events.map(x => String(x.domain || '').toLowerCase())).size;
    const categories = topCategories(events);
    const recent = [...events].sort((a,b) => new Date(b.timestamp) - new Date(a.timestamp)).slice(0,6);
    const blockedRelay = events.filter(x => category(x.domain, x.category) === 'privacy-proxy' && actionState(x.action) === 'blocked').length;
    const relayTotal = events.filter(x => category(x.domain, x.category) === 'privacy-proxy').length;

    byId('deviceProfileConfidence').textContent = `${inferred.confidence}% identification confidence`;
    byId('deviceProfileBody').innerHTML = `
      <div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px;margin-bottom:14px">
        <article class="mini-metrics" style="display:block"><span>Likely device</span><strong style="display:block;margin-top:6px;font-size:18px">${inferred.icon} ${escapeHtml(inferred.label)}</strong></article>
        <article class="mini-metrics" style="display:block"><span>Health</span><strong style="display:block;margin-top:6px;font-size:18px">${status.score}/100 · ${escapeHtml(status.label)}</strong></article>
        <article class="mini-metrics" style="display:block"><span>Unique domains</span><strong style="display:block;margin-top:6px;font-size:18px">${unique}</strong></article>
        <article class="mini-metrics" style="display:block"><span>Private Relay</span><strong style="display:block;margin-top:6px;font-size:18px">${relayTotal ? (blockedRelay === relayTotal ? 'Blocked' : 'Detected') : 'Not seen'}</strong></article>
      </div>
      <div style="display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);gap:16px">
        <div><strong>Identification evidence</strong><div class="domain-chips" style="margin-top:9px">${inferred.reasons.map(x => `<span class="domain-chip">${escapeHtml(x)}</span>`).join('')}</div></div>
        <div><strong>Top activity categories</strong><div class="domain-chips" style="margin-top:9px">${categories.map(([name,count]) => `<span class="domain-chip"><span>${escapeHtml(name)}</span><strong>${count}</strong></span>`).join('') || '<span class="muted">No recent activity</span>'}</div></div>
      </div>
      <div style="margin-top:16px"><strong>Recent activity story</strong><div style="margin-top:8px">${recent.map(event => {
        const state = actionState(event.action);
        const outcome = state === 'blocked' ? ' · Blocked' : state === 'rewritten' ? ' · Rewritten' : state === 'cached' ? ' · Cached' : ' · Allowed';
        return `<div style="padding:8px 0;border-bottom:1px solid #172b43"><span class="muted">${new Date(event.timestamp).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'})}</span> · <strong>${escapeHtml(category(event.domain,event.category))}</strong> · ${escapeHtml(event.domain)}${outcome}</div>`;
      }).join('') || '<p class="muted">No recent activity.</p>'}</div></div>`;
  }

  async function loadProfile() {
    ensureUi();
    const selected = document.querySelector('#deviceList [data-device-id].selected');
    const id = selected?.dataset.deviceId;
    if (!id) return;
    lastDeviceId = id;
    const current = ++requestNumber;
    try {
      const params = rangeParams('activityRange', 'activityFrom', 'activityTo');
      params.set('deviceId', id); params.set('pageSize', '500');
      const response = await fetch(`/api/activity?${params}`, { cache:'no-store' });
      if (!response.ok) throw new Error('Profile data is unavailable.');
      const data = await response.json();
      if (current !== requestNumber || id !== lastDeviceId) return;
      render(allDevices.find(device => device.id === id) || {}, data.events || []);
    } catch (error) {
      if (current === requestNumber) byId('deviceProfileBody').innerHTML = `<p class="error">${escapeHtml(error.message)}</p>`;
    }
  }

  function start() {
    ensureUi();
    byId('deviceList')?.addEventListener('click', event => {
      if (event.target.closest('[data-device-id]')) setTimeout(loadProfile, 180);
    });
    byId('activityRange')?.addEventListener('change', () => setTimeout(loadProfile, 100));
    const list = byId('deviceList');
    if (list) new MutationObserver(() => {
      const id = document.querySelector('#deviceList [data-device-id].selected')?.dataset.deviceId || '';
      if (id && id !== lastDeviceId) setTimeout(loadProfile, 100);
    }).observe(list, { childList:true, subtree:true, attributes:true, attributeFilter:['class'] });
    setInterval(() => { if (byId('devicesView')?.classList.contains('active')) loadProfile(); }, 15000);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once:true });
  else start();
})();