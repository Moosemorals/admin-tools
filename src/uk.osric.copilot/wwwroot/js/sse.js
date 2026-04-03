/**
 * sse.js — Server-Sent Events connection and event routing.
 *
 * Opens an EventSource to /events, parses each message, and dispatches it to
 * the appropriate session UI functions.
 *
 * The browser automatically sends the `Last-Event-ID` header on reconnect when
 * the server uses the SSE `id:` line, allowing the server to replay any events
 * missed during a network drop.
 *
 * Additionally, each event's `_id` is stored in localStorage keyed by session so
 * that history can be fetched incrementally on page reload.
 */

import { renderEventCard } from './render.js';
import {
  addSessionToSidebar,
  ensureFeed,
  switchToSession,
  appendToFeed,
  markNewSession,
  getActiveSessionId,
} from './sessions.js';

/**
 * Opens the SSE stream and routes incoming events to the session UI.
 *
 * @param {HTMLElement} statusEl - The status badge element in the page header.
 */
export function connectSSE(statusEl) {
  const es = new EventSource('/events');

  es.onopen = () => {
    statusEl.textContent = 'connected';
    statusEl.className   = 'connected';
  };

  es.onmessage = (e) => {
    try {
      handleEvent(JSON.parse(e.data));
    } catch (err) {
      console.error('Failed to parse SSE message:', err, e.data);
    }
  };

  es.onerror = () => {
    statusEl.textContent = 'disconnected – retrying…';
    statusEl.className   = 'error';
  };
}

// ── Internal ──────────────────────────────────────────────────────────────────

function handleEvent(data) {
  // Persist the highest seen message ID per session so history can be fetched
  // incrementally after a page reload.
  if (data._id !== undefined && data._sessionId) {
    const key = `copilot.session.${data._sessionId}.lastId`;
    const current = Number(localStorage.getItem(key) ?? 0);
    if (data._id > current) {
      localStorage.setItem(key, String(data._id));
    }
  }

  if (data._eventType === 'SessionCreated') {
    const record = {
      id:               data.sessionId,
      title:            data.title,
      lastActiveAt:     data.lastActiveAt,
      workingDirectory: data.workingDirectory ?? null,
    };
    addSessionToSidebar(record);
    ensureFeed(record.id);
    markNewSession(record.id); // brand-new session has no prior history to fetch
    switchToSession(record.id);
    return;
  }

  const sessionId = data._sessionId;
  if (!sessionId) {
    return; // service-level synthetic events (ServiceReady, etc.)
  }

  ensureFeed(sessionId);
  appendToFeed(sessionId, renderEventCard(data), data._id);

  // Mark unread in the sidebar for sessions other than the active one.
  if (sessionId !== getActiveSessionId()) {
    const item = document.querySelector(`.session-item[data-session-id="${sessionId}"]`);
    if (item) {
      item.querySelector('.unread-dot').hidden = false;
    }
  }
}
