(() => {
  let currentCommit = '';

  const applyCommitVersion = () => {
    if (!currentCommit) return;

    const badge = document.getElementById('version');
    const label = `commit ${currentCommit}`;
    if (badge && badge.textContent !== label) {
      badge.textContent = label;
      badge.title = 'Current Git commit';
    }

    const title = `HomeWatch · ${currentCommit}`;
    if (document.title !== title) document.title = title;
  };

  const loadCommit = async () => {
    try {
      const response = await fetch('/api/status', { cache: 'no-store' });
      if (!response.ok) return;
      const status = await response.json();
      const commit = String(status.commit || '').trim();
      if (!commit || commit === 'unknown') return;
      currentCommit = commit;
      applyCommitVersion();
    } catch { }
  };

  const badge = document.getElementById('version');
  if (badge) {
    new MutationObserver(applyCommitVersion).observe(badge, {
      childList: true,
      characterData: true,
      subtree: true
    });
  }

  const titleElement = document.querySelector('title');
  if (titleElement) {
    new MutationObserver(applyCommitVersion).observe(titleElement, {
      childList: true,
      characterData: true,
      subtree: true
    });
  }

  loadCommit();
  window.addEventListener('DOMContentLoaded', () => {
    applyCommitVersion();
    loadCommit();
  }, { once: true });
  setInterval(loadCommit, 30000);

  // Load targeted investigation reliability fixes without changing the main bundle.
  // The cache key ensures every browser receives the corrected ignore and scroll logic.
  if (!document.querySelector('script[data-investigation-fixes]')) {
    const script = document.createElement('script');
    script.src = '/investigation-fixes.js?v=20260730-1';
    script.dataset.investigationFixes = '1';
    document.head.appendChild(script);
  }
})();
