// @ts-check

/**
 * app.js — entry point.
 *
 * Wires up the page: loads existing sessions, opens the SSE stream, and handles
 * the prompt-send flow and project folder picker.
 */

import { addSessionToSidebar, ensureFeed, switchToSession, appendToFeed, getActiveSessionId } from './sessions.js';
import { connectSSE }      from './sse.js';
import { renderEventCard } from './render.js';

// ── DOM refs ──────────────────────────────────────────────────────────────────
const statusEl   = document.getElementById('status');
const newSessBtn = document.getElementById('new-session-btn');
const promptEl   = document.getElementById('prompt');
const sendBtn    = document.getElementById('send-btn');
const dialog     = /** @type {HTMLDialogElement} */ (document.getElementById('project-folder-dialog'));
const folderList = document.getElementById('folder-list');
const cancelBtn  = document.getElementById('pf-cancel-btn');

// ── Initialise ────────────────────────────────────────────────────────────────
/** @returns {Promise<void>} */
async function init() {
  try {
    const sessions = await fetch('/sessions').then(r => r.json());
    for (const s of sessions) {
      addSessionToSidebar(s);
      ensureFeed(s.id);
    }
    if (sessions.length > 0) {
      await switchToSession(sessions[0].id);
    }
  } catch (err) {
    console.error('Failed to load sessions:', err);
  }
  connectSSE(statusEl);
}

init();

// ── Project folder picker ─────────────────────────────────────────────────────

/**
 * Shows the project folder picker dialog and resolves with the chosen path
 * (or null if the user picks "No project folder" or cancels).
 *
 * @returns {Promise<string|null>}
 */
async function pickProjectFolder() {
  let folders = [];
  try {
    folders = await fetch('/project-folders').then(r => r.json());
  } catch (err) {
    console.error('Failed to load project folders:', err);
  }

  folderList.innerHTML = '';

  // "No project folder" option.
  const noneItem = document.createElement('li');
  noneItem.className = 'folder-none';
  noneItem.textContent = 'No project folder';
  noneItem.setAttribute('role', 'option');
  folderList.appendChild(noneItem);

  for (const f of folders) {
    const li = document.createElement('li');
    li.className = 'folder-item';
    li.setAttribute('role', 'option');

    const name = document.createElement('span');
    name.className   = 'folder-item-name';
    name.textContent = f.name;

    const path = document.createElement('span');
    path.className   = 'folder-item-path';
    path.textContent = f.path;

    li.appendChild(name);
    li.appendChild(path);
    folderList.appendChild(li);
  }

  return new Promise(resolve => {
    function cleanup() {
      dialog.removeEventListener('cancel', onCancel);
      folderList.removeEventListener('click', onFolderClick);
      cancelBtn.removeEventListener('click', onCancel);
      dialog.close();
    }

    function onCancel() { cleanup(); resolve(null); }

    function onFolderClick(e) {
      const noneEl = e.target.closest('.folder-none');
      if (noneEl) { cleanup(); resolve(null); return; }
      const item = e.target.closest('.folder-item');
      if (!item) return;
      const chosen = item.querySelector('.folder-item-path').textContent;
      cleanup();
      resolve(chosen);
    }

    dialog.addEventListener('cancel', onCancel);
    cancelBtn.addEventListener('click', onCancel);
    folderList.addEventListener('click', onFolderClick);
    dialog.showModal();
  });
}

// ── New-session button ────────────────────────────────────────────────────────
newSessBtn.addEventListener('click', async () => {
  newSessBtn.disabled = true;
  try {
    const workingDirectory = await pickProjectFolder();
    // null means user cancelled or chose "no folder"
    const body = workingDirectory ? JSON.stringify({ workingDirectory }) : null;
    const res = await fetch('/sessions', {
      method: 'POST',
      headers: body ? { 'Content-Type': 'application/json' } : {},
      body,
    });
    if (!res.ok) console.error('Failed to create session:', await res.text());
    // The SessionCreated SSE event handles sidebar insertion and switching.
  } catch (err) {
    console.error('Network error creating session:', err);
  } finally {
    newSessBtn.disabled = false;
  }
});

// ── Send prompt ───────────────────────────────────────────────────────────────
/** @returns {Promise<void>} */
async function sendPrompt() {
  const sessionId = getActiveSessionId();
  if (!sessionId) return;

  const text = promptEl.value.trim();
  if (!text) return;

  sendBtn.disabled  = true;
  promptEl.disabled = true;

  try {
    const res = await fetch(`/sessions/${sessionId}/send`, {
      method:  'POST',
      headers: { 'Content-Type': 'text/plain' },
      body:    text,
    });
    if (!res.ok) {
      const msg = await res.text();
      appendToFeed(sessionId, renderEventCard({
        _eventType: 'SendError', _timestamp: new Date().toISOString(), message: msg,
      }), undefined);
    } else {
      promptEl.value = '';
    }
  } catch (err) {
    appendToFeed(sessionId, renderEventCard({
      _eventType: 'NetworkError', _timestamp: new Date().toISOString(), error: String(err),
    }), undefined);
  } finally {
    sendBtn.disabled  = false;
    promptEl.disabled = false;
    promptEl.focus();
  }
}

sendBtn.addEventListener('click', sendPrompt);
promptEl.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); sendPrompt(); }
});
