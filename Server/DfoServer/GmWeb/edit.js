import { api } from './api.js';
import { h, note, field, toast, openConfirm } from './ui.js';
import { isOnline } from './ops.js';

// 发放和背包修改共用：每件物品只出现它自己脚本允许的项。词条数值来自脚本，不在这里改写。
export function openEditor(picks, target, done) {
  document.getElementById('modal')?.remove();
  const close = () => modal.remove();
  const list = h('div', { class: 'edit-list' });
  const mode = h('select', { 'aria-label': '发放方式' }, h('option', { value: 'mail', selected: target.mode !== 'direct' }, '邮件发放'), h('option', { value: 'direct', selected: target.mode === 'direct' }, '直接入包'));
  const sending = h('button', { class: 'btn primary' }, target.inventory ? '写入背包' : '发放');
  const box = h('div', { class: 'confirm edit-box' },
    h('header', { class: 'toolbar' }, h('h2', null, target.inventory ? '按词条修改' : '按词条发放'), h('span', { class: 'grow' }), h('button', { class: 'btn sm', onclick: close }, '关闭')),
    target.inventory ? note('能改的项来自这件物品的脚本。有使用期限的可以直接改到期时间。') : h('div', { class: 'toolbar' }, field('方式', mode), note('每件单独设置。脚本写了使用天数时，可以直接改到期时间。')),
    list,
    h('p', null, sending));
  const modal = h('div', { id: 'modal' }, h('div', { class: 'scrim top', onclick: close }), box);
  document.body.append(modal);
  list.append(note('正在读取词条…'));
  const cards = [];
  Promise.all(picks.slice(0, 20).map(async pick => ({ pick, preview: await api.itemPreview(pick.id) }))).then(rows => {
    list.replaceChildren();
    if (picks.length > 20) list.append(note('一次最多 20 件，多出来的这次不带上。', 'warn'));
    for (const row of rows) list.append(card(row.pick, row.preview, cards));
    sending.onclick = () => submit(cards, mode, target, close, done);
  }).catch(e => list.replaceChildren(note(e.message, 'err')));
}

function card(pick, preview, cards) {
  const edit = preview?.edit || {};
  const abilities = edit.abilities || [];
  const abilityValue = abilities.some(a => a.index === pick.ability) ? pick.ability : abilities[0]?.index ?? 0;
  const maxRefine = edit.maxRefine || 8;
  const canGear = !!(edit.gear || edit.reinforce || edit.amplify);
  const state = {
    id: pick.id,
    ref: pick.ref || null,
    upgrade: h('input', { type: 'number', min: 0, max: 31, value: pick.upgrade ?? 0 }),
    refine: h('input', { type: 'number', min: 0, max: maxRefine, value: Math.min(pick.refine ?? 0, maxRefine) }),
    ampType: h('select', null, ...[[0, '不增幅，按强化'], [1, '体力'], [2, '精神'], [3, '力量'], [4, '智力']].map(([v, n]) => h('option', { value: v, selected: (pick.amplifyType ?? 0) === v }, n))),
    ampValue: pick.amplifyValue > 0 ? pick.amplifyValue : 1,
    count: h('input', { type: 'number', min: 1, max: edit.countMax || 1, value: pick.count ?? 1 }),
    ability: h('select', null, ...abilities.map(a => h('option', { value: a.index, selected: a.index === abilityValue }, a.index + ' · ' + a.label))),
    clearPeriod: h('input', { type: 'checkbox', 'aria-label': '不过期' }),
    expireAt: h('input', { type: 'datetime-local', 'aria-label': '到期时间', max: localInput(PERIOD_CAP) }),
    expireOriginal: 0,
    unseal: h('input', { type: 'checkbox', 'aria-label': '解除封装' }),
    edit, canGear
  };
  cards.push(state);
  const fields = [];
  const levelName = h('span', null, '强化');
  const levelHint = h('div', { class: 'desc' });
  const syncLevel = () => {
    const on = canGear && +state.ampType.value > 0;
    levelName.textContent = on ? '增幅等级' : '强化';
    state.upgrade.min = on ? 1 : 0;
    state.upgrade.max = 31;
    if (on && (+state.upgrade.value || 0) < 1) state.upgrade.value = 1;
    if (!on && +state.upgrade.value > 31) state.upgrade.value = 31;
    levelHint.textContent = on
      ? '异次元属性是体力、精神、力量、智力。等级最低 +1，最高 +31。+1 到 +9 失败保级，从 +10 再往上才按高等级失败。'
      : '强化最低 0，最高 31。';
  };
  if (canGear) {
    state.ampType.addEventListener('change', syncLevel);
    fields.push(h('label', { class: 'f' }, levelName, state.upgrade), levelHint, field('异次元属性', state.ampType));
    if (edit.gear && !edit.reinforce) fields.push(note('脚本写了玩家不能强化、不能增幅。GM 仍可写入。'));
    syncLevel();
  }
  if (edit.refine) fields.push(field(`锻造（0–${maxRefine}）`, state.refine), h('div', { class: 'desc' }, '锻造没有最低等级，从 0 起。上限来自本版锻造表。'));
  if (abilities.length) fields.push(field('装扮属性', state.ability));
  if (!pick.ref) fields.push(field(edit.stackable ? '数量' : '件数', state.count));
  else if (edit.stackable) fields.push(field('数量', state.count));
  if (edit.seal && !pick.ref) fields.push(field('解除封装', state.unseal));
  return h('div', { class: 'edit-card' },
    h('b', null, preview?.name || pick.name),
    h('div', { class: 'desc' }, (preview?.stats || []).slice(0, 6).join(' · ') || '这件脚本没有可显示的词条'),
    ...periodNotes(pick, edit, preview, state),
    h('div', { class: 'form' }, ...fields));
}

