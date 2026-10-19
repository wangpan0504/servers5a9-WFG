import { h, table, pager } from './ui.js';
import { bagKey, worn, BAG_TREE } from './labels.js';

// 角色背包：按客户端分类筛选，选中即切换，并在分类内分页。
export function inventoryView(rows, toolbar) {
  const state = { group: 'bag', sub: 'equip', page: 1, size: 25 };
  const host = h('div');
  const chips = h('div', { class: 'chips' });
  const sub = h('div', { class: 'chips' });
  const list = h('div');
    const paint = () => {
    const tree = BAG_TREE.find(g => g[1] === state.group) || BAG_TREE[0];
    chips.replaceChildren(...BAG_TREE.map(g => h('button', { class: 'chip-btn' + (g[1] === state.group ? ' on' : ''), onclick: () => { state.group = g[1]; state.sub = g[2][0][0]; state.page = 1; paint(); } }, g[0])));
    sub.replaceChildren(...tree[2].map(([key, label]) => h('button', { class: 'chip-btn' + (key === state.sub ? ' on' : ''), onclick: () => { state.sub = key; state.page = 1; paint(); } }, label)));
    const matched = rows.filter(r => bagKey(r) === state.sub);
    toolbar.onFilter?.(state, matched);
    const start = (state.page - 1) * state.size;
    const pageRows = matched.slice(start, start + state.size);
    list.replaceChildren(
      table([
        { label: '', render: r => toolbar.check(r) },
        { label: '容器', render: r => (worn(r) ? '穿戴 · ' : '') + (BAG_TREE.flatMap(g => g[2]).find(x => x[0] === bagKey(r))?.[1] || r.listName) },
        { key: 'slot', label: '位置', num: true }, { key: 'id', label: 'ID', num: true, mono: true },
        { label: '名称', render: r => r.name || '—' }, { key: 'count', label: '数量', num: true },
        { label: '强化', num: true, render: r => r.upgrade || '' },
        { label: '状态', render: r => [r.isSealed ? '封装' : '', r.amplifyType ? '增幅' : ''].filter(Boolean).join(' ') },
        { label: '', render: r => toolbar.edit?.(r) || '' },
      ], pageRows),
      pager({ total: matched.length, page: state.page, size: state.size, sizes: [25, 50, 100], onChange: (p, s) => { state.page = p; state.size = s; paint(); } }));
  };
  host.append(chips, sub, toolbar.bar, list);
  paint();
  return host;
}
