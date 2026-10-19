import { api } from './api.js';
import { h, table, field, note, toast, num, pager, skeleton, actionBar, openConfirm } from './ui.js';
import { pageActor } from './ops.js';
import { picker } from './picker.js';

const STATUS = { todo: '未完成', active: '进行中', done: '已完成' };
const selections = new Map();   // 跨页保持：按“账号/角色”记住勾选

// 任务面板（任务页与角色工作台共用）：状态/分类筛选 + 数量、分页、勾选批量、全部完成。
export function questPanel(ctx, account, name, onChange) {
  const key = account + '/' + name;
  const sel = selections.get(key) || new Set(); selections.set(key, sel);
  const f = { q: '', category: '*', status: '*', page: 1, size: 50 };
  const box = h('div', { class: 'quest-panel' }), list = h('div'), bar = h('div');
  let last = null;
  const act = pageActor(account, name, ctx, () => { onChange?.(); load(); });
  const q = h('input', { type: 'search', id: 'q-keyword', placeholder: '任务名称或编号', onkeydown: e => { if (e.key === 'Enter') { f.q = q.value.trim(); f.page = 1; load(); } } });
  const chip = (label, count, on, click, id) => h('button', { class: 'chip-btn' + (on ? ' on' : ''), id, onclick: click }, label, h('small', null, count == null ? '' : num(count)));

  const paintBar = () => {
    if (!last) return;
    const selectable = last.quests.filter(x => !['achievement', 'title'].includes(x.category));
    bar.replaceChildren(actionBar(
      h('span', { class: 'sel-count', id: 'q-sel-count' }, `已选 ${num(sel.size)} 个`),
      h('button', { class: 'btn sm', id: 'q-sel-page', onclick: () => { selectable.forEach(x => sel.add(x.id)); paint(); } }, `全选本页（${selectable.length}）`),
      h('button', { class: 'btn sm', id: 'q-sel-all', onclick: () => { last.ids.forEach(id => sel.add(id)); paint(); } }, `全选当前筛选（${num(last.ids.length)}）`),
      h('button', { class: 'btn sm', onclick: () => { sel.clear(); paint(); }, disabled: !sel.size }, '清除选择'),
      h('span', { class: 'grow' }),
      h('button', { class: 'btn primary', id: 'q-complete-sel', disabled: !sel.size, onclick: () => act('完成选中任务', 'quests/batch', { quests: [...sel] }, { detail: `把已选 ${sel.size} 个任务标记为已完成。` }).then(r => r && sel.clear()) }, '完成选中'),
      h('button', { class: 'btn', id: 'q-cancel-sel', disabled: !sel.size, onclick: cancelSelected }, '取消选中的完成'),
      h('button', { class: 'btn danger', id: 'q-complete-all', disabled: !last.ids.length, onclick: () => act('全部完成', 'quests/batch', { quests: last.ids }, { danger: 'force', detail: `把当前筛选范围内的 ${num(last.ids.length)} 个任务全部标记为已完成（${f.category === '*' ? '全部分类' : last.categories.find(c => c.key === f.category)?.label}，${f.status === '*' ? '全部状态' : STATUS[f.status]}）。成就/称号类任务不包含。` }).then(r => r && sel.clear()) }, `全部完成（${num(last.ids.length)}）`),
      h('button', { class: 'btn danger', onclick: () => act('清空已完成任务', 'quests/clear', {}, { detail: `清空 ${name} 全部已完成任务记录（不可恢复）。` }) }, '清空全部')));
  };
  const cancelSelected = async () => {
    const ids = [...sel].slice(0, 200);
    if (!await openConfirm({ title: '取消选中的完成', detail: `取消 ${ids.length} 个任务的完成状态（逐个提交，逐条记审计）。`, danger: true })) return;
    let ok = 0;
    for (const id of ids) { try { await api.post('quests/remove', { account, name, quest: id }); ok++; sel.delete(id); } catch (e) { toast(e.message, true); break; } }
    toast(`已取消 ${ok} 个任务的完成状态 · 撤销提示：可再次勾选后“完成选中”`); onChange?.(); load();
  };
  const paint = () => {
    const r = last;
    const cats = h('div', { class: 'chips', id: 'q-cats' }, chip('全部', r.matched, f.category === '*', () => { f.category = '*'; f.page = 1; load(); }, 'qc-all'),
      ...r.categories.filter(c => c.count > 0 || c.key === f.category).map(c => chip(c.label, c.count, f.category === c.key, () => { f.category = c.key; f.page = 1; load(); }, 'qc-' + c.key)));
    const sc = r.statusCounts, total = sc.todo + sc.active + sc.done;
    const sts = h('div', { class: 'chips', id: 'q-status' }, chip('全部状态', total, f.status === '*', () => { f.status = '*'; f.page = 1; load(); }, 'qs-all'),
      ...['todo', 'active', 'done'].map(s => chip(STATUS[s], sc[s], f.status === s, () => { f.status = s; f.page = 1; load(); }, 'qs-' + s)));
    const pageIds = r.quests.filter(x => !['achievement', 'title'].includes(x.category)).map(x => x.id);
    const head = h('input', { type: 'checkbox', 'aria-label': '全选本页', checked: pageIds.length > 0 && pageIds.every(id => sel.has(id)), onchange: e => { pageIds.forEach(id => e.target.checked ? sel.add(id) : sel.delete(id)); paint(); } });
    const catLabel = k => r.categories.find(c => c.key === k)?.label || k;
    list.replaceChildren(cats, sts,
      table([{ label: head, render: x => ['achievement', 'title'].includes(x.category) ? h('span', { title: '成就/称号请在“称号”页处理' }, '—') : h('input', { type: 'checkbox', 'aria-label': '选择任务 ' + x.id, checked: sel.has(x.id), onchange: e => { e.target.checked ? sel.add(x.id) : sel.delete(x.id); paintBar(); } }) },
        { key: 'id', label: 'ID', num: true, mono: true }, { key: 'name', label: '名称', wrap: true }, { label: '分类', render: x => catLabel(x.category) }, { key: 'grade', label: 'PVF grade' },
        { key: 'minLevel', label: '最低等级', num: true }, { label: '状态', render: x => h('span', { class: 'badge ' + (x.status === 'done' ? 'ok' : x.status === 'active' ? 'warn' : '') }, STATUS[x.status]) },
        { label: '', render: x => ['achievement', 'title'].includes(x.category) ? '' : h('button', { class: 'btn sm', title: '连同前置任务一起完成', onclick: () => act('完成任务及前置', 'quests/chain', { quest: x.id }, { detail: `把任务 ${x.id} 及其按职业前置的整条链标记为已完成。` }) }, '含前置') }], r.quests),
      pager({ total: r.total, page: r.page, size: r.size, onChange: (p, s) => { f.page = p; f.size = s; load(); } }));
    paintBar();
  };
  async function load() {
    list.replaceChildren(skeleton(8));
    try { last = await api.questSearch(account, name, { q: f.q, category: f.category, status: f.status, job: 'mine', page: f.page, pageSize: f.size }); f.page = last.page; paint(); }
    catch (e) { list.replaceChildren(note(e.message, 'err')); }
  }
  box.append(note('只显示当前角色职业的任务，以及脚本标成全职业或未限制职业的任务。其他职业的任务不会出现。'),
    h('div', { class: 'toolbar' }, field('关键词', q), h('button', { class: 'btn primary', onclick: () => { f.q = q.value.trim(); f.page = 1; load(); } }, '查询')), list, bar);
  load();
  return box;
}

export function render(root, ctx) {
  const holder = h('div');
  const target = picker(ctx, () => mount());
  const mount = () => { const t = target.get(); holder.replaceChildren(); if (t) holder.append(questPanel(ctx, t.account, t.name)); };
  root.append(h('h1', null, '任务'), h('p', { class: 'lead' }, '按状态与分类查看、勾选批量完成。只列出当前职业和当前分支能接到的任务，没写职业或全职业的任务仍在里面。'), target.el, holder);
  mount();
}
