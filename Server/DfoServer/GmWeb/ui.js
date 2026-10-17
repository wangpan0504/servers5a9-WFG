// 小型 DOM 工具：所有文本一律走 textContent，不拼 HTML。
export function h(tag, attrs, ...kids) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'value') el.value = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const kid of kids.flat()) if (kid != null && kid !== false) el.append(kid.nodeType ? kid : document.createTextNode(String(kid)));
  return el;
}
export const $ = (sel, root = document) => root.querySelector(sel);
const layer = () => document.getElementById('layer');

export function toast(text, err = false) {
  let box = document.getElementById('toasts');
  if (!box) { box = h('div', { id: 'toasts' }); document.body.append(box); }
  const t = h('div', { class: 'toast' + (err ? ' err' : ''), role: 'status' }, text);
  box.append(t);
  setTimeout(() => t.remove(), err ? 7000 : 4500);
}

export const fmtTime = (s) => s ? new Date(typeof s === 'number' && s < 1e12 ? s * 1000 : s).toLocaleString('zh-CN', { hour12: false }) : '-';
export const num = (n) => (n ?? 0).toLocaleString('zh-CN');

// 骨架屏与空状态
export function skeleton(rows = 6) {
  return h('div', { class: 'skeleton', 'aria-busy': 'true', 'aria-label': '加载中' }, ...Array.from({ length: rows }, (_, i) => h('div', { class: 'sk-row', style: `width:${92 - (i % 3) * 14}%` })));
}
export const empty = (text, hint) => h('div', { class: 'empty' }, h('div', { class: 'empty-ico' }, '∅'), h('div', null, text), hint ? h('small', null, hint) : null);

// 简单表格：columns = [{key,label,num?,mono?,render?,wrap?}]；行可聚焦，上下方向键换行，Enter 触发 onRow。
export function table(columns, rows, onRow) {
  if (!rows.length) return h('div', { class: 'tw' }, empty('没有数据', '换个条件试试'));
  const head = h('tr', null, columns.map(c => h('th', { class: c.num ? 'n' : '' }, c.label)));
  const body = rows.map(r => {
    const tr = h('tr', { class: onRow ? 'click' : '', tabindex: onRow ? 0 : null },
      columns.map(c => h('td', { class: (c.num ? 'n ' : '') + (c.mono ? 'm ' : '') + (c.wrap ? 'wrap' : '') }, c.render ? c.render(r) : (r[c.key] ?? ''))));
    if (onRow) {
      tr.addEventListener('click', e => { if (!e.target.closest('input,button,a,select')) onRow(r); });
      tr.addEventListener('keydown', e => {
        if (e.target !== tr) return;
        if (e.key === 'Enter') onRow(r);
        else if (e.key === 'ArrowDown') { tr.nextElementSibling?.focus(); e.preventDefault(); }
        else if (e.key === 'ArrowUp') { tr.previousElementSibling?.focus(); e.preventDefault(); }
      });
    }
    return tr;
  });
  return h('div', { class: 'tw' }, h('table', null, h('thead', null, head), h('tbody', null, body)));
}

