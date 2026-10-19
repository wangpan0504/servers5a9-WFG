import { api } from './api.js';
import { h, table, note, fmtTime, toast, openConfirm, pager } from './ui.js';

export function render(root, ctx) {
  const cards = h('div', { class: 'grid cards' });
  const onlineBox = h('div');
  const debugBox = h('div', { class: 'body' });
  root.append(h('h1', null, '总览'), h('p', { class: 'lead' }, '服务进程状态与在线角色；点击一行打开角色工作台。'), cards,
    h('div', { class: 'panel' }, h('header', null, h('h2', null, '在线角色')), h('div', { class: 'body' }, onlineBox)),
    h('div', { class: 'panel' }, h('header', null, h('h2', null, '运行调试')), debugBox));
  const switches = [
    ['infiniteFatigue', 'infinite-fatigue', '无限疲劳', '开启后选角显示满疲劳且不扣点。只对当前进程有效，重启恢复为开。'],
    ['ignoreAbsoluteExpiration', 'cash-shop-expiration', '忽略商城绝对日期', '开启后忽略物品的固定到期日。重启恢复为开。'],
    ['ignoreUsablePeriod', 'cash-shop-usable-period', '忽略商城相对期限', '开启后忽略使用后天数。重启恢复为关。'],
    ['premiumPcRoom', 'pc-room', '网吧身份', '开启后本进程按网吧身份。不改黑钻期限，重启恢复为关。'],
  ];
  const loadDebug = async () => {
    try {
      const d = await api.debug();
      debugBox.replaceChildren(note('这些开关不写角色存档，只改当前服务进程。切换前会确认。'),
        ...switches.map(([field, key, label, detail]) => {
          const on = d[field] === true;
          return h('div', { class: 'toolbar' },
            h('span', { class: 'chip' }, h('i', { class: 'dot' + (on ? ' ok' : '') }), label, ' ', h('b', null, d[field] == null ? '不可用' : on ? '开' : '关')),
            h('span', { class: 'grow' }),
            h('button', { class: 'btn sm', disabled: d[field] == null || on, onclick: () => flip(key, label, 'on', detail) }, '开启'),
            h('button', { class: 'btn sm', disabled: d[field] == null || !on, onclick: () => flip(key, label, 'off', detail) }, '关闭'));
        }));
    } catch (e) { debugBox.replaceChildren(note(e.message, 'err')); }
  };
  const flip = async (key, label, value, detail) => {
    if (!await openConfirm({ title: (value === 'on' ? '开启' : '关闭') + label, detail, danger: true })) return;
    try { await api.post('debug', { key, value }); toast(label + '已' + (value === 'on' ? '开启' : '关闭')); loadDebug(); }
    catch (e) { toast(e.message, true); }
  };
  const card = (k, v, s) => h('div', { class: 'card' }, h('div', { class: 'k' }, k), h('div', { class: 'v' + (s ? ' s' : '') }, v));
  let onlinePage = 1, onlineSize = 25;
  const load = async () => {
    try {
      const [s, online] = await Promise.all([api.status(), api.online()]);
      cards.replaceChildren(card('在线', online.length), card('进程 PID', s.pid), card('启动时间', fmtTime(s.startedAt), 1),
        card('频道 / 游戏端口', `${s.channelPort} / ${s.gamePort}`), card('GM 命令端口', s.gmPort || '关闭'), card('GM 网页端口', s.gmWebPort),
        card('PVF', s.pvfPath, 1), card('存档目录', s.dataDirectory, 1));
      const paint = () => {
        const slice = online.slice((onlinePage - 1) * onlineSize, onlinePage * onlineSize);
        onlineBox.replaceChildren(table([{ key: 'name', label: '角色' }, { key: 'account', label: '账号' },
          { label: '状态', render: r => r.ready ? '游戏中' : '加载中' }, { key: 'phase', label: '阶段' }, { label: '场景', render: r => r.userState || '—' }], slice, r => ctx.openCharacter(r.account, r.name)),
        pager({ total: online.length, page: onlinePage, size: onlineSize, onChange: (p, s) => { onlinePage = p; onlineSize = s; paint(); } }));
      };
      paint();
    } catch (e) { onlineBox.replaceChildren(note(e.message, 'err')); }
  };
  load(); loadDebug();
  const timer = setInterval(load, 5000);
  return () => clearInterval(timer);
}