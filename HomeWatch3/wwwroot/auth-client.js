window.HomeWatchTime = {
  date(value) {
    if(value==null || value==='')return null;
    const raw=String(value).trim();
    const utc=/^\d{4}-\d{2}-\d{2}T/.test(raw)&&!/(?:Z|[+-]\d{2}:?\d{2})$/i.test(raw)?raw+'Z':raw;
    const date=new Date(typeof value==='number'?value:utc);
    return Number.isNaN(date.getTime())?null:date;
  },
  format(value,options={}) {
    return this.date(value)?.toLocaleString(undefined,{...options,timeZone:'America/Toronto'})||'—';
  }
};
(() => {
  const originalFetch = window.fetch.bind(window);

  window.fetch = async (...args) => {
    const response = await originalFetch(...args);
    if (response.status === 401 && location.pathname !== '/login.html') {
      const destination = `${location.pathname}${location.search}${location.hash}`;
      location.replace(`/login.html?returnUrl=${encodeURIComponent(destination)}`);
    }
    return response;
  };

  window.addEventListener('DOMContentLoaded', () => {
    const nav = document.querySelector('.topnav');
    if (!nav || nav.querySelector('[data-homewatch-logout]')) return;

    const supplementalLinks = [
      ['/adult-analysis.html', 'Adult Analysis'],
      ['/history.html', 'History'],
      ['/tls-inspection.html', 'TLS'],
      ['/security.html', 'Security'],
      ['/zenarmor.html', 'Zenarmor']
    ];
    for (const [href, label] of supplementalLinks) {
      if (nav.querySelector(`a[href="${href}"]`)) continue;
      const link = document.createElement('a');
      link.className = `nav-link${location.pathname === href ? ' active' : ''}`;
      link.href = href;
      link.textContent = label;
      const insertionPoint = nav.querySelector('a[href="/notifications.html"]') || nav.querySelector('.pill');
      nav.insertBefore(link, insertionPoint);
    }

    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'nav-link auth-logout';
    button.dataset.homewatchLogout = 'true';
    button.textContent = 'Sign out';
    button.addEventListener('click', async () => {
      button.disabled = true;
      button.textContent = 'Signing out…';
      try { await originalFetch('/api/auth/logout', { method: 'POST' }); } catch {}
      location.replace('/login.html');
    });
    nav.appendChild(button);
  });
})();
