const $ = (id) => document.getElementById(id);
const form = $('authForm');
const message = $('message');
const submitButton = $('submitButton');
const modeButton = $('modeButton');
let mode = 'login';

function safeReturnUrl() {
  const requested = new URLSearchParams(location.search).get('returnUrl');
  if (!requested || !requested.startsWith('/') || requested.startsWith('//') || requested.startsWith('/login.html')) return '/';
  return requested;
}

function showMessage(text, kind = 'error') {
  message.textContent = text;
  message.className = `message ${kind}`;
  message.hidden = false;
}

function clearMessage() {
  message.hidden = true;
  message.textContent = '';
}

function setBusy(busy) {
  submitButton.disabled = busy;
  submitButton.textContent = busy
    ? (mode === 'login' ? 'Signing in…' : 'Updating password…')
    : (mode === 'login' ? 'Sign in' : 'Reset password');
}

function setMode(nextMode) {
  const currentEmail = $('email').value;
  mode = nextMode;
  const resetting = mode === 'reset';
  $('authTitle').textContent = resetting ? 'Reset your password' : 'Secure sign in';
  $('authDescription').textContent = resetting
    ? 'Use your MoneyPilot email and current authenticator code to choose a new password.'
    : 'Use the same credentials and 6-digit authenticator code as MoneyPilot.';
  $('passwordField').hidden = resetting;
  $('password').required = !resetting;
  $('resetFields').hidden = !resetting;
  $('newPassword').required = resetting;
  $('confirmPassword').required = resetting;
  $('rememberField').hidden = resetting;
  modeButton.textContent = resetting ? 'Back to sign in' : 'Forgot password?';
  form.reset();
  $('email').value = currentEmail;
  clearMessage();
  setBusy(false);
}

async function readResponse(response) {
  const payload = await response.json().catch(() => null);
  if (!response.ok) throw new Error(payload?.message ?? payload?.error ?? 'Authentication failed');
  return payload;
}

async function signIn() {
  const response = await fetch('/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      email: $('email').value.trim(),
      password: $('password').value,
      code: $('code').value,
      rememberDevice: $('rememberDevice').checked
    })
  });
  await readResponse(response);
  location.replace(safeReturnUrl());
}

async function resetPassword() {
  const newPassword = $('newPassword').value;
  if (newPassword !== $('confirmPassword').value) throw new Error('The new passwords do not match.');

  const response = await fetch('/api/auth/forgot-password', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: $('email').value.trim(), code: $('code').value, newPassword })
  });
  const payload = await readResponse(response);
  setMode('login');
  showMessage(payload?.message ?? 'Password updated. You can now sign in.', 'success');
}

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  clearMessage();
  if (!form.reportValidity()) return;
  setBusy(true);
  try {
    if (mode === 'login') await signIn();
    else await resetPassword();
  } catch (error) {
    showMessage(error instanceof Error ? error.message : 'Authentication failed');
  } finally {
    setBusy(false);
  }
});

modeButton.addEventListener('click', () => setMode(mode === 'login' ? 'reset' : 'login'));
$('code').addEventListener('input', (event) => { event.target.value = event.target.value.replace(/\D/g, '').slice(0, 6); });

if (location.protocol !== 'https:' && !['127.0.0.1', 'localhost', '::1'].includes(location.hostname)) {
  $('connectionWarning').hidden = false;
}

fetch('/api/auth/me', { headers: { Accept: 'application/json' } })
  .then((response) => { if (response.ok) location.replace(safeReturnUrl()); })
  .catch(() => {});
