'use strict';
const $ = id => document.getElementById(id);
const cards = new Map();
let code = '', timer, clockTimer, request, selected = null, serverOffset = 0, lastData = null;
try { code = sessionStorage.getItem('afk-code') || ''; } catch { /* Private browser storage may be disabled. */ }
function remember(value) { try { value ? sessionStorage.setItem('afk-code', value) : sessionStorage.removeItem('afk-code'); } catch {} }
function setConnected(connected) { $('login').hidden = connected; $('dashboard').hidden = !connected; $('sign-out').hidden = !connected; }
async function api(path, signal) {
  const response = await fetch(path, {headers:{Authorization:'Bearer ' + code}, cache:'no-store', credentials:'omit', signal});
  if (response.status === 401) { disconnect('Access code expired or incorrect. Copy the current code from the PC.'); throw new Error('Unauthorized'); }
  if (!response.ok) throw new Error('Dashboard unavailable');
  return response;
}
function clearCard(card) { if (card.url) URL.revokeObjectURL(card.url); card.url = null; card.image.hidden = true; card.image.removeAttribute('src'); }
function disconnect(message = '') {
  code = ''; remember(''); clearTimeout(timer); clearInterval(clockTimer); request?.abort(); request = null;
  $('detail').close(); selected = null; $('detail-image').removeAttribute('src');
  for (const card of cards.values()) { clearCard(card); card.button.remove(); }
  cards.clear(); lastData = null; $('code').value = ''; setConnected(false);
  $('connection').textContent = 'Private, read-only monitoring'; $('login-error').textContent = message;
}
function ageText(item) {
  if (!item.screenshotAt) return item.previewStatus;
  const seconds = Math.max(0, Math.floor((Date.now() + serverOffset - Date.parse(item.screenshotAt)) / 1000));
  const prefix = item.previewStatus !== 'Preview available' ? 'Unavailable · last ' : seconds < 30 ? 'Updated ' : 'Outdated · ';
  return prefix + (seconds < 60 ? seconds + 's ago' : Math.floor(seconds / 60) + 'm ago');
}
function needsAttention(item) { return !item.running || !item.responding || item.previewStatus !== 'Preview available' || !item.screenshotAt || Date.now() + serverOffset - Date.parse(item.screenshotAt) >= 30000; }
function sessionText(item) {
  let text = item.gameName || 'Game not identified';
  if (item.joinedAt && item.sessionStatus === 'Joined (log)') {
    const minutes = Math.max(0, Math.floor((Date.now() + serverOffset - Date.parse(item.joinedAt)) / 60000));
    text += ' · ' + (minutes < 1 ? '<1m' : minutes < 60 ? minutes + 'm' : Math.floor(minutes / 60) + 'h ' + minutes % 60 + 'm');
  }
  return text;
}
function paintTime() {
  for (const card of cards.values()) {
    card.button.querySelector('.session').textContent = sessionText(card.item);
    card.age.textContent = ageText(card.item); card.age.className = needsAttention(card.item) ? 'age warning' : 'age';
    card.age.title = card.item.screenshotAt ? 'Last successful screenshot: ' + new Date(card.item.screenshotAt).toLocaleString() : card.item.previewStatus;
  }
  if (selected && cards.has(selected)) paintDetail(cards.get(selected));
}
function paintDetail(card) {
  const item = card.item;
  $('detail-title').textContent = item.label;
  $('detail-image').hidden = !card.url;
  if (card.url) $('detail-image').src = card.url; else $('detail-image').removeAttribute('src');
  $('detail-status').textContent = (item.running ? 'Process running' : 'Window closed / not detected') + ' · PID ' + item.processId + ' · ' + item.status + '. ' + (item.sessionStatus || 'Session unknown') + '. ' + sessionText(item) + '. ' + item.previewStatus;
  $('detail-time').textContent = ageText(item) + (item.screenshotAt ? ' · ' + new Date(item.screenshotAt).toLocaleString() : '');
}
function newCard(item) {
  const button = document.createElement('button'); button.className = 'client'; button.type = 'button';
  // All process/window metadata uses textContent, never parsed as HTML.
  button.innerHTML = '<div class="client-top"><strong class="client-name"></strong><span class="state"></span></div><div class="preview"><span class="placeholder">Waiting for preview</span><img alt="" hidden></div><div class="client-bottom"><span class="session"></span><div class="metrics"><span class="pid"></span><span class="ram"></span></div><span class="age"></span></div>';
  const card = {button, item, image:button.querySelector('img'), age:button.querySelector('.age'), url:null, version:null};
  button.addEventListener('click', () => { selected = card.item.id; paintDetail(card); $('detail').showModal(); });
  $('grid').append(button); cards.set(item.id, card); return card;
}
async function updateImage(card, signal) {
  const stamp = card.item.screenshotAt;
  if (!stamp) { clearCard(card); card.version = null; return; }
  if (stamp === card.version && card.url) return;
  try {
    const response = await api('/api/preview/' + encodeURIComponent(card.item.id), signal);
    const blob = await response.blob();
    if (signal.aborted || !cards.has(card.item.id)) return;
    const old = card.url; card.url = URL.createObjectURL(blob); card.version = stamp;
    card.image.src = card.url; card.image.hidden = false;
    if (selected === card.item.id) paintDetail(card);
    if (old) URL.revokeObjectURL(old);
  } catch (error) {
    if (error.name !== 'AbortError' && code) card.button.querySelector('.placeholder').textContent = 'Image unavailable';
  }
}
async function poll() {
  clearTimeout(timer);
  if (!code || document.hidden || request) return;
  const controller = new AbortController(); request = controller;
  const timeout = setTimeout(() => controller.abort(), 12000);
  try {
    const response = await api('/api/clients', controller.signal), data = await response.json();
    if (!code || controller.signal.aborted) return;
    serverOffset = Date.parse(data.serverTime) - Date.now(); lastData = data;
    setConnected(true); $('login-error').textContent = ''; $('connection').textContent = 'Connected · read-only';
    const metadataOld = !data.detectedAt || Date.now() + serverOffset - Date.parse(data.detectedAt) > 30000;
    $('notice').hidden = !metadataOld; $('notice').textContent = 'Client detection is delayed. Process status may be outdated.';
    $('count').textContent = data.clients.filter(c => c.running).length + ' running · ' + data.clients.length + ' shown';
    $('updated').textContent = 'Checked ' + new Date(data.serverTime).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'});
    const ids = new Set(data.clients.map(c => c.id));
    for (const [id, card] of cards) if (!ids.has(id)) { clearCard(card); card.button.remove(); cards.delete(id); if (selected === id) { $('detail').close(); selected = null; } }
    for (const item of data.clients) {
      const card = cards.get(item.id) || newCard(item); card.item = item;
      card.button.querySelector('.client-name').textContent = item.label;
      const state = card.button.querySelector('.state'); state.textContent = !item.running ? 'Not detected' : !item.responding ? 'Unresponsive' : item.sessionStatus === 'Disconnected (log)' ? 'Disconnected' : item.sessionStatus === 'Possibly stuck joining' ? 'Check join' : 'Window open'; state.className = !item.running || !item.responding || ['Disconnected (log)', 'Possibly stuck joining'].includes(item.sessionStatus) ? 'state warning' : 'state';
      card.button.querySelector('.pid').textContent = 'PID ' + item.processId;
      card.button.querySelector('.ram').textContent = item.memoryBytes === null ? 'RAM n/a' : Math.round(item.memoryBytes / 1048576) + ' MB';
      card.button.querySelector('.placeholder').textContent = item.previewStatus;
      card.button.setAttribute('aria-label', 'Enlarge ' + item.label + ', ' + state.textContent);
    }
    $('empty').hidden = data.clients.length !== 0; paintTime();
    // Bound Safari's decode/network memory instead of fetching every image at once.
    const pending = [...cards.values()];
    for (let i = 0; i < pending.length; i += 3) await Promise.all(pending.slice(i, i + 3).map(card => updateImage(card, controller.signal)));
  } catch (error) {
    if (code && !document.hidden) {
      $('connection').textContent = 'Disconnected · retrying';
      if (lastData) { $('notice').hidden = false; $('notice').textContent = 'Connection lost. Images and process status below may be outdated. Check Tailscale and the PC.'; }
      else $('login-error').textContent = 'Cannot reach the dashboard. Check Tailscale and that monitoring is running on the PC.';
    }
  } finally {
    clearTimeout(timeout); if (request === controller) request = null;
    if (code && !document.hidden) timer = setTimeout(poll, 4000);
  }
}
$('login').addEventListener('submit', event => { event.preventDefault(); code = $('code').value.trim().toLowerCase(); remember(code); $('code').value = ''; clearInterval(clockTimer); clockTimer = setInterval(paintTime, 1000); poll(); });
$('sign-out').addEventListener('click', () => disconnect());
$('close-detail').addEventListener('click', () => $('detail').close());
$('detail').addEventListener('close', () => { selected = null; $('detail-image').removeAttribute('src'); });
document.addEventListener('visibilitychange', () => {
  clearTimeout(timer); clearInterval(clockTimer);
  if (document.hidden) request?.abort();
  else if (code) { clockTimer = setInterval(paintTime, 1000); poll(); }
});
window.addEventListener('pagehide', () => { clearTimeout(timer); clearInterval(clockTimer); request?.abort(); });
window.addEventListener('pageshow', () => { if (code) { clearInterval(clockTimer); clockTimer = setInterval(paintTime, 1000); poll(); } });
if (code) { clockTimer = setInterval(paintTime, 1000); poll(); }
