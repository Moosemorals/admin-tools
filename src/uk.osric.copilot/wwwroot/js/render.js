// @ts-check

/**
 * @typedef {{ _eventType?: string, _id?: number, _sessionId?: string,
 *             _timestamp?: string, requestId?: string,
 *             choices?: string[], allowFreeform?: boolean,
 *             [key: string]: unknown }} SseEvent
 */

import { esc, formatTs, scalarClass, previewOf, colorClass } from './helpers.js';

const LARGE_STRING_THRESHOLD = 120;

/**
 * Renders a full event card (<details> element) for a single SSE event payload.
 * AssistantMessage, AgentMessage, error, and user-input events are expanded by default.
 *
 * @param {SseEvent} data - Parsed SSE event object (fields prefixed with _ are metadata).
 * @returns {HTMLDetailsElement}
 */
export function renderEventCard(data) {
  const eventType = data._eventType ?? 'Unknown';
  const timestamp = data._timestamp ? formatTs(data._timestamp) : '';

  const card = document.createElement('details');
  card.className = `event-card ${colorClass(eventType)}`;

  if (/AssistantMessage|AgentMessage|SessionError|SendError|NetworkError|UserInput/.test(eventType)) {
    card.open = true;
  }

  const summary = document.createElement('summary');
  summary.innerHTML =
    `<span class="event-type">${esc(eventType)}</span>` +
    `<span class="event-ts">${esc(timestamp)}</span>`;
  card.appendChild(summary);

  const props = document.createElement('div');
  props.className = 'props';
  for (const [key, value] of Object.entries(data)) {
    if (key.startsWith('_')) continue;
    props.appendChild(renderProp(key, value));
  }
  card.appendChild(props);

  if (eventType === 'UserInputRequested' && data.requestId) {
    appendReplyForm(card, data);
  }

  return card;
}

// ── Internal helpers ──────────────────────────────────────────────────────────

/** @param {string} key @param {unknown} value @returns {HTMLElement} */
function renderProp(key, value) {
  if (value !== null && typeof value === 'object') {
    return renderNestedDetails(key, value);
  }
  if (typeof value === 'string' && value.length > LARGE_STRING_THRESHOLD) {
    return renderLargeString(key, value);
  }

  const row = document.createElement('div');
  row.className = 'prop-row';

  const k = document.createElement('span');
  k.className   = 'prop-key';
  k.textContent = key;
  row.appendChild(k);

  const v = document.createElement('span');
  v.className   = `prop-scalar ${scalarClass(value)}`;
  v.textContent = value === null ? 'null' : String(value);
  row.appendChild(v);

  return row;
}

/** @param {string} key @param {object} value @returns {HTMLDetailsElement} */
function renderNestedDetails(key, value) {
  const d = document.createElement('details');
  d.className = 'nested-details';

  const s = document.createElement('summary');
  s.innerHTML =
    `<span class="prop-key">${esc(key)}</span>` +
    `<span class="preview">${esc(previewOf(value))}</span>`;
  d.appendChild(s);

  const content = document.createElement('div');
  content.className = 'nested-content';
  if (Array.isArray(value)) {
    value.forEach((item, i) => content.appendChild(renderProp(String(i), item)));
  } else {
    for (const [k, v] of Object.entries(value)) {
      content.appendChild(renderProp(k, v));
    }
  }
  d.appendChild(content);
  return d;
}

/** @param {string} key @param {string} value @returns {HTMLDetailsElement} */
function renderLargeString(key, value) {
  const d = document.createElement('details');
  d.className = 'nested-details';

  const preview = value.slice(0, 60).replace(/\s+/g, ' ') + '…';
  const s = document.createElement('summary');
  s.innerHTML =
    `<span class="prop-key">${esc(key)}</span>` +
    `<span class="preview">${esc(preview)}</span>`;
  d.appendChild(s);

  const pre = document.createElement('pre');
  pre.className   = 'prop-value';
  pre.textContent = value;
  d.appendChild(pre);
  return d;
}

/** @param {HTMLDetailsElement} card @param {SseEvent} data */
function appendReplyForm(card, data) {
  const form = document.createElement('div');
  form.className = 'reply-form';

  let selectedChoice = null;
  let input = null;

  // Choice buttons
  if (Array.isArray(data.choices) && data.choices.length > 0) {
    const choicesDiv = document.createElement('div');
    choicesDiv.className = 'reply-choices';
    data.choices.forEach(choice => {
      const btn = document.createElement('button');
      btn.className   = 'choice-btn';
      btn.textContent = choice;
      btn.addEventListener('click', () => {
        choicesDiv.querySelectorAll('.choice-btn').forEach(b => b.classList.remove('selected'));
        btn.classList.add('selected');
        selectedChoice = choice;
        if (input) input.value = choice;
        if (!data.allowFreeform) submitReply();
      });
      choicesDiv.appendChild(btn);
    });
    form.appendChild(choicesDiv);
  }

  // Freeform text input
  if (data.allowFreeform || !Array.isArray(data.choices) || data.choices.length === 0) {
    input = document.createElement('input');
    input.type        = 'text';
    input.className   = 'reply-input';
    input.placeholder = 'Type your reply…';
    input.addEventListener('keydown', e => { if (e.key === 'Enter') submitReply(); });
    form.appendChild(input);
  }

  // Reply button (only when freeform is allowed or no choices)
  if (data.allowFreeform || !Array.isArray(data.choices) || data.choices.length === 0) {
    const replyBtn = document.createElement('button');
    replyBtn.className   = 'reply-btn';
    replyBtn.textContent = 'Reply';
    replyBtn.addEventListener('click', submitReply);
    form.appendChild(replyBtn);
  }

  card.appendChild(form);

  async function submitReply() {
    const rawAnswer  = input ? input.value.trim() : (selectedChoice ?? '');
    const wasFreeform = !!(input && rawAnswer !== '' && rawAnswer !== selectedChoice);

    form.querySelectorAll('button, input').forEach(el => el.disabled = true);

    try {
      const res = await fetch('/user-input-reply', {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify({ requestId: data.requestId, answer: rawAnswer, wasFreeform }),
      });
      if (res.ok) {
        form.innerHTML = `<span class="reply-confirmed">✓ Replied: ${esc(rawAnswer || '(empty)')}</span>`;
      } else {
        const errText = await res.text();
        form.innerHTML = `<span class="reply-error">✗ Error: ${esc(errText)}</span>`;
      }
    } catch (err) {
      form.innerHTML = `<span class="reply-error">✗ Network error: ${esc(String(err))}</span>`;
    }
  }
}
