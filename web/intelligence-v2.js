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
    if ((session.siteSequence || []).length) {
      items.push(`Direct visits: ${session.siteSequence.join(' → ')}`);
    }
    if ((session.streamRequests || 0) > 0 || (session.counts?.stream || 0) > 0) {
      items.push(`${Math.max(session.streamRequests || 0, session.counts?.stream || 0)} media-delivery signals`);
    }
    if ((session.possiblePlaybackPhases || 0) > 0) {
      items.push(`${session.possiblePlaybackPhases} possible playback phase${session.possiblePlaybackPhases === 1 ? '' : 's'}`);
    }
    if ((session.assetLoadingBursts || 0) > 0) {
      items.push(`${session.assetLoadingBursts} image/asset-loading burst${session.assetLoadingBursts === 1 ? '' : 's'}`);
    }
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

  function sessionCard(session) {
    const groups = roleGroups(session);
    const evidence = evidenceItems(session);
    const advertisingCount = groups.ad.length + groups.popup.length + groups.tracker.length;
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
    .intelEvidence{background:#f4f8fa;border-radius:9px;padding:11px;margin:10px 0}.intelEvidence>b{display:block;margin-bottom:6px}.intelEvidence span{display:block;color:#4f6870;font-size:12px;margin:3px 0}
    .contentHints{display:flex;align-items:center;gap:6px;flex-wrap:wrap;margin:10px 0}.contentHints>b{margin-right:4px}.contentHints span{background:#fff2d9;border-radius:999px;padding:4px 8px;font-size:11px}
    .intelGroups{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px;margin-top:12px}.intelGroup{border:1px solid #dbe6ea;border-radius:8px;background:#fff}.intelGroup summary{cursor:pointer;padding:9px 11px;font-weight:700;font-size:12px}.intelGroup summary span{float:right;background:#e8f0f3;border-radius:999px;padding:1px 7px}.intelGroup>div{padding:0 10px 10px;max-height:170px;overflow:auto}.intelGroup code{display:block;white-space:normal;overflow-wrap:anywhere;font-size:11px;padding:3px 0;color:#526a73}
    @media(max-width:800px){.intelSessionHeader{grid-template-columns:1fr}.intelConfidence{text-align:left}.intelMetrics{grid-template-columns:repeat(2,1fr)}.intelGroups{grid-template-columns:1fr}}
  `;
  document.head.appendChild(style);
})();