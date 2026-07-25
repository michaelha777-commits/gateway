const HW1_VERSION = '2.0.0-alpha.17';

function hw1Category(domain, storedCategory) {
  const d = String(domain || '').toLowerCase();
  if (/scorecardresearch|servedbyadbutler/.test(d)) return 'advertising';
  if (storedCategory && storedCategory !== 'dns' && storedCategory !== 'unknown') return storedCategory;
  const rules = [
    [/porn|xvideos|xnxx|xhamster|redtube|youporn|spankbang|onlyfans|chaturbate|stripchat|livejasmin|myfreecams|adultfriendfinder|literotica|nhentai|rule34/, 'adult'],
    [/youtube|googlevideo|netflix|nflx|primevideo|spotify|twitch|hulu|disneyplus/, 'streaming'],
    [/facebook|fbcdn|instagram|tiktok|twitter|x\.com$|reddit|snapchat|pinterest/, 'social'],
    [/doubleclick|googlesyndication|adservice|adsystem|adbutler|scorecardresearch|taboola|outbrain|tracking|analytics/, 'advertising'],
    [/github|visualstudio|stackoverflow|npmjs|nuget|docker|azure|aws|cloudflare/, 'development'],
    [/office|outlook|teams|microsoftonline|sharepoint|zoom|slack/, 'productivity'],
    [/amazon|ebay|walmart|bestbuy|etsy|shopify/, 'shopping'],
    [/steampowered|xbox|playstation|epicgames|roblox|fortnite/, 'gaming'],
    [/dns\.google|cloudflare-dns|dns\.quad9|doh\.|nextdns/, 'encrypted-dns'],
    [/proxy|vpn|torproject|protonvpn|nordvpn|expressvpn|surfshark/, 'bypass'],
    [/malware|phishing|botnet|cryptominer/, 'security']
  ];
  const match = rules.find(([pattern]) => pattern.test(d));
  return match ? match[1] : 'routine';
}

function hw1Description(domain, category) {
  const d = String(domain || '').toLowerCase();
  const known = [
    [/scorecardresearch/, 'Audience measurement and analytics service; not an adult website'],
    [/servedbyadbutler/, 'Advertising-delivery service; not an adult website'],
    [/chatgpt|openai/, 'OpenAI / ChatGPT artificial-intelligence service'],
    [/youtube|googlevideo/, 'YouTube video and content-delivery service'],
    [/netflix|nflx/, 'Netflix video-streaming infrastructure'],
    [/primevideo|amazonvideo/, 'Amazon Prime Video streaming infrastructure'],
    [/whatsapp/, 'WhatsApp messaging and calling service'],
    [/facebook|fbcdn|instagram/, 'Meta social-media service'],
    [/tiktok/, 'TikTok social-video service'],
    [/github/, 'GitHub software-development platform'],
    [/visualstudio|azureedge|vsassets/, 'Microsoft Visual Studio or developer service'],
    [/office|outlook|teams|microsoftonline/, 'Microsoft 365, Outlook, or Teams service'],
    [/google|gstatic|googleapis|gvt/, 'Google application, content, or infrastructure service'],
    [/apple|icloud/, 'Apple or iCloud service'],
    [/amazon|cloudfront|amazonaws/, 'Amazon retail, cloud, or content-delivery service'],
    [/spotify/, 'Spotify music-streaming service'],
    [/reddit/, 'Reddit social discussion service']
  ];
  const match = known.find(([pattern]) => pattern.test(d));
  if (match) return match[1];
  const descriptions = {
    adult: 'Adult-content domain detected by HomeWatch',
    streaming: 'Video or audio streaming service', social: 'Social-media or community service',
    advertising: 'Advertising, marketing, analytics, or tracking infrastructure', development: 'Software-development or cloud service',
    productivity: 'Work, communication, or productivity service', shopping: 'Retail or online-shopping service',
    gaming: 'Gaming platform or game infrastructure', 'encrypted-dns': 'Encrypted DNS or alternate name-resolution service',
    bypass: 'VPN, proxy, Tor, or possible filtering-bypass service', security: 'Potential security-risk domain',
    routine: 'Routine internet service or supporting infrastructure'
  };
  return descriptions[category] || 'Internet domain contacted by this device';
}

