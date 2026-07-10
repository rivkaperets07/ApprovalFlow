function gatewayBase() {
    return document.getElementById('gatewayUrl').value.replace(/\/$/, '');
}

// --- Real credential-based session -------------------------------
// Each role-bearing section is tagged data-role="submitter"|"approver"|"admin";
// admin (which RequireRole treats as satisfying both policies — see
// AuthPolicies) sees everything. Nothing role-gated is visible until a real
// login/register succeeds, since every one of those actions would 401/403 anyway.
let authToken = null;
let currentRole = null;
let currentEmail = null;

function applyRoleVisibility(role) {
    document.querySelectorAll('section[data-role]').forEach(section => {
        section.style.display = (role === 'admin' || section.dataset.role === role) ? '' : 'none';
    });
}
applyRoleVisibility(null);

function showLoginForm() {
    document.getElementById('loginForm').style.display = 'block';
    document.getElementById('registerForm').style.display = 'none';
}
function showRegisterForm() {
    document.getElementById('loginForm').style.display = 'none';
    document.getElementById('registerForm').style.display = 'block';
}
document.getElementById('showLoginBtn').addEventListener('click', showLoginForm);
document.getElementById('showRegisterBtn').addEventListener('click', showRegisterForm);

function applySession(data, email) {
    authToken = data.token;
    currentRole = data.role;
    currentEmail = email;
    document.getElementById('sessionState').textContent = `Signed in as ${data.name ?? data.role} (${data.role}).`;
    document.getElementById('loginForm').style.display = 'none';
    document.getElementById('registerForm').style.display = 'none';
    document.getElementById('showLoginBtn').style.display = 'none';
    document.getElementById('showRegisterBtn').style.display = 'none';
    document.getElementById('logoutBtn').style.display = '';
    document.getElementById('changePasswordForm').style.display = 'block';
    applyRoleVisibility(currentRole);
    loadEscalations();
    loadStats();
    loadVendors();
}

