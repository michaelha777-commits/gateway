(() => {
  'use strict';

  const byId = id => document.getElementById(id);
  let threatSettings = { virusTotalConfigured: false, urlscanConfigured: false };
  let sessionLabelTimer = null;
  let sessionLabelInProgress = false;
  let lastThreatRefresh = 0;

  function settingsPayload() {
    return {
      virusTotalApiKey: byId('runtimeVtKey')?.value.trim() || '',
      urlscanApiKey: byId('runtimeUrlscanKey')?.value.trim() || '',
      ntfyBaseUrl: byId('runtimeNtfyBase')?.value.trim() || 'https://ntfy.sh',
      ntfyTopic: byId('runtimeNtfyTopic')?.value.trim() || '',
      ntfyUsername: byId('runtimeNtfyUser')?.value.trim() || '',
      ntfyPassword: byId('runtimeNtfyPassword')?.value || ''
    };
  }

  async function api(path, options = {}) {
    const response = await fetch(path, { cache: 'no-store', ...options });
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || `Request failed (${response.status})`);
    return data;
  }

  function statusText(configured, service) {
    return configured ? `Connected — ${service} API key is saved.` : `Not configured — enter and save a ${service} API key.`;
  }

  function renderThreatStatus(message = '') {
    const vt = byId('runtimeVtStatus');
    const scan = byId('runtimeUrlscanStatus');
    if (vt) {
      vt.textContent = statusText(threatSettings.virusTotalConfigured, 'VirusTotal');
      vt.style.color = threatSettings.virusTotalConfigured ? '#63e6a8' : '';
    }
    if (scan) {
      scan.textContent = statusText(threatSettings.urlscanConfigured, 'urlscan');
      scan.style.color = threatSettings.urlscanConfigured ? '#63e6a8' : '';
    }
    if (message && byId('runtimeThreatResult')) byId('runtimeThreatResult').textContent = message;
  }

  function ensureSettingsUi() {
    const view = byId('settingsView');
    if (!view || byId('runtimeIntegrations')) return;
    const section = document.createElement('section');
    section.id = 'runtimeIntegrations';
    section.innerHTML = `
      <section class="panel" style="margin-top:18px;padding:22px">
        <div class="subheading"><h3>Threat intelligence</h3><span>VirusTotal and urlscan</span></div>
        <p class="muted">Keys are stored locally by HomeWatch. External lookups run only when you click Details, and results are cached for 24 hours.</p>
        <div style="display:grid;gap:14px;margin-top:16px">
          <div style="padding:14px;border:1px solid #1d344f;border-radius:12px">
            <div style="display:flex;justify-content:space-between;gap:12px;align-items:center;flex-wrap:wrap"><strong>VirusTotal</strong><span id="runtimeVtStatus" class="muted">Checking…</span></div>
            <div class="activity-controls" style="padding:0;border:0;grid-template-columns:1fr auto;margin-top:10px">
              <input id="runtimeVtKey" type="password" autocomplete="off" placeholder="VirusTotal API key">
              <button id="runtimeTestVt" type="button">Test VirusTotal</button>
            </div>
          </div>
          <div style="padding:14px;border:1px solid #1d344f;border-radius:12px">
            <div style="display:flex;justify-content:space-between;gap:12px;align-items:center;flex-wrap:wrap"><strong>urlscan</strong><span id="runtimeUrlscanStatus" class="muted">Checking…</span></div>
            <div class="activity-controls" style="padding:0;border:0;grid-template-columns:1fr auto;margin-top:10px">
              <input id="runtimeUrlscanKey" type="password" autocomplete="off" placeholder="urlscan API key">
              <button id="runtimeTestUrlscan" type="button">Test urlscan</button>
            </div>
          </div>
        </div>
        <div style="display:flex;gap:10px;flex-wrap:wrap;margin-top:14px">
          <button id="runtimeSaveThreat" type="button">Save API keys</button>
        </div>
        <p id="runtimeThreatResult" class="muted" style="margin:12px 0 0"></p>
      </section>
      <section class="panel" style="margin-top:18px;padding:22px">
        <div class="subheading"><h3>ntfy subscription</h3><span>Editable without restarting</span></div>
        <div class="activity-controls" style="padding:0;border:0;grid-template-columns:1fr 1fr;margin-top:14px">
          <input id="runtimeNtfyBase" type="url" placeholder="https://ntfy.sh">
          <input id="runtimeNtfyTopic" type="text" placeholder="Topic">
          <input id="runtimeNtfyUser" type="text" autocomplete="username" placeholder="Username (optional)">
          <input id="runtimeNtfyPassword" type="password" autocomplete="current-password" placeholder="Password (optional)">
        </div>
        <div style="display:flex;gap:10px;flex-wrap:wrap;margin-top:14px">
          <button id="runtimeSave" type="button">Save notification settings</button>
          <button id="runtimeTestNtfy" type="button">Send test notification</button>
        </div>
        <p id="runtimeSaveResult" class="muted" style="margin:12px 0 0"></p>
      </section>`;
    view.appendChild(section);

    byId('runtimeSaveThreat').addEventListener('click', () => saveSettings(true));
    byId('runtimeSave').addEventListener('click', () => saveSettings(false));
    byId('runtimeTestVt').addEventListener('click', () => testService('virustotal', 'VirusTotal'));
    byId('runtimeTestUrlscan').addEventListener('click', () => testService('urlscan', 'urlscan'));
    byId('runtimeTestNtfy').addEventListener('click', () => testService('ntfy', 'ntfy'));
    loadRuntimeSettings();
  }

  function connectedSummary() {
    const services = [];
    if (threatSettings.virusTotalConfigured) services.push('VirusTotal connected');
    if (threatSettings.urlscanConfigured) services.push('urlscan connected');
    return services.length ? `${services.join(' · ')}. Click Details to look up a domain.` : 'Threat intelligence is not configured.';
  }

  async function refreshThreatSettings(force = false) {
    if (!force && Date.now() - lastThreatRefresh < 30000) return null;
    try {
      const data = await api('/api/runtime-settings');
      threatSettings = {
        virusTotalConfigured: Boolean(data.virusTotalConfigured),
        urlscanConfigured: Boolean(data.urlscanConfigured)
      };
      lastThreatRefresh = Date.now();
      renderThreatStatus();
      return data;
    } catch {
      return null;
    }
  }

  async function loadRuntimeSettings() {
    try {
      const data = await refreshThreatSettings(true);
      if (!data) throw new Error('Could not load settings.');
      byId('runtimeVtKey').value = data.virusTotalApiKey || '';
      byId('runtimeUrlscanKey').value = data.urlscanApiKey || '';
      byId('runtimeNtfyBase').value = data.ntfyBaseUrl || 'https://ntfy.sh';
      byId('runtimeNtfyTopic').value = data.ntfyTopic || '';
      byId('runtimeNtfyUser').value = data.ntfyUsername || '';
      byId('runtimeNtfyPassword').value = data.ntfyPassword || '';
      byId('runtimeSaveResult').textContent = 'Notification settings loaded.';
      renderThreatStatus(connectedSummary());
    } catch (error) {
      byId('runtimeSaveResult').textContent = `Settings unavailable: ${error.message}`;
    }
  }

  async function saveSettings(threatOnly) {
    const result = threatOnly ? byId('runtimeThreatResult') : byId('runtimeSaveResult');
    result.textContent = 'Saving…';
    try {
      const data = await api('/api/runtime-settings', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(settingsPayload())
      });
      threatSettings = {
        virusTotalConfigured: Boolean(data.virusTotalConfigured),
        urlscanConfigured: Boolean(data.urlscanConfigured)
      };
      lastThreatRefresh = Date.now();
      result.textContent = threatOnly ? 'API keys saved and active across HomeWatch.' : 'Notification settings saved.';
      renderThreatStatus(threatOnly ? 'API keys saved and active across HomeWatch.' : '');
      scheduleSessionLabels(true);
    } catch (error) { result.textContent = `Save failed: ${error.message}`; }
  }

  async function testService(service, label) {
    const result = service === 'ntfy' ? byId('runtimeSaveResult') : byId('runtimeThreatResult');
    result.textContent = `Testing ${label}…`;
    try {
      const data = await api(`/api/runtime-settings/test/${service}`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(settingsPayload())
      });
      result.textContent = `${label}: ${data.status || 'Connected'}.`;
      if (service === 'virustotal') threatSettings.virusTotalConfigured = true;
      if (service === 'urlscan') threatSettings.urlscanConfigured = true;
      lastThreatRefresh = Date.now();
      renderThreatStatus(result.textContent);
    } catch (error) { result.textContent = `${label} test failed: ${error.message}`; }
  }

  async function labelSessionCards(forceRefresh = false) {
    if (sessionLabelInProgress) return;
    sessionLabelInProgress = true;
    try {
      await refreshThreatSettings(forceRefresh);
      const summary = connectedSummary();
      document.querySelectorAll('#sessionList .session-card').forEach(card => {
        let box = card.querySelector('.domain-intelligence');
        if (!box) {
          const target = card.querySelector('.session-domain') || card;
          box = document.createElement('div');
          box.className = 'domain-intelligence muted';
          box.style.marginTop = '6px';
          box.style.fontSize = '.85rem';
          target.appendChild(box);
        }
        if (box.textContent !== summary) box.textContent = summary;
      });
    } finally {
      sessionLabelInProgress = false;
    }
  }

  function scheduleSessionLabels(forceRefresh = false) {
    clearTimeout(sessionLabelTimer);
    sessionLabelTimer = setTimeout(() => labelSessionCards(forceRefresh), 250);
  }

  function start() {
    ensureSettingsUi();
    refreshThreatSettings(true);
    document.querySelector('.nav [data-view="settings"]')?.addEventListener('click', () => setTimeout(() => { ensureSettingsUi(); loadRuntimeSettings(); }, 0));
    const list = byId('sessionList');
    if (list) new MutationObserver(() => scheduleSessionLabels(false)).observe(list, { childList: true });
    document.querySelector('.nav [data-view="sessions"]')?.addEventListener('click', () => scheduleSessionLabels(false));
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();