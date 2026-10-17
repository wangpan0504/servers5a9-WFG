import { api } from './api.js';
import { h, field, note, table, toast, num, openDrawer } from './ui.js';
import { isOnline, itemIcon } from './ops.js';
import { picker } from './picker.js';

// 背包与发放：物品/套装/金币邮件；只读背包视图。发放前在抽屉里二次确认并写审计。
export function render(root, ctx) {
  const target = picker(ctx, () => { if (tab === 'inv') showTab('inv'); });
  let tab = 'item';
  const pend = ctx.state.pending; ctx.state.pending = null;
  const content = h('div');
  const bar = h('div', { class: 'tabs', role: 'tablist' });
  const tabs = [['item', '发物品'], ['set', '发套装'], ['gold', '发金币'], ['inv', '背包（只读）']];
  const needTarget = () => { const t = target.get(); if (!t) toast('请先选择账号和角色', true); return t; };
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
      const id = h('input', { type: 'number', min: 1, value: pend?.itemId || '' }), count = h('input', { type: 'number', min: 1, value: 1 });
      const up = h('input', { type: 'number', min: 0, max: 31, value: 0 }), ref = h('input', { type: 'number', min: 0, max: 99, value: 0 });
      const amp = h('input', { type: 'text', placeholder: 'amp:类型1-4:数值（可空）' });
      const unseal = h('input', { type: 'checkbox', 'aria-label': '解除封装' });
      const info = h('div');
      id.addEventListener('change', async () => { info.replaceChildren(); if (+id.value > 0) { try { const i = await api.itemInfo(+id.value); info.replaceChildren(note(`${i.name} · ${i.kind} · 堆叠上限 ${i.stackLimit} · 最低等级 ${i.minLevel}`)); } catch (e) { info.replaceChildren(note(e.message, 'err')); } } });
      if (id.value) id.dispatchEvent(new Event('change'));
      return h('div', null, h('p', { class: 'lead' }, '输入道具 ID（可在「物品库」查找并点“发放”带入）。非堆叠道具每封邮件最多 10 件。'),
        h('div', { class: 'form' }, field('道具 ID', id), field('数量', count), field('强化 0-31', up), field('锻造 0-99', ref), field('增幅', amp), field('解除封装（仅[sealing]装备）', unseal)), info,
        h('p', null, h('button', { class: 'btn primary', onclick: () => send('邮件发放物品', 'mail/item', { itemId: +id.value, count: +count.value, upgrade: +up.value, refine: +ref.value, amp: amp.value.trim() || null, unseal: unseal.checked },
          t => `向 ${t.name} 发放道具 ${id.value} × ${count.value}（强化 ${up.value} / 锻造 ${ref.value}）。`) }, '发放…')));
    },
    set: () => {
      const idx = h('input', { type: 'number', min: 0, value: pend?.setIndex ?? '' });
      const up = h('input', { type: 'number', min: 0, max: 31, value: 0 }), ref = h('input', { type: 'number', min: 0, max: 99, value: 0 });
      return h('div', null, h('p', { class: 'lead' }, '套装编号取自「物品库」里装备的“套装”列。整套按邮件发放，背包或期限不满足会部分失败并提示。'),
        h('div', { class: 'form' }, field('套装编号', idx), field('强化 0-31', up), field('锻造 0-99', ref)),
        h('p', null, h('button', { class: 'btn primary', onclick: () => send('邮件发放套装', 'mail/set', { setIndex: +idx.value, upgrade: +up.value, refine: +ref.value }, t => `向 ${t.name} 发放套装 #${idx.value}。`) }, '发放…')));
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
      return h('div', null, note(`${t.name} · 共 ${(inv.items || []).length} 件 · 金币 ${num(inv.gold)}。删除/清空请在角色抽屉的「背包」页操作。`),
        table([{ key: 'listName', label: '容器' }, { key: 'slot', label: '位置', num: true }, { key: 'id', label: 'ID', num: true, mono: true }, { label: '名称', render: r => r.name || '—' }, { key: 'kind', label: '种类' }, { key: 'count', label: '数量', num: true }], inv.items || []));
    },
  };
  // 快捷物品：保存在存档目录 gm-quick-items.json，点击一项把参数填入下面的表单。
  async function quickBar() {
    let list = [];
    try { list = await api.quickItems(); } catch { /* 读取失败当作空 */ }
    const form = () => content.querySelector('.form')?.querySelectorAll('input') || [];
    const save = async next => {
      try { await api.post('quick-items', { items: next }); list = next; toast('快捷物品已保存'); showTab('item'); } catch (e) { toast(e.message, true); }
    };
    const label = h('input', { type: 'text', maxlength: 30, placeholder: '快捷名称' });
    const chips = list.map((q, n) => h('span', { class: 'chip' },
      h('button', { class: 'btn sm', title: `${q.Id} ×${q.Count} 强化${q.Upgrade} 改造${q.Refine}`, onclick: () => { const f = [...form()]; if (f.length >= 4) { f[0].value = q.Id; f[1].value = q.Count; f[2].value = q.Upgrade; f[3].value = q.Refine; f[0].dispatchEvent(new Event('change')); } } }, q.Label || q.Id),
      h('button', { class: 'btn sm', 'aria-label': '删除快捷物品', onclick: () => save(list.filter((_, i) => i !== n)) }, '×')));
    return h('div', { class: 'toolbar' }, ...chips, label,
      h('button', { class: 'btn sm', onclick: () => { const f = [...form()]; if (f.length < 4 || !(+f[0].value > 0)) return toast('先在下面填好物品参数', true);
        save([...list, { Id: +f[0].value, Label: label.value.trim() || String(f[0].value), Count: +f[1].value, Upgrade: +f[2].value, Refine: +f[3].value }].map(q => ({ id: q.Id, label: q.Label, count: q.Count, upgrade: q.Upgrade, refine: q.Refine }))); } }, '保存当前为快捷'));
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