const PERIOD_CAP = 2147483647;

function localInput(unix) {
  const date = new Date(unix * 1000);
  const p = n => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${p(date.getMonth() + 1)}-${p(date.getDate())}T${p(date.getHours())}:${p(date.getMinutes())}`;
}

function inputUnix(value) {
  const ms = new Date(value).getTime();
  return value && !Number.isNaN(ms) ? Math.floor(ms / 1000) : null;
}

function periodNotes(pick, edit, preview, state) {
  const scriptDays = Number(edit.periodDays || preview?.expiration?.usablePeriodDays || 0);
  const abs = preview?.expiration?.absoluteExpireDate || '';
  const script = [scriptDays > 0 ? scriptDays + ' 天' : '', abs ? '绝对到期 ' + abs : ''].filter(Boolean).join('，');
  const timed = scriptDays > 0 || !!abs || (pick.ref && pick.expire > 0);
  if (!timed) return [note(pick.ref ? '这件没有使用期限。' : '脚本没有使用期限，发放后是永久的。')];
  const current = pick.ref && pick.expire > 0 ? pick.expire : (!pick.ref && scriptDays > 0 ? Math.floor(Date.now() / 1000) + scriptDays * 86400 : 0);
  if (current > 0 && current <= PERIOD_CAP) {
    state.expireOriginal = current;
    state.expireAt.value = localInput(current);
  }
  state.clearPeriod.addEventListener('change', () => { state.expireAt.disabled = state.clearPeriod.checked; });
  const permanent = edit.avatar
    ? '勾选不过期后，时装按永久发放，客户端不再显示天数。'
    : '勾选不过期后写成 2038-01-19。脚本仍有使用期限时，客户端会显示剩余时间，不会显示已超过使用期限。到期时间写成 0 会被客户端当成已经过期。';
  return [
    note((script ? '脚本期限：' + script + '。' : '') + '直接改下面的时间。必须晚于现在，下次登录后按剩余时间显示。早于现在的，选角时会被删掉。'),
    field('到期时间', state.expireAt),
    field('不过期', state.clearPeriod),
    h('div', { class: 'desc' }, permanent)];
}

function chosenExpire(row) {
  if (row.clearPeriod.checked || !row.expireAt.value) return null;
  if (row.expireOriginal && row.expireAt.value === localInput(row.expireOriginal)) return null;
  return inputUnix(row.expireAt.value) ?? -1;
}

async function submit(cards, mode, target, close, done) {
  if (!target?.account || !target?.name) return toast('请先选择角色', true);
  if (!cards.length) return toast('没有可发放的物品', true);
  const direct = !target.inventory && mode.value === 'direct';
  let first = false;
  if (direct && await isOnline(target.account, target.name)) {
    if (!await openConfirm({ title: '直接入包', detail: `${target.name} 正在游戏中。确认后会先强制小退，再把物品放进背包。`, danger: true })) return;
    first = true;
  }
  const sending = document.querySelector('.edit-box .btn.primary');
  if (sending) sending.disabled = true;
  let ok = 0;
  for (const row of cards) {
    const ampOn = row.canGear && +row.ampType.value > 0;
    const level = +row.upgrade.value;
    if (row.canGear && (ampOn ? !(level >= 1 && level <= 31) : !(level >= 0 && level <= 31))) {
      toast(ampOn ? '增幅等级是 1–31' : '强化等级是 0–31', true);
      if (sending) sending.disabled = false;
      break;
    }
    const maxRefine = row.edit.maxRefine || 8;
    if (row.edit.refine && !(+row.refine.value >= 0 && +row.refine.value <= maxRefine)) {
      toast('锻造等级是 0–' + maxRefine, true);
      if (sending) sending.disabled = false;
      break;
    }
    const expire = chosenExpire(row);
    if (expire === -1 || (expire !== null && (expire <= Math.floor(Date.now() / 1000) || expire > PERIOD_CAP))) {
      toast('到期时间必须晚于现在，且不晚于 2038-01-19 11:14', true);
      if (sending) sending.disabled = false;
      break;
    }
    const body = {
      account: target.account, name: target.name,
      upgrade: row.canGear ? +row.upgrade.value || 0 : null,
      refine: row.edit.refine ? +row.refine.value || 0 : null,
      amp: !row.canGear ? null : row.ref ? (ampOn ? `amp:${row.ampType.value}:${row.ampValue || 1}` : 'amp:0:0') : (ampOn ? `amp:${row.ampType.value}:${row.ampValue || 1}` : null),
      ability: (row.edit.abilities || []).length ? +row.ability.value : null,
      count: !row.ref || row.edit.stackable ? +row.count.value || 1 : null,
      clearPeriod: row.clearPeriod.checked,
      expire,
      unseal: row.unseal.checked
    };
    try {
      if (row.ref) await api.post('items/edit', { ...body, list: row.ref.list, slot: row.ref.slot, uid: row.ref.uid, returnFirst: true });
      else await api.post(direct ? 'direct/item' : 'mail/item', { ...body, upgrade: body.upgrade || 0, refine: body.refine || 0, count: body.count || 1, itemId: row.id, returnFirst: first && ok === 0 });
      ok++;
    } catch (e) { toast(`${row.id}：${e.message}`, true); if (sending) sending.disabled = false; break; }
  }
  if (ok) { toast(target.inventory ? `已修改 ${ok} 件` : `已发放 ${ok} 件`); close(); done?.(); }
}
