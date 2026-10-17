import { h, note } from './ui.js';

// 当前角色条：所有页面默认作用于顶部选择的角色，不再逐页重复选账号/角色。get() 返回 {account,name} 或 null。
export function picker(ctx, onChange) {
  const el = h('div', { class: 'curchar', id: 'curchar' });
  const get = () => ctx.state.cur;
  const paint = () => {
    const c = get();
    el.replaceChildren(c
      ? h('span', { class: 'chip' }, '当前角色 ', h('b', null, c.name), ' · 账号 ', c.account, h('button', { class: 'btn sm', onclick: () => ctx.openPicker?.() }, '更换'))
      : note('请先在顶部选择角色（Alt+C）。', 'warn'));
  };
  const on = () => { if (!el.isConnected) { removeEventListener('gm:char', on); return; } paint(); onChange?.(get()); };
  addEventListener('gm:char', on);
  paint();
  return { el, get };
}
