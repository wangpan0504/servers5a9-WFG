import { api } from './api.js';
import { h, table, note, openDrawer, kv, fmtTime, pager, skeleton } from './ui.js';

// 只提示该操作的反向命令，不自动撤销。before 是修改前角色摘要文本（若有）。
export function undoHint(x) {
  const [account, name] = (x.target || '').split('/');
  const cmd = (x.command || '').trim();
  const pick = (re) => { const m = re.exec(x.before || ''); return m ? m[1] : null; };
  switch (x.action) {
    case 'gold': { const v = pick(/gold=(\d+)/); return v ? `account ${account} gold ${name} ${v}（把金币设回修改前）` : '修改前金币未记录，无法给出反向命令'; }
    case 'level': { const v = pick(/level=(\d+)/); return v ? `account ${account} level ${name} ${v}（等级设回；经验不一定相同）` : '修改前等级未记录'; }
    case 'quest-add': { const q = /quest-add \S+ \S+ (\d+)/.exec(cmd)?.[1]; return q ? `account ${account} quest-remove ${name} ${q}` : null; }
    case 'quest-remove': { const q = /quest-remove \S+ \S+ (\d+)/.exec(cmd)?.[1]; return q ? `account ${account} quest-add ${name} ${q}` : null; }
    case 'rename': { const m = /rename (\S+) (\S+)$/.exec(cmd); return m ? `account ${account} rename ${m[2]} ${m[1]}` : null; }
    case 'mail-gold': case 'mail-item': case 'mail-set': return '邮件已发出：对方未领取前可在“邮箱”里用 mail-delete 删除；已领取的无法自动回收';
    case 'direct-item': case 'direct-set': return '在背包页选中对应物品执行“删除选中物品”（item-delete）';
    case 'item-delete': case 'item-clear': case 'mail-delete': return '不可撤销；“修改前”里有摘要，可手工用发放功能补发';
    case 'character-delete': return '不可撤销（95 无软删除）；审计里保存了删除前快照';
    case 'title-complete': case 'quest-clear': return '不可撤销（批量完成/清空）';
    case 'extra-sp': case 'extra-tp': { const k = x.action === 'extra-sp' ? 'sp' : 'tp'; const v = pick(new RegExp(k + '=(\\d+)')); return v ? `account ${account} ${k} ${name} ${v}（仅当摘要里的 ${k} 即额外值时适用，请先核对）` : null; }
    case 'force-return': return '无需撤销：角色只是被带回选角';
    default: return null;
  }
}

export function render(root) {
  const out = h('div');
  let rows = [], page = 1, size = 50, total = 0;
  const f = { account: '', action: '', from: '', to: '', ok: '' };
  const filter = () => ({ ...f, from: f.from ? new Date(f.from).toISOString() : '', to: f.to ? new Date(f.to).toISOString() : '' });
  const input = (key, label, type = 'text', ph = '') => h('label', { class: 'f' }, label,
    h('input', { id: 'af-' + key, type, placeholder: ph, oninput: e => { f[key] = e.target.value.trim(); } }));
  const okSel = h('select', { id: 'af-ok', onchange: e => { f.ok = e.target.value; } }, h('option', { value: '' }, '全部'), h('option', { value: '1' }, '成功'), h('option', { value: '0' }, '失败'));
  const click = (href, name) => { const a = h('a', { href, download: name }); document.body.append(a); a.click(); a.remove(); };
  const load = async (p = 1) => {
    page = p; out.replaceChildren(skeleton(8));
    try {
      ({ rows, total } = await api.auditPage(filter(), page, size));
      out.replaceChildren(note(`每次写操作（含失败）追加到数据目录的 gm-audit.jsonl；当前显示 ${rows.length} 条（最多 500，导出最多 5000）。`),
        table([{ label: '时间', render: x => fmtTime(x.time) }, { key: 'action', label: '操作' }, { key: 'target', label: '对象' },
          { label: '结果', render: x => h('span', { class: 'badge ' + (x.ok ? 'ok' : 'err') }, x.ok ? '成功' : '失败') }, { key: 'operator', label: '操作者' }, { key: 'result', label: '返回' }], rows,
          x => openDrawer('审计详情', d => {
            d.body.append(kv({ 时间: fmtTime(x.time), 操作者: x.operator, 操作: x.action, 对象: x.target, 命令: x.command, 结果: x.ok ? '成功' : '失败', 返回: x.result, 修改前: x.before, 修改后: x.after }));
            const hint = x.ok ? undoHint(x) : null;
            if (hint) d.body.append(h('div', { class: 'note warn', id: 'undo-hint' }, '反向操作提示（不会自动执行）：' + hint));
          })), pager({ total, page, size, onChange: (pg, sz) => { size = sz; load(pg); } }));
    } catch (e) { out.replaceChildren(note(e.message, 'err')); }
  };
  const bar = h('div', { class: 'toolbar' }, input('account', '账号', 'text', '例如 10000'), input('action', '操作', 'text', '例如 gold'),
    input('from', '起', 'datetime-local'), input('to', '止', 'datetime-local'), h('label', { class: 'f' }, '结果', okSel),
    h('button', { class: 'btn primary', id: 'af-go', onclick: () => load(1) }, '筛选'),
    h('button', { class: 'btn', id: 'af-json', onclick: async () => { try { const all = await api.audit(5000, filter()); click(URL.createObjectURL(new Blob([JSON.stringify(all, null, 1)], { type: 'application/json' })), 'gm-audit.json'); } catch (e) { out.prepend(note(e.message, 'err')); } } }, '导出 JSON'),
    h('button', { class: 'btn', id: 'af-csv', onclick: () => click(api.auditCsvUrl(filter()), 'gm-audit.csv') }, '导出 CSV'));
  root.append(h('h1', null, '审计日志'), h('p', { class: 'lead' }, 'GM 网页写操作记录：操作者本机、时间、对象、前后值摘要。可按账号、操作、时间、结果筛选并导出。'), bar, out);
  load();
}
