(() => {
  'use strict';

  const byId = id => document.getElementById(id);
  const escape = value => String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));

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
        <p class="muted">Keys are stored locally by HomeWatch and used by the server. Domain results are cached for 24 hours.</p>
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

  async function loadRuntimeSettings() {
    try {
      const data = await api('/api/runtime-settings');
      byId('runtimeVtKey').value = data.virusTotalApiKey || '';
      byId('runtimeUrlscanKey').value = data.urlscanApiKey || '';
      byId('runtimeNtfyBase').value = data.ntfyBaseUrl || 'https://ntfy.sh';
      byId('runtimeNtfyTopic').value = data.ntfyTopic || '';
      byId('runtimeNtfyUser').value = data.ntfyUsername || '';
      byId('runtimeNtfyPassword').value = data.ntfyPassword || '';
      byId('runtimeSaveResult').textContent = 'Settings loaded.';
    } catch (error) {
      byId('runtimeSaveResult').textContent = `Settings unavailable: ${error.message}`;
    }
  }

  async function saveSettings() {
    const result = byId('runtimeSaveResult');
    result.textContent = 'Saving…';
    try {
      await api('/api/runtime-settings', {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(settingsPayload())
      });
      result.textContent = 'Saved. New ntfy notifications will use this topic.';
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

  const pending = new Set();
  async function enrichSessionCards() {
    const cards = [...document.querySelectorAll('#sessionList .session-card')];
    for (const card of cards.slice(0, 30)) {
      if (card.dataset.intelligenceLoaded === '1') continue;
      const strongs = [...card.querySelectorAll('strong')];
      const domainNode = strongs.find(x => /\./.test(x.textContent || ''));
      const domain = domainNode?.textContent?.trim().toLowerCase();
      if (!domain || pending.has(domain)) continue;
      card.dataset.intelligenceLoaded = '1';
      pending.add(domain);
      const target = card.querySelector('.session-domain') || card;
      const box = document.createElement('div');
      box.className = 'domain-intelligence muted';
      box.style.marginTop = '6px';
      box.style.fontSize = '.85rem';
      box.textContent = 'Checking VirusTotal and urlscan…';
      target.appendChild(box);
      try {
        const data = await api(`/api/domain-intelligence?domain=${encodeURIComponent(domain)}`);
        const parts = [];
        const vt = data.virusTotal;
        if (vt?.available) {
          const verdict = Number(vt.malicious || 0) > 0 ? `${vt.malicious} malicious` : Number(vt.suspicious || 0) > 0 ? `${vt.suspicious} suspicious` : 'no malicious detections';
          parts.push(`VirusTotal: ${verdict}`);
          if (Array.isArray(vt.categories) && vt.categories.length) parts.push(`Category: ${vt.categories.slice(0,2).join(', ')}`);
        } else if (vt) parts.push('VirusTotal: unavailable');
        const scan = data.urlscan;
        if (scan?.available) {
          if (scan.title) parts.push(`Page: ${scan.title}`);
          const host = [scan.server, scan.country].filter(Boolean).join(' · ');
          if (host) parts.push(host);
        } else if (scan) parts.push('urlscan: no prior scan found');
        box.innerHTML = parts.length ? parts.map(escape).join('<br>') : 'Add API keys in Settings for external intelligence.';
      } catch (error) { box.textContent = `Intelligence unavailable: ${error.message}`; }
      finally { pending.delete(domain); }
    }
  }

  function start() {
    ensureSettingsUi();
    const settingsButton = document.querySelector('.nav [data-view="settings"]');
    settingsButton?.addEventListener('click', () => setTimeout(() => { ensureSettingsUi(); loadRuntimeSettings(); }, 0));
    const list = byId('sessionList');
    if (list) new MutationObserver(() => setTimeout(enrichSessionCards, 0)).observe(list, { childList: true, subtree: true });
    document.querySelector('.nav [data-view="sessions"]')?.addEventListener('click', () => setTimeout(enrichSessionCards, 500));
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();
