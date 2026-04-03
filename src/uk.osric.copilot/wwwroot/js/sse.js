/**
 * sse.js — Server-Sent Events connection and event routing.
 *
 * Opens an EventSource to /events, parses each message, and dispatches it to
 * the appropriate session UI functions.
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
  if (data._eventType === 'SessionCreated') {
    const record = {
      id:           data.sessionId,
      title:        data.title,
      lastActiveAt: data.lastActiveAt,
    };
    addSessionToSidebar(record);
    ensureFeed(record.id);
    markNewSession(record.id); // brand-new session has no prior history to fetch
    switchToSession(record.id);
    return;
  }

  const sessionId = data._sessionId;
  if (!sessionId) return; // service-level synthetic events (ServiceReady, etc.)

  ensureFeed(sessionId);
  appendToFeed(sessionId, renderEventCard(data));

  // Mark unread in the sidebar for sessions other than the active one.
  if (sessionId !== getActiveSessionId()) {
    const item = document.querySelector(`.session-item[data-session-id="${sessionId}"]`);
    if (item) item.querySelector('.unread-dot').hidden = false;
  }
}
