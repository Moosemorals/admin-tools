// SPDX-FileCopyrightText: Copyright (c) 2026 Osric Wilkinson
// SPDX-License-Identifier: MIT

// @ts-check
import { formatTs, esc } from './helpers.js';

/**
 * @param {string} id
 * @param {string} text
 * @param {boolean} ok
 */
function showMsg(id, text, ok) {
    const el = document.getElementById(id);
    if (!el) { return; }
    el.className = 'msg ' + (ok ? 'msg-ok' : 'msg-err');
    el.textContent = text;
}

/**
 * @param {{ isRevoked: boolean, notBefore: string, notAfter: string }} cert
 * @returns {string} CSS class suffix
 */
function certStatusClass(cert) {
    if (cert.isRevoked) { return 'revoked'; }
    const now = Date.now();
    const nb = new Date(cert.notBefore).getTime();
    const na = new Date(cert.notAfter).getTime();
    if (now < nb || now > na) { return 'expired'; }
    return 'valid';
}

/**
 * @param {string} statusClass
 * @returns {HTMLElement}
 */
function makeBadge(statusClass) {
    const span = document.createElement('span');
    span.className = `badge badge-${statusClass}`;
    span.textContent = statusClass.charAt(0).toUpperCase() + statusClass.slice(1);
    return span;
}

/**
 * @param {Array<object>} certs
 */
function renderTable(certs) {
    const section = /** @type {HTMLElement} */ (document.getElementById('table-section'));
    const tbody = /** @type {HTMLTableSectionElement} */ (document.getElementById('certsBody'));
    while (tbody.firstChild) { tbody.removeChild(tbody.firstChild); }

    if (certs.length === 0) {
        const tr = document.createElement('tr');
        const td = document.createElement('td');
        td.colSpan = 6;
        td.style.textAlign = 'center';
        td.style.color = 'var(--text-muted)';
        td.textContent = 'No certificates found.';
        tr.appendChild(td);
        tbody.appendChild(tr);
    } else {
        for (const c of certs) {
            const tr = document.createElement('tr');

            const tdId = document.createElement('td');
            tdId.textContent = String(c.id);
            tr.appendChild(tdId);

            const tdFp = document.createElement('td');
            const code = document.createElement('code');
            code.textContent = c.fingerprint ?? '';
            tdFp.appendChild(code);
            tr.appendChild(tdFp);

            const tdNb = document.createElement('td');
            tdNb.textContent = formatTs(c.notBefore);
            tr.appendChild(tdNb);

            const tdNa = document.createElement('td');
            tdNa.textContent = formatTs(c.notAfter);
            tr.appendChild(tdNa);

            const tdStatus = document.createElement('td');
            tdStatus.appendChild(makeBadge(certStatusClass(c)));
            tr.appendChild(tdStatus);

            const tdActions = document.createElement('td');
            tdActions.className = 'actions';

            const dlBtn = document.createElement('a');
            dlBtn.href = `/api/keys/${c.id}/download`;
            const dlInner = document.createElement('button');
            dlInner.className = 'btn-small';
            dlInner.textContent = 'Download';
            dlBtn.appendChild(dlInner);
            tdActions.appendChild(dlBtn);

            if (!c.isRevoked) {
                const revBtn = document.createElement('button');
                revBtn.className = 'btn-danger';
                revBtn.textContent = 'Revoke';
                revBtn.addEventListener('click', () => revokeKey(c.id));
                tdActions.appendChild(revBtn);
            }

            tr.appendChild(tdActions);
            tbody.appendChild(tr);
        }
    }

    section.style.display = 'block';
}

async function generateKey() {
    const emailEl = /** @type {HTMLInputElement} */ (document.getElementById('emailInput'));
    const daysEl = /** @type {HTMLInputElement} */ (document.getElementById('daysInput'));
    const keyTypeEl = /** @type {HTMLSelectElement} */ (document.getElementById('keyTypeSelect'));
    const email = emailEl.value.trim();
    const days = parseInt(daysEl.value, 10) || 365;
    const keyType = keyTypeEl.value;
    if (!email) {
        showMsg('genMsg', 'Please enter an email address.', false);
        return;
    }
    try {
        const res = await fetch('/api/keys/generate', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ emailAddress: email, validDays: days, keyType }),
        });
        if (!res.ok) {
            showMsg('genMsg', `Error: ${res.status} ${res.statusText}`, false);
            return;
        }
        const data = await res.json();
        const msgEl = document.getElementById('genMsg');
        if (!msgEl) { return; }
        msgEl.className = 'msg msg-ok';
        while (msgEl.firstChild) { msgEl.removeChild(msgEl.firstChild); }

        const text = document.createTextNode(
            `Generated key ID ${data.id} — fingerprint ${data.fingerprint ?? ''}. ` +
            `Valid until ${formatTs(data.notAfter)}. `
        );
        msgEl.appendChild(text);

        const link = document.createElement('a');
        link.href = data.downloadUrl;
        link.textContent = 'Download PFX';
        msgEl.appendChild(link);

        await loadKeys();
    } catch (e) {
        showMsg('genMsg', `Network error: ${e.message}`, false);
    }
}

async function loadKeys() {
    const emailEl = /** @type {HTMLInputElement} */ (document.getElementById('emailInput'));
    const email = emailEl.value.trim();
    if (!email) {
        showMsg('genMsg', 'Please enter an email address.', false);
        return;
    }
    try {
        const res = await fetch(`/api/keys?email=${encodeURIComponent(email)}`);
        if (!res.ok) {
            showMsg('genMsg', `Error: ${res.status} ${res.statusText}`, false);
            return;
        }
        const certs = await res.json();
        renderTable(certs);
    } catch (e) {
        showMsg('genMsg', `Network error: ${e.message}`, false);
    }
}

/**
 * @param {number} id
 */
async function revokeKey(id) {
    if (!confirm(`Revoke certificate ID ${id}? This cannot be undone.`)) { return; }
    try {
        const res = await fetch(`/api/keys/${id}`, { method: 'DELETE' });
        if (res.ok || res.status === 204) {
            await loadKeys();
        } else {
            alert(`Failed to revoke: ${res.status} ${res.statusText}`);
        }
    } catch (e) {
        alert(`Network error: ${e.message}`);
    }
}

document.addEventListener('DOMContentLoaded', () => {
    document.getElementById('generateBtn')?.addEventListener('click', generateKey);
    document.getElementById('loadBtn')?.addEventListener('click', loadKeys);
});
