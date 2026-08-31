// Aurora control panel (RFC 11).
//
// Everything shown here is read from the RFC 10 API and nothing is inferred. Where Aurora does not
// know something, the panel says so rather than filling the gap — the whole point of the panel is
// that a person can check what the system actually did.

'use strict';

/** How old a view may be before acting on it needs a reload first (RFC 11 limit case). */
const STALE_AFTER_MS = 60_000;

const state = {
  route: 'work',
  loadedAt: null,
  degraded: false,
  auditCursor: 0,
  audit: [],
};

const $ = (id) => document.getElementById(id);
const announce = (message) => { $('announcer').textContent = message; };

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => (
    { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]
  ));
}

// ---- talking to the server -------------------------------------------------------------------

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: { 'Content-Type': 'application/json', ...(options.headers || {}) },
    credentials: 'same-origin',
  });

  if (response.status === 401 || response.status === 403) {
    const body = await response.json().catch(() => null);
    const error = new Error(body?.errors?.[0]?.message || 'Not permitted.');
    error.status = response.status;
    throw error;
  }

  const body = await response.json().catch(() => null);
  if (!response.ok) {
    const error = new Error(body?.errors?.[0]?.message || `Request failed (${response.status}).`);
    error.status = response.status;
    error.code = body?.errors?.[0]?.code;
    throw error;
  }

  return body?.data;
}

/** A fresh key per attempt, so a retry after a failure is a new request and not a replay. */
const newKey = () => (crypto.randomUUID ? crypto.randomUUID() : String(Date.now() + Math.random()));

// ---- freshness -------------------------------------------------------------------------------

function isStale() {
  return state.loadedAt === null || (Date.now() - state.loadedAt) > STALE_AFTER_MS;
}

function paintFreshness() {
  const node = $('freshness');
  if (state.loadedAt === null) {
    node.textContent = 'Not loaded yet.';
    return;
  }

  const seconds = Math.round((Date.now() - state.loadedAt) / 1000);
  const age = seconds < 60 ? `${seconds}s ago` : `${Math.round(seconds / 60)} min ago`;

  node.textContent = `Read ${age}.`;
  node.classList.toggle('stale', isStale());

  if (isStale()) {
    node.textContent = `Read ${age} — reload before deciding anything.`;
  }
}

function setDegraded(degraded) {
  state.degraded = degraded;
  $('degraded').hidden = !degraded;

  // Rule: do not accept an action the server cannot confirm.
  document.querySelectorAll('main button.primary, main button.reject')
    .forEach((button) => { button.disabled = degraded; });
}

// ---- rendering helpers -----------------------------------------------------------------------

/**
 * Rule 1: generated, draft, proposed, ongoing and completed are five different things, and the
 * panel must not let them look alike. The word is the signal; the border style repeats it without
 * colour, for anyone who cannot rely on colour.
 */
function badge(kind, label) {
  return `<span class="state state-${kind}">${escapeHtml(label || kind)}</span>`;
}

function card(inner) {
  const element = document.createElement('div');
  element.className = 'card';
  element.innerHTML = inner;
  return element;
}

function empty(message) {
  return card(`<p class="empty">${escapeHtml(message)}</p>`);
}

function fill(id, nodes) {
  const host = $(id);
  host.replaceChildren(...nodes);
}

function definitions(pairs) {
  const rows = pairs
    .filter(([, value]) => value !== null && value !== undefined && value !== '')
    .map(([term, value]) => `<dt>${escapeHtml(term)}</dt><dd>${value}</dd>`)
    .join('');

  return rows ? `<dl>${rows}</dl>` : '';
}

/**
 * Rule 4: sensitive material is hidden by default and revealed only by an explicit, temporary
 * gesture. Temporary literally — the reveal times itself out, so a panel left open on a desk does
 * not keep showing it.
 */
function sensitive(value) {
  const id = `s${Math.random().toString(36).slice(2)}`;
  return `<span class="sensitive">
    <span class="value" id="${id}" data-hidden="true" data-value="${escapeHtml(value)}">••••••••</span>
    <button type="button" class="ghost" data-reveal="${id}">Reveal for 10s</button>
  </span>`;
}

document.addEventListener('click', (event) => {
  const target = event.target.closest('[data-reveal]');
  if (!target) {
    return;
  }

  const node = $(target.dataset.reveal);
  node.textContent = node.dataset.value;
  node.dataset.hidden = 'false';
  target.disabled = true;
  announce('Revealed for ten seconds.');

  setTimeout(() => {
    node.textContent = '••••••••';
    node.dataset.hidden = 'true';
    target.disabled = false;
  }, 10_000);
});

