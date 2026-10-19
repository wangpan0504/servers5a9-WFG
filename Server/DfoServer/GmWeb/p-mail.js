import { api } from './api.js';
import { h, table, note, fmtTime, num } from './ui.js';
import { picker } from './picker.js';

export function render(root, ctx) {
  const out = h('div');
  const target = picker(ctx, () => run());
  const run = async () => {
    const t = target.get(); if (!t) return out.replaceChildren(note('请先选择账号和角色。'));
    out.replaceChildren(h('div', { class: 'empty' }, '加载中…'));
    try {
      const r = await api.section(t.account, t.name, 'mails');
      out.replaceChildren(note('点击任意一行打开角色工作台的邮件页，可勾选删除邮件；发放物品/金币邮件请到「背包与发放」。'),
        table([{ key: 'id', label: 'ID', num: true }, { key: 'sender', label: '发件人' }, { key: 'text', label: '内容' }, { key: 'gold', label: '金币', num: true, render: m => num(m.gold) },
          { label: '创建', render: m => fmtTime(m.created) }, { label: '到期', render: m => fmtTime(m.expires) }, { label: '状态', render: m => m.expired ? '已过期' : '有效' }], r.mails || [], () => ctx.openCharacter(t.account, t.name, 'mails')));
    } catch (e) { out.replaceChildren(note(e.message, 'err')); }
  };
  root.append(h('h1', null, '邮箱'), h('p', { class: 'lead' }, '查看角色邮箱。'), target.el, out);
  run();
}