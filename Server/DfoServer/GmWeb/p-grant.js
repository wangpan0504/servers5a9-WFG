import { api } from './api.js';
import { h, field, note, table, toast, num, openDrawer, pager } from './ui.js';
import { isOnline, itemIcon } from './ops.js';
import { picker } from './picker.js';
import { ITEM_TREE, tagLabel } from './labels.js';
import { inventoryView } from './bag.js';
import { openEditor } from './edit.js';

export function catalogBrowser(onPick, initialTag, multi, getJob) {
  const sets = initialTag === 'sets';
  const q = h('input', { type: 'search', placeholder: sets ? '套装编号或名称' : '名称或 ID', onkeydown: e => { if (e.key === 'Enter') run(1); } });
  const tag = h('select', { 'aria-label': '分类' }, h('option', { value: sets ? 'sets' : '*', selected: sets }, sets ? '按套装' : '全部分类'));
  if (!sets) for (const [group, rows] of ITEM_TREE) {
    const og = h('optgroup', { label: group });
    for (const [value, label] of rows) og.append(h('option', { value }, label));
    tag.append(og);
  }
  const out = h('div');
  const f = { page: 1, size: 25 };
  let last = null;
  const paint = () => {
    const rows = last?.items || [];
    const columns = (sets ? [
      { key: 'set', label: '套装', num: true, mono: true },
      { label: '代表物品', wrap: true, render: x => x.name },
      { label: '分类', render: x => tagLabel(x.tag) },
      { key: 'pieces', label: '部位', num: true },
      { label: '', render: x => h('button', { class: 'btn sm', onclick: () => onPick(x) }, '选这套') },
    ] : [
      multi ? { label: '', render: x => h('input', { type: 'checkbox', 'aria-label': '选择 ' + x.name, checked: multi.selected.has(x.id), onchange: e => { e.target.checked ? multi.selected.set(x.id, x.name) : multi.selected.delete(x.id); multi.changed(); } }) } : { label: '', render: x => itemIcon(x.id) },
      { label: '', render: x => itemIcon(x.id) },
      { key: 'id', label: 'ID', mono: true, num: true }, { key: 'name', label: '名称', wrap: true },
      { label: '分类', render: x => h('span', { title: x.tag || '' }, tagLabel(x.tag)) },
      multi ? null : { label: '', render: x => h('button', { class: 'btn sm', onclick: () => onPick(x) }, '选择') },
    ]).filter(Boolean);
    out.replaceChildren(table(columns, rows), pager({ total: last?.total || 0, page: f.page, size: f.size, sizes: [25, 50], onChange: (p, s) => { f.size = s; run(p); } }));
  };
  async function run(page = 1) {
    f.page = page;
    try {
      const fit = typeof getJob === 'function' ? await getJob() : null;
      const job = fit == null ? null : typeof fit === 'object' ? fit.job : fit;
      const grow = fit != null && typeof fit === 'object' ? fit.first : null;
      last = await api.searchItems({ page, pageSize: f.size, tag: tag.value, min: 0, max: 0, rarity: '*', q: q.value.trim(), ...(job == null ? {} : { job }), ...(grow == null ? {} : { grow }) });
      paint();
    }
    catch (e) { out.replaceChildren(note(e.message, 'err')); }
  }
  if (multi) multi.repaint = paint;
  queueMicrotask(() => run(1));
  return h('div', null, h('div', { class: 'toolbar' }, field('关键词', q), sets ? null : field('分类', tag), h('button', { class: 'btn primary', onclick: () => run(1) }, '搜索')), out);
}

