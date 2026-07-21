(() => {
  const baseRender = render;

  function uniq(values) {
    return [...new Set((values || []).filter(Boolean))];
  }

  function roleGroups(session) {
    const groups = {
      stream: [], thumb: [], image: [], search: [], live: [], chat: [], api: [],
      ad: [], popup: [], tracker: [], auth: [], download: [], other: []
    };
    (session.events || []).forEach(event => {
      const role = event.hostnameRole || 'other';
      (groups[role] || groups.other).push(event.domain);
    });
    Object.keys(groups).forEach(key => groups[key] = uniq(groups[key]));
    return groups;
  }

  function evidenceItems(session) {
    const items = [];
    if ((session.siteSequence || []).length) items.push(`Direct visits: ${session.siteSequence.join(' → ')}`);
    if ((session.streamRequests || 0) > 0 || (session.counts?.stream || 0) > 0) items.push(`${Math.max(session.streamRequests || 0, session.counts?.stream || 0)} media-delivery signals`);
    if ((session.possiblePlaybackPhases || 0) > 0) items.push(`${session.possiblePlaybackPhases} possible playback phase${session.possiblePlaybackPhases === 1 ? '' : 's'}`);
    if ((session.assetLoadingBursts || 0) > 0) items.push(`${session.assetLoadingBursts} image/asset-loading burst${session.assetLoadingBursts === 1 ? '' : 's'}`);
    if ((session.counts?.search || 0) > 0) items.push(`${session.counts.search} search/discovery signal${session.counts.search === 1 ? '' : 's'}`);
    if ((session.counts?.popup || 0) > 0) items.push(`${session.counts.popup} popup/redirect signal${session.counts.popup === 1 ? '' : 's'}`);
    if ((session.counts?.ad || 0) > 0) items.push(`${session.counts.ad} advertising signal${session.counts.ad === 1 ? '' : 's'}`);
    if ((session.counts?.tracker || 0) > 0) items.push(`${session.counts.tracker} tracking signal${session.counts.tracker === 1 ? '' : 's'}`);
    return items;
  }

  function activityLabel(value) {
    if (value >= 80) return 'Very high';
    if (value >= 60) return 'High';
    if (value >= 35) return 'Moderate';
    if (value >= 15) return 'Low';
    return 'Minimal';
  }

  function groupBlock(label, domains, open = false) {
    if (!domains.length) return '';
    return `<details class="intelGroup" ${open ? 'open' : ''}><summary>${esc(label)} <span>${domains.length}</span></summary><div>${domains.map(d => `<code>${esc(d)}</code>`).join('')}</div></details>`;
  }

  function buildTimeline(session) {
    const events = [...(session.events || [])].sort((a,b)=>new Date(a.time)-new Date(b.time));
    const timeline = [];
    let lastSite = '';
    let lastRole = '';
    let lastAt = 0;
    const add = (event, label, detail, confidence) => {
      const at = new Date(event.time).getTime();
      if (label === lastRole && at - lastAt < 45000) return;
      timeline.push({time:event.time,label,detail,confidence});
      lastRole = label; lastAt = at;
    };
    events.forEach(event => {
      const role = event.hostnameRole || 'other';
      if (event.evidence === 'direct') {
        const site = siteName(event.domain);
        if (site !== lastSite) {
          add(event, `Opened ${site}`, 'Direct adult-site hostname observed', Math.max(90,event.confidence||0));
          lastSite = site;
        }
      } else if (role === 'search') add(event,'Search or discovery activity','Search-related hostname observed',event.hostnameRoleConfidence||70);
      else if (role === 'thumb') add(event,'Browsed previews or thumbnails','Thumbnail hostname burst observed',event.hostnameRoleConfidence||75);
      else if (role === 'stream' || event.evidence === 'stream') add(event,'Possible video playback','Media-delivery hostname observed',Math.max(80,event.hostnameRoleConfidence||0,event.confidence||0));
      else if (role === 'live') add(event,'Possible live-cam activity','Live-service hostname observed',event.hostnameRoleConfidence||75);
      else if (role === 'popup') add(event,'Possible popup or redirect','Redirect-style hostname observed',event.hostnameRoleConfidence||75);
      else if (role === 'ad' || event.evidence === 'ad') add(event,'Advertising request','Advertising hostname observed',event.hostnameRoleConfidence||65);
      else if (role === 'chat') add(event,'Chat or messaging activity','Messaging hostname observed',event.hostnameRoleConfidence||65);
    });
    return timeline.slice(0,24);
  }

  function fingerprint(session, groups) {
    const primary = (session.siteSequence || [])[0] || 'Not established';
    const secondary = (session.siteSequence || []).slice(1);
    const adNetworks = groups.ad.length + groups.popup.length + groups.tracker.length;
    return [
      ['Primary site', primary],
      ['Other sites', secondary.length ? secondary.join(', ') : 'None observed'],
      ['Observed duration', `${session.durationMinutes || 1} min`],
      ['Possible playback phases', session.possiblePlaybackPhases || 0],
      ['Site changes', Math.max(0,(session.siteSequence || []).length - 1)],
      ['Advertising/tracking networks', adNetworks],
      ['Popup hosts', groups.popup.length],
      ['Overall confidence', `${session.confidence}%`]
    ];
  }

  function sessionCard(session) {
    const groups = roleGroups(session);
    const evidence = evidenceItems(session);
    const advertisingCount = groups.ad.length + groups.popup.length + groups.tracker.length;
    const timeline = buildTimeline(session);
    const fp = fingerprint(session,groups);
    return `<article class="session intelSession">
      <div class="intelSessionHeader">
        <div><strong>${esc(session.clientName)}</strong><span>${esc(session.client)}</span></div>
        <div><strong>${new Date(session.start).toLocaleTimeString([], {hour:'2-digit', minute:'2-digit'})}–${new Date(session.end).toLocaleTimeString([], {hour:'2-digit', minute:'2-digit'})}</strong><span>${new Date(session.start).toLocaleDateString()} · ${session.durationSeconds}s observed</span></div>
        <div class="intelConfidence"><strong>${session.confidence}%</strong><span>session confidence</span></div>
      </div>
      <div class="intelSummary">
        <h3>${esc(session.behaviorSummary)}</h3>
        <p>${esc(session.narrative)}</p>
        ${(session.siteSequence || []).length ? `<div class="sitePath"><b>Site path</b> ${session.siteSequence.map(esc).join(' → ')}</div>` : ''}
      </div>
      <div class="intelMetrics">
        <div><b>${session.videoLikelihood}%</b><span>Video likelihood</span></div>
        <div><b>${session.browsingLikelihood}%</b><span>Browsing likelihood</span></div>
        <div><b>${session.liveLikelihood}%</b><span>Live-cam likelihood</span></div>
        <div><b>${session.popupLikelihood}%</b><span>Popup likelihood</span></div>
        <div><b>${activityLabel(session.adIntensity)}</b><span>Advertising · ${advertisingCount} networks</span></div>
      </div>
      <div class="intelSplit">
        <section class="fingerprint"><h4>Session fingerprint</h4>${fp.map(([k,v])=>`<div><span>${esc(k)}</span><b>${esc(v)}</b></div>`).join('')}</section>
        <section class="behaviorTimeline"><h4>Behavior timeline</h4>${timeline.length?timeline.map(item=>`<div class="timelineItem"><time>${new Date(item.time).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',second:'2-digit'})}</time><span><b>${esc(item.label)}</b><small>${esc(item.detail)} · ${item.confidence}% confidence</small></span></div>`).join(''):'<p>No distinct behavior transitions were inferred.</p>'}</section>
      </div>
      ${evidence.length ? `<div class="intelEvidence"><b>Why HomeWatch reached this conclusion</b>${evidence.map(x => `<span>✓ ${esc(x)}</span>`).join('')}</div>` : ''}
      ${(session.contentHints || []).length ? `<div class="contentHints"><b>Hostname content hints</b>${session.contentHints.map(x => `<span>${esc(x)}</span>`).join('')}</div>` : ''}
      <div class="intelGroups">
        ${groupBlock('Video delivery', groups.stream, true)}
        ${groupBlock('Thumbnails and previews', groups.thumb)}
        ${groupBlock('Images and static assets', groups.image)}
        ${groupBlock('Search and discovery', groups.search)}
        ${groupBlock('Live-cam services', groups.live)}
        ${groupBlock('Chat and messaging', groups.chat)}
        ${groupBlock('Application APIs', groups.api)}
        ${groupBlock('Advertising', groups.ad)}
        ${groupBlock('Popup and redirect hosts', groups.popup, groups.popup.length > 0)}
        ${groupBlock('Tracking and analytics', groups.tracker)}
        ${groupBlock('Authentication', groups.auth)}
        ${groupBlock('Downloads', groups.download)}
        ${groupBlock('Other supporting domains', groups.other)}
      </div>
    </article>`;
  }

  render = function () {
    baseRender();
    const selection = filtered();
    const target = document.querySelector('#sessions');
    if (target && selection.sessions.length) target.innerHTML = selection.sessions.map(sessionCard).join('');
  };

  const style = document.createElement('style');
  style.textContent = `
    .intelSession{display:block!important;padding:18px!important;margin-bottom:12px}
    .intelSessionHeader{display:grid;grid-template-columns:1fr 1fr auto;gap:18px;align-items:center;border-bottom:1px solid #dce7eb;padding-bottom:12px}
    .intelSessionHeader span,.intelConfidence span{display:block;color:#6b818a;font-size:12px;margin-top:3px}
    .intelConfidence{text-align:right}.intelSummary{padding:14px 0 8px}.intelSummary h3{margin:0 0 6px;font-size:17px}.intelSummary p{margin:0;color:#516970;line-height:1.45}
    .sitePath{margin-top:10px;color:#47636d}.sitePath b{margin-right:8px}
    .intelMetrics{display:grid;grid-template-columns:repeat(5,minmax(110px,1fr));gap:8px;margin:10px 0}
    .intelMetrics>div{background:#edf8f6;border-radius:9px;padding:10px}.intelMetrics b{display:block;font-size:16px}.intelMetrics span{display:block;color:#60777f;font-size:11px;margin-top:2px}
    .intelSplit{display:grid;grid-template-columns:minmax(240px,.8fr) minmax(320px,1.2fr);gap:10px;margin:12px 0}.fingerprint,.behaviorTimeline{background:#f7fafb;border:1px solid #dce7eb;border-radius:10px;padding:12px}.fingerprint h4,.behaviorTimeline h4{margin:0 0 9px}.fingerprint>div{display:flex;justify-content:space-between;gap:12px;border-top:1px solid #e4ecef;padding:6px 0;font-size:12px}.fingerprint>div:first-of-type{border-top:0}.fingerprint span{color:#60777f}.fingerprint b{text-align:right}.behaviorTimeline{max-height:330px;overflow:auto}.timelineItem{display:grid;grid-template-columns:78px 1fr;gap:9px;padding:7px 0;border-top:1px solid #e4ecef}.timelineItem:first-of-type{border-top:0}.timelineItem time{font:11px Consolas,monospace;color:#50707b}.timelineItem span b,.timelineItem span small{display:block}.timelineItem span b{font-size:12px}.timelineItem span small{color:#6a7e86;font-size:11px;margin-top:2px}
    .intelEvidence{background:#f4f8fa;border-radius:9px;padding:11px;margin:10px 0}.intelEvidence>b{display:block;margin-bottom:6px}.intelEvidence span{display:block;color:#4f6870;font-size:12px;margin:3px 0}
    .contentHints{display:flex;align-items:center;gap:6px;flex-wrap:wrap;margin:10px 0}.contentHints>b{margin-right:4px}.contentHints span{background:#fff2d9;border-radius:999px;padding:4px 8px;font-size:11px}
    .intelGroups{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px;margin-top:12px}.intelGroup{border:1px solid #dbe6ea;border-radius:8px;background:#fff}.intelGroup summary{cursor:pointer;padding:9px 11px;font-weight:700;font-size:12px}.intelGroup summary span{float:right;background:#e8f0f3;border-radius:999px;padding:1px 7px}.intelGroup>div{padding:0 10px 10px;max-height:170px;overflow:auto}.intelGroup code{display:block;white-space:normal;overflow-wrap:anywhere;font-size:11px;padding:3px 0;color:#526a73}
    @media(max-width:800px){.intelSessionHeader{grid-template-columns:1fr}.intelConfidence{text-align:left}.intelMetrics{grid-template-columns:repeat(2,1fr)}.intelGroups,.intelSplit{grid-template-columns:1fr}}
  `;
  document.head.appendChild(style);
})();