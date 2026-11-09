const $ = id => document.getElementById(id);
const bridge = window.chrome?.webview;
const editor = $('editor');
const groupEditor = $('groupEditor');
const expandedGroups = new Set();
let selectedGroup;
const states = { active: 'Atividade', video: 'Reprodução', idle: 'Inatividade' };
const time = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit' });
const date = new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: 'short' });
const dateWithYear = new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: 'short', year: 'numeric' });
let period = 'day', view = 'overview', limit = 8, snapshot, selected, pending, settingsDirty;
let customRange = {}, retroPreview, previewDraft;
const filterIds = ['filterApp', 'filterCategory', 'filterProject'];
function rangeKey() { return period === 'custom' ? customRange.start + '/' + customRange.end : period; }
function send(action, fields = {}) { bridge?.postMessage({ action, period, rangeKey: rangeKey(), ...customRange, ...fields }); }
function make(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}
function duration(ms) {
  const s = Math.floor(ms / 1000), h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60;
  if (h) return h + ' h ' + m + ' min';
  return m ? m + ' min ' + (s % 60) + ' s' : s + ' s';
}
const defaultIcon = 'app-placeholder.svg';
function appIcon(app) {
  return snapshot.icons && Object.hasOwn(snapshot.icons, app) ? snapshot.icons[app] : null;
}
function setAppIcon(image, url) {
  image.onerror = () => { image.onerror = null; image.src = defaultIcon; };
  image.src = url || defaultIcon;
}
function listRow(label, tail, action, app) {
  const row = make(action ? 'button' : 'div', undefined, 'row');
  if (action) { row.type = 'button'; row.onclick = action; }
  const title = make('span', undefined, 'row-label');
  if (app) {
    const icon = make('img', undefined, 'app-icon');
    icon.alt = '';
    setAppIcon(icon, appIcon(app));
    title.append(icon);
  }
  title.append(make('span', label));
  row.append(title, make('strong', tail));
  return row;
}
function group(rows, key, fallback) {
  const totals = new Map();
  for (const row of rows) {
    const label = row[key] || fallback;
    totals.set(label, (totals.get(label) ?? 0) + row.end - row.start);
  }
  return [...totals].sort((a, b) => b[1] - a[1]);
}
function groupAppTotals(rows, groups) {
  const membership = new Map(groups.flatMap(group => group.apps.map(app => [app, group])));
  const totals = new Map();
  for (const [app, amount] of group(rows, 'app', '')) {
    const appGroup = membership.get(app);
    const key = appGroup ? 'group:' + appGroup.id : 'app:' + app;
    if (!totals.has(key)) totals.set(key, { app, appGroup, amount: 0, members: [] });
    const entry = totals.get(key);
    entry.amount += amount;
    entry.members.push([app, amount]);
  }
  return [...totals.values()].sort((a, b) => b.amount - a.amount);
}
function appTotalRow({ app, appGroup, amount, members }) {
  if (!appGroup) return listRow(app, duration(amount), () => openEditor({ app }, 'app'), app);
  const block = make('div', undefined, 'app-group');
  const children = make('div', undefined, 'app-group-members');
  const iconApp = members.find(([name]) => appIcon(name))?.[0] || app;
  const button = listRow(appGroup.name, duration(amount), () => {
    if (expandedGroups.has(appGroup.id)) expandedGroups.delete(appGroup.id);
    else expandedGroups.add(appGroup.id);
    syncExpanded();
  }, iconApp);
  function syncExpanded() {
    const expanded = expandedGroups.has(appGroup.id);
    children.hidden = !expanded;
    button.setAttribute('aria-expanded', String(expanded));
    marker.textContent = expanded ? '▾' : '▸';
  }
  const marker = make('span', undefined, 'group-chevron');
  marker.setAttribute('aria-hidden', 'true');
  button.querySelector('.row-label').append(marker);
  children.append(...members.map(([name, total]) => listRow(name, duration(total), () => openEditor({ app: name }, 'app'), name)));
  block.append(button, children);
  syncExpanded();
  return block;
}
function groupSessions(rows) {
  const sessions = [];
  for (const row of rows) {
    const previous = sessions.at(-1);
    if (previous && previous.end === row.start && previous.app === row.app &&
        previous.category === row.category && previous.project === row.project && previous.state === row.state) {
      previous.end = row.end;
      if (row.id) previous.ids.push(row.id);
      else previous.live = true;
    } else {
      sessions.push({ ...row, ids: row.id ? [row.id] : [], live: !row.id });
    }
  }
  return sessions;
}
function fill(id, children) {
  $(id).replaceChildren(...(children.length ? children : [make('p', 'Ainda sem registros neste período.', 'muted')]));
}
function filterOptions(id, all, options) {
  const select = $(id), previous = select.value;
  if (document.activeElement === select) return;
  select.replaceChildren(new Option(all, ''), ...options.map(([value, label]) => new Option(label, value)));
  select.value = options.some(([value]) => value === previous) ? previous : '';
}
function filterTimeline(rows, groups) {
  filterOptions('filterApp', 'Todos', [
    ...groups.map(group => ['group:' + group.id, group.name + ' (grupo)']),
    ...group(rows.filter(row => row.app), 'app', '').map(([app]) => ['app:' + app, app])
  ]);
  const active = rows.filter(row => row.state !== 'idle');
  filterOptions('filterCategory', 'Todas', group(active, 'category', 'Sem categoria').map(([name]) => ['v:' + name, name]));
  filterOptions('filterProject', 'Todos', group(active, 'project', 'Sem projeto').map(([name]) => ['v:' + name, name]));
  const [app, category, project] = filterIds.map(id => $(id).value);
  const members = groups.find(group => 'group:' + group.id === app)?.apps;
  $('clearFilters').disabled = !(app || category || project);
  return rows.filter(row =>
    (!app || (members ? members.includes(row.app) : app === 'app:' + row.app)) &&
    (!category || (row.state !== 'idle' && category === 'v:' + (row.category || 'Sem categoria'))) &&
    (!project || (row.state !== 'idle' && project === 'v:' + (row.project || 'Sem projeto'))));
}
function applyTimelineFilter(id, value) {
  $(id).value = value;
  limit = 8;
  render(true);
  $('timelineHeading').scrollIntoView({ block: 'start' });
}
function render(force = false) {
  if (!snapshot) return;
  const { paused, seconds, rules, current, update, groups, startup } = snapshot;
  $('startWithWindows').disabled = !startup.available;
  $('startWithWindows').checked = startup.enabled;
  $('installedVersion').textContent = update.version;
  $('checkUpdate').hidden = !update.enabled || Boolean(update.available);
  $('checkUpdate').disabled = update.checking;
  $('installUpdate').hidden = !update.available;
  const updateStatus = update.error || (update.available ? 'Versão ' + update.available + ' pronta para instalar.'
    : update.checking ? 'Verificando e baixando atualizações…'
    : !update.enabled ? 'As atualizações automáticas estão disponíveis na versão instalada pelo assistente.'
    : update.checkedUpdate ? 'Nenhuma atualização pronta para instalar.'
    : 'O Pulso verifica atualizações automaticamente.');
  if ($('updateStatus').textContent !== updateStatus) $('updateStatus').textContent = updateStatus;
  $('newRule').disabled = false;
  $('newGroup').disabled = false;
  $('toggle').disabled = false;
  $('toggle').textContent = paused ? 'Retomar coleta' : 'Pausar coleta';
  $('status').replaceChildren(make('span', '', paused ? 'dot off' : 'dot'), paused ? 'Coleta pausada' : 'Coleta ativa');
  if (!settingsDirty && document.activeElement !== $('seconds')) $('seconds').value = seconds;
  if (!force && (editor.open || groupEditor.open || document.activeElement?.closest('.row, #more'))) return;

  const rows = current ? [...snapshot.rows, current] : snapshot.rows;
  const sessions = groupSessions(filterTimeline(rows, groups));
  const active = rows.filter(row => row.state !== 'idle');
  const totals = { active: 0, video: 0, idle: 0 };
  for (const row of rows) totals[row.state] += row.end - row.start;
  $('comparison').textContent = 'Tempo em uso (atividade + reprodução): ' + duration(totals.active + totals.video) +
    ' · período anterior equivalente: ' + duration(snapshot.previousActive);
  $('metrics').replaceChildren(...Object.entries(totals).map(([key, amount]) => {
    const card = make('article', undefined, 'card metric');
    card.append(make('span', states[key], 'muted'), make('strong', duration(amount)));
    return card;
  }));

  const start = snapshot.from, end = snapshot.until;
  const lastDay = Math.max(start, end - 1);
  const formatter = period === 'custom' ? dateWithYear : date;
  const firstDate = formatter.format(start), lastDate = formatter.format(lastDay);
  $('date').textContent = firstDate === lastDate ? firstDate : firstDate + ' — ' + lastDate;
  const width = Math.max(1, end - start);
  const bar = document.createDocumentFragment();
  for (const row of sessions) {
    const mark = make('span', undefined, row.state);
    mark.style.left = Math.max(0, (row.start - +start) / width * 100) + '%';
    mark.style.width = (row.end - row.start) / width * 100 + '%';
    bar.append(mark);
  }
  $('timeline').replaceChildren(bar);
  fill('intervals', sessions.slice(-limit).reverse().map(row => {
    const label = [date.format(row.start), time.format(row.start), states[row.state], row.app, row.project].filter(Boolean).join(' · ');
    return listRow(label, duration(row.end - row.start), !row.live && row.app ? () => openEditor(row, 'interval') : undefined, row.app);
  }));
  $('more').hidden = sessions.length <= limit;
  fill('categories', group(active, 'category', 'Sem categoria').map(([label, amount]) =>
    listRow(label, duration(amount), () => applyTimelineFilter('filterCategory', 'v:' + label))));
  fill('projects', group(active, 'project', 'Sem projeto').map(([label, amount]) =>
    listRow(label, duration(amount), () => applyTimelineFilter('filterProject', 'v:' + label))));
  fill('apps', groupAppTotals(active, groups).map(appTotalRow));
  fill('groupList', groups.map(group => listRow(group.name, group.apps.length + ' app(s)', () => openGroup(group),
    group.apps.find(app => appIcon(app)) || group.apps[0])));
  fill('ruleList', rules.map(rule => listRow(
    [rule.app, rule.contains, rule.category, rule.project].filter(Boolean).join(' · '), 'Editar', () => openEditor(rule, 'rule'), rule.app)));
}
function openGroup(group = { id: 0, name: '', apps: [] }) {
  selectedGroup = group;
  $('groupName').value = group.name;
  $('groupMembers').replaceChildren();
  $('groupAppsStatus').textContent = 'Buscando aplicativos…';
  $('groupError').hidden = true;
  $('deleteGroup').hidden = !group.id;
  $('saveGroup').disabled = true;
  groupEditor.showModal();
  send('groupApps');
}
function showGroupApps(apps) {
  const otherGroups = new Map(snapshot.groups.filter(group => group.id !== selectedGroup.id)
    .flatMap(group => group.apps.map(app => [app, group.name])));
  $('groupMembers').replaceChildren(...apps.map(({ app, icon }) => {
    const label = make('label', undefined, 'group-choice');
    const checkbox = make('input');
    checkbox.type = 'checkbox';
    checkbox.value = app;
    checkbox.checked = selectedGroup.apps.includes(app);
    checkbox.disabled = otherGroups.has(app);
    const image = make('img', undefined, 'app-icon');
    image.alt = '';
    setAppIcon(image, icon);
    const title = otherGroups.has(app) ? app + ' · ' + otherGroups.get(app) : app;
    label.append(checkbox, image, make('span', title));
    return label;
  }));
  $('saveGroup').disabled = !apps.length;
  $('groupAppsStatus').textContent = apps.length ? '' : 'Ainda não há aplicativos detectados.';
}
$('newGroup').onclick = () => openGroup();
$('closeGroup').onclick = () => groupEditor.close();
$('groupForm').onsubmit = event => {
  event.preventDefault();
  const apps = [...$('groupMembers').querySelectorAll('input:checked')].map(item => item.value);
  edit('saveGroup', { id: selectedGroup.id, name: $('groupName').value, apps });
};
$('deleteGroup').onclick = () => edit('deleteGroup', { id: selectedGroup.id });
function openEditor(row, type) {
  if (!snapshot) return;
  selected = { row, type };
  clearRetro();
  $('editorTitle').textContent = type === 'interval' ? 'Classificar sessão' : 'Regra de classificação';
  $('mode').value = type === 'interval' ? 'correct' : 'saveRule';
  $('mode').options[0].disabled = type !== 'interval';
  $('app').value = row.app ?? '';
  $('openApps').replaceChildren(new Option('Selecione um aplicativo', ''));
  updateSelectedAppIcon();
  $('openAppsStatus').textContent = '';
  $('fragment').value = row.contains ?? '';
  $('category').value = row.category ?? '';
  $('project').value = row.project ?? '';
  $('deleteRule').hidden = type !== 'rule';
  $('editError').hidden = true;
  const categories = new Set(['Trabalho', 'Jogos', 'Lazer']), projects = new Set();
  for (const item of [...snapshot.rules, ...snapshot.rows]) {
    if (item.category) categories.add(item.category);
    if (item.project) projects.add(item.project);
  }
  $('categoryNames').replaceChildren(...[...categories].map(name => new Option(name)));
  $('projectNames').replaceChildren(...[...projects].map(name => new Option(name)));
  editor.showModal();
  updateMode();
}
function updateMode() {
  $('ruleFields').hidden = $('mode').value === 'correct';
  $('app').required = !$('ruleFields').hidden;
  $('retroControls').hidden = $('ruleFields').hidden;
  clearRetro();
  if (!$('ruleFields').hidden) refreshApps();
}
function refreshApps() {
  $('openAppsStatus').textContent = 'Buscando aplicativos abertos…';
  send('openApps');
}
$('refreshApps').onclick = refreshApps;
$('openApps').onchange = () => {
  if ($('openApps').value) $('app').value = $('openApps').value;
  updateSelectedAppIcon();
};
$('app').oninput = () => {
  $('openApps').value = $('app').value;
  updateSelectedAppIcon();
};
function updateSelectedAppIcon() {
  const icon = $('openApps').selectedOptions[0]?.dataset.icon;
  setAppIcon($('selectedAppIcon'), icon || appIcon($('app').value));
}
function edit(action, payload) {
  if (pending) return;
  pending = action;
  $('editError').hidden = true;
  $('groupError').hidden = true;
  send(action, payload);
}
$('mode').onchange = updateMode;
$('closeEditor').onclick = () => editor.close();
$('editForm').onsubmit = event => {
  event.preventDefault();
  const action = $('mode').value;
  edit(action, ruleFields());
};
function ruleFields() {
  const { row, type } = selected;
  const action = $('mode').value;
  return {
    id: type === 'rule' ? row.id : 0, ids: action === 'correct' ? row.ids : [],
    app: $('app').value, contains: $('fragment').value,
    category: $('category').value, project: $('project').value
  };
}
function clearRetro() {
  retroPreview = null;
  previewDraft = null;
  $('retroSummary').hidden = true;
  $('applyRetro').hidden = true;
}
$('editForm').addEventListener('input', clearRetro);
$('previewRule').onclick = () => {
  if (pending || !$('editForm').reportValidity()) return;
  clearRetro();
  const fields = ruleFields();
  previewDraft = JSON.stringify(fields);
  edit('previewRule', fields);
};
$('applyRetro').onclick = () => {
  if (retroPreview && previewDraft === JSON.stringify(ruleFields())) {
    edit('saveRule', { ...ruleFields(), throughId: retroPreview.throughId });
  }
};
$('deleteRule').onclick = () => edit('deleteRule', { id: selected.row.id });
$('newRule').onclick = () => openEditor({}, 'new');
$('toggle').onclick = () => send('toggle');
$('startWithWindows').onchange = () => send('startup', { enabled: $('startWithWindows').checked });
$('checkUpdate').onclick = () => send('checkUpdate');
$('installUpdate').onclick = () => send('installUpdate');
$('more').onclick = () => { limit += 20; render(true); };
$('seconds').oninput = () => { settingsDirty = true; };
$('settingsForm').onsubmit = event => {
  event.preventDefault();
  settingsDirty = false;
  send('settings', { seconds: Number($('seconds').value) });
};
for (const button of document.querySelectorAll('[data-view]')) {
  button.onclick = () => {
    $(view).hidden = true;
    view = button.dataset.view;
    $(view).hidden = false;
    for (const item of document.querySelectorAll('[data-view]')) item.removeAttribute('aria-current');
    button.setAttribute('aria-current', 'page');
    render(true);
  };
}
function setPeriod(value) {
  period = value;
  limit = 8;
  for (const item of document.querySelectorAll('[data-period]')) {
    item.setAttribute('aria-pressed', String(item.dataset.period === value));
  }
  send('refresh');
}
for (const id of filterIds) $(id).onchange = () => { limit = 8; render(true); };
$('clearFilters').onclick = () => {
  for (const id of filterIds) $(id).value = '';
  limit = 8;
  render(true);
};
for (const button of document.querySelectorAll('[data-period]')) {
  button.onclick = () => {
    const custom = button.dataset.period === 'custom';
    $('customPeriod').hidden = custom ? !$('customPeriod').hidden : true;
    document.querySelector('[data-period="custom"]').setAttribute('aria-expanded', String(!$('customPeriod').hidden));
    if (!custom) setPeriod(button.dataset.period);
    else if (!$('customPeriod').hidden) {
      const now = new Date();
      const today = new Date(now - now.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
      for (const id of ['periodStart', 'periodEnd']) {
        $(id).max = today;
        if (!$(id).value) $(id).value = today;
      }
    }
  };
}
$('customPeriod').onsubmit = event => {
  event.preventDefault();
  const start = $('periodStart').value, end = $('periodEnd').value;
  if (start > end) {
    $('error').textContent = 'A data inicial precisa ser anterior ou igual à final.';
    $('error').hidden = false;
    return;
  }
  customRange = { start, end };
  setPeriod('custom');
};
bridge?.addEventListener('message', ({ data }) => {
  if (data.action === 'groupApps') {
    if (!groupEditor.open) return;
    if (data.error) $('groupAppsStatus').textContent = data.error;
    else showGroupApps(data.apps);
    return;
  }
  if (data.action === 'openApps') {
    if (!editor.open) return;
    const apps = data.apps ?? [];
    $('openApps').replaceChildren(new Option('Selecione um aplicativo', ''),
      ...apps.map(({ app, title, icon }) => {
        const option = new Option(title + ' (' + app + ')', app);
        if (icon) option.dataset.icon = icon;
        return option;
      }));
    $('openApps').value = $('app').value;
    updateSelectedAppIcon();
    $('openAppsStatus').textContent = data.error
      ? 'Não foi possível carregar a lista. Você pode digitar o processo.'
      : apps.length ? '' : 'Nenhum aplicativo aberto encontrado. Você pode digitar o processo.';
    return;
  }
  const completed = pending && pending === data.action;
  const error = completed && groupEditor.open ? $('groupError') : completed && editor.open ? $('editError') : $('error');
  if (data.action === 'previewRule' && !data.error) {
    pending = null;
    if (!editor.open || previewDraft !== JSON.stringify(ruleFields())) return;
    retroPreview = data;
    $('retroSummary').textContent = data.count
      ? duration(data.milliseconds) + ' de tempo registrado será reclassificado. Revise antes de aplicar.'
      : 'Nenhum registro anterior precisa mudar.';
    $('retroSummary').hidden = false;
    $('applyRetro').hidden = !data.count;
    return;
  }
  if (data.error) {
    error.hidden = false;
    error.textContent = data.detail || data.error;
  } else if (data.rangeKey === rangeKey()) {
    error.hidden = true;
    snapshot = data;
    if (completed && editor.open) editor.close();
    if (completed && groupEditor.open) groupEditor.close();
    render(Boolean(completed));
  }
  if (completed) pending = null;
});
if (bridge) {
  send('refresh');
  setInterval(() => { if (!pending && !document.hidden) send('refresh'); }, 2500);
} else {
  $('status').textContent = 'Abra este painel pelo aplicativo Pulso';
}