// 分页器：state = { total, page, size }；onChange(page, size)。
export function pager({ total, page, size, onChange, sizes = [25, 50, 100, 200] }) {
  const pages = Math.max(1, Math.ceil(total / size));
  const go = p => onChange(Math.min(Math.max(1, p), pages), size);
  const nums = [];
  const lo = Math.max(1, page - 2), hi = Math.min(pages, page + 2);
  if (lo > 1) nums.push(1);
  if (lo > 2) nums.push('…');
  for (let p = lo; p <= hi; p++) nums.push(p);
  if (hi < pages - 1) nums.push('…');
  if (hi < pages) nums.push(pages);
  const jump = h('input', { type: 'number', min: 1, max: pages, value: page, 'aria-label': '页码', class: 'pg-jump', onkeydown: e => { if (e.key === 'Enter') go(+jump.value || 1); } });
  return h('div', { class: 'pager', role: 'navigation', 'aria-label': '分页' },
    h('span', { class: 'badge', id: 'pg-total' }, `共 ${num(total)} 条`),
    h('select', { 'aria-label': '每页条数', class: 'pg-size', onchange: e => onChange(1, +e.target.value) }, ...sizes.map(s => h('option', { value: s, selected: s === size }, `${s} 条/页`))),
    h('button', { class: 'btn sm', disabled: page <= 1, onclick: () => go(page - 1), 'aria-label': '上一页' }, '上一页'),
    ...nums.map(n => n === '…' ? h('span', { class: 'pg-gap' }, n) : h('button', { class: 'btn sm' + (n === page ? ' primary' : ''), 'aria-current': n === page ? 'page' : null, onclick: () => go(n) }, n)),
    h('button', { class: 'btn sm', disabled: page >= pages, onclick: () => go(page + 1), 'aria-label': '下一页' }, '下一页'),
    h('span', { class: 'pg-to' }, '跳至', jump, `/ ${pages}`, h('button', { class: 'btn sm', onclick: () => go(+jump.value || 1) }, '前往')));
}

export function kv(obj) {
  const dl = h('dl', { class: 'kv' });
  for (const [k, v] of Object.entries(obj)) if (v !== undefined && typeof v !== 'object') dl.append(h('dt', null, k), h('dd', null, String(v)));
  return dl;
}
// "name id=1 level=50" 形式的文本转键值
export function parseSummary(text) {
  const out = {}; const parts = String(text).split(' ');
  out['名称'] = parts.shift();
  for (const p of parts) { const i = p.indexOf('='); if (i > 0) out[p.slice(0, i)] = p.slice(i + 1); }
  return out;
}

export function field(label, input) { return h('label', { class: 'f' }, label, input); }
export const note = (text, kind = '') => h('div', { class: 'note ' + kind }, text);

// 二次确认：居中模态；danger 且给出 expect 时必须输入对应文字才能点“确认执行”。Enter 确认、Esc 取消。
export function openConfirm({ title, detail, danger = false, expect = '' }) {
  return new Promise(resolve => {
    document.getElementById('modal')?.remove();
    const done = v => { modal.remove(); resolve(v); };
    const input = expect ? h('input', { type: 'text', placeholder: `输入 ${expect} 以确认`, 'aria-label': '输入确认', oninput: () => { ok.disabled = input.value.trim() !== expect; } }) : null;
    const ok = h('button', { class: 'btn ' + (danger ? 'danger solid' : 'primary'), disabled: !!expect, onclick: () => done(true) }, '确认执行');
    const box = h('div', { class: 'confirm' + (danger ? ' danger' : ''), role: 'alertdialog', 'aria-label': title },
      h('p', null, h('b', null, title)), h('p', null, detail),
      h('div', { class: 'row' }, input, ok, h('button', { class: 'btn', onclick: () => done(false) }, '取消')));
    const modal = h('div', { id: 'modal' }, h('div', { class: 'scrim top', onclick: () => done(false) }), box);
    modal.addEventListener('keydown', e => {
      if (e.key === 'Escape') { e.stopPropagation(); done(false); }
      else if (e.key === 'Enter' && !ok.disabled && e.target.tagName !== 'BUTTON') { e.preventDefault(); done(true); }
    });
    document.body.append(modal); (input || ok).focus();
  });
}

