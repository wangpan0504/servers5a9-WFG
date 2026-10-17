import { api } from './api.js';
import { h, closeDrawer } from './ui.js';

// 顶部全局角色选择器：账号/角色搜索，记住最近使用；选一次后所有页面默认作用于该角色。
const RECENT = 'gm.recent';
export const recents = () => { try { return JSON.parse(localStorage.getItem(RECENT) || '[]'); } catch { return []; } };
export function remember(account, name) {
  const rows = [{ account, name }, ...recents().filter(r => !(r.account === account && r.name === name))].slice(0, 8);
  localStorage.setItem(RECENT, JSON.stringify(rows));
}

let cache = { at: 0, rows: [] };
async function allCharacters() {
  if (Date.now() - cache.at < 15000) return cache.rows;
  const [accounts, online] = await Promise.all([api.accounts(), api.online().catch(() => [])]);
  const rows = [];
  await Promise.all(accounts.map(async a => {
    try { for (const c of await api.characters(a.account)) rows.push({ account: a.account, name: c.name, level: c.level, online: online.some(o => o.account === a.account && o.name === c.name) }); }
    catch { /* 账号刚被删除 */ }
  }));
  rows.sort((x, y) => x.account.localeCompare(y.account) || x.name.localeCompare(y.name));
  cache = { at: Date.now(), rows };
  return rows;
}

export function charPicker(ctx) {
  const btn = h('button', { class: 'btn sm charbtn', id: 'charpick', title: '选择角色（Alt+C）', onclick: () => open() });
  const paint = () => {
    const c = ctx.state.cur;
    btn.replaceChildren(h('span', { class: 'dot' + (c ? ' ok' : '') }), c ? h('b', null, c.name) : '选择角色', c ? h('small', null, ' · ' + c.account) : null, h('kbd', null, 'Alt C'));
  };
  const open = async () => {
    closeDrawer();
    const input = h('input', { type: 'text', placeholder: '搜索账号或角色名…', 'aria-label': '搜索角色' });
    const list = h('ul', { role: 'listbox', id: 'charlist' }, h('li', null, '加载中…'));
    let rows = [], idx = 0, shown = [];
    const paintList = () => {
      const q = input.value.trim().toLowerCase();
      const rec = recents();
      const key = r => r.account + '/' + r.name;
      const recentSet = new Set(rec.map(key));
      const match = r => !q || (r.account + ' ' + r.name).toLowerCase().includes(q);
      const recentRows = rec.map(r => rows.find(x => key(x) === key(r))).filter(r => r && match(r));
      const rest = rows.filter(r => !recentSet.has(key(r)) && match(r));
      shown = [...recentRows, ...rest].slice(0, 80);
      idx = Math.min(idx, Math.max(shown.length - 1, 0));
      list.replaceChildren(...shown.map((r, n) => h('li', { role: 'option', 'aria-selected': n === idx, onclick: () => pick(r) },
        r.name, h('small', null, `${r.account} · Lv${r.level}`), r.online ? h('span', { class: 'badge ok' }, '在线') : null, n < recentRows.length ? h('span', { class: 'badge' }, '最近') : null)));
      if (!shown.length) list.append(h('li', null, rows.length ? '没有匹配的角色' : '还没有角色'));
      list.querySelector('[aria-selected=true]')?.scrollIntoView({ block: 'nearest' });
    };
    const pick = r => { closeDrawer(); ctx.selectCharacter(r.account, r.name); };
    input.addEventListener('input', () => { idx = 0; paintList(); });
    input.addEventListener('keydown', e => {
      if (e.key === 'ArrowDown') { idx = Math.min(idx + 1, shown.length - 1); paintList(); e.preventDefault(); }
      else if (e.key === 'ArrowUp') { idx = Math.max(idx - 1, 0); paintList(); e.preventDefault(); }
      else if (e.key === 'Enter' && shown[idx]) pick(shown[idx]);
    });
    document.getElementById('layer').append(h('div', { class: 'scrim', onclick: closeDrawer }), h('div', { class: 'palette charpop', role: 'dialog', id: 'drawer', 'aria-label': '选择角色' }, input, list));
    input.focus();
    try { rows = await allCharacters(); } catch (e) { list.replaceChildren(h('li', null, e.message)); return; }
    paintList();
  };
  paint();
  return { el: btn, open, paint };
}