async function loadHw1DashboardTools() {
  const response = await fetch('/api/dashboard?hours=' + encodeURIComponent(document.getElementById('hwHours')?.value || 24), { cache: 'no-store' });
  if (!response.ok) return;
  const data = await response.json();
  const events = data.events || [];
  const device = (document.getElementById('hwDevice')?.value || '').toLowerCase();
  const category = document.getElementById('hwCategory')?.value || 'all';
  const search = (document.getElementById('hwDomainSearch')?.value || '').trim().toLowerCase();
  const filtered = events.filter(event => {
    const cat = hw1Category(event.domain, event.category);
    return (!device || String(event.deviceId).toLowerCase() === device) &&
      (category === 'all' || cat === category || (category === 'blocked' && String(event.action).toLowerCase().includes('block'))) &&
      (!search || String(event.domain).toLowerCase().includes(search));
  });
  const deviceSelect = document.getElementById('hwDevice');
  if (deviceSelect && deviceSelect.options.length <= 1) {
    const map = new Map();
    events.forEach(e => map.set(e.deviceId, e.deviceName || e.deviceIp || 'Unknown device'));
    [...map.entries()].sort((a,b) => a[1].localeCompare(b[1])).forEach(([id,name]) => deviceSelect.add(new Option(name, String(id).toLowerCase())));
  }
  const metrics = {
    adult: filtered.filter(e => hw1Category(e.domain, e.category) === 'adult').length,
    direct: new Set(filtered.map(e => `${e.deviceId}|${String(e.domain).split('.').slice(-2).join('.')}`)).size,
    streaming: filtered.filter(e => hw1Category(e.domain, e.category) === 'streaming').length,
    bypass: filtered.filter(e => ['bypass','encrypted-dns'].includes(hw1Category(e.domain, e.category))).length,
    unique: new Set(filtered.map(e => e.domain)).size,
    blocked: filtered.filter(e => String(e.action).toLowerCase().includes('block')).length
  };
  Object.entries(metrics).forEach(([key,value]) => { const node = document.getElementById('hwMetric-' + key); if (node) node.textContent = value; });
  const table = document.getElementById('hwEvidenceRows');
  if (table) table.innerHTML = filtered.slice(0,500).map(e => {
    const cat = hw1Category(e.domain, e.category);
    return `<tr><td>${escapeHtml(formatFullTime(e.timestamp))}</td><td>${escapeHtml(e.deviceName || e.deviceIp || 'Unknown')}</td><td><strong>${escapeHtml(e.domain)}</strong><small style="display:block">${escapeHtml(hw1Description(e.domain, cat))}</small></td><td><span class="pill">${escapeHtml(cat)}</span></td><td>${escapeHtml(e.action || 'observed')}</td></tr>`;
  }).join('') || '<tr><td colspan="5">No activity matches these filters.</td></tr>';
  window.hw1FilteredEvents = filtered;
}

function alertDomain(a) {
  if (a.domain) return String(a.domain).toLowerCase();
  const match = String(a.detail || '').match(/(?:first detected site|first site|primary site|domain):\s*([a-z0-9.-]+)/i);
  return match ? match[1].toLowerCase() : '';
}

async function markAlertNotAdult(button) {
  const domain = button.dataset.domain;
  if (!domain) return alert('HomeWatch could not identify the domain in this alert.');
  if (!confirm(`Mark ${domain} as not adult? HomeWatch will remember this correction for future requests.`)) return;
  button.disabled = true;
  button.textContent = 'Saving correction…';
  try {
    const response = await fetch('/api/intelligence/adult/false-positive', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ domain })
    });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || 'Could not save the correction.');
    await loadHw1Alerts();
    await loadDashboard();
  } catch (error) {
    alert(error.message);
    button.disabled = false;
    button.textContent = 'Not an adult site';
  }
}

