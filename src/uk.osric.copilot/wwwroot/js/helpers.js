// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

// @ts-check

/** Escape a value for safe insertion into HTML attribute or text content.
 * @param {*} s
 * @returns {string}
 */
export function esc(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

/** Format an ISO timestamp as HH:MM:SS.mmm using the browser locale.
 * @param {string} iso
 * @returns {string}
 */
export function formatTs(iso) {
  try {
    return new Date(iso).toLocaleTimeString(undefined, {
      hour12: false, hour: '2-digit', minute: '2-digit',
      second: '2-digit', fractionalSecondDigits: 3,
    });
  } catch {
    return iso;
  }
}

/** Express a timestamp as a human-readable relative string (e.g. "3m ago").
 * @param {string} iso
 * @returns {string}
 */
export function relativeTime(iso) {
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 60_000)     return 'just now';
  if (diff < 3_600_000)  return `${Math.floor(diff / 60_000)}m ago`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)}h ago`;
  return `${Math.floor(diff / 86_400_000)}d ago`;
}

/** CSS utility class for the scalar type of a JSON value.
 * @param {unknown} value
 * @returns {string}
 */
export function scalarClass(value) {
  if (value === null)             return 'prop-null';
  if (typeof value === 'boolean') return 'prop-bool';
  if (typeof value === 'number')  return 'prop-number';
  return 'prop-string';
}

/** One-line preview string for an object or array.
 * @param {object} value
 * @returns {string}
 */
export function previewOf(value) {
  if (Array.isArray(value)) return `[${value.length} items]`;
  const keys = Object.keys(value);
  if (keys.length === 0) return '{}';
  return `{ ${keys.slice(0, 3).join(', ')}${keys.length > 3 ? ', …' : ''} }`;
}

/** CSS class for the coloured left border of an event card.
 * @param {string} eventType
 * @returns {string}
 */
export function colorClass(eventType) {
  if (/^Assistant|^Agent/.test(eventType)) return 'type-assistant';
  if (/^User/.test(eventType))             return 'type-user';
  if (/^Tool/.test(eventType))             return 'type-tool';
  if (/^Session/.test(eventType))          return 'type-session';
  if (/Error|error/.test(eventType))       return 'type-error';
  if (/^System/.test(eventType))           return 'type-system';
  return '';
}
