import { api } from './api.js';
import { h, note, field, kv, toast, openConfirm } from './ui.js';

const pad = n => String(n).padStart(2, '0');
const toInput = unix => {
  if (!unix) return '';
  const d = new Date(unix * 1000);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
};
const showTime = unix => unix ? new Date(unix * 1000).toLocaleString('zh-CN', { hour12: false }) : '未设置';

export function render(root) {
  const out = h('div');
  const load = async () => {
    try {
      const e = await api.events();
      const startsAt = e.startsAt ?? e.StartsAt ?? 0;
      const endsAt = e.endsAt ?? e.EndsAt ?? 0;
      const start = h('input', { type: 'datetime-local', value: toInput(startsAt) });
      const end = h('input', { type: 'datetime-local', value: toInput(endsAt) });
      const reason = e.mode === 'on' ? '强制开启' : e.mode === 'off' ? '强制关闭' : !startsAt ? '还没设时间' : e.active ? '处在时间表内' : '处在时间表外';
      const run = async (label, body) => {
        if (!await openConfirm({ title: label, detail: `黄金卡牌双倍：${label}。下一场副本结算才按新状态翻牌。`, danger: true })) return;
        try { await api.post('events', { id: e.eventId, ...body }); toast(label + ' 完成'); load(); }
        catch (x) { toast(x.message, true); }
      };
      out.replaceChildren(
        note('开启后，下一场副本结算的翻牌按双倍。已经翻开的牌不变。有黄金卡服务时这张双倍牌免费，没有就按原价。未设置时关闭，重启后仍记住。'),
        kv({ 活动: '黄金卡牌双倍', 编号: e.eventId, 现在: e.active ? '生效' : '未生效', 原因: reason, 开始: showTime(startsAt), 结束: showTime(endsAt) }),
        h('div', { class: 'toolbar' },
          h('button', { class: 'btn primary', onclick: () => run('强制开启', { op: 'on' }) }, '强制开启'),
          h('button', { class: 'btn danger', onclick: () => run('强制关闭', { op: 'off' }) }, '强制关闭'),
          h('button', { class: 'btn', onclick: () => run('按时间表', { op: 'auto' }) }, '按时间表')),
        h('div', { class: 'toolbar' }, field('开始（北京时间）', start), field('结束（北京时间，不包含）', end),
          h('button', { class: 'btn', onclick: () => {
            if (!start.value || !end.value) return toast('请填写开始和结束时间', true);
            if (end.value <= start.value) return toast('结束必须晚于开始', true);
            run('设置起止时间', { op: 'schedule', start: start.value + ':00+08:00', end: end.value + ':00+08:00' });
          } }, '保存时间')));
    } catch (e) { out.replaceChildren(note(e.message, 'err')); }
  };
  root.append(h('h1', null, '黄金卡牌双倍'), h('p', { class: 'lead' }, '全服活动 64。公告 74 不是这个开关。'), out);
  load();
}
