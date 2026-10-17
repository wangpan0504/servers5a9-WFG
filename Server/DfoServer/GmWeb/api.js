// 与内嵌 GM 站点通信：写请求带 X-GM-Request 头，服务端据此拒绝跨站请求。
// 物品/任务索引在后台构建时 GET 返回 400（…正在后台构建）；自动指数退避重试并在页面顶部显示进度。
function indexBar(text) {
  let bar = document.getElementById('idxbar');
  if (!text) { bar?.remove(); return; }
  if (!bar) { bar = document.createElement('div'); bar.id = 'idxbar'; bar.className = 'note warn'; bar.setAttribute('role', 'status'); document.getElementById('top')?.after(bar); }
  bar.textContent = text;
}
async function call(method, path, body) {
  for (let attempt = 0; ; attempt++) {
    try { const r = await callOnce(method, path, body); if (attempt) indexBar(''); return r; }
    catch (e) {
      if (method === 'GET' && /账号正在使用/.test(e.message) && attempt < 3) { await new Promise(r => setTimeout(r, 300 * (attempt + 1))); continue; }
      if (method !== 'GET' || !/后台构建/.test(e.message) || attempt >= 8) { if (attempt) indexBar(''); throw e; }
      const wait = Math.min(500 * 2 ** attempt, 8000);
      indexBar('正在构建索引（' + (attempt + 1) + '/8），' + Math.round(wait / 100) / 10 + ' 秒后自动重试…');
      await new Promise(r => setTimeout(r, wait));
    }
  }
}
async function callOnce(method, path, body) {
  const init = { method, headers: { 'X-GM-Request': '1' } };
  if (body !== undefined) { init.headers['Content-Type'] = 'application/json'; init.body = JSON.stringify(body); }
  const res = await fetch(path, init);
  let json = null;
  try { json = await res.json(); } catch { /* 非 JSON 响应 */ }
  if (!res.ok || (json && json.ok === false)) throw new Error((json && (json.error || json.message)) || ('请求失败 ' + res.status));
  return json;
}
// 带响应头的 GET（审计分页要读 X-Total-Count）；同样走索引/租约重试。
async function getWithHeaders(path) {
  for (let attempt = 0; ; attempt++) {
    const res = await fetch(path, { headers: { 'X-GM-Request': '1' } });
    let json = null; try { json = await res.json(); } catch { /* 非 JSON */ }
    if (res.ok) return { json, total: +(res.headers.get('X-Total-Count') || 0) };
    const msg = (json && (json.error || json.message)) || ('请求失败 ' + res.status);
    if (/账号正在使用/.test(msg) && attempt < 3) { await new Promise(r => setTimeout(r, 300 * (attempt + 1))); continue; }
    throw new Error(msg);
  }
}
const clean = (o) => Object.fromEntries(Object.entries(o).filter(([, v]) => v !== '' && v != null));
const enc = encodeURIComponent;
export const api = {
  status: () => call('GET', '/api/status'),
  online: () => call('GET', '/api/online'),
  accounts: () => call('GET', '/api/accounts'),
  characters: (a) => call('GET', `/api/accounts/${enc(a)}/characters`),
  section: (a, n, s, q) => call('GET', `/api/characters/${enc(a)}/${enc(n)}/${s}` + (q ? `?q=${enc(q)}` : '')).then(r => r.data ?? r.text ?? r),
  expTable: () => call('GET', '/api/exp-table').then(r => r.data),
  growth: (a, n) => call('GET', `/api/characters/${enc(a)}/${enc(n)}/growth`).then(r => r.data),
  categories: () => call('GET', '/api/items/categories').then(r => r.data),
  searchItems: (p) => call('GET', '/api/items/search?' + new URLSearchParams(p)).then(r => r.data),
  filterItems: (p) => call('GET', '/api/items/filter?' + new URLSearchParams(p)).then(r => r.data),
  statKeys: () => call('GET', '/api/items/stat-keys').then(r => r.data),
  itemInfo: (id) => call('GET', `/api/items/${id}`).then(r => r.data),
  quickItems: () => call('GET', '/api/quick-items').then(r => r.data),
  itemPreview: (id) => call('GET', `/api/items/${id}/preview`).then(r => r.data),
  audit: (limit = 200, filter = {}) => call('GET', '/api/audit?' + new URLSearchParams({ limit, ...Object.fromEntries(Object.entries(filter).filter(([, v]) => v !== '' && v != null)) })),
  auditCsvUrl: (filter = {}) => '/api/audit?' + new URLSearchParams({ format: 'csv', limit: 5000, ...Object.fromEntries(Object.entries(filter).filter(([, v]) => v !== '' && v != null)) }),
  indexStatus: () => call('GET', '/api/index-status').then(r => r.data),
  questSearch: (a, n, p) => call('GET', `/api/characters/${enc(a)}/${enc(n)}/quest-search?` + new URLSearchParams(clean(p))).then(r => r.data),
  auditPage: async (filter, page, size) => { const r = await getWithHeaders('/api/audit?' + new URLSearchParams({ page, pageSize: size, ...clean(filter) })); return { rows: r.json, total: r.total }; },
  post: (path, body) => call('POST', '/api/' + path, { ...body, confirm: true }),
};