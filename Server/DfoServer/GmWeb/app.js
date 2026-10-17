import { api } from './api.js';
import { h, $, openPalette, toast } from './ui.js';
import { openCharacter } from './drawer-char.js';
import { charPicker, remember } from './charpick.js';
import * as overview from './p-overview.js';
import * as accounts from './p-accounts.js';
import * as char from './p-char.js';
import * as grant from './p-grant.js';
import * as quests from './p-quests.js';
import * as mail from './p-mail.js';
import * as items from './p-items.js';
import * as audit from './p-audit.js';
import * as events from './p-events.js';

const pages = [
  ['overview', '概览', '◎', overview], ['accounts', '账号与角色', '☰', accounts], ['char', '角色工作台', '★', char], ['items', '物品库', '▦', items],
  ['quests', '任务', '✓', quests], ['grant', '成套发放', '✦', grant], ['mail', '邮件', '✉', mail], ['events', '活动', '⚑', events], ['audit', '审计日志', '≡', audit],
];
// 当前角色：顶部选一次，发放、任务、技能、邮件、属性等页面默认作用于它。
export const state = {
  cur: JSON.parse(sessionStorage.getItem('gm.cur') || localStorage.getItem('gm.cur') || 'null'),
  pending: null, status: null,
};
export function setCurrent(account, name) {
  state.cur = { account, name };
  const text = JSON.stringify(state.cur);
  sessionStorage.setItem('gm.cur', text); localStorage.setItem('gm.cur', text);
}
const selectCharacter = (account, name) => {
  setCurrent(account, name); remember(account, name); picker.paint();
  dispatchEvent(new CustomEvent('gm:char', { detail: state.cur }));
  toast(`当前角色：${name}（账号 ${account}）`);
};
export const ctx = {
  state, setCurrent, selectCharacter,
  openCharacter: (a, n) => { selectCharacter(a, n); location.hash = '#char'; if (current === 'char') route(); },
  openPicker: () => picker.open(), go: (page) => { location.hash = '#' + page; }, refreshTop,
};
const picker = charPicker(ctx);

let stopPage = null, current = '';
function route() {
  const key = (location.hash.slice(1).split('?')[0]) || 'overview';
  const entry = pages.find(p => p[0] === key) || pages[0];
  current = entry[0];
  stopPage?.(); stopPage = null;
  document.querySelectorAll('#nav a').forEach(a => { if (a.dataset.page === entry[0]) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current'); });
  const root = $('#page'); root.replaceChildren();
  document.title = entry[1] + ' · S5A9 GM';
  stopPage = entry[3].render(root, ctx) || null;
  root.scrollTop = 0;
  root.focus({ preventScroll: true });
}

function buildNav() {
  const nav = $('#nav');
  nav.append(h('div', { class: 'brand' }, 'S5A9 GM', h('small', null, '嵌入服务进程 · 仅本机')));
  for (const [key, label, icon] of pages) nav.append(h('a', { href: '#' + key, 'data-page': key, title: label }, h('i', null, icon), h('span', null, label)));
}

function theme() {
  const saved = localStorage.getItem('gm.theme');
  if (saved) document.documentElement.dataset.theme = saved;
}
function toggleTheme() {
  const dark = matchMedia('(prefers-color-scheme: dark)').matches;
  const now = document.documentElement.dataset.theme || (dark ? 'dark' : 'light');
  const next = now === 'dark' ? 'light' : 'dark';
  document.documentElement.dataset.theme = next; localStorage.setItem('gm.theme', next);
}

async function refreshTop() {
  const top = $('#top');
  let s = null;
  try { s = (await api.status()); state.status = s; } catch (e) { /* 服务重启时保留旧值 */ }
  const base = p => (p || '').split(/[\\/]/).filter(Boolean).pop() || '';
  top.replaceChildren(
    picker.el,
    h('span', { class: 'chip' }, h('i', { class: 'dot' + (s ? ' ok' : '') }), s ? '已连接' : '无法连接'),
    s && h('span', { class: 'chip hide-sm' }, 'PVF ', h('b', null, base(s.pvfPath))),
    s && h('span', { class: 'chip hide-sm', title: s.dataDirectory }, '存档 ', h('b', null, base(s.dataDirectory) || s.dataDirectory)),
    s && h('span', { class: 'chip' }, '在线 ', h('b', null, s.online), ' 人'),
    h('span', { class: 'grow' }),
    h('button', { class: 'btn sm', onclick: paletteOpen }, '命令 ', h('kbd', null, 'Ctrl K')),
    h('button', { class: 'btn sm', onclick: toggleTheme, 'aria-label': '切换浅色/深色' }, '◐ 主题'));
}

let cache = { accounts: [], online: [] };
async function paletteOpen() {
  try { cache = { accounts: await api.accounts(), online: await api.online() }; } catch (e) { toast(e.message, true); }
  openPalette(() => [
    ...pages.map(([key, label]) => ({ label: '前往 ' + label, hint: '#' + key, run: () => ctx.go(key) })),
    ...cache.online.map(o => ({ label: '在线角色 ' + o.name, hint: o.account, run: () => ctx.openCharacter(o.account, o.name) })),
    ...cache.accounts.map(a => ({ label: '账号 ' + a.account, hint: a.characters + ' 个角色', run: () => { sessionStorage.setItem('gm.account', a.account); ctx.go('accounts'); route(); } })),
  ]);
}
addEventListener('keydown', e => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); paletteOpen(); }
  else if (e.altKey && e.key.toLowerCase() === 'c') { e.preventDefault(); picker.open(); }
});
addEventListener('hashchange', route);
addEventListener('gm:char', () => { if (current === 'char') route(); });
theme(); buildNav(); refreshTop(); route(); setInterval(refreshTop, 10000);
