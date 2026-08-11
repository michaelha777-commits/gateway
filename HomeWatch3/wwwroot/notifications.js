const $ = id => document.getElementById(id);
const priorityLabels = { min: 'Minimum', low: 'Low', default: 'Default', high: 'High', max: 'Maximum' };
const eventIcons = { 'adult-content': '!', 'new-device': '+', 'new-ip': 'IP' };
let state = null;
let dirty = false;

async function api(url, options) {
  const response = await fetch(url, options);
  const body = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(body.error || `Request failed (${response.status})`);
  return body;
}

function setPageStatus(text, kind) {
  $('pageStatus').textContent = text;
  $('pageStatus').className = `pill ${kind}`;
}

function setDirty(value = true) {
  dirty = value;
  $('saveBtn').disabled = !dirty;
  if (dirty) {
    $('saveMessage').textContent = 'Unsaved changes';
    $('saveMessage').className = 'save-message';
  }
}

function renderConnection() {
  const configured = Boolean(state.configured);
  $('connectionDot').className = `ntfy-dot ${configured ? 'ok' : 'bad'}`;
  $('connectionStatus').textContent = configured ? 'Configured' : 'Not configured';
  $('connectionDetail').textContent = configured
    ? 'HomeWatch can send to the configured ntfy topic.'
    : state.transportEnabled === false ? 'ntfy is disabled in appsettings.' : 'Add the ntfy topic URL in appsettings.';
  setPageStatus(configured ? 'Configured' : 'Setup needed', configured ? 'ok' : 'bad');
  $('testBtn').disabled = !configured;
}

function renderEvents() {
  $('masterEnabled').checked = Boolean(state.enabled);
  $('masterEnabled').nextElementSibling.nextElementSibling.textContent = state.enabled ? 'On' : 'Off';
  const priorities = Array.isArray(state.priorities) && state.priorities.length ? state.priorities : Object.keys(priorityLabels);
  $('eventRows').innerHTML = state.events.map(event => `
    <div class="ntfy-event-row ${event.enabled ? '' : 'is-disabled'}" data-event="${event.type}">
      <div class="event-info"><span class="event-icon">${eventIcons[event.type] || '•'}</span><div><strong>${escapeHtml(event.name)}</strong><p>${escapeHtml(event.description)}</p></div></div>
      <label class="switch event-switch"><input class="event-enabled" type="checkbox" ${event.enabled ? 'checked' : ''} aria-label="Notify for ${escapeHtml(event.name)}"><span aria-hidden="true"></span><b class="switch-label">${event.enabled ? 'On' : 'Off'}</b></label>
      <select class="text-input priority-select" aria-label="Priority for ${escapeHtml(event.name)}">${priorities.map(priority => `<option value="${priority}" ${priority === event.priority ? 'selected' : ''}>${priorityLabels[priority] || priority}</option>`).join('')}</select>
    </div>`).join('');

  document.querySelectorAll('.ntfy-event-row').forEach(row => {
    const checkbox = row.querySelector('.event-enabled');
    const select = row.querySelector('.priority-select');
    checkbox.addEventListener('change', () => {
      row.classList.toggle('is-disabled', !checkbox.checked);
      checkbox.nextElementSibling.nextElementSibling.textContent = checkbox.checked ? 'On' : 'Off';
      updateEnabledCount(); setDirty();
    });
    select.addEventListener('change', () => setDirty());
  });
  updateEnabledCount();
}

function updateEnabledCount() {
  const count = document.querySelectorAll('.event-enabled:checked').length;
  $('enabledCount').textContent = `${count} enabled`;
}

function collectUpdate() {
  return {
    enabled: $('masterEnabled').checked,
    events: [...document.querySelectorAll('.ntfy-event-row')].map(row => ({
      type: row.dataset.event,
      enabled: row.querySelector('.event-enabled').checked,
      priority: row.querySelector('.priority-select').value
    }))
  };
}

async function load() {
  try {
    state = await api('/api/notifications/settings');
    renderConnection(); renderEvents(); setDirty(false);
    $('saveMessage').textContent = '';
  } catch (error) {
    setPageStatus('Unavailable', 'bad');
    $('eventRows').innerHTML = `<div class="ntfy-empty">${escapeHtml(error.message)}</div>`;
  }
}

async function save() {
  $('saveBtn').disabled = true;
  $('saveBtn').textContent = 'Saving…';
  try {
    const saved = await api('/api/notifications/settings', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(collectUpdate()) });
    state = { ...state, ...saved };
    renderEvents(); setDirty(false);
    $('saveMessage').textContent = 'Notification settings saved.';
    $('saveMessage').className = 'save-message ok';
  } catch (error) {
    $('saveMessage').textContent = error.message;
    $('saveMessage').className = 'save-message bad';
    $('saveBtn').disabled = false;
  } finally {
    $('saveBtn').textContent = 'Save changes';
  }
}

async function sendTest() {
  const priority = $('testPriority').value;
  $('testBtn').disabled = true;
  $('testBtn').textContent = 'Sending…';
  $('testResult').textContent = '';
  try {
    await api(`/api/notifications/test?priority=${encodeURIComponent(priority)}`, { method: 'POST' });
    $('testResult').textContent = `${priorityLabels[priority]} test sent successfully.`;
    $('testResult').className = 'test-result ok';
  } catch (error) {
    $('testResult').textContent = error.message;
    $('testResult').className = 'test-result bad';
  } finally {
    $('testBtn').disabled = !state?.configured;
    $('testBtn').textContent = 'Send test';
  }
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[character]);
}

$('masterEnabled').addEventListener('change', () => {
  $('masterEnabled').nextElementSibling.nextElementSibling.textContent = $('masterEnabled').checked ? 'On' : 'Off';
  setDirty();
});
$('saveBtn').addEventListener('click', save);
$('testBtn').addEventListener('click', sendTest);
window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
load();