// ---- sections --------------------------------------------------------------------------------

/**
 * Rule 5: never suggest Aurora is working in the background without a verifiable active task.
 * Only cycles the server reports as open are shown, and an empty list says exactly that.
 */
function renderWork(status) {
  const running = (status.schedules || []).filter((s) => s.status === 'ACTIVE');
  const nodes = [];

  nodes.push(card(`
    <h4>Right now</h4>
    <p>${badge('completed', 'idle')} Aurora is not in the middle of anything it started itself.</p>
    <p class="empty">A running cycle would appear here. Nothing is running.</p>
    ${definitions([
      ['Risk posture', escapeHtml(status.situation.risk_posture)],
      ['Suggested mode', escapeHtml(status.situation.recommended_response_mode)],
      ['Quiet hours', status.situation.quiet_hours_active ? 'active' : 'not active'],
    ])}`));

  if (running.length === 0) {
    nodes.push(empty('No schedule is active, so nothing is due to start on its own.'));
  } else {
    running.forEach((schedule) => nodes.push(card(`
      <h4>${escapeHtml(schedule.title)}</h4>
      <p>${badge('ongoing', 'scheduled')} Runs on its own, and every occurrence is still checked.</p>
      ${definitions([
        ['Trigger', `${escapeHtml(schedule.trigger)} — <code>${escapeHtml(schedule.expression)}</code>`],
        ['Time zone', escapeHtml(schedule.timezone)],
        ['Next', escapeHtml(schedule.next_run_at_utc || 'nothing scheduled')],
        ['If missed', escapeHtml(schedule.missed_run_policy)],
      ])}`)));
  }

  fill('work-body', nodes);
}

function renderGoals(status) {
  const needs = status.needs || [];
  const nodes = [];

  if (needs.length === 0) {
    nodes.push(empty('Nothing is waiting on Aurora.'));
  }

  needs.forEach((need) => nodes.push(card(`
    <h4>${escapeHtml(need.subject_ref)}</h4>
    <p>${badge(need.status === 'PLANNED' ? 'draft' : 'proposed', need.kind)}
       ${escapeHtml(need.satisfaction_condition)}</p>
    ${definitions([
      ['Noticed because', escapeHtml((need.evidence_refs || []).join('; '))],
      ['Intensity', need.intensity.toFixed(2)],
      ['Owner', escapeHtml(need.owner)],
      ['Status', escapeHtml(need.status)],
      ['Goal drafted', need.recommended_goal_ref
        ? `${escapeHtml(need.recommended_goal_ref)} (draft — nothing has started)`
        : 'none'],
    ])}`)));

  fill('goals-body', nodes);
}

/**
 * Rule 3: the origin, the trust and the lifecycle of a memory, and the means to correct or delete
 * it. All three are shown together because a claim without its source is not checkable.
 */
function renderMemories(result) {
  const matches = result.matches || [];
  const nodes = [];

  if (!result.confident) {
    nodes.push(card(`<p class="problem">
      Search was degraded${result.degradation ? `: ${escapeHtml(result.degradation)}` : ''}.
      An empty result here does <strong>not</strong> mean Aurora knows nothing about this.
    </p>`));
  }

  if (matches.length === 0) {
    nodes.push(empty('Nothing recorded matches that.'));
  }

  matches.forEach(({ memory, score }) => {
    const classified = memory.sensitivity_class === 'CONFIDENTIAL' || memory.sensitivity_class === 'SECRET';

    nodes.push(card(`
      <h4>${escapeHtml(memory.summary)}</h4>
      <p>${badge(memory.status === 'ACTIVE' ? 'completed' : 'draft', memory.status)}
         ${escapeHtml(memory.kind)} · confidence ${memory.confidence.toFixed(2)} · match ${score.toFixed(2)}</p>
      ${definitions([
        ['Came from', escapeHtml((memory.source_refs || []).join('; ')) || 'not recorded'],
        ['Evidence', escapeHtml((memory.evidence_refs || []).join('; ')) || 'not recorded'],
        ['Anchored to', escapeHtml((memory.anchors || []).map((a) => `${a.kind} ${a.ref} — ${a.reason}`).join('; '))],
        ['Recorded by', escapeHtml(memory.created_by)],
        ['Classification', escapeHtml(memory.sensitivity_class)],
        ['Value', classified ? sensitive(memory.object_json) : `<code>${escapeHtml(memory.object_json)}</code>`],
      ])}
      <div class="actions">
        <button type="button" class="primary" data-correct="${escapeHtml(memory.id)}">Correct this</button>
        <button type="button" class="reject" data-forget="${escapeHtml(memory.id)}">Ask Aurora to forget it</button>
      </div>`));
  });

  fill('memory-body', nodes);
}

