const $ = id => document.getElementById(id);
const bridge = window.chrome?.webview;
const editor = $('editor');
const states = { active: 'Atividade', video: 'Reprodução', idle: 'Inatividade' };
const time = new Intl.DateTimeFormat('pt-BR', { hour: '2-digit', minute: '2-digit' });
const date = new Intl.DateTimeFormat('pt-BR', { day: '2-digit', month: 'short' });
let period = 'day', view = 'overview', limit = 8, snapshot, selected, pending, settingsDirty;
function send(action, fields = {}) { bridge?.postMessage({ action, period, ...fields }); }
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
function listRow(label, tail, action) {
  const row = make(action ? 'button' : 'div', undefined, 'row');
  if (action) { row.type = 'button'; row.onclick = action; }
  row.append(make('span', label), make('strong', tail));
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
function fill(id, children) {
  $(id).replaceChildren(...(children.length ? children : [make('p', 'Ainda sem registros neste período.', 'muted')]));
}
function render(force = false) {
  if (!snapshot) return;
  const { paused, seconds, rules, current } = snapshot;
  $('newRule').disabled = false;
  $('toggle').disabled = false;
  $('toggle').textContent = paused ? 'Retomar coleta' : 'Pausar coleta';
  $('status').replaceChildren(make('span', '', paused ? 'dot off' : 'dot'), paused ? 'Coleta pausada' : 'Coleta ativa');
  if (!settingsDirty && document.activeElement !== $('seconds')) $('seconds').value = seconds;
  if (!force && (editor.open || document.activeElement?.closest('.row, #more'))) return;

  const rows = current ? [...snapshot.rows, current] : snapshot.rows;
  const active = rows.filter(row => row.state !== 'idle');
  const totals = { active: 0, video: 0, idle: 0 };
  for (const row of rows) totals[row.state] += row.end - row.start;
  $('metrics').replaceChildren(...Object.entries(totals).map(([key, amount]) => {
    const card = make('article', undefined, 'card metric');
    card.append(make('span', states[key], 'muted'), make('strong', duration(amount)));
    return card;
  }));

  const end = new Date(), start = new Date(end);
  start.setHours(0, 0, 0, 0);
  if (period === 'week') start.setDate(start.getDate() - (start.getDay() + 6) % 7);
  $('date').textContent = period === 'week' ? date.format(start) + ' — ' + date.format(end) : date.format(end);
  const width = Math.max(1, end - start);
  const bar = document.createDocumentFragment();
  for (const row of rows) {
    const mark = make('span', undefined, row.state);
    mark.style.left = Math.max(0, (row.start - +start) / width * 100) + '%';
    mark.style.width = (row.end - row.start) / width * 100 + '%';
    bar.append(mark);
  }
  $('timeline').replaceChildren(bar);
  fill('intervals', rows.slice(-limit).reverse().map(row => {
    const label = [date.format(row.start), time.format(row.start), states[row.state], row.app, row.project].filter(Boolean).join(' · ');
    return listRow(label, duration(row.end - row.start), row.id && row.app ? () => openEditor(row, 'interval') : undefined);
  }));
  $('more').hidden = rows.length <= limit;
  fill('categories', group(active, 'category', 'Sem categoria').map(([label, amount]) => listRow(label, duration(amount))));
  fill('projects', group(active, 'project', 'Sem projeto').map(([label, amount]) => listRow(label, duration(amount))));
  fill('apps', group(active, 'app', '').map(([app, amount]) => listRow(app, duration(amount), () => openEditor({ app }, 'app'))));
  fill('ruleList', rules.map(rule => listRow(
    [rule.app, rule.contains, rule.category, rule.project].filter(Boolean).join(' · '), 'Editar', () => openEditor(rule, 'rule'))));
}
function openEditor(row, type) {
  if (!snapshot) return;
  selected = { row, type };
  $('editorTitle').textContent = type === 'interval' ? 'Classificar intervalo' : 'Regra de classificação';
  $('mode').value = type === 'interval' ? 'correct' : 'saveRule';
  $('mode').options[0].disabled = type !== 'interval';
  $('app').value = row.app ?? '';
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
  updateMode();
  editor.showModal();
}
function updateMode() {
  $('ruleFields').hidden = $('mode').value === 'correct';
  $('app').required = !$('ruleFields').hidden;
}
function edit(action, payload) {
  if (pending) return;
  pending = action;
  $('editError').hidden = true;
  send(action, payload);
}
$('mode').onchange = updateMode;
$('closeEditor').onclick = () => editor.close();
$('editForm').onsubmit = event => {
  event.preventDefault();
  const { row, type } = selected;
  const action = $('mode').value;
  edit(action, {
    id: action === 'correct' || type === 'rule' ? row.id : 0,
    app: $('app').value, contains: $('fragment').value,
    category: $('category').value, project: $('project').value
  });
};
$('deleteRule').onclick = () => edit('deleteRule', { id: selected.row.id });
$('newRule').onclick = () => openEditor({}, 'new');
$('toggle').onclick = () => send('toggle');
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
for (const button of document.querySelectorAll('[data-period]')) {
  button.onclick = () => {
    period = button.dataset.period;
    limit = 8;
    for (const item of document.querySelectorAll('[data-period]')) {
      item.setAttribute('aria-pressed', String(item === button));
    }
    send('refresh');
  };
}
bridge?.addEventListener('message', ({ data }) => {
  const completed = pending && pending === data.action;
  const error = completed && editor.open ? $('editError') : $('error');
  if (data.error) {
    error.hidden = false;
    error.textContent = data.detail || data.error;
  } else if (data.period === period) {
    error.hidden = true;
    snapshot = data;
    if (completed && editor.open) editor.close();
    render(Boolean(completed));
  }
  if (completed) pending = null;
});
if (bridge) {
  send('refresh');
  setInterval(() => { if (!pending && !document.hidden) send('refresh'); }, 2500);
} else {
  $('status').textContent = 'Abra este painel pelo aplicativo Janela';
}
