(() => {
  'use strict';
  const byId = id => document.getElementById(id);

  function download(path) {
    const link = document.createElement('a');
    link.href = path;
    link.style.display = 'none';
    document.body.appendChild(link);
    link.click();
    link.remove();
  }

  function addSettingsPanel() {
    const view = byId('settingsView');
    if (!view || byId('backupDiagnosticsPanel')) return;
    const panel = document.createElement('section');
    panel.id = 'backupDiagnosticsPanel';
    panel.className = 'panel';
    panel.style.cssText = 'margin-top:18px;padding:22px';
    panel.innerHTML = `
      <div class="subheading"><h3>Backup & diagnostics</h3><span>Export, migrate and recover HomeWatch</span></div>
      <p class="muted">Activity exports contain everything HomeWatch captured and categorized. Full backups preserve the database and HomeWatch settings for a clean reinstall.</p>
      <div style="display:flex;flex-wrap:wrap;gap:10px;margin-top:16px">
        <button type="button" id="exportAllActivity">Export all activity</button>
        <button type="button" id="exportVisibleActivity">Export non-ignored devices</button>
        <button type="button" id="exportAllSessions">Export sessions</button>
        <button type="button" id="createFullBackup">Create full backup</button>
      </div>
      <div style="margin-top:22px;padding-top:18px;border-top:1px solid #1d344f">
        <div class="subheading"><h3>Full restore</h3><span>Use a .hwbackup file</span></div>
        <p class="muted">Restore replaces the HomeWatch database and saved configuration. HomeWatch must be restarted after a successful restore.</p>
        <div style="display:flex;flex-wrap:wrap;align-items:center;gap:10px;margin-top:12px">
          <input id="restoreBackupFile" type="file" accept=".hwbackup,application/zip">
          <button type="button" id="restoreFullBackup">Restore full backup</button>
        </div>
        <p id="backupRestoreStatus" class="muted" style="margin:12px 0 0"></p>
      </div>`;
    view.appendChild(panel);

    byId('exportAllActivity').onclick = () => download('/api/exports/activity?includeIgnored=true');
    byId('exportVisibleActivity').onclick = () => download('/api/exports/activity?includeIgnored=false');
    byId('exportAllSessions').onclick = () => download('/api/exports/sessions?includeIgnored=true');
    byId('createFullBackup').onclick = () => download('/api/backup/full');
    byId('restoreFullBackup').onclick = restoreBackup;
  }

  function addSessionExportButton() {
    const heading = document.querySelector('#sessionsView .page-heading');
    if (!heading || byId('exportSessions')) return;
    const existing = byId('refreshSessions');
    const wrap = document.createElement('div');
    wrap.style.cssText = 'display:flex;gap:10px;flex-wrap:wrap';
    const button = document.createElement('button');
    button.type = 'button';
    button.id = 'exportSessions';
    button.textContent = 'Export sessions';
    button.onclick = () => download('/api/exports/sessions?includeIgnored=true');
    if (existing) {
      existing.replaceWith(wrap);
      wrap.append(button, existing);
    } else {
      wrap.appendChild(button);
      heading.appendChild(wrap);
    }
  }

  async function restoreBackup() {
    const input = byId('restoreBackupFile');
    const button = byId('restoreFullBackup');
    const status = byId('backupRestoreStatus');
    const file = input?.files?.[0];
    if (!file) { status.textContent = 'Select a .hwbackup file first.'; return; }
    if (!confirm('This will replace all current HomeWatch data and settings with the selected backup. Continue?')) return;

    button.disabled = true;
    button.textContent = 'Restoring…';
    status.textContent = 'Validating and restoring backup…';
    try {
      const form = new FormData();
      form.append('backup', file);
      const response = await fetch('/api/backup/restore', { method: 'POST', body: form });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(data.error || `Restore failed (${response.status})`);
      status.textContent = data.message || 'Restore completed. Restart HomeWatch now.';
      alert('Restore completed successfully. Restart HomeWatch now.');
    } catch (error) {
      status.textContent = error.message;
      button.disabled = false;
      button.textContent = 'Restore full backup';
    }
  }

  function start() {
    addSettingsPanel();
    addSessionExportButton();
    document.querySelector('.nav [data-view="settings"]')?.addEventListener('click', () => setTimeout(addSettingsPanel, 0));
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();