async function login() {
    const state = document.getElementById('sessionState');
    const email = document.getElementById('loginEmail').value.trim();
    const response = await fetch(`${gatewayBase()}/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            Email: email,
            Password: document.getElementById('loginPassword').value
        })
    });
    const data = await response.json().catch(() => null);
    if (!response.ok || !data?.token) {
        state.textContent = data?.error || `Could not log in (HTTP ${response.status}).`;
        return;
    }
    applySession(data, email);
}
document.getElementById('loginBtn').addEventListener('click', login);

async function register() {
    const state = document.getElementById('sessionState');
    const email = document.getElementById('registerEmail').value.trim();
    const response = await fetch(`${gatewayBase()}/register`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            Name: document.getElementById('registerName').value.trim(),
            Email: email,
            Password: document.getElementById('registerPassword').value
        })
    });
    const data = await response.json().catch(() => null);
    if (!response.ok || !data?.token) {
        state.textContent = data?.error || `Could not register (HTTP ${response.status}).`;
        return;
    }
    applySession(data, email);
}
document.getElementById('registerBtn').addEventListener('click', register);

function logout() {
    authToken = null;
    currentRole = null;
    currentEmail = null;
    document.getElementById('loginPassword').value = '';
    document.getElementById('currentPassword').value = '';
    document.getElementById('newPassword').value = '';
    document.getElementById('changePasswordState').textContent = '';
    document.getElementById('sessionState').textContent = 'Not signed in — log in or register above.';
    document.getElementById('showLoginBtn').style.display = '';
    document.getElementById('showRegisterBtn').style.display = '';
    document.getElementById('logoutBtn').style.display = 'none';
    document.getElementById('changePasswordForm').style.display = 'none';
    showLoginForm();
    applyRoleVisibility(null);
}
document.getElementById('logoutBtn').addEventListener('click', logout);

document.getElementById('changePasswordBtn').addEventListener('click', async () => {
    const state = document.getElementById('changePasswordState');
    const currentPassword = document.getElementById('currentPassword').value;
    const newPassword = document.getElementById('newPassword').value;
    const { ok, status, data } = await callApi('change-password', 'POST', {
        Email: currentEmail,
        CurrentPassword: currentPassword,
        NewPassword: newPassword
    });
    if (!ok) {
        state.style.color = '#b8791c';
        state.textContent = data?.error || `Could not change password (HTTP ${status}).`;
        return;
    }
    state.style.color = '#2e7d32';
    state.textContent = 'Password updated.';
    document.getElementById('currentPassword').value = '';
    document.getElementById('newPassword').value = '';
});

// --- Admin-only user creation -----------------------------------------
document.getElementById('addUserBtn').addEventListener('click', async () => {
    const { status, data } = await callApi('users', 'POST', {
        Name: document.getElementById('addUserName').value.trim(),
        Email: document.getElementById('addUserEmail').value.trim(),
        Password: document.getElementById('addUserPassword').value,
        Role: document.getElementById('addUserRole').value
    });
    document.getElementById('addUserResult').textContent = `HTTP ${status}\n${JSON.stringify(data, null, 2)}`;
    if (status === 200) {
        document.getElementById('addUserName').value = '';
        document.getElementById('addUserEmail').value = '';
        document.getElementById('addUserPassword').value = '';
        document.getElementById('addUserRole').value = 'submitter';
    }
});

// --- Admin-only role assignment -----------------------------------
document.getElementById('setRoleBtn').addEventListener('click', async () => {
    const email = document.getElementById('setRoleEmail').value.trim();
    const role = document.getElementById('setRoleValue').value;
    const { status, data } = await callApi(`users/${encodeURIComponent(email)}/role`, 'PUT', { Role: role });
    document.getElementById('setRoleResult').textContent = `HTTP ${status}\n${JSON.stringify(data, null, 2)}`;
});

// --- Admin-only vendor directory ---------------------------------------
document.getElementById('addVendorBtn').addEventListener('click', async () => {
    const { status, data } = await callApi('vendors', 'POST', {
        Vendor: document.getElementById('addVendorName').value.trim(),
        Category: document.getElementById('addVendorCategory').value.trim()
    });
    document.getElementById('addVendorResult').textContent = `HTTP ${status}\n${JSON.stringify(data, null, 2)}`;
    if (status === 200) {
        document.getElementById('addVendorName').value = '';
        document.getElementById('addVendorCategory').value = '';
        loadVendors(); // refresh the known-vendors table/datalist so the submit form sees it right away
    }
});

// --- Admin-only policy editor --------------------------------------------
document.getElementById('loadPolicyBtn').addEventListener('click', async () => {
    const resultEl = document.getElementById('policyResult');
    const { ok, status, data } = await callApi('policy');
    if (!ok) {
        resultEl.textContent = `HTTP ${status}: could not load policy.`;
        return;
    }
    document.getElementById('policyEditor').value = JSON.stringify(data, null, 2);
    resultEl.textContent = 'Loaded current policy below.';
});

document.getElementById('savePolicyBtn').addEventListener('click', async () => {
    const resultEl = document.getElementById('policyResult');
    let parsed;
    try {
        parsed = JSON.parse(document.getElementById('policyEditor').value);
    } catch (e) {
        resultEl.textContent = `Not valid JSON: ${e.message}`;
        return;
    }
    const { status, data } = await callApi('policy', 'PUT', parsed);
    resultEl.textContent = `HTTP ${status}\n${JSON.stringify(data, null, 2)}`;
});

async function callApi(endpoint, method = 'GET', body = null) {
    const headers = { 'Content-Type': 'application/json' };
    if (authToken) headers['Authorization'] = `Bearer ${authToken}`;
    const response = await fetch(`${gatewayBase()}/${endpoint}`, {
        method,
        headers,
        body: body ? JSON.stringify(body) : null
    });
    const data = await response.json().catch(() => null);
    return { ok: response.ok, status: response.status, data };
}

function badge(value) {
    if (!value) return '<span class="empty">—</span>';
    return `<span class="badge badge-${value}">${value}</span>`;
}

function renderStatusCard(container, invoice) {
    container.style.display = 'block';
    const aiCategoryRow = invoice.aiSuggestedCategory
        ? `<div class="status-row"><span>AI classified as</span><span>${invoice.aiSuggestedCategory} (confidence ${invoice.aiConfidence ?? '—'})</span></div>`
        : `<div class="status-row"><span>AI classified as</span><span class="empty">AI was not consulted (e.g. blocked before or above the risk threshold)</span></div>`;
    // docs/policy.md rule_ids PolicyRetriever surfaced and the AI actually cited —
    // only present once the AI's been consulted at all (see aiCategoryRow above).
    const policyRulesRow = invoice.aiPolicyRulesCited && invoice.aiPolicyRulesCited.length > 0
        ? `<div class="status-row"><span>Policy rules cited</span><span>${invoice.aiPolicyRulesCited.join(', ')}</span></div>`
        : '';
    container.innerHTML = `
        <div class="status-row"><span>Tracking ID</span><span>${invoice.trackingId ?? ''}</span></div>
        <div class="status-row"><span>Decision</span>${badge(invoice.status)}</div>
        <div class="status-row"><span>Decided by</span><span>${invoice.decidedBy ?? '—'}</span></div>
        <div class="status-row"><span>Reason</span><span>${invoice.reason ?? '—'}</span></div>
        <div class="status-row"><span>Vendor / Amount</span><span>${invoice.vendor ?? ''} / ${invoice.totalAmount ?? ''}</span></div>
        ${aiCategoryRow}
        ${policyRulesRow}
        <div class="status-row"><span>Payment</span>${badge(invoice.paymentStatus)}</div>
        ${invoice.paymentMessage ? `<div class="status-row"><span>Payment detail</span><span>${invoice.paymentMessage}</span></div>` : ''}
    `;
}

// --- Vendor-driven conditional fields (Meals entertainment / Travel TripId) ---
// Category is no longer a free-text field the submitter fills in — it's resolved
// from the vendor directory (same source PolicyEngine's GLOBAL-VENDOR uses), via
// vendorCategories built in loadVendors() below. An unrecognized vendor shows
// neither block; picking a known one reveals whichever applies.
function updateVendorCategoryHints() {
    const vendor = document.getElementById('vendor').value.trim().toLowerCase();
    const category = (vendorCategories[vendor] || '').toLowerCase();
    document.getElementById('mealsField').classList.toggle('visible', category.includes('meal'));
    document.getElementById('travelField').classList.toggle('visible', category.includes('travel'));
}
document.getElementById('vendor').addEventListener('input', updateVendorCategoryHints);

document.getElementById('isClientEntertainment').addEventListener('change', (e) => {
    document.getElementById('clientEntertainmentFields').style.display = e.target.checked ? 'block' : 'none';
});

// --- Line items (GLOBAL-RECEIPT / GLOBAL-MATH: no OCR, the submitter provides
// the itemized breakdown directly instead of the AI guessing it) ---
function addLineItemRow(description = '', amount = '') {
    const container = document.getElementById('lineItemsContainer');
    const row = document.createElement('div');
    row.className = 'line-item-row';
    row.innerHTML = `
        <input type="text" class="line-item-desc" placeholder="Description" value="${description}" />
        <input type="number" step="0.01" class="line-item-amount" placeholder="0.00" value="${amount}" />
        <button type="button" class="line-item-remove" title="Remove">×</button>
    `;
    row.querySelector('.line-item-remove').addEventListener('click', () => {
        row.remove();
        updateLineItemsHint();
    });
    row.querySelectorAll('input').forEach(input => input.addEventListener('input', updateLineItemsHint));
    container.appendChild(row);
    updateLineItemsHint();
}
document.getElementById('addLineItemBtn').addEventListener('click', () => addLineItemRow());

function collectLineItems() {
    return Array.from(document.querySelectorAll('.line-item-row')).map(row => ({
        Description: row.querySelector('.line-item-desc').value,
        Amount: parseFloat(row.querySelector('.line-item-amount').value) || 0
    })).filter(item => item.Description || item.Amount);
}

function updateLineItemsHint() {
    const hint = document.getElementById('lineItemsHint');
    const requiredTag = document.getElementById('lineItemsRequired');
    const total = parseFloat(document.getElementById('totalAmount').value) || 0;
    const items = collectLineItems();
    const itemsSum = items.reduce((sum, i) => sum + i.Amount, 0);

    requiredTag.style.display = total > 25 ? 'inline' : 'none';

    if (items.length === 0) {
        hint.textContent = total > 25
            ? 'No line items yet — required for amounts over $25 (GLOBAL-RECEIPT), or this will be escalated.'
            : 'No line items yet (optional under $25).';
        hint.classList.toggle('warn', total > 25);
        return;
    }

    const tolerance = Math.min(total * 0.02, 10);
    const variance = Math.abs(total - itemsSum);
    const withinTolerance = variance <= tolerance;
    hint.textContent = `Line items total $${itemsSum.toFixed(2)} vs. invoice total $${total.toFixed(2)}` +
        (withinTolerance ? ' — OK.' : ` — off by $${variance.toFixed(2)} (allowed: $${tolerance.toFixed(2)}, GLOBAL-MATH).`);
    hint.classList.toggle('warn', !withinTolerance);
}
document.getElementById('totalAmount').addEventListener('input', updateLineItemsHint);

// --- TrackingId: generated client-side and held steady across
// retries of the *same* submission attempt, so an accidental double POST
// (network retry, browser back-button resubmit, a click that slips past the
// disabled button) carries the same id and the Gateway's idempotency check
// (ISubmissionStore) actually catches it. Previously the Gateway minted a
// fresh GUID server-side whenever the client sent none, so every "accidental"
// resubmit looked like a brand-new invoice. A new id is only rotated in once
// a submission has been confirmed accepted, so the *next distinct* expense
// gets its own id.
function generateTrackingId() {
    return (window.crypto && crypto.randomUUID)
        ? crypto.randomUUID()
        : `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}
document.getElementById('trackingId').value = generateTrackingId();

// --- Submit + auto-poll ---------------------------------------------
document.getElementById('invoiceForm').addEventListener('submit', async (e) => {
    e.preventDefault();
    const submitBtn = e.target.querySelector('.btn-submit');
    if (submitBtn.disabled) return; // a submit for this click is already in flight

    const trackingIdField = document.getElementById('trackingId');
    if (!trackingIdField.value) trackingIdField.value = generateTrackingId(); // safety net

    const tripIdValue = document.getElementById('tripId').value;
    const isClientEntertainment = document.getElementById('isClientEntertainment').checked;
    const payload = {
        TrackingId: trackingIdField.value,
        Vendor: document.getElementById('vendor').value,
        InvoiceNumber: document.getElementById('invoiceNumber').value || null,
        TotalAmount: parseFloat(document.getElementById('totalAmount').value),
        Notes: document.getElementById('description').value,
        Currency: document.getElementById('currency').value || null,
        TripId: tripIdValue || null,
        IsPremiumTravel: document.getElementById('isPremiumTravel').checked,
        IsClientEntertainment: isClientEntertainment,
        BusinessJustification: isClientEntertainment ? (document.getElementById('businessJustification').value || null) : null,
        ClientName: isClientEntertainment ? (document.getElementById('clientName').value || null) : null,
        LineItems: collectLineItems()
    };
    const statusBox = document.getElementById('submitStatus');
    statusBox.style.display = 'block';
    statusBox.textContent = 'Submitting...';

    // Only guard the POST itself — an accidental double-click could fire it twice.
    // Re-enable right after so the submitter can immediately start a
    // separate, unrelated expense without waiting for this one's status polling.
    submitBtn.disabled = true;
    let submitResult;
    try {
        submitResult = await callApi('submit', 'POST', payload);
    } finally {
        submitBtn.disabled = false;
    }
    if (!submitResult.ok || !submitResult.data?.trackingId) {
        // Deliberately do not rotate trackingIdField.value here: if the submitter
        // retries after a failure, the retry should carry the same id so the
        // Gateway can tell it's the same attempt.
        statusBox.textContent = `Error: HTTP ${submitResult.status}`;
        return;
    }

    const trackingId = submitResult.data.trackingId;
    statusBox.textContent = `Submitted as ${trackingId}. Waiting for a decision...`;
    trackingIdField.value = generateTrackingId(); // accepted — next submission gets a fresh id

    // Push-based notification instead of polling — this holds one connection
    // open and the Gateway wakes it up the moment invoice.decided fires (or replies
    // immediately if the decision already landed). Falls back to "still pending" +
    // manual Check Status after ~15s, same as the old polling loop did, in case the
    // connection stalls.
    const giveUp = setTimeout(() => {
        source.close();
        statusBox.textContent = `Submitted as ${trackingId}. Still pending — use "Check Status" below to keep watching.`;
        document.getElementById('checkTrackingId').value = trackingId;
    }, 15000);

    // EventSource cannot set an Authorization header, so the token travels as an
    // access_token query parameter — the Gateway's JwtBearer OnMessageReceived
    // accepts it for /notifications only.
    const source = new EventSource(`${gatewayBase()}/notifications/${trackingId}?access_token=${encodeURIComponent(authToken ?? '')}`);
    source.onmessage = async () => {
        clearTimeout(giveUp);
        source.close();
        const { ok, data } = await callApi(`status/${trackingId}`);
        if (ok) renderStatusCard(statusBox, data);
    };
    source.onerror = () => {
        clearTimeout(giveUp);
        source.close();
        statusBox.textContent = `Submitted as ${trackingId}. Connection lost — use "Check Status" below.`;
        document.getElementById('checkTrackingId').value = trackingId;
    };
});

// --- Manual status check ---------------------------------------------
document.getElementById('checkStatusBtn').addEventListener('click', async () => {
    const id = document.getElementById('checkTrackingId').value.trim();
    const box = document.getElementById('checkStatusResult');
    if (!id) return;
    box.style.display = 'block';
    box.textContent = 'Loading...';
    const { ok, status, data } = await callApi(`status/${id}`);
    if (!ok) {
        box.textContent = `HTTP ${status}: not found.`;
        return;
    }
    renderStatusCard(box, data);
});

// --- Audit trail ---------------------------------------------
document.getElementById('pullAuditBtn').addEventListener('click', async () => {
    const id = document.getElementById('auditTrackingId').value.trim();
    const result = document.getElementById('auditResult');
    if (!id) return;
    result.textContent = 'Loading...';
    const { ok, status, data } = await callApi(`audit/${id}`);
    result.textContent = ok ? JSON.stringify(data, null, 2) : `HTTP ${status}: not found.`;
});

// --- Escalation queue ---------------------------------------------
async function loadEscalations() {
    const list = document.getElementById('escalationsList');
    const { ok, data } = await callApi('escalations');
    if (!ok || !data || data.length === 0) {
        list.innerHTML = '<p class="empty">No escalated items right now.</p>';
        return;
    }
    const rows = data.map(item => `
        <tr>
            <td>${item.trackingId}</td>
            <td>${item.vendor}</td>
            <td>${item.aiSuggestedCategory ?? '<span class="empty">n/a</span>'}${item.aiConfidence ? ` (${item.aiConfidence})` : ''}</td>
            <td>${item.aiPolicyRulesCited && item.aiPolicyRulesCited.length > 0 ? item.aiPolicyRulesCited.join(', ') : '<span class="empty">—</span>'}</td>
            <td>${item.totalAmount}</td>
            <td>${item.reason}</td>
        </tr>
    `).join('');
    list.innerHTML = `
        <table>
            <thead><tr><th>Tracking ID</th><th>Vendor</th><th>AI Category (confidence)</th><th>Policy Rules Cited</th><th>Amount</th><th>Reason</th></tr></thead>
            <tbody>${rows}</tbody>
        </table>
    `;
}
document.getElementById('refreshEscalationsBtn').addEventListener('click', loadEscalations);

// --- Known vendors ---------------------------------------------
let vendorCategories = {};
async function loadVendors() {
    const list = document.getElementById('vendorsList');
    const datalist = document.getElementById('knownVendors');
    const { ok, data } = await callApi('vendors');
    if (!ok || !data || data.length === 0) {
        list.innerHTML = '<p class="empty">No known vendors configured.</p>';
        datalist.innerHTML = '';
        vendorCategories = {};
        return;
    }
    const rows = data.map(v => `<tr><td>${v.vendor}</td><td>${v.category}</td></tr>`).join('');
    list.innerHTML = `
        <table>
            <thead><tr><th>Vendor</th><th>Category</th></tr></thead>
            <tbody>${rows}</tbody>
        </table>
    `;
    datalist.innerHTML = data.map(v => `<option value="${v.vendor}"></option>`).join('');
    vendorCategories = Object.fromEntries(data.map(v => [v.vendor.toLowerCase(), v.category]));
    updateVendorCategoryHints();
}
document.getElementById('refreshVendorsBtn').addEventListener('click', loadVendors);

// --- Manual approve/reject ---------------------------------------------
document.getElementById('approveBtn').addEventListener('click', async () => {
    const id = document.getElementById('manualTrackingId').value.trim();
    const { status, data } = await callApi(`approve/${id}`, 'POST');
    document.getElementById('manualResult').textContent = `HTTP ${status}\n${JSON.stringify(data, null, 2)}`;
    loadEscalations();
});

document.getElementById('rejectBtn').addEventListener('click', async () => {
    const id = document.getElementById('manualTrackingId').value.trim();
    const { status, data } = await callApi(`reject/${id}`, 'POST');
    document.getElementById('manualResult').textContent = `HTTP ${status}\n${JSON.stringify(data, null, 2)}`;
    loadEscalations();
});

// The third leg of the approver's one-action set — send the item back to the
// submitter instead of deciding outright. Drops it out of the escalation queue
// until the submitter responds via "Provide More Info" below.
document.getElementById('requestInfoBtn').addEventListener('click', async () => {
    const id = document.getElementById('manualTrackingId').value.trim();
    const message = document.getElementById('manualMessage').value.trim() || null;
    const { status, data } = await callApi(`request-info/${id}`, 'POST', { Message: message });
    document.getElementById('manualResult').textContent = `HTTP ${status}\n${JSON.stringify(data, null, 2)}`;
    loadEscalations();
});

// --- "Provide More Info": look up the invoice's category on Tracking ID and show
// only the fields that category actually needs, same idea as the Submit form's
// updateVendorCategoryHints but driven by the stored invoice instead of the vendor
// field.
async function lookupInfoCategory() {
    const id = document.getElementById('infoTrackingId').value.trim();
    const stateEl = document.getElementById('infoCategoryState');
    const mealsField = document.getElementById('infoMealsField');
    const travelField = document.getElementById('infoTravelField');
    const defaultHint = 'Leave this field to look up the invoice and show only the fields relevant to its category.';

    if (!id) {
        stateEl.textContent = defaultHint;
        mealsField.classList.remove('visible');
        travelField.classList.remove('visible');
        return;
    }

    const { ok, data } = await callApi(`status/${id}`);
    if (!ok || !data) {
        stateEl.textContent = `Could not look up invoice ${id} — check the tracking ID and that you're signed in.`;
        mealsField.classList.remove('visible');
        travelField.classList.remove('visible');
        return;
    }

    // aiSuggestedCategory is what actually drove the decision (PolicyEngine
    // resolves it from the vendor directory); the submitter's own free-text
    // category is only a fallback for items escalated before the AI was ever
    // consulted, e.g. GLOBAL-RECEIPT — see InvoicePayload for why the two fields
    // can differ.
    const resolvedCategory = data.aiSuggestedCategory || data.category || '';
    const lower = resolvedCategory.toLowerCase();
    // Only truly undetermined (empty) falls back to showing both blocks — a
    // resolved category that's neither Meals nor Travel (SaaS, Hardware,
    // OfficeSupplies, Marketing, Other) means neither block applies, and both
    // should stay hidden, not show as an "unknown category" fallback.
    const categoryUndetermined = resolvedCategory === '';
    mealsField.classList.toggle('visible', lower.includes('meal') || categoryUndetermined);
    travelField.classList.toggle('visible', lower.includes('travel') || categoryUndetermined);
    stateEl.textContent = resolvedCategory
        ? `Category: ${resolvedCategory} (status: ${data.status}) — showing its relevant fields below.`
        : `Category not yet determined for this invoice — showing all optional fields.`;
}
document.getElementById('infoTrackingId').addEventListener('blur', lookupInfoCategory);

// --- "Provide More Info" line items (independent of the submit form's) ---
function addInfoLineItemRow() {
    const container = document.getElementById('infoLineItemsContainer');
    const row = document.createElement('div');
    row.className = 'line-item-row';
    row.innerHTML = `
        <input type="text" class="info-line-item-desc" placeholder="Description" />
        <input type="number" step="0.01" class="info-line-item-amount" placeholder="0.00" />
        <button type="button" class="line-item-remove" title="Remove">×</button>
    `;
    row.querySelector('.line-item-remove').addEventListener('click', () => row.remove());
    container.appendChild(row);
}
document.getElementById('infoAddLineItemBtn').addEventListener('click', addInfoLineItemRow);

function collectInfoLineItems() {
    const items = Array.from(document.querySelectorAll('#infoLineItemsContainer .line-item-row')).map(row => ({
        Description: row.querySelector('.info-line-item-desc').value,
        Amount: parseFloat(row.querySelector('.info-line-item-amount').value) || 0
    })).filter(item => item.Description || item.Amount);
    return items.length > 0 ? items : null; // null = "leave what's already stored"
}

// Resume half of send-back-for-info: only send fields the submitter actually filled
// in (empty -> null) so this doesn't blank out data the reviewer didn't ask about.
document.getElementById('provideInfoBtn').addEventListener('click', async () => {
    const id = document.getElementById('infoTrackingId').value.trim();
    const orNull = (value) => value ? value : null;
    const payload = {
        Notes: orNull(document.getElementById('infoNotes').value),
        LineItems: collectInfoLineItems(),
        BusinessJustification: orNull(document.getElementById('infoBusinessJustification').value),
        ClientName: orNull(document.getElementById('infoClientName').value),
        Currency: orNull(document.getElementById('infoCurrency').value),
        TripId: orNull(document.getElementById('infoTripId').value),
        IsPremiumTravel: document.getElementById('infoIsPremiumTravel').checked ? true : null
    };
    const { status, data } = await callApi(`provide-info/${id}`, 'POST', payload);
    document.getElementById('provideInfoResult').textContent = `HTTP ${status}\n${JSON.stringify(data, null, 2)}`;
    loadEscalations();
});

// --- Dashboard stats ---------------------------------------------
async function loadStats() {
    const panel = document.getElementById('statsPanel');
    const { ok, data } = await callApi('stats');
    if (!ok || !data) {
        panel.innerHTML = '<p class="empty">Could not load stats.</p>';
        return;
    }
    const boxes = [
        ['Total submitted', data.totalSubmitted],
        ['Auto-approved (AI)', data.autoApproved],
        ['Human-approved', data.humanApproved],
        ['Escalated (pending)', data.escalatedPending],
        ['Needs info (pending)', data.needsInfo],
        ['Rejected', data.rejected],
        ['Duplicate (rejected)', data.duplicateRejected],
        ['Auto-approval rate', `${Math.round((data.autoApprovalRate ?? 0) * 100)}%`],
        ['$ auto-approved', data.autoApprovedAmount],
        ['$ human-approved', data.humanApprovedAmount],
    ];
    panel.innerHTML = boxes.map(([label, value]) => `
        <div class="stat-box"><div class="n">${value}</div><div class="l">${label}</div></div>
    `).join('');
}
document.getElementById('refreshStatsBtn').addEventListener('click', loadStats);

// Initial load: sign in with the default role first, then the panels load
// from inside login() once a token is available.
login();
updateLineItemsHint();
