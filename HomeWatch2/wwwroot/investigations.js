(() => {
  const $ = id => document.getElementById(id);
  const esc = value => String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  const date = value => typeof parseServerDate === 'function' ? parseServerDate(value) : new Date(value);
  let investigations = [];

  function rootDomain(value) {
    const domain = String(value || '').trim().toLowerCase().replace(/^\.+|\.+$/g, '');
    if (!domain) return 'unknown';
    if (/^\d{1,3}(\.\d{1,3}){3}$/.test(domain) || domain === 'localhost') return domain;
    const parts = domain.split('.').filter(Boolean);
    if (parts.length <= 2) return domain;
    const commonSecondLevelSuffixes = new Set([
      'co.uk','org.uk','gov.uk','ac.uk','com.au','net.au','org.au','co.nz','com.br','com.mx','co.jp','co.kr','co.in','com.sg','com.tr','com.cn','com.hk','com.tw','co.za','com.ar','com.sa','com.ng'
    ]);
    const lastTwo = parts.slice(-2).join('.');
    return commonSecondLevelSuffixes.has(lastTwo) && parts.length >= 3
      ? parts.slice(-3).join('.')
      : lastTwo;
  }

  function typeOf(event) {
    const category = String(event.category || '').toLowerCase();
    const domain = String(event.domain || '').toLowerCase();
    if (category === 'adult') return 'adult';
    if (/(private-relay|privaterelay|dns\.google|cloudflare-dns|nextdns|quad9|doh|proxy|vpn)/.test(domain) || /(bypass|encrypted-dns|proxy|vpn)/.test(category)) return 'privacy-proxy';
    if (/(youtube|googlevideo|ytimg|netflix|nflx|spotify|twitch|vimeo|hulu|primevideo)/.test(domain) || category === 'streaming') return 'streaming';
    if (/(facebook|instagram|tiktok|snapchat|reddit|twitter|x\.com|pinterest)/.test(domain) || category === 'social') return 'social';
    if (/(whatsapp|telegram|signal|messenger|discord|skype|teams)/.test(domain) || category === 'messaging') return 'messaging';
    if (/(amazon|ebay|etsy|walmart|bestbuy|shopify|temu|aliexpress)/.test(domain) || category === 'shopping') return 'shopping';
    if (/(doubleclick|googlesyndication|googleadservices|analytics|tracking|pixel)/.test(domain) || category === 'advertising') return 'advertising';
    if (/(github|gitlab|npmjs|nuget|docker|stackoverflow|visualstudio)/.test(domain) || category === 'development') return 'development';
    if (/(office|outlook|notion|slack|zoom|dropbox|drive\.google|docs\.google)/.test(domain) || category === 'productivity') return 'productivity';
    if (/(steam|xbox|playstation|epicgames|roblox|minecraft|nintendo)/.test(domain) || category === 'gaming') return 'gaming';
    return 'system';
  }

  function build(events) {
    const sorted = [...events].sort((a,b) => date(a.timestamp) - date(b.timestamp));
    const groups = new Map();
    for (const event of sorted) {
      const deviceKey = event.deviceId || event.deviceName || event.deviceIp || 'unknown';
      const root = rootDomain(event.domain);
      const groupKey = `${deviceKey}|${root}`;
      if (!groups.has(groupKey)) groups.set(groupKey, []);
      const list = groups.get(groupKey);
      let item = list[list.length - 1];
      const timestamp = date(event.timestamp);
      if (!item || timestamp - date(item.end) > 15 * 60 * 1000) {
        item = {
          id: `${deviceKey}-${root}-${timestamp.getTime()}`,
          deviceId: event.deviceId || '',
          deviceName: event.deviceName || event.deviceIp || 'Unknown device',
          deviceIp: event.deviceIp || '',
          rootDomain: root,
          start: event.timestamp,
          end: event.timestamp,
          events: []
        };
        list.push(item);
      }
      item.end = event.timestamp;
      item.events.push({...event, rootDomain: root, investigationType: typeOf(event)});
    }
    const result = [...groups.values()].flat();
    for (const item of result) enrich(item);
    return result.sort((a,b) => date(b.end) - date(a.end));
  }

  function enrich(item) {
    const counts = {};
    const domains = new Map();
    item.events.forEach(event => {
      counts[event.investigationType] = (counts[event.investigationType] || 0) + 1;
      const key = String(event.domain || 'unknown').toLowerCase();
      if (!domains.has(key)) domains.set(key, {domain:key, type:event.investigationType, count:0, first:event.timestamp, last:event.timestamp});
      const d = domains.get(key); d.count++; d.last = event.timestamp;
    });
    item.domains = [...domains.values()].sort((a,b) => b.count - a.count);
    item.counts = counts;
    item.primaryType = Object.entries(counts).sort((a,b) => b[1]-a[1])[0]?.[0] || 'system';
    const adultDomains = item.domains.filter(d => d.type === 'adult').length;
    const proxySignals = counts['privacy-proxy'] || 0;
    const streamingSignals = counts.streaming || 0;
    item.confidence = item.primaryType === 'adult' ? Math.min(99, 55 + adultDomains * 12 + Math.min(20, streamingSignals * 2)) : Math.min(95, 45 + Math.min(35, item.domains.length * 3));
    item.risk = item.primaryType === 'adult' ? (item.confidence >= 85 ? 'High' : 'Medium') : proxySignals ? 'Medium' : 'Low';
    item.durationSeconds = Math.max(0, Math.round((date(item.end)-date(item.start))/1000));
    item.reviewed = localStorage.getItem(`hw-reviewed-${item.id}`) === '1';
  }

  function label(type) { return ({adult:'Adult activity','privacy-proxy':'Privacy or bypass activity',streaming:'Streaming activity',social:'Social-media activity',messaging:'Messaging activity',shopping:'Shopping activity',advertising:'Advertising and tracking',development:'Development activity',productivity:'Productivity activity',gaming:'Gaming activity',system:'General network activity'})[type] || 'Network activity'; }
  function duration(seconds) { if (seconds < 60) return seconds <= 5 ? 'Single visit' : `${seconds}s`; if (seconds < 3600) return `${Math.max(1, Math.round(seconds/60))} min`; return `${Math.floor(seconds/3600)}h ${Math.round((seconds%3600)/60)}m`; }
  function time(value) { return date(value).toLocaleTimeString([], {hour:'2-digit', minute:'2-digit', second:'2-digit'}); }

  function card(item) {
    const badge = item.reviewed ? '<span class="investigation-reviewed">Reviewed</span>' : '';
    return `<article class="panel investigation-card" data-investigation-id="${esc(item.id)}">
      <div class="investigation-risk risk-${esc(item.risk.toLowerCase())}"><strong>${esc(item.risk)}</strong><span>risk</span></div>
      <div class="investigation-main"><div class="investigation-title"><span class="device-avatar">${esc(item.deviceName.slice(0,1).toUpperCase())}</span><div><h3>${esc(item.rootDomain)}</h3><button type="button" data-open-device="${esc(item.deviceId)}">${esc(item.deviceName)}</button><small>${esc(item.deviceIp)} · ${esc(label(item.primaryType))}</small></div></div>
      <p>${esc(summary(item))}</p><div class="investigation-tags"><span>Root domain: ${esc(item.rootDomain)}</span>${Object.entries(item.counts).sort((a,b)=>b[1]-a[1]).slice(0,3).map(([key,value])=>`<span>${esc(label(key))}: ${value}</span>`).join('')}</div></div>
      <div class="investigation-stats"><strong>${item.confidence}%</strong><span>confidence</span><strong>${item.events.length}</strong><span>DNS events</span><strong>${item.domains.length}</strong><span>subdomains</span></div>
      <div class="investigation-time"><strong>${esc(duration(item.durationSeconds))}</strong><span>${esc(time(item.start))} – ${esc(time(item.end))}</span>${badge}<button type="button" class="primary" data-open-investigation="${esc(item.id)}">Investigate</button></div>
    </article>`;
  }

  function summary(item) {
    const top = item.domains.slice(0,3).map(x=>x.domain).join(', ');
    if (item.primaryType === 'adult') return `HomeWatch grouped ${item.events.length} DNS event(s) under ${item.rootDomain}, including ${item.domains.length} related hostname(s). Adult-related evidence includes ${top || item.rootDomain}.`;
    if (item.primaryType === 'privacy-proxy') return `HomeWatch grouped encrypted-DNS, private-relay, VPN, or proxy indicators under ${item.rootDomain}. Review the timeline for possible monitoring bypass.`;
    return `${item.deviceName} contacted ${item.rootDomain} through ${item.domains.length} related hostname(s), including ${top || item.rootDomain}.`;
  }

  function render() {
    const search = $('sessionSearch')?.value.trim().toLowerCase() || '';
    const selected = $('sessionCategory')?.value || 'all';
    const filtered = investigations.filter(item => {
      const searchMatch = !search || [item.deviceName,item.deviceIp,item.rootDomain,...item.domains.map(d=>d.domain)].some(v=>String(v||'').toLowerCase().includes(search));
      const typeMatch = selected === 'all' || item.primaryType === selected || (selected === 'other' && item.primaryType === 'system');
      return searchMatch && typeMatch;
    });
    $('sessionList').innerHTML = filtered.length ? filtered.map(card).join('') : '<div class="panel empty">No investigations match the selected filters.</div>';
    document.querySelectorAll('[data-open-investigation]').forEach(button => button.addEventListener('click', () => open(button.dataset.openInvestigation)));
    document.querySelectorAll('[data-open-device]').forEach(button => button.addEventListener('click', async () => { if (!button.dataset.openDevice) return; switchView('devices'); await loadDevices(false,true); if (allDevices.some(d=>d.id===button.dataset.openDevice)) await selectDevice(button.dataset.openDevice,true); }));
  }

  async function load() {
    const button = $('refreshSessions'); if (button) button.disabled = true;
    $('sessionList').innerHTML = '<div class="panel empty">Building root-domain investigations…</div>';
    try {
      const response = await fetch(`/api/dashboard?hours=${encodeURIComponent($('sessionHours').value)}`, {cache:'no-store'});
      if (!response.ok) throw new Error('Could not load network activity.');
      const data = await response.json(); investigations = build(data.events || []); render();
    } catch (error) { $('sessionList').innerHTML = `<div class="panel empty error">${esc(error.message)}</div>`; }
    finally { if (button) button.disabled = false; }
  }

  function open(id) {
    const item = investigations.find(x=>x.id===id); if (!item) return;
    const drawer = $('investigationDrawer');
    $('investigationDrawerContent').innerHTML = `<div class="investigation-header"><div><span class="eyebrow">ROOT-DOMAIN INVESTIGATION</span><h2>${esc(item.rootDomain)}</h2><p>${esc(item.deviceName)} · ${esc(item.deviceIp)} · ${esc(label(item.primaryType))}</p></div><div class="score"><strong>${item.confidence}%</strong><span>confidence</span></div></div>
      <section class="investigation-summary"><h3>Assessment</h3><p>${esc(summary(item))}</p><div class="investigation-facts"><span><b>Risk</b>${esc(item.risk)}</span><span><b>Duration</b>${esc(duration(item.durationSeconds))}</span><span><b>Events</b>${item.events.length}</span><span><b>Subdomains</b>${item.domains.length}</span></div></section>
      <section><h3>Evidence groups</h3><div class="evidence-groups">${Object.entries(item.counts).sort((a,b)=>b[1]-a[1]).map(([key,value])=>`<article><strong>${esc(label(key))}</strong><span>${value} signal${value===1?'':'s'}</span></article>`).join('')}</div></section>
      <section><h3>Hostnames grouped under ${esc(item.rootDomain)}</h3><div class="domain-evidence">${item.domains.map(d=>`<article><div><strong>${esc(d.domain)}</strong><span>${esc(label(d.type))}</span></div><b>${d.count}</b></article>`).join('')}</div></section>
      <section><h3>Timeline</h3><div class="investigation-timeline">${item.events.slice().sort((a,b)=>date(a.timestamp)-date(b.timestamp)).map(event=>`<article><time>${esc(time(event.timestamp))}</time><span></span><div><strong>${esc(event.domain)}</strong><small>${esc(label(event.investigationType))} · ${esc(event.action || 'observed')}</small></div></article>`).join('')}</div></section>
      <div class="investigation-actions"><button id="markInvestigationReviewed">${item.reviewed?'Mark unreviewed':'Mark reviewed'}</button><button id="exportInvestigation">Export JSON</button>${item.deviceId?'<button id="openInvestigationDevice">Open device history</button>':''}</div>`;
    drawer.hidden = false; document.body.classList.add('investigation-open');
    $('markInvestigationReviewed').onclick = () => { item.reviewed=!item.reviewed; localStorage.setItem(`hw-reviewed-${item.id}`, item.reviewed?'1':'0'); close(); render(); };
    $('exportInvestigation').onclick = () => { const blob=new Blob([JSON.stringify(item,null,2)],{type:'application/json'}); const a=document.createElement('a'); a.href=URL.createObjectURL(blob); a.download=`homewatch-${item.rootDomain}-${item.id}.json`; a.click(); URL.revokeObjectURL(a.href); };
    $('openInvestigationDevice')?.addEventListener('click', async()=>{ close(); switchView('devices'); await loadDevices(false,true); if(allDevices.some(d=>d.id===item.deviceId)) await selectDevice(item.deviceId,true); });
  }
  function close(){ $('investigationDrawer').hidden=true; document.body.classList.remove('investigation-open'); }

  window.loadSessions = load;
  document.addEventListener('DOMContentLoaded', () => {
    $('closeInvestigationDrawer')?.addEventListener('click', close);
    $('investigationBackdrop')?.addEventListener('click', close);
    $('refreshSessions')?.addEventListener('click', load);
    $('sessionSearch')?.addEventListener('input', render);
    $('sessionCategory')?.addEventListener('change', render);
    $('sessionHours')?.addEventListener('change', load);
  });
})();