/**
 * Rule 2: an approval card shows what would happen, to what, what it would disclose and how long
 * it stands — and offers a plain refusal. No ambiguous buttons: the labels say what they do.
 */
function renderApprovals(status) {
  const pending = (status.signals || []).filter((s) => s.kind === 'ALERT');
  const nodes = [];

  nodes.push(card(`
    <h4>How approvals reach you</h4>
    <p>Aurora asks whenever an action reaches outside itself. A request appears here, and
       approving it covers <strong>that request only</strong> — the same action with different
       input asks again.</p>
    <p class="empty">Pending requests are listed below when there are any.</p>`));

  if (pending.length === 0) {
    nodes.push(empty('Nothing is waiting for your decision.'));
  }

  pending.forEach((signal) => nodes.push(card(`
    <h4>${escapeHtml(signal.kind)}</h4>
    <p>${badge('proposed', 'waiting for you')}</p>
    ${definitions([
      ['Raised from', escapeHtml(signal.source_event_ref)],
      ['Concerns', escapeHtml((signal.target_refs || []).join('; '))],
      ['Valid until', escapeHtml(signal.expires_at_utc)],
    ])}`)));

  fill('approvals-body', nodes);
}

function renderCapabilities(catalog) {
  const nodes = (catalog.actions || []).map((action) => card(`
    <h4><code>${escapeHtml(action.action_id)}</code></h4>
    <p>${escapeHtml(action.description)}</p>
    ${definitions([
      ['Risk', escapeHtml(action.risk)],
      ['Reaches', (action.effects || []).length
        ? escapeHtml(action.effects.join(', '))
        : 'nothing outside Aurora — reads only'],
      ['Each call', action.approval_required
        ? 'needs your approval, scoped to that exact input'
        : 'runs without asking (low risk, no effect)'],
      // Repeated authority is the thing a person most needs to read before granting it, so the
      // window is named on the card rather than left to be discovered at the second call.
      ...(action.opens_window ? [['Approving it', 'opens a window for '
        + escapeHtml((action.opens_window.actions || []).join(', '))
        + ' — up to ' + escapeHtml(String(action.opens_window.max_actions))
        + ' calls, ending after ' + escapeHtml(String(action.opens_window.lifetime))
        + ', and sooner if you revoke, restart or change policy']] : []),
    ])}`));

  fill('capabilities-body', nodes.length ? nodes : [empty('No capability is offered.')]);
}

function renderHealth(status) {
  const resources = status.resources;
  const unmeasured = resources.unmeasured || [];

  fill('health-body', [card(`
    <h4>System</h4>
    <p>${badge(resources.status === 'NORMAL' ? 'completed' : 'proposed', resources.status)}
       operational capacity ${(resources.operational_energy * 100).toFixed(0)}%</p>
    ${definitions([
      ['CPU', resources.cpu_pct === null ? 'not measurable here' : `${(resources.cpu_pct * 100).toFixed(0)}%`],
      ['Memory', resources.memory_pct === null ? 'not measurable here' : `${(resources.memory_pct * 100).toFixed(0)}%`],
      ['Disk', resources.disk_pct === null ? 'not measurable here' : `${(resources.disk_pct * 100).toFixed(0)}%`],
      ['Not measured', unmeasured.length ? escapeHtml(unmeasured.join(', ')) : 'everything was readable'],
    ])}
    ${unmeasured.length ? '<p class="empty">A metric Aurora cannot read is reported as unknown, never as healthy.</p>' : ''}`)]);
}

function renderAudit(records) {
  const nodes = records.map((record) => card(`
    <h4><code>${escapeHtml(record.action_id)}</code> — ${escapeHtml(record.outcome)}</h4>
    ${definitions([
      ['When', escapeHtml(record.created_at_utc)],
      ['Asked by', escapeHtml(record.principal_client_id)],
      ['Decided', escapeHtml(record.decision || 'not recorded')],
      ['Under policy', escapeHtml(record.policy_ids || 'none')],
      ['Reason', escapeHtml(record.reason || '')],
      ['Sequence', String(record.sequence)],
    ])}`));

  fill('audit-body', nodes.length ? nodes : [empty('Nothing has been audited yet.')]);
}

// ---- loading ---------------------------------------------------------------------------------

