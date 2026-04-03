/**
 * app.js — entry point.
 *
 * Wires up the page: loads existing sessions, opens the SSE stream, and handles
 * the prompt-send flow.
 */

import { addSessionToSidebar, ensureFeed, switchToSession, appendToFeed, getActiveSessionId } from './sessions.js';
import { connectSSE }    from './sse.js';
import { renderEventCard } from './render.js';

// ── DOM refs ──────────────────────────────────────────────────────────────────
const statusEl    = document.getElementById('status');
const newSessBtn  = document.getElementById('new-session-btn');
const promptEl    = document.getElementById('prompt');
const sendBtn     = document.getElementById('send-btn');

// ── Initialise ────────────────────────────────────────────────────────────────
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

// ── New-session button ────────────────────────────────────────────────────────
newSessBtn.addEventListener('click', async () => {
  newSessBtn.disabled = true;
  try {
    const res = await fetch('/sessions', { method: 'POST' });
    if (!res.ok) console.error('Failed to create session:', await res.text());
    // The SessionCreated SSE event handles sidebar insertion and switching.
  } catch (err) {
    console.error('Network error creating session:', err);
  } finally {
    newSessBtn.disabled = false;
  }
});

// ── Send prompt ───────────────────────────────────────────────────────────────
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
      }));
    } else {
      promptEl.value = '';
    }
  } catch (err) {
    appendToFeed(sessionId, renderEventCard({
      _eventType: 'NetworkError', _timestamp: new Date().toISOString(), error: String(err),
    }));
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
