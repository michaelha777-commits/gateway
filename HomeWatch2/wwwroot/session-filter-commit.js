(() => {
  const sessionTypeForEvent = event => {
    const category = String(event?.category || '').toLowerCase();
    const domain = String(event?.domain || '').toLowerCase();

    if (category === 'adult') return 'adult';
    if (/(facebook|instagram|tiktok|snapchat|twitter|x\.com|reddit|linkedin|pinterest)/.test(domain) || category === 'social') return 'social';
    if (/(youtube|googlevideo|netflix|nflx|spotify|twitch|vimeo|disneyplus|hulu|primevideo|ytimg)/.test(domain) || category === 'streaming') return 'streaming';
    if (/(whatsapp|telegram|signal|messenger|discord|skype|teams)/.test(domain) || category === 'messaging') return 'messaging';
    if (/(amazon|ebay|etsy|walmart|bestbuy|shopify|temu|aliexpress)/.test(domain) || category === 'shopping') return 'shopping';
    if (/(steam|xbox|playstation|epicgames|roblox|minecraft|nintendo)/.test(domain) || category === 'gaming') return 'gaming';
    if (/(office|outlook|notion|slack|zoom|dropbox|drive\.google|docs\.google)/.test(domain) || category === 'productivity') return 'productivity';
    if (/(github|gitlab|npmjs|nuget|docker|stackoverflow|visualstudio)/.test(domain) || category === 'development') return 'development';
    if (/(doubleclick|googlesyndication|googleadservices|adservice|analytics|tracking|pixel)/.test(domain) || category === 'advertising') return 'advertising';
    if (/(private-relay|privaterelay|cloudflare-dns|dns\.google|quad9|nextdns|adguard-dns|doh|dot)/.test(domain) || ['privacy-proxy','bypass','encrypted-dns','vpn','proxy'].includes(category)) return 'privacy-proxy';
    if (['dns','system','network'].includes(category) || /(apple|icloud|googleapis|gstatic|microsoft|windowsupdate|samsungcloud|amazonaws|cloudfront)/.test(domain)) return 'system';
    return 'other';
  };

  window.buildSessions = events => {
    const sorted = [...events].sort((a, b) => parseServerDate(a.timestamp) - parseServerDate(b.timestamp));
    const sessions = [];
    const open = new Map();

    for (const event of sorted) {
      const type = sessionTypeForEvent(event);
      const key = `${event.deviceId || event.deviceName}|${event.domain}|${type}`;
      const timestamp = parseServerDate(event.timestamp);
      let session = open.get(key);

      if (!session || timestamp - parseServerDate(session.end) > 600000) {
        session = {
          deviceId: event.deviceId || '',
          deviceName: event.deviceName || event.deviceIp || 'Unknown device',
          deviceIp: event.deviceIp || '',
          domain: event.domain,
          category: event.category || 'dns',
          sessionType: type,
          start: event.timestamp,
          end: event.timestamp,
          requests: 1
        };
        sessions.push(session);
        open.set(key, session);
      } else {
        session.end = event.timestamp;
        session.requests++;
      }
    }

    return sessions.sort((a, b) => parseServerDate(b.end) - parseServerDate(a.end));
  };

  window.loadSessions = async () => {
    const refresh = byId('refreshSessions');
    if (refresh) refresh.disabled = true;
    const hours = byId('sessionHours').value;
    const search = byId('sessionSearch').value.trim().toLowerCase();
    const selectedType = byId('sessionCategory')?.value || 'all';
    byId('sessionList').innerHTML = '<div class="panel empty">Loading sessions…</div>';

    try {
      const response = await fetch(`/api/dashboard?hours=${encodeURIComponent(hours)}`, { cache: 'no-store' });
      if (!response.ok) throw new Error('Could not load sessions.');
      const data = await response.json();
      const sessions = buildSessions(data.events || []).filter(session => {
        const matchesSearch = !search || [session.deviceName, session.deviceIp, session.domain]
          .some(value => String(value || '').toLowerCase().includes(search));
        const matchesType = selectedType === 'all' || session.sessionType === selectedType;
        return matchesSearch && matchesType;
      });

      byId('sessionList').innerHTML = sessions.length
        ? sessions.map(sessionCard).join('')
        : '<div class="panel empty">No sessions match the selected type, period, or search.</div>';
      document.querySelectorAll('[data-open-device]').forEach(button =>
        button.addEventListener('click', () => openDeviceFromSession(button.dataset.openDevice)));
    } catch (error) {
      byId('sessionList').innerHTML = `<div class="panel empty error">${escapeHtml(error.message)}</div>`;
    } finally {
      if (refresh) refresh.disabled = false;
    }
  };

  const categorySelect = byId('sessionCategory');
  if (categorySelect) {
    const replacement = categorySelect.cloneNode(true);
    categorySelect.replaceWith(replacement);
    replacement.addEventListener('change', () => window.loadSessions());
  }

  const showCommitVersion = async () => {
    try {
      const response = await fetch('/api/status', { cache: 'no-store' });
      if (!response.ok) return;
      const status = await response.json();
      const commit = String(status.commit || '').trim();
      if (commit && commit !== 'unknown') {
        byId('version').textContent = `commit ${commit}`;
        byId('version').title = 'Current Git commit';
      }
    } catch { }
  };

  showCommitVersion();
  setInterval(showCommitVersion, 30000);
})();
