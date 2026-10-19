import { h, note } from './ui.js';
import { buildCharacter } from './drawer-char.js';
import { openConfirm } from './ui.js';

// 角色工作台：整页视图（基础 / 背包 / 技能 / 任务 / 称号 / 邮件 / 属性 / 快捷操作），不再用窄抽屉，也不在页面间跳转。
export function render(root, ctx) {
  const c = ctx.state.cur;
  if (!c) {
    root.append(h('h1', null, '角色工作台'), note('先选择一个角色：点顶部“选择角色”（Alt+C），或到“账号与角色”里点一行。', 'warn'),
      h('button', { class: 'btn primary', onclick: () => ctx.openPicker() }, '选择角色'));
    return;
  }
  const title = h('h1', null, c.name);
  const host = h('div', { class: 'workbench' });
  root.append(title, h('p', { class: 'lead' }, `账号 ${c.account}`), host);
  const tab = ctx.state.tab || 'summary'; ctx.state.tab = 'summary';
  buildCharacter({ body: host, setTitle: t => { title.textContent = t; }, close: () => ctx.go('accounts'), confirm: openConfirm }, c.account, c.name, ctx, tab);
}