// 背包与发放：物品/套装/金币邮件；只读背包视图。发放前在抽屉里二次确认并写审计。
export function render(root, ctx) {
  const target = picker(ctx, () => { if (tab === 'inv') showTab('inv'); });
  let tab = 'item';
  const pend = ctx.state.pending; ctx.state.pending = null;
  const content = h('div');
  const bar = h('div', { class: 'tabs', role: 'tablist' });
  const tabs = [['item', '发物品'], ['set', '发套装'], ['gold', '发金币'], ['inv', '背包（只读）']];
  const needTarget = () => { const t = target.get(); if (!t) toast('请先选择账号和角色', true); return t; };
  let wearer = null;
  const wearerJob = async () => {
    const t = target.get();
    if (!t) return null;
    const key = t.account + '/' + t.name;
    if (wearer?.key === key) return wearer.fit;
    const g = await api.growth(t.account, t.name);
    wearer = { key, fit: { job: g.job, first: g.first } };
    return wearer.fit;
  };
  const mode = h('select', { 'aria-label': '发放方式' }, h('option', { value: 'mail' }, '邮件发放'), h('option', { value: 'direct' }, '直接入包'));
  const send = async (label, path0, body, detail) => {
    const t = needTarget(); if (!t) return;
    const direct = mode.value === 'direct' && path0 !== 'mail/gold';
    const path = direct ? path0.replace('mail/', 'direct/') : path0;
    const online = direct && await isOnline(t.account, t.name);
    const warn = direct ? (online ? '【直接入包】该角色正在游戏中：确认后会先强制小退，再把物品直接放进背包。' : '【直接入包】物品将直接放入背包，背包放不下会失败。') : '';
    if (direct) { label = label.replace('邮件', '入包'); body = { ...body, returnFirst: online }; }
    openDrawer(label, d => {
      d.body.append(note(detail(t)));
      (online ? d.confirm({ title: label, danger: true, expect: '', detail: warn + `目标：${t.account} / ${t.name}。邮件发放不要求角色小退，发出后写入审计日志。` }) : Promise.resolve(true)).then(async ok => {
        if (!ok) return d.close();
        try { const r = await api.post(path, { ...t, ...body }); d.body.replaceChildren(note('已发放：' + JSON.stringify(r.data ?? r.text), 'warn')); toast(label + ' 完成'); }
        catch (e) { d.body.replaceChildren(note(e.message, 'err')); }
      });
    });
  };
  const views = {
    item: () => {
      const selected = new Map();
      const multi = { selected, changed() { paintFloat(); }, repaint() {} };
      const float = h('div', { class: 'float-grant' });
      const paintFloat = () => {
        if (!selected.size) { float.remove(); return; }
        float.replaceChildren(h('b', null, `已选 ${selected.size} 种`),
          h('button', { class: 'btn primary', onclick: () => { const t = needTarget(); if (!t) return; openEditor([...selected].map(([id, name]) => ({ id, name })), { ...t, mode: mode.value }, () => { selected.clear(); multi.repaint(); paintFloat(); }); } }, '设置并发放'),
          h('button', { class: 'btn sm', onclick: () => { selected.clear(); multi.repaint(); paintFloat(); } }, '清除'));
        content.append(float);
      };
      multi.changed = paintFloat;
      if (pend?.itemId) selected.set(pend.itemId, '物品 ' + pend.itemId);
      queueMicrotask(paintFloat);
      return h('div', null, note('勾选物品后点右下角。弹窗按每件自己的词条给出强化、增幅、锻造、数量或装扮属性。'),
        catalogBrowser(() => {}, '*', multi, wearerJob));
    },
    set: () => {
      const apply = async row => {
        const t = needTarget(); if (!t) return;
        try {
          const data = await api.setMembers(t.account, t.name, row.set);
          const pieces = data.items || [];
          if (pieces.length < 2) return toast('这套对当前职业没有可发放的部位', true);
          openEditor(pieces.map(x => ({ id: x.id, name: x.name })), { ...t, mode: mode.value });
        } catch (e) { toast(e.message, true); }
      };
      if (pend?.setIndex) queueMicrotask(() => apply({ set: pend.setIndex, name: '套装 #' + pend.setIndex }));
      return h('div', null, note('一行是一套。只列出当前职业能穿、并且没有写死其他分支的部位；没写分支的套装也保留。'), catalogBrowser(apply, 'sets', null, wearerJob));
    },
    gold: () => {
      const gold = h('input', { type: 'number', min: 1, max: 2000000000, value: 1000000 }), text = h('input', { type: 'text', maxlength: 60, placeholder: '开发GM发放金币' });
      return h('div', null, h('p', { class: 'lead' }, '金币通过邮件发放，玩家领取时受服务端金币上限约束。'),
        h('div', { class: 'form' }, field('金币', gold), field('邮件文字', text)),
        h('p', null, h('button', { class: 'btn primary', onclick: () => send('邮件发放金币', 'mail/gold', { gold: +gold.value, text: text.value }, t => `向 ${t.name} 发放 ${num(+gold.value)} 金币。`) }, '发放…')));
    },
    inv: async () => {
      const t = target.get(); if (!t) return note('请先选择账号和角色。');
      const inv = await api.section(t.account, t.name, 'inventory');
      return h('div', null, note(`${t.name} · 共 ${(inv.items || []).length} 件 · 金币 ${num(inv.gold)}。删除请到角色工作台的背包。`),
        inventoryView(inv.items || [], { bar: h('div'), check: () => '' }));
    },
  };
  // 快捷物品：保存在存档目录 gm-quick-items.json，点击一项把参数填入下面的表单。
  async function quickBar() {
    let list = [];
    try { list = await api.quickItems(); } catch { /* 读取失败当作空 */ }
    const save = async next => {
      try { await api.post('quick-items', { items: next }); list = next; toast('快捷物品已保存'); showTab('item'); } catch (e) { toast(e.message, true); }
    };
    const chips = list.map((q, n) => h('span', { class: 'chip' },
      h('button', { class: 'btn sm', title: `${q.id ?? q.Id} ×${q.count ?? q.Count}`, onclick: () => { const t = needTarget(); if (!t) return; openEditor([{ id: q.id ?? q.Id, name: (q.label ?? q.Label) || String(q.id ?? q.Id), count: q.count ?? q.Count, upgrade: q.upgrade ?? q.Upgrade, refine: q.refine ?? q.Refine }], t); } }, (q.label ?? q.Label) || (q.id ?? q.Id)),
      h('button', { class: 'btn sm', 'aria-label': '删除快捷物品', onclick: () => save(list.filter((_, i) => i !== n)) }, '×')));
    return h('div', { class: 'toolbar' }, ...chips);
  }
  async function showTab(k) {
    tab = k; bar.querySelectorAll('button').forEach(b => b.setAttribute('aria-selected', b.dataset.k === k));
    content.replaceChildren(h('div', { class: 'empty' }, '加载中…'));
    try { content.replaceChildren(...(k === 'item' ? [await quickBar()] : []), await views[k]()); } catch (e) { content.replaceChildren(note(e.message, 'err')); }
  }
  for (const [k, l] of tabs) bar.append(h('button', { role: 'tab', 'data-k': k, onclick: () => showTab(k) }, l));
  root.append(h('h1', null, '背包与发放'), h('p', { class: 'lead' }, '先选择角色，再发放物品、套装或金币（邮件）。'), target.el, h('div', { class: 'toolbar' }, field('发放方式', mode)), bar, content);
  showTab(pend?.setIndex != null ? 'set' : 'item');
}