(() => {
  const adultBrands = [
    'xnxx', 'xvideos', 'pornhub', 'xhamster', 'redtube', 'youporn', 'spankbang', 'tube8',
    'brazzers', 'erome', 'jerkmate', 'chaturbate', 'stripchat', 'livejasmin', 'bongacams',
    'myfreecams', 'onlyfans', 'nhentai', 'hentaihaven', 'rule34', 'literotica', 'anysex'
  ];

  function isAdultDomain(domain) {
    const labels = String(domain || '').toLowerCase().split('.').filter(Boolean);
    return labels.some(label => adultBrands.some(brand =>
      label === brand || label.startsWith(`${brand}-`) || label.endsWith(`-${brand}`)
    ));
  }

  function labelSessions() {
    document.querySelectorAll('#sessionList .session-card').forEach(card => {
      const domainNode = card.querySelector('.session-domain strong');
      if (!domainNode || !isAdultDomain(domainNode.textContent)) return;
      if (card.querySelector('.adult-session-label')) return;

      const label = document.createElement('span');
      label.className = 'pill adult-session-label';
      label.textContent = 'Adult / porn streaming';
      label.style.marginTop = '6px';
      label.style.display = 'inline-block';
      label.style.borderColor = '#ef4444';
      label.style.color = '#fecaca';
      domainNode.parentElement.appendChild(label);
    });
  }

  const target = document.getElementById('sessionList');
  if (!target) return;
  new MutationObserver(labelSessions).observe(target, { childList: true, subtree: true });
  labelSessions();
})();