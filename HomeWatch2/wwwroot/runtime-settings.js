(() => {
  'use strict';

  const byId = id => document.getElementById(id);
  let threatSettings = { virusTotalConfigured: false, urlscanConfigured: false };

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

  function ensureSettingsUi() {
    const view = byId('settingsView');
    if (!view || byId('runtimeIntegrations')) return;
    const section = document.createElement('section');
    section.id = 'runtimeIntegrations';
    section.innerHTML = `
      <section class="panel" style="margin-top:18px;padding:22px">
        <div class="subheading"><h3>Threat intelligence</h3><span>VirusTotal and urlscan</span></div>
        <p class="muted">Keys are stored locally by HomeWatch and used by the server. Domain results are cached for 24 hours. External lookups run only when you click Details, which prevents API rate-limit errors.</p>
        <div class="activity-controls" style="padding:0;border:0;grid-template-columns:1fr auto;margin-top:14px">
          <input id="runtimeVtKey" type="password" autocomplete="off" placeholder="VirusTotal API key">
          <button id="runtimeTestVt" type="button">Test VirusTotal</button>
          <input id="runtimeUrlscanKey" type="password" autocomplete="off" placeholder="urlscan API key">
          <button id="runtimeTestUrlscan" type="button">Test urlscan</button>
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
          <button id="runtimeSave" type="button">Save settings</button>
          <button id="runtimeTestNtfy" type="button">Send test notification</button>
        </div>
        <p id="runtimeSaveResult" class="muted" style="margin:12px 0 0"></p>
      </section>`;
    view.appendChild(section);

    byId('runtimeSave').addEventListener('click', saveSettings);
    byId('runtimeTestVt').addEventListener('click', () => testService('virustotal', 'VirusTotal'));
    byId('runtimeTestUrlscan').addEventListener('click', () => testService('urlscan', 'urlscan'));
    byId('runtimeTestNtfy').addEventListener('click', () => testService('ntfy', 'ntfy'));
    loadRuntimeSettings();
  }

  function connectedSummary() {
    const services = [];
    if (threatSettings.virusTotalConfigured) services.push('VirusTotal connected');
    if (threatSettings.urlscanConfigured) services.push('urlscan connected');
    return services.length
      ? `${services.join(' · ')}. Click Details to look up a domain.`
      : 'Add API keys in Settings for external intelligence.';
  }

  async function refreshThreatSettings() {
    try {
      const data = await api('/api/runtime-settings');
      threatSettings = {
        virusTotalConfigured: Boolean(data.virusTotalConfigured),
        urlscanConfigured: Boolean(data.urlscanConfigured)
      };
      return data;
    } catch {
      return null;
    }
  }

  async function loadRuntimeSettings() {
    try {
      const data = await refreshThreatSettings();
      if (!data) throw new Error('Could not load settings.');
      byId('runtimeVtKey').value = data.virusTotalApiKey || '';
      byId('runtimeUrlscanKey').value = data.urlscanApiKey || '';
      byId('runtimeNtfyBase').value = data.ntfyBaseUrl || 'https://ntfy.sh';
      byId('runtimeNtfyTopic').value = data.ntfyTopic || '';
      byId('runtimeNtfyUser').value = data.ntfyUsername || '';
      byId('runtimeNtfyPassword').value = data.ntfyPassword || '';
      byId('runtimeSaveResult').textContent = 'Settings loaded.';
      byId('runtimeThreatResult').textContent = connectedSummary();
    } catch (error) {
      byId('runtimeSaveResult').textContent = `Settings unavailable: ${error.message}`;
    }
  }

  async function saveSettings() {
    const result = byId('runtimeSaveResult');
    result.textContent = 'Saving…';
    try {
      const data = await api('/api/runtime-settings', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(settingsPayload())
      });
      threatSettings = {
        virusTotalConfigured: Boolean(data.virusTotalConfigured),
        urlscanConfigured: Boolean(data.urlscanConfigured)
      };
      result.textContent = 'Saved. Settings are active across HomeWatch.';
      byId('runtimeThreatResult').textContent = connectedSummary();
      labelSessionCards();
    } catch (error) { result.textContent = `Save failed: ${error.message}`; }
  }

  async function testService(service, label) {
    const result = service === 'ntfy' ? byId('runtimeSaveResult') : byId('runtimeThreatResult');
    result.textContent = `Testing ${label}…`;
    try {
      const data = await api(`/api/runtime-settings/test/${service}`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(settingsPayload())
      });
      result.textContent = data.status || `${label} connected.`;
    } catch (error) { result.textContent = `${label} test failed: ${error.message}`; }
  }

  async function labelSessionCards() {
    await refreshThreatSettings();
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
      box.textContent = connectedSummary();
    });
  }

  function start() {
    ensureSettingsUi();
    refreshThreatSettings();
    const settingsButton = document.querySelector('.nav [data-view="settings"]');
    settingsButton?.addEventListener('click', () => setTimeout(() => { ensureSettingsUi(); loadRuntimeSettings(); }, 0));
    const list = byId('sessionList');
    if (list) new MutationObserver(() => setTimeout(labelSessionCards, 0)).observe(list, { childList: true, subtree: true });
    document.querySelector('.nav [data-view="sessions"]')?.addEventListener('click', () => setTimeout(labelSessionCards, 500));
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();