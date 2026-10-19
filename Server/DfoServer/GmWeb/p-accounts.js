import { api } from './api.js';
import { h, table, note, num, pager, skeleton, field } from './ui.js';

// 账号与角色：左侧账号（搜索 + 分页），右侧角色；点角色行 = 设为当前角色并进入工作台。
export function render(root, ctx) {
  const list = h('div'), chars = h('div');
  const title = h('h2', null, '角色');
  const search = h('input', { type: 'search', id: 'a-q', placeholder: '搜索账号', oninput: () => { page = 1; paintAccounts(); } });
  let all = [], page = 1, size = 25;
  const showChars = async account => {
    sessionStorage.setItem('gm.account', account);
    title.textContent = '角色 · ' + account;
    chars.replaceChildren(skeleton(4));
    try {
      const rows = await api.characters(account);
      let page = 1, size = 25;
      const paint = () => {
        const slice = rows.slice((page - 1) * size, page * size);
        chars.replaceChildren(table([{ key: 'slot', label: '栏位', num: true }, { key: 'name', label: '名称' }, { key: 'level', label: '等级', num: true },
          { key: 'job', label: '职业', num: true }, { key: 'growType', label: '成长', num: true }, { key: 'gold', label: '金币', num: true, render: r => num(r.gold) },
          { key: 'revision', label: '版本', num: true }], slice, r => ctx.openCharacter(account, r.name)),
        pager({ total: rows.length, page, size, onChange: (p, s) => { page = p; size = s; paint(); } }));
      };
      paint();
    } catch (e) { chars.replaceChildren(note(e.message, 'err')); }
  };
  const paintAccounts = () => {
    const q = search.value.trim().toLowerCase();
    const rows = all.filter(r => !q || r.account.toLowerCase().includes(q));
    const slice = rows.slice((page - 1) * size, page * size);
    list.replaceChildren(table([{ key: 'account', label: '账号' }, { key: 'characters', label: '角色数', num: true },
      { label: '状态', render: r => r.disabled ? '已禁用' : '正常' }, { key: 'revision', label: '版本', num: true }], slice, r => showChars(r.account)),
    pager({ total: rows.length, page, size, sizes: [10, 25, 50, 100], onChange: (p, s) => { page = p; size = s; paintAccounts(); } }));
  };
  root.append(h('h1', null, '账号与角色'), h('p', { class: 'lead' }, '点角色行即设为当前角色并进入工作台（背包、技能、任务、称号、邮件、快捷操作都在一页里）。'),
    h('div', { class: 'split' },
      h('div', { class: 'panel' }, h('header', null, h('h2', null, '账号'), field('', search)), h('div', { class: 'body' }, list)),
      h('div', { class: 'panel' }, h('header', null, title), h('div', { class: 'body' }, chars))));
  list.replaceChildren(skeleton(6));
  api.accounts().then(rows => {
    all = rows; paintAccounts();
    const saved = sessionStorage.getItem('gm.account') || ctx.state.cur?.account;
    if (saved && rows.some(r => r.account === saved)) showChars(saved);
  }).catch(e => list.replaceChildren(note(e.message, 'err')));
}
