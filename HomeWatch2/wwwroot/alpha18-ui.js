(() => {
  const VERSION = '2.0.0-alpha.18';
  const applyVersion = () => {
    const badge = document.getElementById('version');
    if (badge) badge.textContent = `v${VERSION}`;
  };
  applyVersion();
  window.addEventListener('DOMContentLoaded', applyVersion, { once: true });
  setTimeout(applyVersion, 500);
})();
