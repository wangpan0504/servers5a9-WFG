import { api } from './api.js';
import { toast, openConfirm } from './ui.js';

export async function isOnline(account, name) {
  try { return (await api.online()).some(o => o.account === account && o.name === name); } catch { return false; }
}

// 只有真正危险的操作才二次确认：删除、清空、踢人/断线、整批全部完成。其余直接执行，用 toast 给出结果与撤销提示。
const DANGER = new Set(['items/delete', 'items/clear', 'mails/delete', 'characters/delete', 'kick', 'disconnect', 'quests/clear']);
const UNDO = {
  'gold': '如需还原，再次设置为原金币即可（原值见审计日志）',
  'black-diamond': '原到期时间见审计日志，可再次设置',
  'expert-job': '原副职业类型与熟练度见审计日志，可再次设置',
  'level': '等级可再次设置回原值（原值见审计日志）', 'exp': '等级可再次设置回原值（原值见审计日志）', 'job': '可再次转职回原分支（原值见审计日志）',
  'sp': '原值见审计日志，可再次设置', 'tp': '原值见审计日志，可再次设置', 'currency': '原值见审计日志，可再次设置', 'account-currency': '原值见审计日志，可再次设置', 'vault-gold': '原值见审计日志，可再次设置',
  'quests/add': '可用“取消完成”还原', 'quests/batch': '可在任务页勾选后“取消完成”还原', 'quests/chain': '可在任务页勾选后“取消完成”还原', 'quests/remove': '可再次标记完成',
  'mail/gold': '未领取前可在“邮件”里删除', 'mail/item': '未领取前可在“邮件”里删除', 'mail/set': '未领取前可在“邮件”里删除',
  'direct/item': '可在背包里选中后删除', 'direct/set': '可在背包里选中后删除', 'titles/complete': '已完成的成就无法撤销', 'characters/rename': '可再改回原名',
};
export function isDanger(path, body, opts) {
  return opts?.danger === 'force' || DANGER.has(path) || (path === 'titles/complete' && /^(all|book)/.test(body?.spec || ''));
}

// 角色写操作：[确认（仅危险）] → 提交 → toast（含撤销提示）→ 刷新。
// needReturn：在线角色且要写背包/邮件/改名类操作时，先弹确认并强制小退（带 returnFirst 提交）。
export function actor(d, account, name, ctx, after) {
  return async (label, path, body, opts = {}) => {
    const { detail = '', needReturn = false } = opts;
    const online = needReturn && await isOnline(account, name);
    const risky = isDanger(path, body, opts);
    if (risky || online) {
      const text = detail + (online ? ' 注意：该角色正在游戏中，确认后会先强制小退回到选角界面，再执行本操作。' : '');
      const ok = await (d.confirm || openConfirm)({ title: label, detail: text, danger: true, expect: path === 'characters/delete' ? name : '' });
      if (!ok) return null;
    }
    try {
      const r = await api.post(path, { account, name, ...body, ...(needReturn ? { returnFirst: online } : {}) });
      toast(label + ' 完成' + (UNDO[path] ? ' · 撤销提示：' + UNDO[path] : ''));
      ctx.refreshTop?.();
      after?.(r);
      return r;
    } catch (e) { toast(e.message, true); return null; }
  };
}
export const pageActor = (account, name, ctx, after) => actor({ confirm: openConfirm }, account, name, ctx, after);

export function itemIcon(id, size = 20) {
  const img = document.createElement('img');
  img.className = 'ico-img'; img.width = size; img.height = size; img.loading = 'lazy'; img.alt = '';
  img.src = `/api/items/${id}/icon`;
  const fallback = document.createElement('span');
  fallback.className = 'ico';
  fallback.textContent = '?';
  img.addEventListener('error', () => img.replaceWith(fallback));
  return img;
}

export const RARITY = [['0', '普通', 'r0'], ['1', '高级', 'r1'], ['2', '稀有', 'r2'], ['3', '神器', 'r3'], ['4', '史诗', 'r4'], ['5', '勇者', 'r5'], ['6', '传说', 'r6']];
export const rarityName = n => RARITY.find(r => +r[0] === +n)?.[1] ?? String(n);