async function loadHw1Alerts() {
  const list = document.getElementById('alertList');
  if (!list) return;
  list.innerHTML = '<div class="empty">Loading alerts…</div>';
  try {
    const response = await fetch('/api/alerts', { cache: 'no-store' });
    if (!response.ok) throw new Error('Could not load alerts.');
    const data = await response.json();
    const showAcknowledged = document.getElementById('showAcknowledgedAlerts')?.checked;
    const alerts = (data.alerts || []).filter(a => showAcknowledged || !a.acknowledged);
    list.innerHTML = alerts.length ? alerts.map(a => {
      const domain = alertDomain(a);
      const adultAlert = /adult/i.test(String(a.title || '')) && domain;
      return `<article class="panel" style="padding:18px;margin-bottom:12px;opacity:${a.acknowledged ? '.62' : '1'}"><div class="panel-heading"><div><span class="eyebrow">${escapeHtml(String(a.severity || 'medium').toUpperCase())}</span><h2>${escapeHtml(a.title)}</h2></div><time>${escapeHtml(formatFullTime(a.createdAt))}</time></div><p><strong>${escapeHtml(a.deviceName || a.deviceIp || 'Unknown device')}</strong> · ${escapeHtml(a.deviceIp || '')}</p><p>${escapeHtml(a.detail || '')}</p>${adultAlert ? `<p class="muted">Why flagged: this domain matched HomeWatch's adult-intelligence database or hostname rules.</p>` : ''}<div style="display:flex;gap:10px;flex-wrap:wrap">${a.acknowledged ? `<p class="muted">Acknowledged ${escapeHtml(formatFullTime(a.acknowledgedAt))}</p>` : `<button class="ack-alert" data-alert-id="${escapeHtml(a.id)}">Acknowledge</button>${adultAlert ? `<button class="not-adult-alert" data-domain="${escapeHtml(domain)}">Not an adult site</button>` : ''}`}</div></article>`;
    }).join('') : '<div class="empty">No matching alerts.</div>';
    document.querySelectorAll('.ack-alert').forEach(button => button.addEventListener('click', async () => {
      button.disabled = true;
      const response = await fetch(`/api/alerts/${encodeURIComponent(button.dataset.alertId)}/acknowledge`, { method:'POST' });
      if (!response.ok) alert('Could not acknowledge this alert.');
      await loadHw1Alerts(); await loadDashboard();
    }));
    document.querySelectorAll('.not-adult-alert').forEach(button => button.addEventListener('click', () => markAlertNotAdult(button)));
  } catch (error) { list.innerHTML = `<div class="empty error">${escapeHtml(error.message)}</div>`; }
}

function exportHw1(format) {
  const events = window.hw1FilteredEvents || cachedDashboardEvents || [];
  let content, type, extension;
  if (format === 'json') { content = JSON.stringify(events, null, 2); type = 'application/json'; extension = 'json'; }
  else {
    const rows = [['Time','Device','IP','Domain','Category','Description','Action'], ...events.map(e => [formatFullTime(e.timestamp), e.deviceName || '', e.deviceIp || '', e.domain || '', hw1Category(e.domain,e.category), hw1Description(e.domain,hw1Category(e.domain,e.category)), e.action || ''])];
    content = rows.map(row => row.map(value => `"${String(value).replaceAll('"','""')}"`).join(',')).join('\r\n'); type = 'text/csv'; extension = 'csv';
  }
  const url = URL.createObjectURL(new Blob([content], { type }));
  const a = document.createElement('a'); a.href = url; a.download = `HomeWatch-activity-${new Date().toISOString().slice(0,10)}.${extension}`; a.click(); URL.revokeObjectURL(url);
}

function initializeHw1Features() {
  const version = document.getElementById('version');
  if (version) version.textContent = `v${HW1_VERSION}`;
  ['hwHours','hwDevice','hwCategory'].forEach(id => document.getElementById(id)?.addEventListener('change', loadHw1DashboardTools));
  document.getElementById('hwDomainSearch')?.addEventListener('input', loadHw1DashboardTools);
  document.getElementById('hwRefresh')?.addEventListener('click', loadHw1DashboardTools);
  document.getElementById('exportHwCsv')?.addEventListener('click', () => exportHw1('csv'));
  document.getElementById('exportHwJson')?.addEventListener('click', () => exportHw1('json'));
  document.getElementById('refreshAlerts')?.addEventListener('click', loadHw1Alerts);
  document.getElementById('showAcknowledgedAlerts')?.addEventListener('change', loadHw1Alerts);
  const originalSwitch = window.switchView;
  window.switchView = function(name) { originalSwitch(name); if (name === 'alerts') loadHw1Alerts(); if (name === 'dashboard') setTimeout(loadHw1DashboardTools, 50); };
  document.querySelectorAll('.nav button').forEach(button => {
    const clone = button.cloneNode(true); button.replaceWith(clone);
    clone.addEventListener('click', () => window.switchView(clone.dataset.view));
  });
  loadHw1DashboardTools();
}

document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', initializeHw1Features) : initializeHw1Features();