async function load() {
  try {
    const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
    const [status, catalog, audit] = await Promise.all([
      api(`/v1/status?timezone=${encodeURIComponent(timezone)}`),
      api('/v1/catalog').catch(() => ({ actions: [] })),
      api('/v1/audit?limit=25'),
    ]);

    state.loadedAt = Date.now();
    state.audit = audit;
    state.auditCursor = audit.length ? audit[audit.length - 1].sequence : 0;
    setDegraded(false);

    renderWork(status);
    renderGoals(status);
    renderApprovals(status);
    renderCapabilities(catalog);
    renderHealth(status);
    renderAudit(audit);
    await searchMemory($('memory-q').value);

    paintFreshness();
    announce('Panel updated.');
  } catch (error) {
    setDegraded(true);
    announce(`Aurora is not answering: ${error.message}`);
  }
}

async function searchMemory(query) {
  try {
    renderMemories(await api(`/v1/memories?q=${encodeURIComponent(query || '')}`));
  } catch (error) {
    fill('memory-body', [card(`<p class="problem">${escapeHtml(error.message)}</p>`)]);
  }
}

// ---- actions ---------------------------------------------------------------------------------

/** Nothing is decided on a view that might already be wrong. */
function refuseIfStale() {
  if (!isStale()) {
    return false;
  }

  announce('This view is out of date. Reload before deciding anything.');
  paintFreshness();
  return true;
}

document.addEventListener('click', async (event) => {
  const correct = event.target.closest('[data-correct]');
  const forget = event.target.closest('[data-forget]');

  if (correct) {
    if (refuseIfStale()) {
      return;
    }

    const reason = prompt('What is wrong with this? Aurora records the reason with the correction.');
    if (!reason) {
      return;
    }

    try {
      await api(`/v1/memories/${encodeURIComponent(correct.dataset.correct)}`, {
        method: 'PATCH',
        headers: { 'Idempotency-Key': newKey() },
        body: JSON.stringify({ reason }),
      });
      announce('Correction recorded.');
      await load();
    } catch (error) {
      announce(`Not corrected: ${error.message}`);
    }
  }

  if (forget) {
    if (refuseIfStale()) {
      return;
    }

    const confirmed = confirm(
      'Aurora will stop using this for reasoning and it will not come back. '
      + 'The audit record of it having existed remains. Continue?');

    if (!confirmed) {
      return;
    }

    try {
      const tombstone = await api(`/v1/memories/${encodeURIComponent(forget.dataset.forget)}`, {
        method: 'DELETE',
        headers: { 'Idempotency-Key': newKey() },
      });
      announce(tombstone.scope || 'Forgotten.');
      await load();
    } catch (error) {
      announce(`Not forgotten: ${error.message}`);
    }
  }
});

// ---- routing and chrome ----------------------------------------------------------------------

function show(route) {
  state.route = route;
  document.querySelectorAll('.route').forEach((section) => {
    section.hidden = section.id !== route;
  });
  document.querySelectorAll('.tabs button').forEach((tab) => {
    tab.setAttribute('aria-selected', String(tab.dataset.route === route));
  });
  announce(`${route} section shown.`);
}

document.querySelectorAll('.tabs button').forEach((tab) => {
  tab.addEventListener('click', () => show(tab.dataset.route));

  // Arrow-key movement between tabs, so the panel is usable without a mouse.
  tab.addEventListener('keydown', (event) => {
    const tabs = [...document.querySelectorAll('.tabs button')];
    const step = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
    if (step === 0) {
      return;
    }

    event.preventDefault();
    const next = tabs[(tabs.indexOf(tab) + step + tabs.length) % tabs.length];
    next.focus();
    show(next.dataset.route);
  });
});

$('reload').addEventListener('click', load);

$('signout').addEventListener('click', async () => {
  await fetch('/ui/session/end', { method: 'POST', credentials: 'same-origin' });
  document.body.innerHTML =
    '<main><h2>Signed out</h2><p>Run <code>ui</code> on the Aurora console for a new link.</p></main>';
});

$('memory-search').addEventListener('submit', (event) => {
  event.preventDefault();
  searchMemory($('memory-q').value);
});

$('audit-more').addEventListener('click', async () => {
  try {
    const older = await api(`/v1/audit?after=${state.auditCursor}&limit=25`);
    if (older.length === 0) {
      announce('No older records.');
      return;
    }

    state.audit = state.audit.concat(older);
    state.auditCursor = older[older.length - 1].sequence;
    renderAudit(state.audit);
    announce(`${older.length} older records loaded.`);
  } catch (error) {
    announce(`Could not load older records: ${error.message}`);
  }
});

setInterval(paintFreshness, 5_000);
load();
