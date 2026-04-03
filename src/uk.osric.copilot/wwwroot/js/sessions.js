/**
 * sessions.js — session state, sidebar, feeds, and smart-scroll logic.
 *
 * This module owns all mutable session state and exposes a clean API so that
 * sse.js and app.js never touch the raw state Maps directly.
 */

import { renderEventCard } from './render.js';
import { relativeTime }    from './helpers.js';

// ── DOM refs ──────────────────────────────────────────────────────────────────
const feedsEl      = document.getElementById('feeds');
const noSessionMsg = document.getElementById('no-session-msg');
const sessionList  = document.getElementById('session-list');
const promptEl     = document.getElementById('prompt');
const sendBtn      = document.getElementById('send-btn');
const newMsgsEl    = document.getElementById('new-msgs-indicator');

// ── Module-private state ──────────────────────────────────────────────────────

/** @type {string|null} */
let activeSessionId = null;

/** @type {Map<string, HTMLDivElement>} sessionId → feed element */
const feedElements = new Map();

/** @type {Set<string>} sessionIds whose history has already been loaded */
const loadedHistory = new Set();

/**
 * Per-session count of events appended while the feed was not scrolled to the
 * bottom.  Drives the floating "↓ N new messages" indicator.
 * @type {Map<string, number>}
 */
const unseenCounts = new Map();

// ── Scroll helpers ────────────────────────────────────────────────────────────

function isNearBottom(el, threshold = 80) {
  return el.scrollHeight - el.scrollTop - el.clientHeight <= threshold;
}

function scrollToBottom(el) {
  el.scrollTop = el.scrollHeight;
}

function updateScrollIndicator() {
  const count = activeSessionId ? (unseenCounts.get(activeSessionId) ?? 0) : 0;
  if (count > 0) {
    newMsgsEl.textContent = `↓ ${count} new message${count === 1 ? '' : 's'}`;
    newMsgsEl.hidden = false;
  } else {
    newMsgsEl.hidden = true;
  }
}

newMsgsEl.addEventListener('click', () => {
  const feed = activeSessionId ? feedElements.get(activeSessionId) : null;
  if (feed) scrollToBottom(feed);
  if (activeSessionId) unseenCounts.delete(activeSessionId);
  updateScrollIndicator();
});

// ── Public API ────────────────────────────────────────────────────────────────

/** Returns the currently active session ID (or null). */
export function getActiveSessionId() {
  return activeSessionId;
}

/**
 * Appends a rendered event card to the named session's feed.
 * Handles smart-scroll and the new-messages indicator automatically.
 *
 * @param {string}      sessionId
 * @param {HTMLElement} card  - A rendered element (e.g. from renderEventCard).
 */
export function appendToFeed(sessionId, card) {
  const feed = feedElements.get(sessionId);
  if (!feed) return;

  feed.appendChild(card);

  if (sessionId === activeSessionId) {
    if (isNearBottom(feed)) {
      scrollToBottom(feed);
    } else {
      unseenCounts.set(sessionId, (unseenCounts.get(sessionId) ?? 0) + 1);
      updateScrollIndicator();
    }
  }
}

/**
 * Marks a session as having no server-side history to load.
 * Call this for brand-new sessions created during this page session.
 */
export function markNewSession(sessionId) {
  loadedHistory.add(sessionId);
}

/** Creates a feed div for sessionId if one does not already exist. */
export function ensureFeed(sessionId) {
  if (feedElements.has(sessionId)) return;

  const div = document.createElement('div');
  div.className = 'session-feed';
  div.addEventListener('scroll', () => {
    if (activeSessionId === sessionId && isNearBottom(div)) {
      unseenCounts.delete(sessionId);
      updateScrollIndicator();
    }
  }, { passive: true });

  feedsEl.appendChild(div);
  feedElements.set(sessionId, div);
}

/**
 * Adds a session entry to the sidebar.
 * Skips silently if the session is already listed.
 *
 * @param {{ id: string, title: string, lastActiveAt: string }} record
 */
export function addSessionToSidebar(record) {
  if (document.querySelector(`.session-item[data-session-id="${record.id}"]`)) return;

  const item = document.createElement('div');
  item.className = 'session-item';
  item.dataset.sessionId = record.id;

  const title = document.createElement('span');
  title.className   = 'session-title';
  title.textContent = record.title;

  const ts = document.createElement('span');
  ts.className   = 'session-ts';
  ts.textContent = relativeTime(record.lastActiveAt);

  const dot = document.createElement('span');
  dot.className   = 'unread-dot';
  dot.textContent = '●';
  dot.hidden      = true;

  const del = document.createElement('button');
  del.className = 'session-delete-btn';
  del.textContent = '×';
  del.title = 'Delete session';
  del.setAttribute('aria-label', 'Delete session');
  del.addEventListener('click', async (e) => {
    e.stopPropagation();
    if (!confirm(`Delete session "${record.title}"?`)) return;
    try {
      await fetch(`/sessions/${record.id}`, { method: 'DELETE' });
      item.remove();
      feedElements.get(record.id)?.remove();
      feedElements.delete(record.id);
      loadedHistory.delete(record.id);
      unseenCounts.delete(record.id);
      if (activeSessionId === record.id) {
        activeSessionId = null;
        noSessionMsg.style.display = 'flex';
        promptEl.disabled    = true;
        promptEl.placeholder = 'Select or create a session…';
        sendBtn.disabled     = true;
        updateScrollIndicator();
      }
    } catch (err) {
      console.error('Failed to delete session:', err);
    }
  });

  item.appendChild(title);
  if (record.workingDirectory) {
    const wd = document.createElement('span');
    wd.className   = 'session-workdir';
    wd.textContent = '📁 ' + record.workingDirectory.split('/').pop();
    wd.title       = record.workingDirectory;
    item.appendChild(wd);
  }
  item.appendChild(ts);
  item.appendChild(dot);
  item.appendChild(del);
  item.addEventListener('click', () => switchToSession(record.id));
  sessionList.appendChild(item);
}

/**
 * Switches the active session, lazily loading history on the first visit.
 * Scrolls the feed to the bottom after switching.
 *
 * @param {string} sessionId
 */
export async function switchToSession(sessionId) {
  if (activeSessionId === sessionId) return;

  if (activeSessionId && feedElements.has(activeSessionId)) {
    feedElements.get(activeSessionId).classList.remove('active');
  }

  activeSessionId = sessionId;
  noSessionMsg.style.display = 'none';

  ensureFeed(sessionId);
  feedElements.get(sessionId).classList.add('active');

  document.querySelectorAll('.session-item').forEach(el => {
    el.classList.toggle('active', el.dataset.sessionId === sessionId);
  });
  const item = document.querySelector(`.session-item[data-session-id="${sessionId}"]`);
  if (item) item.querySelector('.unread-dot').hidden = true;

  updateScrollIndicator();

  promptEl.disabled    = false;
  promptEl.placeholder = 'Send instructions to Copilot…';
  sendBtn.disabled     = false;

  if (!loadedHistory.has(sessionId)) {
    loadedHistory.add(sessionId);
    try {
      const msgs = await fetch(`/sessions/${sessionId}/messages`).then(r => r.json());
      const feed = feedElements.get(sessionId);
      for (const msg of msgs) {
        feed.appendChild(renderEventCard(msg));
      }
      scrollToBottom(feed);
    } catch (err) {
      console.error('Failed to load history for', sessionId, err);
    }
  } else {
    // Already loaded; scroll to bottom so the latest messages are in view.
    const feed = feedElements.get(sessionId);
    if (feed) scrollToBottom(feed);
  }
}
