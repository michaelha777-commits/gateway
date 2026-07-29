(() => {
  const LIVE_POLL_MS = 3000;
  const SESSION_GAP_MS = 120000;
  const ACTIVE_WINDOW_MS = 90000;

  const adultPattern = /(mylust|stripchat|xgroovy|youjizz|xxxjmp|fuckserve|cam-content|magsrv|orbsrv|doppiocdn|tsyndicate|ahcdn|chaturbate|pornhub|xvideos|xnxx|redtube|tube8|spankbang|brazzers)/i;
  const infrastructurePattern = /(cdn|static|assets|img\.|image\.|video\.|websocket|edge-hls|analytics|tagmanager|sentry|fonts\.|cloudflare|doubleclick|ads?\.|rtb-|pixel|pxl-)/i;
  const mediaPattern = /(video|hls|stream|media|websocket|\.m3u8|doppiocdn|ahcdn)/i;

  function isAdultEvent(event) {
    return String(event.category || '').toLowerCase() === 'adult' || adultPattern.test(String(event.domain || ''));
  }

  function eventKind(domain) {
    if (mediaPattern.test(domain)) return 'Media activity';
    if (infrastructurePattern.test(domain)) return 'Supporting service';
    return 'Site activity';
  }

  function deviceKey(event) {
    return event.deviceId || event.deviceName || event.deviceIp || 'unknown';
  }

  function buildLiveAdultSessions(events) {
    const sorted = [...events]
      .filter(event => event && event.domain)
      .sort((a, b) => parseServerDate(a.timestamp) - parseServerDate(b.timestamp));

    const sessions = [];
    const openByDevice = new Map();

    for (const event of sorted) {
      const key = deviceKey(event);
      const at = parseServerDate(event.timestamp);
      if (Number.isNaN(at.getTime())) continue;

      let session = openByDevice.get(key);
      const adult = isAdultEvent(event);
      const canJoin = session && at - parseServerDate(session.end) <= SESSION_GAP_MS;

      if (!session || !canJoin) {
        if (!adult) continue;
        session = {
          key,
          deviceName: event.deviceName || event.deviceIp || 'Unknown device',
          deviceIp: event.deviceIp || '',
          start: event.timestamp,
          end: event.timestamp,
          adultRequests: 0,
          totalRequests: 0,
          timeline: [],
          domains: new Map()
        };
        sessions.push(session);
        openByDevice.set(key, session);
      }

      session.end = event.timestamp;
      session.totalRequests++;
      if (adult) session.adultRequests++;

      const domain = String(event.domain).toLowerCase();
      const existing = session.domains.get(domain) || {
        domain,
        first: event.timestamp,
        last: event.timestamp,
        count: 0,
        adult,
        kind: eventKind(domain)
      };
      existing.count++;
      existing.last = event.timestamp;
      existing.adult = existing.adult || adult;
      session.domains.set(domain, existing);

      const previous = session.timeline[session.timeline.length - 1];
      const root = rootDomain(domain);
      const shouldAdd = adult && (!previous || previous.root !== root || at - parseServerDate(previous.last) > 15000);
      if (shouldAdd) {
        session.timeline.push({ root, domain, first: event.timestamp, last: event.timestamp, count: 1, kind: eventKind(domain) });
      } else if (previous && previous.root === root) {
        previous.last = event.timestamp;
        previous.count++;
      }
    }

    return sessions
      .filter(session => session.adultRequests > 0)
      .sort((a, b) => parseServerDate(b.end) - parseServerDate(a.end));
  }

  function liveSessionCard(session) {
    const now = Date.now();
    const endMs = parseServerDate(session.end).getTime();
    const active = now - endMs <= ACTIVE_WINDOW_MS;
    const durationEnd = active ? now : endMs;
    const durationSeconds = Math.max(1, Math.round((durationEnd - parseServerDate(session.start).getTime()) / 1000));
    const domains = [...session.domains.values()].sort((a, b) => b.count - a.count);
    const mainSites = domains.filter(item => item.adult && !infrastructurePattern.test(item.domain));
    const supporting = domains.filter(item => infrastructurePattern.test(item.domain));
    const latestMain = [...mainSites].sort((a, b) => parseServerDate(b.last) - parseServerDate(a.last))[0];
    const current = (latestMain || domains[0] || {}).domain || 'Unknown';
    const status = active ? '<span class="session-live-badge"><span></span>LIVE</span>' : '<span class="session-ended-badge">ENDED</span>';

    const timeline = session.timeline.length
      ? session.timeline.map(item => `<li class="session-timeline-item">
          <time>${escapeHtml(formatEventTime(item.first))}</time>
          <span class="session-timeline-dot"></span>
          <div><strong>${escapeHtml(item.domain)}</strong><small>${escapeHtml(item.kind)}${item.count > 1 ? ` · ${item.count} related requests` : ''}</small></div>
        </li>`).join('')
      : '<li class="session-timeline-empty">No timeline entries available.</li>';

    const technical = supporting.length
      ? `<details class="session-evidence"><summary>Technical evidence (${supporting.length} domains)</summary><div class="session-evidence-grid">${supporting.slice(0, 60).map(item => `<span><strong>${escapeHtml(item.domain)}</strong><small>${item.count} requests</small></span>`).join('')}</div></details>`
      : '';

    return `<article class="panel live-session-card ${active ? 'is-live' : ''}">
      <header class="live-session-header">
        <div class="session-device"><span class="device-avatar">${escapeHtml(session.deviceName.slice(0, 1).toUpperCase())}</span><div><div class="session-title-line">${status}<strong>${escapeHtml(session.deviceName)}</strong></div><small>${escapeHtml(session.deviceIp)}</small></div></div>
        <div class="session-current"><span>Current / latest site</span><strong>${escapeHtml(current)}</strong><small>Last activity ${escapeHtml(formatRelative(session.end))}</small></div>
      </header>
      <div class="live-session-metrics">
        <span><small>Duration</small><strong>${escapeHtml(formatDuration(durationSeconds))}</strong></span>
        <span><small>Adult requests</small><strong>${session.adultRequests}</strong></span>
        <span><small>Unique domains</small><strong>${domains.length}</strong></span>
        <span><small>Started</small><strong>${escapeHtml(formatEventTime(session.start))}</strong></span>
      </div>
      <section class="session-timeline-section"><div class="session-subheading"><strong>Live timeline</strong><span>${active ? 'Updates every 3 seconds' : `Ended ${escapeHtml(formatEventTime(session.end))}`}</span></div><ol class="session-timeline">${timeline}</ol></section>
      ${technical}
    </article>`;
  }

  async function loadLiveSessions() {
    const list = byId('sessionList');
    const refresh = byId('refreshSessions');
    if (!list || !refresh) return;
    refresh.disabled = true;
    const params = rangeParams('sessionRange', 'sessionFrom', 'sessionTo');
    params.set('pageSize', '500');
    const search = byId('sessionSearch').value.trim().toLowerCase();
    try {
      const response = await fetch(`/api/activity?${params}`, { cache: 'no-store' });
      if (!response.ok) throw new Error('Could not load live sessions.');
      const data = await response.json();
      const sessions = buildLiveAdultSessions(data.events || []).filter(session => !search || [session.deviceName, session.deviceIp, ...session.domains.keys()].some(value => String(value || '').toLowerCase().includes(search)));
      list.innerHTML = sessions.length ? sessions.map(liveSessionCard).join('') : '<div class="panel empty">No adult-content sessions match this period or search.</div>';
    } catch (error) {
      list.innerHTML = `<div class="panel empty error">${escapeHtml(error.message)}</div>`;
    } finally {
      refresh.disabled = false;
    }
  }

  window.loadSessions = loadLiveSessions;

  const oldRefresh = byId('refreshSessions');
  if (oldRefresh) {
    const newRefresh = oldRefresh.cloneNode(true);
    oldRefresh.replaceWith(newRefresh);
    newRefresh.addEventListener('click', loadLiveSessions);
  }

  byId('sessionRange')?.addEventListener('change', loadLiveSessions);

  setInterval(() => {
    if (byId('sessionsView')?.classList.contains('active')) loadLiveSessions();
  }, LIVE_POLL_MS);
})();