// 宽抽屉：默认约 56% 视口宽，左缘可拖拽调宽并记住，可最大化；窄屏自动全宽。返回 { body, close, setTitle, confirm }
const DRAWER_KEY = 'gm.drawerW';
export function openDrawer(title, build) {
  closeDrawer();
  const scrim = h('div', { class: 'scrim', onclick: closeDrawer });
  const titleEl = h('h2', null, title);
  const body = h('div', { class: 'body' });
  const saved = +localStorage.getItem(DRAWER_KEY) || 0;
  const apply = w => { drawer.style.width = drawer.classList.contains('max') ? '100vw' : `${Math.min(Math.max(w, 420), innerWidth)}px`; };
  const handle = h('div', { class: 'drawer-grip', role: 'separator', 'aria-orientation': 'vertical', 'aria-label': '拖拽调整宽度', tabindex: 0 });
  const maxBtn = h('button', { class: 'btn sm', id: 'drawer-max', onclick: () => { drawer.classList.toggle('max'); maxBtn.textContent = drawer.classList.contains('max') ? '还原' : '最大化'; apply(saved || innerWidth * .56); } }, '最大化');
  const drawer = h('aside', { class: 'drawer', role: 'dialog', 'aria-label': title, id: 'drawer' }, handle,
    h('header', null, titleEl, maxBtn, h('button', { class: 'btn sm', onclick: closeDrawer, 'aria-label': '关闭' }, '关闭 Esc')), body);
  apply(saved || Math.round(innerWidth * .56));
  let startX = 0, startW = 0;
  const move = e => { const w = startW + (startX - e.clientX); apply(w); };
  const up = () => { removeEventListener('pointermove', move); removeEventListener('pointerup', up); localStorage.setItem(DRAWER_KEY, String(Math.round(drawer.getBoundingClientRect().width))); document.body.classList.remove('resizing'); };
  handle.addEventListener('pointerdown', e => { startX = e.clientX; startW = drawer.getBoundingClientRect().width; document.body.classList.add('resizing'); addEventListener('pointermove', move); addEventListener('pointerup', up); e.preventDefault(); });
  handle.addEventListener('keydown', e => { const w = drawer.getBoundingClientRect().width; if (e.key === 'ArrowLeft') { apply(w + 40); } else if (e.key === 'ArrowRight') { apply(w - 40); } else return; localStorage.setItem(DRAWER_KEY, String(Math.round(drawer.getBoundingClientRect().width))); });
  layer().append(scrim, drawer);
  const api = { body, setTitle: t => { titleEl.textContent = t; }, close: closeDrawer, confirm: openConfirm };
  build(api);
  return api;
}
export function closeDrawer() { document.getElementById('layer').replaceChildren(); }
addEventListener('keydown', e => { if (e.key === 'Escape' && !document.getElementById('modal') && document.getElementById('drawer')) closeDrawer(); });

// Ctrl+K 命令面板：items = [{label, hint, run}]；方向键选择、Enter 执行。
export function openPalette(getItems) {
  closeDrawer();
  const input = h('input', { type: 'text', placeholder: '跳转模块、搜索账号、角色…', 'aria-label': '命令面板' });
  const list = h('ul', { role: 'listbox' });
  let items = [], idx = 0;
  const paint = () => {
    const q = input.value.trim().toLowerCase();
    items = getItems().filter(i => !q || (i.label + ' ' + (i.hint || '')).toLowerCase().includes(q)).slice(0, 40);
    idx = Math.min(idx, Math.max(items.length - 1, 0));
    list.replaceChildren(...items.map((i, n) => h('li', { role: 'option', 'aria-selected': n === idx, onclick: () => run(i) }, i.label, h('small', null, i.hint || ''))));
    if (!items.length) list.append(h('li', null, '没有匹配项'));
    list.querySelector('[aria-selected=true]')?.scrollIntoView({ block: 'nearest' });
  };
  const run = i => { closeDrawer(); i.run(); };
  input.addEventListener('input', () => { idx = 0; paint(); });
  input.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { idx = Math.min(idx + 1, items.length - 1); paint(); e.preventDefault(); }
    else if (e.key === 'ArrowUp') { idx = Math.max(idx - 1, 0); paint(); e.preventDefault(); }
    else if (e.key === 'Enter' && items[idx]) run(items[idx]);
  });
  layer().append(h('div', { class: 'scrim', onclick: closeDrawer }), h('div', { class: 'palette', role: 'dialog', id: 'drawer' }, input, list));
  paint(); input.focus();
}

// 底部操作栏：选择 + 参数 + 动作按钮（贴着页面底部）。
export function actionBar(...kids) { return h('div', { class: 'abar', role: 'toolbar', 'aria-label': '批量操作' }, ...kids); }
