import { api } from './api.js';
import { h, table, kv, note, fmtTime } from './ui.js';

export function render(root, ctx) {
  const cards = h('div', { class: 'grid cards' });
  const onlineBox = h('div');
  root.append(h('h1', null, '总览'), h('p', { class: 'lead' }, '服务进程状态与在线角色；点击一行打开角色抽屉。'), cards, h('div', { class: 'panel' }, h('header', null, h('h2', null, '在线角色')), h('div', { class: 'body' }, onlineBox)));
  const card = (k, v, s) => h('div', { class: 'card' }, h('div', { class: 'k' }, k), h('div', { class: 'v' + (s ? ' s' : '') }, v));
  const load = async () => {
    try {
      const [s, online] = await Promise.all([api.status(), api.online()]);
      cards.replaceChildren(card('在线', online.length), card('进程 PID', s.pid), card('启动时间', fmtTime(s.startedAt), 1),
        card('频道 / 游戏端口', `${s.channelPort} / ${s.gamePort}`), card('GM 命令端口', s.gmPort || '关闭'), card('GM 网页端口', s.gmWebPort),
        card('PVF', s.pvfPath, 1), card('存档目录', s.dataDirectory, 1));
      onlineBox.replaceChildren(table([{ key: 'name', label: '角色' }, { key: 'account', label: '账号' },
        { label: '状态', render: r => r.ready ? '游戏中' : '加载中' }, { key: 'phase', label: '阶段' }, { key: 'state', label: '场景' }], online, r => ctx.openCharacter(r.account, r.name)));
    } catch (e) { onlineBox.replaceChildren(note(e.message, 'err')); }
  };
  load();
  const timer = setInterval(load, 5000);
  return () => clearInterval(timer);
}