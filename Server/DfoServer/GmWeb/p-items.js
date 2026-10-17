import { api } from './api.js';
import { h, table, field, note, toast, num, pager, skeleton, actionBar, openDrawer, kv, empty, openConfirm } from './ui.js';
import { itemIcon, RARITY, rarityName, isOnline } from './ops.js';

const selected = new Map();   // 跨页保持的勾选：物品 id → 名称
const ONE = '*';

// 物品库：搜索 + 筛选 + 分页 + 勾选，底部操作栏内联数量/强化后直接发给“当前角色”。
export function render(root, ctx) {
  const q = h('input', { type: 'search', id: 'i-q', placeholder: 'ID 或名称', onkeydown: e => { if (e.key === 'Enter') run(1); } });
  const min = h('input', { type: 'number', min: 0, max: 255, value: 0, 'aria-label': '最低等级' }), max = h('input', { type: 'number', min: 0, max: 255, value: 0, title: '0 = 不限', 'aria-label': '最高等级' });
  const rarity = h('select', { id: 'i-rarity', 'aria-label': '稀有度' }, h('option', { value: ONE }, '全部稀有度'), ...RARITY.map(([v, n, c]) => h('option', { value: v }, `${v} · ${n}`)));
  const tag = h('select', { id: 'i-tag', 'aria-label': '分类' }, h('option', { value: ONE }, '全部分类'));
  const stat = h('select', { id: 'i-stat', 'aria-label': '词条' }, h('option', { value: ONE }, '不限词条'));
  const period = h('select', { id: 'i-period', 'aria-label': '期限' }, ...[['*', '期限：不限'], ['none', '无期限'], ['rel', '使用后计时'], ['abs', '绝对到期'], ['any', '有期限（任意）']].map(([v, t]) => h('option', { value: v }, t)));
  const skillJob = h('select', { id: 'i-skilljob', 'aria-label': '技能职业' }, h('option', { value: '*' }, '技能职业：不限'), h('option', { value: 'all' }, '全职业技能加成'));
  const out = h('div'), bar = h('div'), banner = h('div');
  const f = { page: 1, size: 50 };
  let last = null;

  // 词条下拉：带命中数；命中 0 的词条置灰（该 PVF 里没有物品使用）；索引未就绪时先给词条名，就绪后刷新命中数。
  const loadStats = async () => {
    try {
      const keep = stat.value; const r = await fetch('/api/items/stat-keys', { headers: { 'X-GM-Request': '1' } }).then(x => x.json());
      stat.replaceChildren(h('option', { value: ONE }, '不限词条'), ...(r.data || []).map(x => h('option', { value: x.key, disabled: x.count === 0 }, x.label + (x.count == null ? '' : x.count === 0 ? '（无物品）' : `（${num(x.count)}）`))));
      stat.value = keep || ONE; return r.ready;
    } catch { return false; }
  };
  // 索引预热提示：只在真正构建中显示；服务启动后台预热，并按 PVF 大小+修改时间缓存到存档目录，之后重启直接读缓存。
  const watchIndex = async () => {
    for (let n = 0; n < 120; n++) {
      let s; try { s = await api.indexStatus(); } catch { return; }
      if (s.items && s.attributes) { banner.replaceChildren(); await loadStats(); return; }
      banner.replaceChildren(note(`正在构建索引：物品 ${s.items ? '✓' : '构建中…'}，词条 ${s.attributes ? '✓' : '构建中…'}。服务启动时已在后台预热，构建完成后会缓存到存档目录，下次启动不再重建。`, 'warn'));
      if (!banner.firstChild.id) banner.firstChild.id = 'idx-banner';
      await new Promise(r => setTimeout(r, 2000));
    }
  };
  api.categories().then(c => {
    for (const j of c.jobs || []) skillJob.append(h('option', { value: j.replace(/ /g, '_') }, j));
    for (const x of c.categories || []) tag.append(h('option', { value: x.tag }, `${x.equipment ? '装备' : '物品'} · ${x.tag} (${num(x.count)})`));
  }).catch(() => {});
  loadStats().then(ready => { if (!ready) watchIndex(); });

  const paintBar = () => {
    const count = h('input', { type: 'number', min: 1, max: 99999, value: 1, id: 'i-count', 'aria-label': '数量' });
    const up = h('input', { type: 'number', min: 0, max: 31, value: 0, id: 'i-up', 'aria-label': '强化' });
    const ref = h('input', { type: 'number', min: 0, max: 99, value: 0, id: 'i-ref', 'aria-label': '锻造' });
    const cur = ctx.state.cur;
    const send = async mode => {
      const t = ctx.state.cur; if (!t) return toast('请先在顶部选择角色（Alt+C）', true);
      const ids = [...selected.keys()]; if (!ids.length) return;
      let first = false;
      if (mode === 'direct' && await isOnline(t.account, t.name)) {
        if (!await openConfirm({ title: '直接入包', detail: `${t.name} 正在游戏中，确认后会先强制小退回到选角界面，再放入背包。`, danger: true })) return;
        first = true;
      }
      let ok = 0;
      for (const id of ids) {
        try {
          await api.post(mode === 'mail' ? 'mail/item' : 'direct/item', { account: t.account, name: t.name, itemId: id, count: +count.value || 1, upgrade: +up.value || 0, refine: +ref.value || 0, ...(mode === 'direct' ? { returnFirst: first && ok === 0 } : {}) });
          ok++;
        } catch (e) { toast(`${id} ${selected.get(id)}：${e.message}`, true); break; }
      }
      if (ok) toast(`已${mode === 'mail' ? '邮寄给' : '放入'} ${t.name}：${ok}/${ids.length} 种物品 · 撤销提示：${mode === 'mail' ? '未领取前可在“邮件”里删除' : '可在背包里选中后删除'}`);
      if (ok === ids.length) selected.clear();
      paintBar(); paintList();
    };
    bar.replaceChildren(actionBar(
      h('span', { class: 'sel-count', id: 'i-sel-count' }, `已选 ${num(selected.size)} 种`),
      field('数量', count), field('强化', up), field('锻造', ref),
      h('span', { class: 'chip', id: 'i-target' }, '发给 ', h('b', null, cur ? cur.name : '（先选角色）')),
      h('span', { class: 'grow' }),
      h('button', { class: 'btn primary', id: 'i-send-mail', disabled: !selected.size, onclick: () => send('mail') }, '发邮件'),
      h('button', { class: 'btn', id: 'i-send-direct', disabled: !selected.size, onclick: () => send('direct') }, '直接入包'),
      h('button', { class: 'btn sm', disabled: !selected.size, onclick: () => { selected.clear(); paintBar(); paintList(); } }, '清除选择')));
  };
  const paintList = () => {
    if (!last) return;
    const rows = last.items || [];
    const head = h('input', { type: 'checkbox', 'aria-label': '全选本页', checked: rows.length > 0 && rows.every(x => selected.has(x.id)), onchange: e => { rows.forEach(x => e.target.checked ? selected.set(x.id, x.name) : selected.delete(x.id)); paintBar(); paintList(); } });
    out.replaceChildren(
      table([{ label: head, render: x => h('input', { type: 'checkbox', 'aria-label': '选择 ' + x.name, checked: selected.has(x.id), onchange: e => { e.target.checked ? selected.set(x.id, x.name) : selected.delete(x.id); paintBar(); head.checked = rows.every(y => selected.has(y.id)); } }) },
        { label: '', render: x => itemIcon(x.id) }, { key: 'id', label: 'ID', num: true, mono: true }, { key: 'name', label: '名称', wrap: true },
        { key: 'tag', label: '分类' }, { key: 'minLevel', label: '等级', num: true },
        { label: '稀有度', render: x => h('span', { class: 'badge rar r' + x.rarity, title: 'PVF rarity=' + x.rarity }, rarityName(x.rarity)) },
        { key: 'set', label: '套装', num: true, render: x => x.set > 0 ? x.set : '' }], rows, detail),
      pager({ total: last.total, page: f.page, size: f.size, onChange: (p, s) => { f.size = s; run(p); } }));
  };
  const run = async (page = 1) => {
    f.page = page; out.replaceChildren(skeleton(10));
    try {
      const base = { page, pageSize: f.size, min: min.value || 0, max: max.value || 0, rarity: rarity.value, tag: tag.value, q: q.value.trim() };
      const advanced = stat.value !== ONE || period.value !== '*' || skillJob.value !== '*';
      last = advanced ? await api.filterItems({ ...base, stat: stat.value, period: period.value, skillJob: skillJob.value }) : await api.searchItems(base);
      paintList(); paintBar();
    } catch (e) { out.replaceChildren(note(e.message + '（物品索引在后台构建时会自动重试）', 'err')); }
  };
  const previewBox = id => {
    const box = h('div');
    api.itemPreview(id).then(p => {
      const big = itemIcon(id, 48); big.classList.add('ico-big');
      box.append(h('div', { class: 'toolbar' }, big, h('div', null, h('b', null, p.name), h('div', { class: 'desc' }, `${p.kind === 'equipment' ? '装备' : '物品'} · ${p.tag || '-'} · Lv${p.minLevel} · ${rarityName(p.rarity)}（rarity ${p.rarity}）`))),
        p.usableJob?.length ? note('可用职业：' + p.usableJob.join(' ')) : null,
        p.basicExplain ? h('div', { class: 'desc' }, p.basicExplain) : null, p.explain ? h('div', { class: 'desc' }, p.explain) : null, p.detailExplain ? h('div', { class: 'desc' }, p.detailExplain) : null,
        p.stats?.length ? h('ul', { class: 'stats' }, p.stats.map(s => h('li', null, s))) : null,
        p.enchant?.length ? h('div', null, h('b', null, '附魔属性'), h('ul', { class: 'stats' }, p.enchant.map(s => h('li', null, s)))) : null,
        p.avatarAbility?.length ? h('div', null, h('b', null, '时装属性'), h('ul', { class: 'stats' }, p.avatarAbility.map(s => h('li', null, s)))) : null,
        p.skillBonus?.length ? h('div', null, h('b', null, '技能加成'), h('ul', { class: 'stats' }, p.skillBonus.map(s => h('li', null, s)))) : null,
        p.setBonus ? h('div', null, h('b', null, `套装效果：${p.setBonus.name}（#${p.setBonus.index}）`), h('ul', { class: 'stats' }, p.setBonus.tiers.flatMap(t => t.lines.map((s, i) => h('li', null, (i === 0 ? `${t.pieces} 件：` : '　') + s))))) : null,
        p.expiration?.usablePeriodDays || p.expiration?.absoluteExpireDate ? note('期限：' + [p.expiration.usablePeriodDays ? `使用后 ${p.expiration.usablePeriodDays} 天` : '', p.expiration.absoluteExpireDate ? `绝对到期 ${p.expiration.absoluteExpireDate}` : ''].filter(Boolean).join('；')) : null,
        p.flavorText ? h('div', { class: 'desc' }, p.flavorText) : null,
        !p.iconsAvailable ? note('未找到客户端 ImagePacks2，图标显示为占位。可用环境变量 DFO_IMAGEPACKS2 指定其目录。') : null);
    }).catch(() => {});
    return box;
  };
  const detail = row => openDrawer(row.name, async d => {
    d.body.append(previewBox(row.id));
    try {
      const i = await api.itemInfo(row.id);
      d.body.append(kv({ ID: i.id, 名称: i.name, 类型: i.kind, 最低等级: i.minLevel, 稀有度: `${rarityName(i.rarity)}（${i.rarity}）`, 堆叠上限: i.stackLimit, 可堆叠: i.stackable, 可强化: i.upgradable, 可锻造: i.refinable }),
        h('p', { style: 'margin-top:16px' }, h('button', { class: 'btn primary', onclick: () => { selected.set(row.id, row.name); d.close(); paintBar(); paintList(); } }, '加入选择'),
          row.set > 0 && h('button', { class: 'btn', style: 'margin-left:8px', onclick: () => { ctx.state.pending = { setIndex: row.set }; d.close(); ctx.go('grant'); } }, '去发放整套 #' + row.set)));
    } catch (e) { d.body.append(note(e.message, 'err')); }
  });
  root.append(h('h1', null, '物品库'), h('p', { class: 'lead' }, '搜索、筛选后勾选物品（可跨页），在底部填数量/强化，一步发给顶部选中的角色。'), banner,
    h('div', { class: 'toolbar filters' }, field('关键词', q), field('最低等级', min), field('最高等级（0 不限）', max), field('稀有度', rarity), field('分类', tag), field('词条', stat), field('期限', period), field('技能职业', skillJob),
      h('button', { class: 'btn primary', id: 'i-go', onclick: () => run(1) }, '搜索')), out, bar);
  paintBar(); run(1);
}
