(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const app = $('lists-app');
  const canEdit = app.dataset.canEdit === 'true';
  let busy = false;
  const state = { definition: null, records: [], cursor: null, context: null, draft: null, preview: null, pendingList: null, pendingListContext: null };
  const token = document.querySelector('meta[name="list-csrf-token"]').content;
  const tokenHeader = document.querySelector('meta[name="list-csrf-header"]').content;
  function captureContext(workspaceId = $('workspace').value, listId = $('list').value) {
    const workspaceRoot = `/api/workspaces/${workspaceId}/lists`;
    return Object.freeze({ workspaceId, listId, workspaceRoot, listRoot: `${workspaceRoot}/${listId}` });
  }
  const own = (obj, key) => Object.prototype.hasOwnProperty.call(obj, key);
  const copy = value => structuredClone(value);
  const status = message => { $('status').textContent = message; };
  const errorText = error => error.status === 404 ? 'This list or record is unavailable to your account.'
    : error.status === 400 ? 'Check the field values. If your session expired, sign in again.'
    : error.status === 413 ? 'The list or request exceeds a configured limit.'
    : 'The request could not finish. Your draft is still open.';
  function node(tag, text) { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; return n; }
  function button(text, action) { const n = node('button', text); n.type = 'button'; n.addEventListener('click', action); return n; }
  function option(select, value, text) { const n = node('option', text); n.value = value; select.append(n); }

  // Internal markers keep numeric tokens exact through structuredClone and merge
  // snapshots. Only validated JSON number tokens can bypass normal string quoting.
  const rawNumberKey = '__cellBridgeListRawNumber';
  const numberToken = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/;
  function rawNumber(value) {
    if (!value || typeof value !== 'object' || !own(value, rawNumberKey)) return null;
    const token = value[rawNumberKey];
    if (Object.keys(value).length !== 1 || typeof token !== 'string' || token.length > 128 || token.trim() !== token || !numberToken.test(token))
      throw new Error('Invalid internal number token');
    return token;
  }
  function valueText(value) { return rawNumber(value) ?? String(value); }
  function parseJson(text) {
    const preserved = text.replace(/"(?:\\.|[^"\\])*"|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?/g, value => {
      if (value[0] === '"') return value;
      const exact = /[.eE]/.test(value) || value === '-0'
        || BigInt(value) > BigInt(Number.MAX_SAFE_INTEGER) || BigInt(value) < BigInt(Number.MIN_SAFE_INTEGER);
      if (!exact) return value;
      if (value.length > 128 || !numberToken.test(value)) throw new Error('Numeric token exceeds sample limits');
      return JSON.stringify({ [rawNumberKey]: value });
    });
    return JSON.parse(preserved);
  }
  function bodyJson(value) {
    const json = JSON.stringify(value, (key, item) => { rawNumber(item); return item; });
    // JSON.stringify escapes quotes inside user text, so text cannot match this
    // object token. The replacer above validates every matching marker first.
    return json.replace(/\{"__cellBridgeListRawNumber":"([^"\\]*)"\}/g, (match, token) => token);
  }
  async function api(path, method = 'GET', body) {
    const headers = {};
    if (method !== 'GET') { headers['Content-Type'] = 'application/json'; headers[tokenHeader] = token; }
    const response = await fetch(path, { method, headers, credentials: 'same-origin',
      body: body === undefined ? undefined : bodyJson(body) });
    if (!response.ok) { const error = new Error('List request failed'); error.status = response.status; throw error; }
    return response.status === 204 ? null : parseJson(await response.text());
  }
  const query = cursor => ({ query: { filters: [], limit: 100 }, cursor });
  const visibleFields = () => state.definition.fields.filter(f => !f.display?.hidden)
    .sort((a, b) => (a.display?.order || 0) - (b.display?.order || 0));
  const editable = f => !f.display?.readOnly && !['Integer', 'Decimal'].includes(f.type);
  function displayValue(field, values) {
    if (!own(values, field.id)) return '';
    const value = values[field.id];
    if (value === null) return '(null)';
    if (field.type === 'Choice') return field.choices?.find(c => c.id === value)?.label || valueText(value);
    return valueText(value);
  }
  function lockNavigation(locked) {
    locked ||= busy || Boolean(state.draft) || Boolean(state.pendingList);
    const ready = Boolean(state.context && state.definition);
    // run() locks before taking a save snapshot. The fieldset preserves each
    // control's own read-only/unconfirmed-create state when it unlocks.
    $('record-fields').disabled = busy || Boolean(state.preview);
    for (const select of $('merge-fields').querySelectorAll('select')) select.disabled = busy;
    $('cancel-record').disabled = busy;
    $('back-to-draft').disabled = busy;
    $('workspace').disabled = locked || !$('workspace').options.length;
    $('list').disabled = locked || !$('list').options.length;
    $('refresh').disabled = busy || !ready;
    if ($('new-record')) $('new-record').disabled = locked || !ready;
    if ($('add-field')) $('add-field').querySelector('button').disabled = locked || !ready;
    if ($('create-list')) {
      const retryCreate = Boolean(state.pendingList) && !busy && !state.draft;
      $('create-list').querySelector('button').disabled = !retryCreate && (locked || !ready);
    }
  }
  function clearList() {
    state.context = null; state.definition = null; state.records = []; state.cursor = null;
    $('list-content').hidden = true;
    $('records').querySelector('thead').replaceChildren();
    $('records').querySelector('tbody').replaceChildren();
    $('more').hidden = true;
    lockNavigation(false);
  }
  function renderRecords() {
    const context = state.context;
    const head = node('tr'); head.append(node('th', 'Number'));
    for (const field of visibleFields()) head.append(node('th', field.name));
    head.append(node('th', canEdit ? 'Edit' : 'View'));
    $('records').querySelector('thead').replaceChildren(head);
    const rows = state.records.map(record => {
      const row = node('tr'); row.append(node('td', `${state.definition.numberPrefix || ''}${valueText(record.number)}`));
      for (const field of visibleFields()) row.append(node('td', displayValue(field, record.values)));
      const actions = node('td'); actions.append(button(canEdit ? 'Edit' : 'View', () => run(() => openRecord(record.id, context)))); row.append(actions);
      return row;
    });
    $('records').querySelector('tbody').replaceChildren(...rows);
    $('more').hidden = !state.cursor;
  }
  function renderDefinition() {
    const context = state.context;
    $('list-content').hidden = false;
    $('list-name').textContent = state.definition.name;
    $('schema-info').textContent = `Schema version ${valueText(state.definition.schemaVersion)}`;
    $('csv').href = `${context.listRoot}/export.csv`;
    $('xlsx').href = `${context.listRoot}/export.xlsx`;
    $('feed').href = `${context.listRoot}/odata`;
  }
  async function loadRecords(more = false, context = state.context) {
    if (!context) { status('Choose an available customer list first.'); return; }
    const result = await api(`${context.listRoot}/records/query`, 'POST', query(more ? state.cursor : null));
    state.records = more ? state.records.concat(result.records) : result.records;
    state.cursor = result.nextCursor;
    renderRecords();
    status(`${state.records.length} records loaded${state.cursor ? '. More records are available.' : '.'}`);
  }
  async function loadList(context = captureContext()) {
    closeDraft();
    clearList();
    state.definition = await api(context.listRoot);
    state.context = context;
    renderDefinition();
    await loadRecords(false, context);
  }
  async function loadWorkspace(selectId, context = captureContext()) {
    clearList();
    $('list').replaceChildren();
    const lists = await api(context.workspaceRoot);
    $('workspace').value = context.workspaceId;
    $('list').replaceChildren();
    for (const list of lists) option($('list'), list.id, list.name);
    if (!lists.length) { status('You have no visible lists in this customer workspace.'); return; }
    const chosenId = selectId && lists.some(l => l.id === selectId) ? selectId : lists[0].id;
    $('list').value = chosenId;
    await loadList(captureContext(context.workspaceId, chosenId));
  }
  async function references(field, context) {
    const target = `${context.workspaceRoot}/${field.referenceListId}`;
    const definition = await api(target);
    const labelField = definition.fields.find(f => f.id === field.referenceDisplayFieldId) || definition.fields.find(f => f.type === 'Text');
    const records = [];
    let cursor = null;
    do {
      const page = await api(`${target}/records/query`, 'POST', query(cursor));
      records.push(...page.records); cursor = page.nextCursor;
    } while (cursor);
    return records.map(r => ({ id: r.id, label: `${definition.numberPrefix || ''}${valueText(r.number)} ${labelField ? displayValue(labelField, r.values) : r.id}` }));
  }
  async function openRecord(id = null, context = state.context) {
    if (!context) { status('Choose an available customer list first.'); return; }
    if (state.draft) { status('Finish or cancel the open draft first.'); return; }
    const definition = await api(context.listRoot);
    const record = id ? await api(`${context.listRoot}/records/${id}`) : null;
    const draft = { id, context, createId: id ? null : crypto.randomUUID(), definition, original: record ? copy(record.values) : {}, revision: record?.revision,
      controls: new Map(), pendingCreate: null, changes: null };
    const fields = [];
    for (const field of definition.fields.filter(f => !f.display?.hidden).sort((a, b) => (a.display?.order || 0) - (b.display?.order || 0))) {
      const label = node('label'); label.append(node('span', field.name + (field.required ? ' *' : '')));
      let control;
      if (field.type === 'Choice' || field.type === 'Reference' || field.type === 'Boolean') {
        control = node('select'); option(control, '', field.required ? 'Choose a value' : '(unset)');
        if (field.type === 'Choice') for (const c of field.choices) option(control, c.id, c.label);
        if (field.type === 'Boolean') { option(control, 'true', 'Yes'); option(control, 'false', 'No'); }
        if (field.type === 'Reference') {
          try { for (const r of await references(field, context)) option(control, r.id, r.label); }
          catch { control.disabled = true; label.append(node('small', 'Target records are unavailable to your account.'));
            if (!record && field.required) throw new Error('A required reference cannot be selected.'); }
          const original = draft.original[field.id];
          if (original && !Array.from(control.options).some(o => o.value === original)) option(control, original, 'Existing reference');
        }
      } else {
        control = node('input'); control.type = field.type === 'Date' ? 'date' : 'text';
        if (field.type === 'Text') control.maxLength = field.maxLength;
        if (field.type === 'Timestamp') control.placeholder = '2026-10-13T12:00:00Z';
      }
      control.id = `field-${field.id}`; control.required = field.required;
      control.value = draft.original[field.id] === undefined || draft.original[field.id] === null ? '' : valueText(draft.original[field.id]);
      control.disabled ||= !canEdit || !editable(field);
      label.append(control);
      if (field.display?.helpText) label.append(node('small', field.display.helpText));
      if (['Integer', 'Decimal'].includes(field.type)) label.append(node('small', 'Use the API to edit numeric fields without losing precision.'));
      fields.push(label); draft.controls.set(field.id, { field, control, initial: control.value });
    }
    state.definition = definition; state.context = context; state.draft = draft;
    $('record-fields').replaceChildren(...fields);
    $('editor-title').textContent = record ? `${canEdit ? 'Edit' : 'View'} record ${definition.numberPrefix || ''}${valueText(record.number)}` : 'New record';
    $('draft-note').textContent = record ? `Loaded revision ${valueText(record.revision)}. Saves patch only the fields you change.` : 'A create retry reuses the same record ID and values.';
    $('save-record').hidden = !canEdit;
    $('save-record').textContent = 'Save record'; $('editor').hidden = false;
    lockNavigation(true); $('editor').scrollIntoView({ block: 'start' });
  }
  function changesFromForm(draft) {
    const changes = [];
    for (const [id, { field, control, initial }] of draft.controls) {
      if (control.disabled || control.value === initial) continue;
      const change = { fieldId: id };
      if (control.value === '' && !field.required) change.remove = true;
      else change.value = field.type === 'Boolean' ? control.value === 'true' : control.value;
      changes.push(change);
    }
    return changes;
  }
  function closeDraft() {
    state.draft = null; state.preview = null;
    $('record-fields').disabled = false; $('save-record').disabled = false;
    $('editor').hidden = true; $('merge').hidden = true;
    lockNavigation(false);
  }
  async function saveRecord() {
    const draft = state.draft;
    if (!draft) return;
    if (!draft.id) {
      if (!draft.pendingCreate) {
        const values = {};
        for (const [id, { field, control }] of draft.controls) {
          if (!control.disabled && (control.value !== '' || field.required)) values[id] = field.type === 'Boolean' ? control.value === 'true' : control.value;
        }
        const currentDefinition = await api(draft.context.listRoot);
        draft.pendingCreate = { id: draft.createId, schemaVersion: currentDefinition.schemaVersion, values };
      }
      for (const { control } of draft.controls.values()) control.disabled = true;
      try {
        const result = await api(`${draft.context.listRoot}/records`, 'POST', draft.pendingCreate);
        closeDraft(); await loadRecords(false, draft.context); status(`Record ${valueText(result.number)} ${result.replayed ? 'confirmed by retry' : 'created'}.`);
      } catch (error) {
        if ([400, 404, 409, 413].includes(error.status)) {
          draft.pendingCreate = null;
          for (const { field, control } of draft.controls.values()) control.disabled = !editable(field);
          $('save-record').textContent = 'Save record';
        } else {
          $('save-record').textContent = 'Retry same create';
          $('draft-note').textContent = 'The response was not confirmed. Retry sends exactly the same record ID and values. Cancel may leave an already created record; refresh before creating another.';
        }
        throw error;
      }
      return;
    }
    draft.changes = changesFromForm(draft);
    if (!draft.changes.length) { status('No fields changed.'); return; }
    const definition = await api(draft.context.listRoot); state.definition = definition;
    try {
      await api(`${draft.context.listRoot}/records/${draft.id}`, 'PATCH', { expectedRevision: draft.revision,
        schemaVersion: definition.schemaVersion, changes: draft.changes });
      closeDraft(); await loadRecords(false, draft.context); status('Record saved.');
    } catch (error) {
      if (error.status !== 409) throw error;
      await previewMerge(draft);
    }
  }
  function mergeValue(present, value) { return present ? value === null ? '(null)' : valueText(value) : '(absent)'; }
  async function previewMerge(draft = state.draft) {
    const definition = await api(draft.context.listRoot); state.definition = definition;
    const preview = await api(`${draft.context.listRoot}/records/${draft.id}/merge-preview`, 'POST', {
      schemaVersion: definition.schemaVersion, originalValues: draft.original, changes: draft.changes });
    state.preview = preview;
    const rows = preview.fields.map(field => {
      const row = node('tr');
      row.append(node('td', definition.fields.find(f => f.id === field.fieldId)?.name || field.fieldId),
        node('td', mergeValue(field.originalPresent, field.originalValue)),
        node('td', mergeValue(field.currentPresent, field.currentValue)),
        node('td', mergeValue(field.proposedPresent, field.proposedValue)));
      const resolution = node('td');
      if (field.conflict) {
        const select = node('select'); select.dataset.fieldId = field.fieldId;
        select.setAttribute('aria-label', 'Conflict resolution');
        option(select, '', 'Choose a value'); option(select, 'mine', 'Use mine'); option(select, 'current', 'Keep current');
        resolution.append(select);
      } else resolution.textContent = draft.changes.some(c => c.fieldId === field.fieldId) ? 'No conflict' : 'Keep their change';
      row.append(resolution); return row;
    });
    $('merge-fields').replaceChildren(...rows); $('merge').hidden = false;
    $('record-fields').disabled = true; $('save-record').disabled = true;
    status('Another save changed this record. Review the differences below.'); $('merge').scrollIntoView({ block: 'start' });
  }
  async function saveMerge() {
    const preview = state.preview, draft = state.draft;
    const changes = copy(preview.suggestedChanges);
    for (const select of $('merge-fields').querySelectorAll('select')) {
      if (!select.value) { status('Choose a resolution for every conflict.'); select.focus(); return; }
      if (select.value === 'mine') {
        const field = preview.fields.find(f => f.fieldId === select.dataset.fieldId);
        changes.push(field.proposedPresent ? { fieldId: field.fieldId, value: field.proposedValue } : { fieldId: field.fieldId, remove: true });
      }
    }
    if (!changes.length) { closeDraft(); await loadRecords(false, draft.context); status('Kept the current record.'); return; }
    try {
      const definition = await api(draft.context.listRoot);
      await api(`${draft.context.listRoot}/records/${draft.id}`, 'PATCH', { expectedRevision: preview.currentRevision,
        schemaVersion: definition.schemaVersion, changes });
      closeDraft(); await loadRecords(false, draft.context); status('Resolved changes saved.');
    } catch (error) {
      if (error.status !== 409) throw error;
      await previewMerge(draft);
    }
  }
  async function addField(form) {
    if (state.draft || !state.context) return;
    const context = state.context;
    const fieldName = form.elements.name.value;
    const definition = await api(context.listRoot);
    const field = { id: crypto.randomUUID(), name: fieldName, type: 'Text', required: false, maxLength: 1024 };
    try {
      state.definition = await api(`${context.listRoot}/schema`, 'PUT', { expectedVersion: definition.schemaVersion,
        name: definition.name, fields: [...definition.fields, field] });
      form.reset(); renderDefinition(); renderRecords(); status('Optional text field added. Existing records remain unchanged.');
    } catch (error) {
      if (error.status === 409) { status('The schema changed. Your field name is still in the form; retry to add it to the current schema.'); return; }
      throw error;
    }
  }
  async function createList(form) {
    if (state.draft || (!state.context && !state.pendingList)) return;
    if (!state.pendingList) {
      state.pendingListContext = captureContext();
      state.pendingList = { id: crypto.randomUUID(), name: form.elements.name.value,
        fields: [{ id: crypto.randomUUID(), name: 'Title', type: 'Text', required: true, maxLength: 1024 }], numberPrefix: 'J-' };
    }
    const pending = state.pendingList, context = state.pendingListContext;
    form.elements.name.disabled = true;
    try {
      const definition = await api(context.workspaceRoot, 'POST', pending);
      state.pendingList = null; state.pendingListContext = null; form.elements.name.disabled = false; form.reset();
      await loadWorkspace(definition.id, context); status('Custom list created.');
    } catch (error) {
      // List creation has no retry receipt. Check the retained ID before another POST.
      try {
        const definition = await api(`${context.workspaceRoot}/${pending.id}`);
        if (definition.name === pending.name) {
          state.pendingList = null; state.pendingListContext = null; form.elements.name.disabled = false;
          await loadWorkspace(definition.id, context); status('Custom list creation confirmed.'); return;
        }
      } catch { /* Keep the pending identity after an uncertain response. */ }
      if ([400, 404, 413].includes(error.status)) {
        state.pendingList = null; state.pendingListContext = null; form.elements.name.disabled = false;
      }
      throw error;
    }
  }
  async function run(action, control) {
    if (busy) return;
    busy = true; lockNavigation(true);
    if (control) control.disabled = true;
    try { await action(); }
    catch (error) { status(errorText(error)); }
    finally {
      busy = false;
      if (control) control.disabled = control === $('save-record') && Boolean(state.preview);
      lockNavigation(Boolean(state.draft));
    }
  }
  $('workspace').addEventListener('change', () => run(() => loadWorkspace()));
  $('list').addEventListener('change', () => run(() => loadList()));
  $('refresh').addEventListener('click', event => run(() => loadRecords(), event.currentTarget));
  $('more').addEventListener('click', event => run(() => loadRecords(true), event.currentTarget));
  $('new-record')?.addEventListener('click', () => run(() => openRecord()));
  $('record-form').addEventListener('submit', event => { event.preventDefault(); run(saveRecord, $('save-record')); });
  $('cancel-record').addEventListener('click', () => { if (busy) return; closeDraft(); $('record-fields').disabled = false; $('save-record').disabled = false; status('Draft closed. Refresh records to check for any unconfirmed save.'); });
  $('save-merge').addEventListener('click', event => run(saveMerge, event.currentTarget));
  $('back-to-draft').addEventListener('click', () => { if (busy) return; $('merge').hidden = true; $('record-fields').disabled = false; $('save-record').disabled = false; state.preview = null; });
  $('add-field')?.addEventListener('submit', event => { event.preventDefault(); run(() => addField(event.currentTarget), event.currentTarget.querySelector('button')); });
  $('create-list')?.addEventListener('submit', event => { event.preventDefault(); run(() => createList(event.currentTarget), event.currentTarget.querySelector('button')); });
  lockNavigation(false);
  if ($('workspace').value) run(() => loadWorkspace());
})();
