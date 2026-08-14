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
