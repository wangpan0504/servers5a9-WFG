import { api } from './api.js';
import { h, note, field, kv, toast, openDrawer, fmtTime } from './ui.js';

// 活动开关：服务端目前只注册了 64 号（金币卡双倍）活动。强制开/关/自动，或设定起止时间。
export function render(root, ctx) {
  const out = h('div');
  const load = async () => {
    try {
      const r = await fetch('/api/events').then(x => x.json());
      if (r.ok === false) throw new Error(r.error);
      const e = r.data;
      const iso = s => s ? new Date(s * 1000).toISOString().replace('.000Z', 'Z') : '';
      const start = h('input', { type: 'text', placeholder: '2026-10-20T00:00:00+08:00', value: iso(e.StartsAt) });
      const end = h('input', { type: 'text', placeholder: '2026-10-27T00:00:00+08:00', value: iso(e.EndsAt) });
      const run = (label, body) => openDrawer(label, d => d.confirm({ title: label, detail: `活动 ${e.eventId}：${label}。立即对全服生效并写审计。`, danger: true }).then(async ok => {
        if (!ok) return d.close();
        try { await api.post('events', { id: e.eventId, ...body }); toast(label + ' 完成'); d.close(); load(); } catch (x) { d.body.replaceChildren(note(x.message, 'err')); }
      }));
      out.replaceChildren(kv({ 活动: e.eventId, 当前: e.active ? '进行中' : '未生效', 模式: { on: '强制开启', off: '强制关闭', auto: '按时间表' }[e.mode] || e.mode, 版本: e.Revision, 开始: fmtTime(e.StartsAt), 结束: fmtTime(e.EndsAt) }),
        h('div', { class: 'toolbar' },
          h('button', { class: 'btn primary', onclick: () => run('强制开启', { op: 'on' }) }, '强制开启'),
          h('button', { class: 'btn danger', onclick: () => run('强制关闭', { op: 'off' }) }, '强制关闭'),
          h('button', { class: 'btn', onclick: () => run('恢复按时间表', { op: 'auto' }) }, '恢复自动')),
        h('div', { class: 'toolbar' }, field('开始（ISO8601）', start), field('结束（ISO8601）', end),
          h('button', { class: 'btn', onclick: () => run('设置起止时间', { op: 'schedule', start: start.value.trim(), end: end.value.trim() }) }, '设置时间')));
    } catch (e) { out.replaceChildren(note(e.message, 'err')); }
  };
  root.append(h('h1', null, '活动'), h('p', { class: 'lead' }, '服务端活动开关（目前仅 64 号金币卡双倍活动已注册）。'), out);
  load();
}