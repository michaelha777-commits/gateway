(() => {
  const previousRender = render;

  const page = new URLSearchParams(location.search).get('page') || 'dashboard';

  function applyPageMode() {
    document.body.dataset.page = page;
    document.querySelectorAll('[data-page-link]').forEach(link => {
      link.classList.toggle('active', link.dataset.pageLink === page);
    });
    document.querySelectorAll('.dashboardPage').forEach(el => el.hidden = page === 'devices');
    document.querySelectorAll('.devicesPage').forEach(el => el.hidden = page !== 'devices');
    const title = document.querySelector('header h1');
    if (title && page === 'devices') title.setAttribute('aria-label', 'HomeWatch Devices');
  }

  function unique(values) {
    return [...new Set((values || []).filter(Boolean))];
  }

  function roleLabel(role) {
    return ({
      stream: 'Video delivery', thumb: 'Thumbnails', image: 'Images/assets', search: 'Search',
      live: 'Live cam', chat: 'Chat', api: 'API', ad: 'Advertising', popup: 'Popup/redirect',
      tracker: 'Tracking', auth: 'Authentication', download: 'Download', other: 'Supporting service'
    })[role] || role;
  }

  function graphData(session) {
    const events = session.events || [];
    const sites = unique(session.siteSequence || []);
    const fallbackSite = sites[0] || 'Adult session';
    const bySite = new Map(sites.map(site => [site, new Map()]));
    if (!bySite.size) bySite.set(fallbackSite, new Map());

    let currentSite = fallbackSite;
    events.slice().sort((a,b) => new Date(a.time)-new Date(b.time)).forEach(event => {
      if (event.evidence === 'direct') {
        const site = siteName(event.domain);
        if (!bySite.has(site)) bySite.set(site, new Map());
        currentSite = site;
      }
      const roles = bySite.get(currentSite) || bySite.values().next().value;
      const role = event.hostnameRole || 'other';
      if (!roles.has(role)) roles.set(role, new Set());
      roles.get(role).add(event.domain);
    });
    return bySite;
  }

  function graphNode(label, type, count = null, details = '') {
    return `<button type="button" class="graphNode ${type}" aria-expanded="false"><span>${esc(label)}</span>${count == null ? '' : `<b>${count}</b>`}${details ? `<small>${esc(details)}</small>` : ''}</button>`;
  }

  function sessionGraph(session) {
    const data = graphData(session);
    const siteBlocks = [...data.entries()].map(([site, roles], siteIndex) => {
      const roleBlocks = [...roles.entries()]
        .filter(([,domains]) => domains.size)
        .sort((a,b) => b[1].size-a[1].size)
        .map(([role, domains]) => {
          const list = [...domains];
          return `<div class="graphBranch roleBranch">
            <div class="graphLine"></div>
            ${graphNode(roleLabel(role), `role-${role}`, list.length)}
            <div class="graphEvidence">${list.map(domain => `<code>${esc(domain)}</code>`).join('')}</div>
          </div>`;
        }).join('');
      return `<div class="graphBranch siteBranch">
        <div class="graphLine"></div>
        ${graphNode(site, 'site', [...roles.values()].reduce((sum,set) => sum + set.size, 0), siteIndex === 0 ? 'Primary observed site' : 'Observed site transition')}
        <div class="graphChildren">${roleBlocks || '<span class="graphEmpty">No classified supporting hosts</span>'}</div>
      </div>`;
    }).join('');

    return `<section class="intelligenceGraph">
      <div class="graphHeading"><div><h4>Intelligence graph</h4><p>Follow each observed site to the DNS roles and hostnames associated with it.</p></div><button type="button" class="secondary expandGraph">Expand all</button></div>
      <div class="graphRoot">
        ${graphNode(session.clientName || session.client, 'root', session.events?.length || session.requests || 0, 'DNS events in this session')}
        <div class="graphChildren rootChildren">${siteBlocks}</div>
      </div>
    </section>`;
  }

  function injectGraphs() {
    if (page === 'devices') return;
    const sessions = filtered().sessions || [];
    document.querySelectorAll('#sessions .intelSession').forEach((card, index) => {
      const session = sessions[index];
      if (!session || card.querySelector('.intelligenceGraph')) return;
      const summary = card.querySelector('.intelSummary');
      if (summary) summary.insertAdjacentHTML('afterend', sessionGraph(session));
    });
  }

  render = function () {
    previousRender();
    applyPageMode();
    injectGraphs();
  };

  document.addEventListener('click', event => {
    const node = event.target.closest('.graphNode');
    if (node) {
      const branch = node.closest('.graphBranch,.graphRoot');
      const children = branch?.querySelector(':scope > .graphChildren, :scope > .graphEvidence');
      if (children) {
        const open = !branch.classList.contains('graphOpen');
        branch.classList.toggle('graphOpen', open);
        node.setAttribute('aria-expanded', String(open));
      }
      return;
    }
    const expand = event.target.closest('.expandGraph');
    if (expand) {
      const graph = expand.closest('.intelligenceGraph');
      const shouldOpen = !graph.classList.contains('allOpen');
      graph.classList.toggle('allOpen', shouldOpen);
      graph.querySelectorAll('.graphBranch,.graphRoot').forEach(branch => branch.classList.toggle('graphOpen', shouldOpen));
      graph.querySelectorAll('.graphNode').forEach(button => button.setAttribute('aria-expanded', String(shouldOpen)));
      expand.textContent = shouldOpen ? 'Collapse all' : 'Expand all';
    }
  });

  const style = document.createElement('style');
  style.textContent = `
    .topNav{display:flex;gap:6px;align-items:center;flex-wrap:wrap}.topNav a{padding:8px 11px;border-radius:8px;text-decoration:none;color:#48636d;font-weight:700;font-size:13px}.topNav a:hover,.topNav a.active{background:#e7f7f4;color:#17766c}.devicesPage[hidden],.dashboardPage[hidden]{display:none!important}
    .devicePageIntro{display:flex;justify-content:space-between;gap:18px;align-items:center}.devicePageIntro p{margin:4px 0 0;color:#607780}.devicePageActions{display:flex;gap:8px;flex-wrap:wrap}
    .intelligenceGraph{margin:12px 0;border:1px solid #d8e5e9;border-radius:11px;background:#fbfdfe;padding:13px}.graphHeading{display:flex;justify-content:space-between;gap:12px;align-items:start}.graphHeading h4{margin:0;font-size:15px}.graphHeading p{margin:4px 0 0;color:#687f88;font-size:12px}.graphRoot{position:relative;margin-top:14px}.graphChildren{display:none;margin-left:24px;padding-left:18px;border-left:2px solid #d8e6ea}.graphOpen>.graphChildren,.graphOpen>.graphEvidence{display:block}.graphBranch{position:relative;padding:8px 0}.graphLine{position:absolute;left:-18px;top:25px;width:18px;border-top:2px solid #d8e6ea}.graphNode{display:grid;grid-template-columns:minmax(120px,auto) auto;gap:3px 10px;align-items:center;text-align:left;border:1px solid #d7e3e7;background:#fff;color:#274852;border-radius:9px;padding:8px 10px;box-shadow:none;max-width:100%;cursor:pointer}.graphNode:hover{background:#f1f8fa}.graphNode span{font-weight:800}.graphNode b{justify-self:end;background:#e7f1f4;border-radius:999px;padding:2px 7px;font-size:11px}.graphNode small{grid-column:1/-1;color:#6b8189}.graphNode.root{background:#e8f7f4;border-color:#bfe2db}.graphNode.site{background:#eef5fb;border-color:#cddfeb}.graphEvidence{display:none;margin:7px 0 0 18px;padding:7px 10px;border-left:2px solid #dce8eb}.graphEvidence code{display:block;overflow-wrap:anywhere;color:#526b74;font-size:11px;padding:2px 0}.graphEmpty{display:block;color:#7b8d94;font-size:12px;padding:8px}.role-ad,.role-popup,.role-tracker{border-color:#edd9c2}.role-stream{border-color:#bcded8}
    @media(max-width:700px){.devicePageIntro,.graphHeading{align-items:stretch;flex-direction:column}.graphChildren{margin-left:12px;padding-left:14px}.graphNode{width:100%}}
  `;
  document.head.appendChild(style);
  applyPageMode();
})();