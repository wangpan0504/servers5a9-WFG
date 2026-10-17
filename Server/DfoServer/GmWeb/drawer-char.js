import { api } from './api.js';
import { h, openDrawer, kv, parseSummary, table, field, note, toast, fmtTime, num, skeleton } from './ui.js';
import { actor, isOnline } from './ops.js';
import { questPanel } from './p-quests.js';

const CLEARABLE = ['Main', 'Avatar', 'Pet', 'PersonalCargo', 'AccountCargo'];
const LIST_LABEL = { Main: '背包', Avatar: '时装', Pet: '宠物', PersonalCargo: '个人仓库', AccountCargo: '账号金库' };

// 角色抽屉：概览 / 背包 / 任务 / 邮箱 / 操作。所有写操作都在抽屉内二次确认并写审计。
export function openCharacter(account, name, ctx) { openDrawer(name, d => buildCharacter(d, account, name, ctx)); }

// 角色工作台内容：既可放进抽屉，也可放进整页（p-char.js）。d = { body, close, setTitle, confirm }
export function buildCharacter(d, account, name, ctx) {
  {
    const tabs = [['summary', '基础'], ['inventory', '背包'], ['skills', '技能'], ['quests', '任务'], ['titles', '称号'], ['mails', '邮件'], ['stats', '属性'], ['ops', '快捷操作']];
    const content = h('div');
    const bar = h('div', { class: 'tabs', role: 'tablist', onkeydown: e => { const bs = [...bar.querySelectorAll('button')]; const i = bs.findIndex(b => b.getAttribute('aria-selected') === 'true'); if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') { const n = bs[(i + (e.key === 'ArrowRight' ? 1 : bs.length - 1)) % bs.length]; n.focus(); n.click(); e.preventDefault(); } } });
    let current = 'summary';
    const scroller = () => d.body.closest('.body, #page') || document.getElementById('page');
    const show = async key => {
      const same = key === current; const top = same ? scroller()?.scrollTop : 0;
      current = key;
      bar.querySelectorAll('button').forEach(b => { const on = b.dataset.k === key; b.setAttribute('aria-selected', on); b.tabIndex = on ? 0 : -1; });
      if (!same) content.replaceChildren(skeleton(6));
      try { content.replaceChildren(await views[key]()); } catch (e) { content.replaceChildren(note(e.message, 'err')); }
      if (same && scroller()) scroller().scrollTop = top;
    };
    const act = actor(d, account, name, ctx, () => show(current));
    for (const [k, label] of tabs) bar.append(h('button', { role: 'tab', 'data-k': k, onclick: () => show(k) }, label));
    const views = {
      summary: async () => {
        // 同一账号的读取共用一把租约：顺序读取，避免并发时的 409 冲突。
        const s = await api.section(account, name, 'summary');
        const w = await api.section(account, name, 'wallet').catch(() => null);
        const p = await api.section(account, name, 'points').catch(() => null);
        const online = await isOnline(account, name);
        const walletRows = w?.currencies || [];
        return h('div', null, h('p', null, h('span', { class: 'badge ' + (online ? 'ok' : '') }, online ? '在线' : '离线'), ' 账号 ' + account),
          kv(parseSummary(s)),
          p && h('h2', { style: 'margin-top:18px' }, '额外 SP / TP'),
          p && kv({ 额外SP: p.bonusSp ?? '未接管', 额外TP: p.bonusTp ?? '未接管', 当前SP: p.sp ?? '-', 当前TP: p.tp ?? '-' }),
          w && h('h2', { style: 'margin-top:18px' }, '钱包'),
          w && kv({ 金币: num(w.gold), 版本: w.rev }),
          w && table([{ key: 'label', label: '货币' }, { key: 'amount', label: '数量', num: true, render: r => num(r.amount) }, { label: '范围', render: r => r.account ? '账号' : '角色' },
            { label: '', render: r => h('button', { class: 'btn sm', onclick: () => editCurrency(r) }, '修改') }], walletRows),
          w?.vault && h('p', null, `账号金库：容量 ${w.vault.capacity}，金币 ${num(w.vault.gold)} `, w.vault.capacity ? h('button', { class: 'btn sm', onclick: () => editCurrency({ key: 'vault', label: '账号金库金币', amount: w.vault.gold, vault: true, account: true }) }, '修改金库金币') : '（未开通）'));
      },
      skills: async () => {
        const s = await api.section(account, name, 'skill-list');
        return h('div', null,
          kv({ 职业: s.job, 等级: s.level, 当前SP: s.sp ?? '-', 当前TP: s.tp ?? '-', 额外SP: s.bonusSp ?? '未接管', 额外TP: s.bonusTp ?? '未接管' }),
          h('p', { class: 'lead' }, s.initialized ? '技能名称来自 PVF 的技能脚本；等级为主技能树当前值。' : '该角色未启用技能学习模型，显示旧技能表。'),
          table([{ key: 'id', label: '技能 ID', mono: true, num: true }, { key: 'name', label: '名称' }, { key: 'level', label: '等级', num: true }], s.skills));
      },
      inventory: async () => {
        const inv = await api.section(account, name, 'inventory');
        const rows = inv.items || [];
        const picked = new Set();
        const clearSel = h('select', { 'aria-label': '容器' }, ...[...new Set(rows.map(r => r.listName))].filter(x => CLEARABLE.includes(x)).map(x => h('option', { value: rows.find(r => r.listName === x).list }, LIST_LABEL[x] || x)));
        const delBtn = h('button', { class: 'btn danger sm', disabled: true, onclick: () => act('删除选中物品', 'items/delete', { refs: [...picked] }, { danger: true, needReturn: true, detail: `永久删除 ${picked.size} 件物品（不可恢复，已锁定的装备不会被删除）。` }) }, '删除选中');
        const sync = () => { delBtn.disabled = picked.size === 0; delBtn.textContent = `删除选中（${picked.size}）`; };
        return h('div', null, note(`共 ${rows.length} 件 · 版本 ${inv.rev} · 金币 ${num(inv.gold)}。删除/清空会写审计；角色在线时需先强制小退。`),
          h('div', { class: 'toolbar' }, delBtn, clearSel,
            h('button', { class: 'btn danger sm', disabled: !clearSel.options.length, onclick: () => act('清空容器', 'items/clear', { container: +clearSel.value }, { danger: true, needReturn: true, detail: `清空「${clearSel.selectedOptions[0]?.textContent}」中所有未锁定物品（不可恢复）。` }) }, '清空容器')),
          table([{ label: '', render: r => h('input', { type: 'checkbox', 'aria-label': '选择', onchange: e => { const ref = `${r.list}:${r.slot}:${r.uid}`; e.target.checked ? picked.add(ref) : picked.delete(ref); sync(); } }) },
            { key: 'listName', label: '容器', render: r => LIST_LABEL[r.listName] || r.listName }, { key: 'slot', label: '位置', num: true }, { key: 'id', label: 'ID', num: true, mono: true },
            { label: '名称', render: r => r.name || '—' }, { key: 'count', label: '数量', num: true }], rows));
      },
      stats: async () => {
        const r = await api.section(account, name, 'stats');
        const s = r.stats || {};
        const f = v => (v === undefined || v === null ? '-' : num(v));
        return h('div', null, note(r.note || '服务端基础属性（只读）。'),
          kv({ 等级: r.level, 职业: r.job, 成长编码: r.growType }),
          h('h2', { style: 'margin-top:18px' }, '基础属性'),
          kv({ 最大HP: f(s.maxHp), 最大MP: f(s.maxMp), 物理攻击: f(s.physicalAttack), 物理防御: f(s.physicalDefense), 魔法攻击: f(s.magicalAttack), 魔法防御: f(s.magicalDefense),
            火抗: f(s.fireResistance), 冰抗: f(s.waterResistance), 暗抗: f(s.darkResistance), 光抗: f(s.lightResistance), MP回复: f(s.mpRecovery),
            移动速度: f(s.moveSpeed), 攻击速度: f(s.attackSpeed), 释放速度: f(s.castSpeed), 受击恢复: f(s.hitRecovery), 跳跃力: f(s.jumpPower), 负重: f(s.weight), 背包上限: f(s.inventoryLimit) }));
      },
      titles: async () => {
        const filter = h('input', { type: 'text', placeholder: '分类 general/specific/pvp/despair/event 或关键字', style: 'min-width:260px' });
        const box = h('div');
        const spec = h('select', { 'aria-label': '范围' },
          h('option', { value: 'book' }, '称号簿内全部成就'), h('option', { value: 'book:general' }, '称号簿·普通'), h('option', { value: 'book:specific' }, '称号簿·特殊'),
          h('option', { value: 'book:pvp' }, '称号簿·PVP'), h('option', { value: 'book:despair' }, '称号簿·绝望之塔'), h('option', { value: 'book:event' }, '称号簿·活动'),
          h('option', { value: 'all' }, '全部成就'));
        const ids = h('input', { type: 'text', placeholder: '成就ID，逗号分隔（最多500个）' });
        const load = async () => {
          const r = await api.section(account, name, 'titles', filter.value.trim());
          box.replaceChildren(note(`成就 ${r.completed}/${r.total} 已完成，称号簿分区 ${r.sections}/${r.sectionTotal}；匹配 ${r.matched} 条${r.matched > r.quests.length ? `，仅显示前 ${r.quests.length} 条` : ''}。`),
            table([{ key: 'id', label: 'ID', num: true, mono: true }, { key: 'name', label: '成就' }, { label: '分类', render: x => x.categories.join('/') || '—' },
              { label: '状态', render: x => h('span', { class: 'badge ' + (x.done ? 'ok' : '') }, x.done ? '已完成' : '未完成') },
              { label: '', render: x => x.done ? '' : h('button', { class: 'btn sm', onclick: () => act('完成成就', 'titles/complete', { spec: String(x.id) }, { danger: true, detail: `直接完成成就 ${x.id}（${x.name}），发放称号与奖励。` }) }, '完成') }], r.quests));
        };
        const run = h('button', { class: 'btn sm', onclick: () => load().catch(e => box.replaceChildren(note(e.message, 'err'))) }, '查询');
        queueMicrotask(() => run.click());
        return h('div', null, note('称号簿与成就：直接完成会按服务端同一套规划发放称号、奖励与邮件，不检查玩法条件；仅选角界面可写，游戏中会先强制小退。已完成的成就无法撤销。', 'warn'),
          h('div', { class: 'toolbar' }, filter, run),
          h('div', { class: 'toolbar' }, spec,
            h('button', { class: 'btn danger sm', onclick: () => act('一键完成称号簿', 'titles/complete', { spec: spec.value }, { danger: true, detail: `一次性完成「${spec.selectedOptions[0].textContent}」，发放全部称号、奖励与邮件（不可撤销；奖励放不下时会逐个跳过）。` }) }, '一键完成'),
            ids, h('button', { class: 'btn sm', onclick: () => act('完成指定成就', 'titles/complete', { spec: ids.value.replace(/\s+/g, '') }, { danger: true, detail: `完成成就 ${ids.value}。` }) }, '完成指定')),
          box);
      },
      quests: async () => questPanel(ctx, account, name, () => {}),
      mails: async () => {
        const r = await api.section(account, name, 'mails');
        const rows = r.mails || [];
        const picked = new Set();
        const delBtn = h('button', { class: 'btn danger sm', disabled: true, onclick: () => act('删除选中邮件', 'mails/delete', { ids: [...picked] }, { danger: true, needReturn: true, detail: `删除 ${picked.size} 封邮件（仅无附件、无金币的邮件可删）。` }) }, '删除选中');
        const sync = () => { delBtn.disabled = picked.size === 0; delBtn.textContent = `删除选中（${picked.size}）`; };
        return h('div', null, note('仅无附件、无金币且未过期的邮件可删除；含附件/金币的邮件需玩家领取。角色在线时需先强制小退。'),
          h('div', { class: 'toolbar' }, delBtn, h('button', { class: 'btn danger sm', onclick: () => act('删除全部可删邮件', 'mails/delete', { all: true }, { danger: true, needReturn: true, detail: '删除全部可删除的邮件（带附件或金币的保留）。' }) }, '删除全部可删')),
          table([{ label: '', render: m => m.deletable ? h('input', { type: 'checkbox', 'aria-label': '选择', onchange: e => { e.target.checked ? picked.add(m.id) : picked.delete(m.id); sync(); } }) : '' },
            { key: 'id', label: 'ID', num: true }, { key: 'sender', label: '发件人' }, { key: 'text', label: '内容' },
            { key: 'gold', label: '金币', num: true, render: m => num(m.gold) }, { label: '附件', render: m => (m.attachments || []).map(a => `${a.name || a.itemId}×${a.count}`).join('，') },
            { label: '到期', render: m => fmtTime(m.expires) }, { label: '状态', render: m => m.expired ? '已过期' : '有效' }], rows));
      },
      ops: async () => opsView(account, name, act),
    };
    const editCurrency = row => {
      const value = h('input', { type: 'number', min: 0, max: 2147483647, value: row.amount });
      const op = h('select', null, h('option', { value: 'set' }, '设为'), h('option', { value: 'add' }, '增加'), h('option', { value: 'sub' }, '减少'));
      d.body.querySelector('.cur-edit')?.remove();
      const box = h('div', { class: 'panel cur-edit' }, h('header', null, h('h2', null, `修改「${row.label}」${row.account ? '（账号级，同账号所有角色共享）' : ''}`)),
        h('div', { class: 'body form' }, field('方式', op), field('数值', value),
          h('button', { class: 'btn danger', onclick: () => row.vault ? act('修改账号金库金币', 'vault-gold', { op: op.value, amount: +value.value }, { danger: true, detail: `对账号 ${account} 的金库金币${op.selectedOptions[0].textContent} ${value.value}（账号下所有角色共享，不超过当前容量档位上限）。` }) : row.account
            ? act('修改账号货币', 'account-currency', { account, key: row.key, op: op.value, amount: +value.value }, { danger: true, detail: `对账号 ${account} 的「${row.label}」${op.selectedOptions[0].textContent} ${value.value}；该货币由账号下所有角色共享。` })
            : act('修改角色货币', 'currency', { key: row.key, op: op.value, amount: +value.value }, { danger: true, detail: `对 ${name} 的「${row.label}」${op.selectedOptions[0].textContent} ${value.value}。` }) }, '执行')));
      content.append(box);
    };
    d.body.append(bar, content);
    show('summary');
  }
}

async function opsView(account, name, act) {
  const gold = h('input', { type: 'number', min: 1, max: 2000000000, value: 1000000 });
  const goldSet = h('input', { type: 'number', min: 0, max: 2147483647, placeholder: '0–2147483647' });
  const level = h('input', { type: 'number', min: 1, max: 95, placeholder: '1–95' });
  const branch = h('input', { type: 'number', min: 0, max: 5, value: 0 });
  const awake = h('input', { type: 'number', min: 0, max: 2, value: 0 });
  const newName = h('input', { type: 'text', maxlength: 14, placeholder: '新名字' });
  const sp = h('input', { type: 'number', min: 0, max: 50000, placeholder: '0–50000' });
  const tp = h('input', { type: 'number', min: 0, max: 50000, placeholder: '0–50000' });
  const itemId = h('input', { type: 'number', min: 1, placeholder: '物品 ID' }), count = h('input', { type: 'number', min: 1, value: 1 });
  const up = h('input', { type: 'number', min: 0, max: 31, value: 0 }), ref = h('input', { type: 'number', min: 0, max: 99, value: 0 });
  const setIdx = h('input', { type: 'number', min: 0, placeholder: '套装序号' });
  const expInput = h('input', { type: 'number', min: 0, max: 6000000000, placeholder: '累计经验' });
  const expHint = h('p', { class: 'lead', id: 'exp-hint' }, '');
  api.expTable().then(t => {
    const update = () => {
      const lv = Math.min(95, Math.max(1, +level.value || 0)); const row = t.levels[lv - 1];
      if (!row) { expHint.textContent = ''; return; }
      expHint.textContent = `Lv${lv} 起始累计经验 ${num(row.start)}` + (lv < t.max ? `；升到 Lv${lv + 1} 还需 ${num(row.span)}` : '（满级）');
    };
    level.addEventListener('input', update);
  }).catch(() => {});
  const panel = (title, ...kids) => h('div', { class: 'panel' }, h('header', null, h('h2', null, title)), h('div', { class: 'body form' }, ...kids));
  return h('div', { class: 'grid' },
    note('只有删除、清空、踢人/断线和“全部完成”会二次确认；其余操作直接执行，并用 toast 给出结果与撤销提示，所有写操作都记录到审计日志。在线角色可直接修改（不用小退）：金币、等级、转职、SP/TP、任务、称号成就、货币、邮件发送；客户端会在下次选角后刷新。背包类写入（直接入包、删除物品、改名）对在线角色会先提示并强制小退。', 'warn'),
    panel('发放金币（邮件）', field('金币数量', gold),
      h('button', { class: 'btn primary', onclick: () => act('发放金币邮件', 'mail/gold', { gold: +gold.value }, { detail: `给 ${name} 发送 ${num(+gold.value)} 金币的邮件；在线角色会收到新邮件提醒。` }) }, '发送邮件')),
    panel('直接设置金币', field('金币（覆盖）', goldSet),
      h('button', { class: 'btn danger', onclick: () => act('设置金币', 'gold', { gold: +goldSet.value }, { danger: true, detail: `把 ${name} 的金币直接设为 ${num(+goldSet.value)}（覆盖原值）。` }) }, '设置')),
    panel('额外 SP / TP', field('额外 SP', sp), h('button', { class: 'btn danger sm', onclick: () => act('设置额外 SP', 'sp', { value: +sp.value }, { danger: true, detail: `把 ${name} 的额外 SP 设为 ${sp.value}。` }) }, '写入 SP'),
      field('额外 TP', tp), h('button', { class: 'btn danger sm', onclick: () => act('设置额外 TP', 'tp', { value: +tp.value }, { danger: true, detail: `把 ${name} 的额外 TP 设为 ${tp.value}。` }) }, '写入 TP')),
    panel('入包发放物品（直接）', field('物品 ID', itemId), field('数量', count), field('强化', up), field('增幅/改造', ref),
      h('button', { class: 'btn danger', onclick: () => act('直接入包发放', 'direct/item', { itemId: +itemId.value, count: +count.value, upgrade: +up.value, refine: +ref.value }, { danger: true, needReturn: true, detail: `把 ${itemId.value} ×${count.value} 直接放入 ${name} 的背包（背包满则失败）。` }) }, '入包')),
    panel('入包发放套装（直接）', field('套装序号', setIdx),
      h('button', { class: 'btn danger', onclick: () => act('直接入包发套装', 'direct/set', { setIndex: +setIdx.value, upgrade: +up.value, refine: +ref.value }, { danger: true, needReturn: true, detail: `把套装 #${setIdx.value} 直接放入 ${name} 的背包（强化/改造沿用上面物品栏的数值）。` }) }, '入包')),
    panel('等级调整', field('目标等级', level), expHint,
      h('button', { class: 'btn danger', onclick: () => act('调整等级', 'level', { level: +level.value }, { danger: true, detail: `把 ${name} 调到 Lv${level.value}（重置技能，属性可能回到初始值）。` }) }, '执行'),
      field('或按累计经验换算等级', expInput),
      h('button', { class: 'btn danger', id: 'exp-go', onclick: () => act('按累计经验设等级', 'exp', { exp: +expInput.value }, { danger: true, detail: `按累计经验 ${num(+expInput.value)} 换算等级并调整 ${name}（只设等级，等级内经验归零；重置技能）。` }) }, '按经验设等级')),
    panel('转职', field('分支 0-5', branch), field('觉醒 0-2', awake),
      h('button', { class: 'btn danger', onclick: () => act('转职', 'job', { branch: +branch.value, awakening: +awake.value }, { danger: true, detail: `把 ${name} 转为分支 ${branch.value} / 觉醒 ${awake.value}（可用选项用 grow-options 查询）。` }) }, '执行')),
    panel('角色改名', field('新角色名', newName),
      h('button', { class: 'btn danger', onclick: () => act('角色改名', 'characters/rename', { newName: newName.value.trim() }, { danger: true, needReturn: true, detail: `把 ${name} 改名为「${newName.value.trim()}」（名字不能与其他角色重复；邮件里已发出的历史发件人名不变）。` }) }, '改名')),
    panel('删除角色（永久）', note('95 服务端没有软删除：删除会永久清除角色及其背包、邮件、任务等全部数据，且不可恢复。审计日志会保存删除前快照。', 'warn'),
      h('button', { class: 'btn danger solid', onclick: () => act('删除角色', 'characters/delete', { confirmName: name }, { danger: true, needReturn: true, detail: `永久删除角色 ${name}（不可恢复）。` }) }, '删除角色')),
    panel('断开连接（账号）', h('p', null, '直接断开该账号的游戏连接（与重复登录顶号同一断线路径），账号下所有在线角色都会掉线。'),
      h('button', { class: 'btn danger', id: 'disconnect-go', onclick: () => act('断开连接', 'disconnect', {}, { danger: true, detail: `断开账号 ${account} 的连接。` }) }, '断开连接')),
    panel('强制小退', h('p', null, '让该角色回到选角界面。在线的话游戏中会提示断开。'),
      h('button', { class: 'btn danger', onclick: () => act('强制小退', 'kick', {}, { danger: true, detail: `${name} 将被送回选角界面。` }) }, '强制小退')));
}