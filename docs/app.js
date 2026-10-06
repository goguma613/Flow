'use strict';

// ─────────────────────────────────────────────────────────────
// Flow 폰
//
// PC의 Flow와 구글 드라이브의 'Flow 폰' 폴더로 이어진다. 파일은 둘뿐이다.
//   phone-view.json  — PC가 쓴다. 오늘 화면 그대로. 폰은 읽기만.
//   phone-inbox.json — 폰이 쓴다. 체크·추가한 '일'을 쌓아 둔다. PC는 읽기만.
// 한 파일을 두 쪽이 같이 쓰면 나중에 쓴 쪽이 앞의 것을 덮는다.
// 쓰는 쪽을 하나로 못박아 그런 일이 구조적으로 안 생기게 했다.
//
// 계산(날짜 넘김·연속 기록·"내일 3시" 읽기)은 전부 PC가 한다.
// 폰은 받은 화면에 아직 PC가 못 받은 일을 얹어 보여 줄 뿐이다.
// ─────────────────────────────────────────────────────────────

// 구글 클라우드 콘솔에서 만든 '웹 애플리케이션' 클라이언트 ID. 원래 공개되는 값이다.
const CLIENT_ID = '909779692688-a8k32ia40o527lkeilt712d9nt6nr09s.apps.googleusercontent.com';

// 구글이 심사 없이 내주는 가장 좁은 권한. 이 앱이 직접 만든 파일만 만질 수 있다.
const SCOPE = 'https://www.googleapis.com/auth/drive.file';

// PC 쪽 PhoneLinkStore 와 반드시 같아야 한다.
const FOLDER_NAME = 'Flow 폰';
const INBOX_NAME = 'phone-inbox.json';
const VIEW_NAME = 'phone-view.json';

const DEMO = new URLSearchParams(location.search).has('demo');

/** 켜져 있는 동안 이만큼마다 PC 쪽 변경을 가져온다. */
const REFRESH_EVERY_MS = 30_000;

/** 연달아 체크하면 모아서 한 번에 보낸다. 체크마다 올리면 드라이브가 그만큼 일한다. */
const FLUSH_DELAY_MS = 700;

// ───────── 이 폰에만 남는 것
//
// 보기용(?demo) 화면은 아무것도 남기지 않는다. 같은 주소라 저장소를 같이 쓰는데,
// 거기서 누른 가짜 체크가 남으면 나중에 진짜로 연결할 때 PC로 넘어가 버린다.

const local = DEMO ? {
  get(key, fallback) { return fallback; },
  set() {},
  remove() {}
} : {
  get(key, fallback) {
    try {
      const raw = localStorage.getItem('flow.' + key);
      return raw == null ? fallback : JSON.parse(raw);
    } catch { return fallback; }
  },
  set(key, value) {
    try { localStorage.setItem('flow.' + key, JSON.stringify(value)); } catch { /* 사생활 모드 등 */ }
  },
  remove(key) {
    try { localStorage.removeItem('flow.' + key); } catch { /* */ }
  }
};

const state = {
  phase: 'boot',                       // signin · today · waiting-pc
  view: local.get('view', null),       // 마지막으로 받은 PC 화면
  pending: local.get('pending', []),   // 아직 PC가 반영 안 한 일 = inbox 내용
  dirty: local.get('dirty', false),    // inbox 에 아직 못 올린 것이 있는지
  rev: 0,                              // 일을 쌓을 때마다 1씩. 보내는 사이 새로 누른 게 있는지 가린다
  sent: new Set(local.get('sent', [])),// 드라이브에 올라간 것이 확인된 일
  authStuck: false,                    // 구글 창이 결과를 안 돌려줬다
  errorDetail: '',
  ids: local.get('ids', null),         // 드라이브 안의 폴더·파일 번호
  token: null,
  tokenExpires: 0,
  busy: false,
  error: null,
  showDone: false,
  showUpcoming: local.get('showUpcoming', false),
  fetchedAt: local.get('fetchedAt', 0)
};

// ───────── 날짜

const WEEKDAYS = ['일요일', '월요일', '화요일', '수요일', '목요일', '금요일', '토요일'];

function pad(n) { return String(n).padStart(2, '0'); }

function isoDate(d) { return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`; }

/** PC가 읽는 꼴(시간대 표시 없는 로컬 시각). PC와 폰이 같은 시간대라는 전제다. */
function localIso(d) {
  return `${isoDate(d)}T${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
}

function dayStartHour() { return state.view?.DayStartHour ?? 4; }

/** 새벽 4시 기준 '오늘'. PC의 DayEngine.LogicalDate 와 같은 계산. */
function logicalToday(now = new Date()) {
  return isoDate(new Date(now.getTime() - dayStartHour() * 3600_000));
}

function parseDate(iso) {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(y, m - 1, d);
}

/** "2026-10-06" 에서 n일 뒤. 달력으로 세므로 서머타임과 상관없다. */
function addDays(iso, n) {
  const d = parseDate(iso);
  d.setDate(d.getDate() + n);
  return isoDate(d);
}

function shortTime(t) {
  if (!t) return '';
  const [h, m] = t.split(':').map(Number);
  return m ? `${h}:${pad(m)}` : `${h}시`;
}

function agoText(iso) {
  if (!iso) return '';
  const then = new Date(iso);
  const minutes = Math.round((Date.now() - then.getTime()) / 60000);
  if (minutes < 1) return '방금';
  if (minutes < 60) return `${minutes}분 전`;
  if (isoDate(then) === isoDate(new Date())) return `${then.getHours()}:${pad(then.getMinutes())}`;
  return `${then.getMonth() + 1}/${then.getDate()} ${then.getHours()}:${pad(then.getMinutes())}`;
}

function uuid() {
  if (crypto.randomUUID) return crypto.randomUUID();
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
    const r = crypto.getRandomValues(new Uint8Array(1))[0] & 15;
    return (c === 'x' ? r : (r & 3) | 8).toString(16);
  });
}

function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// ───────── 구글 로그인
//
// 브라우저만으로는 오래 가는 열쇠(refresh token)를 받을 수 없다. 1시간짜리 열쇠를 쓰고,
// 다 되면 다시 받는다. 다시 받을 때 구글이 작은 창을 띄우는데, 창은 사람이 누른 그 순간에만
// 열 수 있다 — 그래서 열쇠가 없을 때는 저절로 받지 않고, 다음에 무언가를 누를 때 받는다.

let tokenClient = null;

function waitForGoogle(timeoutMs = 6000) {
  return new Promise(resolve => {
    const started = Date.now();
    (function poll() {
      if (window.google?.accounts?.oauth2) return resolve(true);
      if (Date.now() - started > timeoutMs) return resolve(false);
      setTimeout(poll, 100);
    })();
  });
}

function hasToken() {
  return DEMO || (state.token && Date.now() < state.tokenExpires - 60_000);
}

function restoreToken() {
  const saved = local.get('token', null);
  if (saved && Date.now() < saved.expires - 60_000) {
    state.token = saved.value;
    state.tokenExpires = saved.expires;
  }
}

/** 반드시 사람이 누른 처리 안에서 부를 것. 그렇지 않으면 브라우저가 창을 막는다. */
async function requestToken() {
  if (hasToken()) return true;
  if (!CLIENT_ID) { toast('아직 구글 연결 준비 중입니다'); return false; }
  if (!await waitForGoogle()) { toast('구글 로그인 스크립트를 못 불러왔습니다'); return false; }

  // 지난번 계정을 알려 주면 구글이 계정 고르는 화면을 건너뛰고 창을 바로 닫는다.
  // 열쇠를 다시 받는 일이 '번쩍 하고 끝'이 된다.
  tokenClient ??= google.accounts.oauth2.initTokenClient({
    client_id: CLIENT_ID,
    scope: SCOPE,
    hint: local.get('hint', undefined),
    callback: () => {}
  });

  return new Promise(resolve => {
    // 설치한 앱으로 띄우면 구글 창이 끝나도 결과가 안 돌아오는 경우가 있다. 무작정 기다리지 않는다.
    const giveUp = setTimeout(() => {
      state.authStuck = true;
      toast('구글 연결 창이 돌아오지 않았습니다');
      render();
      resolve(false);
    }, 60_000);

    tokenClient.callback = response => {
      clearTimeout(giveUp);
      if (response.error || !response.access_token) return resolve(false);
      state.token = response.access_token;
      state.tokenExpires = Date.now() + Number(response.expires_in || 3600) * 1000;
      state.authStuck = false;
      local.set('token', { value: state.token, expires: state.tokenExpires });
      local.set('connected', true);
      rememberAccount();
      resolve(true);
    };
    tokenClient.error_callback = () => { clearTimeout(giveUp); resolve(false); };
    tokenClient.requestAccessToken({ prompt: local.get('connected', false) ? '' : 'consent' });
  });
}

/** 연결한 계정의 메일 주소를 적어 둔다. 다음에 열쇠를 받을 때 계정 고르기를 건너뛰는 데만 쓴다. */
async function rememberAccount() {
  if (local.get('hint', null)) return;
  try {
    const json = await (await drive('/drive/v3/about?fields=user(emailAddress)')).json();
    if (json?.user?.emailAddress) local.set('hint', json.user.emailAddress);
  } catch { /* 없어도 계정 고르기 화면이 한 번 더 뜰 뿐이다 */ }
}

// ───────── 드라이브

class AuthError extends Error {}
class GoneError extends Error {}

async function drive(path, options = {}) {
  const response = await fetch('https://www.googleapis.com' + path, {
    ...options,
    cache: 'no-store',
    headers: { Authorization: 'Bearer ' + state.token, ...(options.headers || {}) }
  });

  if (response.status === 401) {
    state.token = null;
    local.remove('token');
    throw new AuthError();
  }
  if (response.status === 404) throw new GoneError();
  if (!response.ok) throw new Error('drive ' + response.status);
  return response;
}

async function findOne(query) {
  const params = new URLSearchParams({ q: query, fields: 'files(id,name)', spaces: 'drive', pageSize: '10' });
  const json = await (await drive('/drive/v3/files?' + params)).json();
  return json.files?.[0] ?? null;
}

async function createFolder(name) {
  const response = await drive('/drive/v3/files?fields=id', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, mimeType: 'application/vnd.google-apps.folder' })
  });
  return response.json();
}

async function createFile(name, parent, content) {
  const boundary = 'flow' + Math.random().toString(16).slice(2);
  const meta = { name, parents: [parent], mimeType: 'application/json' };
  const body =
    `--${boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n${JSON.stringify(meta)}\r\n` +
    `--${boundary}\r\nContent-Type: application/json\r\n\r\n${content}\r\n` +
    `--${boundary}--`;

  const response = await drive('/upload/drive/v3/files?uploadType=multipart&fields=id', {
    method: 'POST',
    headers: { 'Content-Type': `multipart/related; boundary=${boundary}` },
    body
  });
  return response.json();
}

/**
 * 'Flow 폰' 폴더와 두 파일을 찾고, 없으면 만든다.
 * 이 앱이 만든 것만 보이는 권한이라, 찾으면 그건 언제나 우리가 만든 것이다.
 */
async function ensureLink() {
  if (state.ids) return state.ids;

  const folderQuery = `name='${FOLDER_NAME}' and mimeType='application/vnd.google-apps.folder' and trashed=false`;
  const folder = await findOne(folderQuery) ?? await createFolder(FOLDER_NAME);

  const inside = name => `name='${name}' and '${folder.id}' in parents and trashed=false`;
  const inbox = await findOne(inside(INBOX_NAME)) ??
    await createFile(INBOX_NAME, folder.id, JSON.stringify({ Version: 1, Ops: state.pending }));
  const view = await findOne(inside(VIEW_NAME)) ??
    await createFile(VIEW_NAME, folder.id, '{}');

  state.ids = { folder: folder.id, inbox: inbox.id, view: view.id };
  local.set('ids', state.ids);
  return state.ids;
}

async function readView() {
  const text = await (await drive(`/drive/v3/files/${state.ids.view}?alt=media`)).text();
  try { return JSON.parse(text); } catch { return null; }   // PC가 쓰는 도중이면 다음에 다시
}

async function readInbox() {
  const text = await (await drive(`/drive/v3/files/${state.ids.inbox}?alt=media`)).text();
  try {
    const json = JSON.parse(text);
    return Array.isArray(json?.Ops) ? json.Ops : [];
  } catch {
    return [];
  }
}

/**
 * 여러 곳에서 모은 일을 번호로 합친다. 사람이 일을 '취소해서 지우는' 경우는 없으므로
 * 합집합이 언제나 맞다. PC가 이미 받아 간 것만 뺀다.
 */
function mergeOps(lists, applied) {
  const byId = new Map();
  for (const list of lists) {
    for (const op of list ?? []) {
      if (op?.Id && !applied.has(op.Id) && !byId.has(op.Id)) byId.set(op.Id, op);
    }
  }
  return [...byId.values()].sort((a, b) => a.At.localeCompare(b.At) || a.Id.localeCompare(b.Id));
}

function sameIds(a, b) {
  if (a.length !== b.length) return false;
  const ids = new Set(a.map(op => op.Id));
  return b.every(op => ids.has(op.Id));
}

/**
 * inbox 를 맞춘다: 드라이브에 있는 것 + 이 폰이 들고 있는 것 − PC가 받아 간 것.
 *
 * 덮어쓰지 않고 합치는 이유: 폰에서 Flow가 두 군데 열려 있으면(크롬 탭과 홈 화면 아이콘)
 * 각자 자기가 아는 것만 써서 서로의 체크를 지웠다. 실제로 체크 하나가 그렇게 사라졌다.
 *
 * 보내는 사이에 새로 누른 것이 있으면 '다 보냈음'으로 치지 않고 한 번 더 보낸다.
 * 예전에는 그 사이 누른 체크가 '보냈음' 표시에 묻혀 영영 안 갔다.
 */
async function syncInbox() {
  const rev = state.rev;
  const applied = new Set(state.view?.AppliedOps ?? []);
  const remote = await readInbox();

  const merged = mergeOps([remote, local.get('pending', []), state.pending], applied);
  state.pending = merged;
  local.set('pending', merged);

  if (!sameIds(remote, merged)) {
    await drive(`/upload/drive/v3/files/${state.ids.inbox}?uploadType=media`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ Version: 1, Ops: merged })
    });
  }

  // 여기까지 왔으면 이 일들은 드라이브에 있다. '아직 폰에만 있음'과 'PC가 받기를 기다림'을 가르는 표시.
  state.sent = new Set(merged.map(op => op.Id));
  local.set('sent', [...state.sent]);

  if (state.rev === rev) {
    state.dirty = false;
    local.set('dirty', false);
  } else {
    scheduleFlush();
  }
}

/** 폴더나 파일을 사람이 지웠다. 번호를 잊고 처음부터 다시 찾거나 만든다. */
async function withLink(work) {
  await ensureLink();
  try {
    return await work();
  } catch (error) {
    if (!(error instanceof GoneError)) throw error;
    state.ids = null;
    local.remove('ids');
    await ensureLink();
    return work();
  }
}

// ───────── 주고받기

let refreshing = null;

async function refresh() {
  if (DEMO || !hasToken()) { render(); return; }
  if (refreshing) return refreshing;

  refreshing = (async () => {
    state.busy = true;
    render();

    try {
      await withLink(async () => {
        const view = await readView();
        if (view?.Today) {
          state.view = view;
          state.fetchedAt = Date.now();
          local.set('view', view);
          local.set('fetchedAt', state.fetchedAt);
        }

        // 새로고침할 때마다 inbox 를 맞춘다. PC가 받아 간 것은 빠지고,
        // 이 폰에 남아 있는데 드라이브에 없는 것(전에 못 보낸 것)은 이때 다시 간다.
        await syncInbox();
      });

      state.error = null;
      state.phase = state.view?.Today ? 'today' : 'waiting-pc';
    } catch (error) {
      state.error = error instanceof AuthError ? 'auth' : 'net';
      state.errorDetail = error instanceof AuthError ? '' : String(error?.message || error);
    } finally {
      state.busy = false;
      refreshing = null;
      render();
    }
  })();

  return refreshing;
}

let flushTimer = 0;

function scheduleFlush() {
  clearTimeout(flushTimer);
  flushTimer = setTimeout(flush, FLUSH_DELAY_MS);
}

async function flush() {
  if (DEMO || !state.dirty) return;
  if (!hasToken()) { render(); return; }

  try {
    await withLink(syncInbox);
    state.error = null;
  } catch (error) {
    state.error = error instanceof AuthError ? 'auth' : 'net';
    state.errorDetail = error instanceof AuthError ? '' : String(error?.message || error);
  }
  render();
}

/** 폰에서 한 일 하나를 쌓는다. 화면에는 바로 반영하고, 드라이브에는 모아서 보낸다. */
function pushOp(kind, target, text) {
  const now = new Date();
  state.pending.push({
    Id: uuid(),
    Kind: kind,
    Target: target ?? null,
    Text: text ?? null,
    At: localIso(now),
    Day: logicalToday(now)
  });
  state.rev++;
  state.dirty = true;
  local.set('pending', state.pending);
  local.set('dirty', true);
  render();
  scheduleFlush();
}

// ───────── 화면에 보일 것 = PC 화면 + 아직 못 받은 일

function composeView() {
  const view = state.view;
  if (!view?.Today) return null;

  const today = logicalToday();
  const stale = view.Today < today;   // PC가 아직 어제에 머물러 있다(밤새 꺼져 있었다)

  let routines = view.Routines.map(r => ({ ...r, kind: 'routine' }));
  let tasks = view.Tasks.map(t => ({ ...t, kind: 'task' }));
  let upcoming = (view.Upcoming ?? []).map(t => ({ ...t, kind: 'task' }));

  if (stale) {
    // 새 하루다. PC가 켜지면 체크가 풀릴 것들을 미리 풀어 보여 준다.
    routines = routines.map(r => ({ ...r, Done: false }));
    tasks = tasks.filter(t => !t.Done);

    // 예정이던 것 중 날이 온 것은 오늘로 내려온다. PC의 예정 탭이 하는 일과 같다.
    tasks.push(...upcoming.filter(t => t.Due <= today));
    upcoming = upcoming.filter(t => t.Due > today);
  }

  const applied = new Set(view.AppliedOps ?? []);
  const byId = new Map([...routines, ...tasks, ...upcoming].map(item => [item.Id, item]));

  for (const op of [...state.pending].sort((a, b) => a.At.localeCompare(b.At))) {
    if (applied.has(op.Id)) continue;

    if (op.Kind === 'add') {
      const item = { Id: op.Id, Title: op.Text, Done: false, kind: 'task', waiting: true, Priority: 'None' };
      tasks.push(item);
      byId.set(op.Id, item);
    } else if (op.Kind === 'done' || op.Kind === 'undone') {
      const item = byId.get(op.Target);
      if (item) {
        item.Done = op.Kind === 'done';
        item.waiting = true;
      }
    }
  }

  // 예정에서 체크한 것은 오늘 끝낸 것이 되므로 오늘 쪽 '완료됨'으로 옮긴다.
  tasks.push(...upcoming.filter(t => t.Done));
  upcoming = upcoming.filter(t => !t.Done);

  return { today: stale ? today : view.Today, stale, routines, tasks, upcoming };
}

// ───────── 그리기

const ICON = {
  check: '<svg viewBox="0 0 16 16" fill="none" stroke="#17171A" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4.5 8.5 7 11l4.5-5.5"/></svg>',
  flame: '<svg viewBox="0 0 16 16" fill="currentColor"><path d="M8 1.4c1.2 3.2 4 4.8 4 8a4 4 0 0 1-8 0c0-2 1.2-3.2 1.8-4.8.6 1.4 1.4 1 1.6 0 .2-1 0-2 .6-3.2Z"/></svg>',
  refresh: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 4v7h-7"/></svg>',
  send: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5"/><path d="m5 12 7-7 7 7"/></svg>',
  chevron: '<svg viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="m3.5 2 3 3-3 3"/></svg>',
  drive: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3h8l6 10-4 7H6l-4-7z"/><path d="m8 3 6 10H2"/><path d="m16 3-6 10 4 7"/></svg>'
};

const app = document.getElementById('app');

function render() {
  if (state.phase === 'signin' || (state.phase === 'boot' && !state.view)) return renderSignin();
  if (state.phase === 'waiting-pc' && !state.view?.Today) return renderWaiting();
  renderToday();
}

function renderSignin() {
  app.innerHTML = `
    <div class="center">
      <div class="brand"><img src="icon-192.png" alt=""><b>Flow</b></div>
      <p class="lead">PC의 Flow와 이어서 씁니다.</p>
      <p class="sub">오늘 할 일을 보고, 체크하고, 생각난 것을 적어 두면 PC가 받아 넣습니다.</p>
      <button class="primary" data-act="connect">${ICON.drive}구글 계정으로 연결</button>
      <p class="sub" style="margin-top:16px">내 드라이브에 <b style="color:var(--text-body)">${esc(FOLDER_NAME)}</b> 폴더 하나만 만들고 그 안의 파일만 씁니다. 드라이브의 다른 파일은 볼 수 없습니다.</p>
    </div>`;
}

function renderWaiting() {
  app.innerHTML = `
    <div class="center">
      <div class="brand"><img src="icon-192.png" alt=""><b>Flow</b></div>
      <p class="lead">연결됐습니다. PC를 기다리는 중입니다.</p>
      <ol class="steps">
        <li>PC의 Flow 데이터가 구글 드라이브의 <code>내 드라이브\\Flow</code> 에 있어야 합니다. (<code>여러 PC에서 쓰기.txt</code> 참고)</li>
        <li>PC에서 Flow를 켜 두세요. 1~2분 안에 오늘 목록이 여기에 뜹니다.</li>
      </ol>
      <button class="primary" data-act="refresh">${state.busy ? '확인 중…' : '다시 확인'}</button>
      ${statusLine()}
    </div>`;
}

/**
 * 지금 상태 한 줄. 일이 어디까지 갔는지를 가려서 말한다:
 *   폰에만 있음(아직 못 보냄) → 드라이브에 감(PC가 받기를 기다림) → PC가 받음(사라짐)
 * 예전에는 앞의 둘을 다 "PC 반영 대기"라고만 해서, 못 보내고 있는 걸 알 수가 없었다.
 */
function statusLine() {
  const applied = new Set(state.view?.AppliedOps ?? []);
  const open = state.pending.filter(op => !applied.has(op.Id));
  const unsent = open.filter(op => !state.sent.has(op.Id)).length;
  const waiting = open.length - unsent;
  const generated = state.view?.GeneratedAt ? `PC ${agoText(state.view.GeneratedAt)} 기준` : '';

  if (unsent > 0) {
    if (state.busy) return `<div class="status pending"><span class="dot"></span>보내는 중… ${unsent}개</div>`;
    if (state.authStuck) {
      return `<button class="status error" data-act="refresh"><span class="dot"></span>폰에만 있음 ${unsent}개 · 구글 연결이 안 돌아왔습니다. 크롬에서 열어 주세요</button>`;
    }
    if (!hasToken() || state.error === 'auth') {
      return `<button class="status error" data-act="refresh"><span class="dot"></span>폰에만 있음 ${unsent}개 · 눌러서 보내기</button>`;
    }
    if (state.error === 'net') {
      return `<button class="status error" data-act="refresh"><span class="dot"></span>폰에만 있음 ${unsent}개 · 보내지 못함(${esc(state.errorDetail)}) · 눌러서 다시</button>`;
    }
    return `<div class="status pending"><span class="dot"></span>보낼 차례 ${unsent}개</div>`;
  }

  if (state.error === 'net') {
    return `<button class="status error" data-act="refresh"><span class="dot"></span>드라이브에 닿지 못했습니다(${esc(state.errorDetail)}) · 눌러서 다시</button>`;
  }
  if (!hasToken() && local.get('connected', false) && !DEMO) {
    return `<button class="status" data-act="refresh"><span class="dot"></span>${generated ? generated + ' · ' : ''}눌러서 최신으로</button>`;
  }
  if (waiting > 0) {
    return `<div class="status pending"><span class="dot"></span>PC 반영 대기 ${waiting}개 · PC가 켜져 있으면 1분 안에</div>`;
  }
  return `<div class="status live"><span class="dot"></span>${generated || '최신'}</div>`;
}

/** 맨 아래 작은 글씨. 문제가 생겼을 때 캡처 한 장으로 사정을 알 수 있게 한다. */
function diagnostics() {
  const standalone = matchMedia('(display-mode: standalone)').matches || navigator.standalone;
  const minutes = hasToken() && !DEMO ? Math.max(0, Math.round((state.tokenExpires - Date.now()) / 60000)) : 0;
  const parts = [
    'v4',
    standalone ? '앱' : '브라우저',
    DEMO ? '보기용' : (hasToken() ? `연결 ${minutes}분 남음` : '연결 끊김'),
    `대기 ${state.pending.length}`
  ];
  return `<div class="diag">${parts.join(' · ')}</div>`;
}

function rowHtml(item) {
  const classes = ['row'];
  if (item.Done) classes.push('done');
  if (item.waiting) classes.push('waiting');

  const ring = item.kind === 'task' && !item.Done
    ? { High: 'high', Medium: 'medium', Low: 'low' }[item.Priority] ?? ''
    : '';

  const meta = [];
  if (item.waiting && item.kind === 'task' && item.Id && !state.view?.Tasks.some(t => t.Id === item.Id)) {
    meta.push('<span class="badge wait">PC 반영 대기</span>');
  }
  if (item.kind === 'routine') {
    if (item.Time) meta.push(`<span class="badge">${esc(shortTime(item.Time))}</span>`);
    if (item.Streak >= 2) meta.push(`<span class="streak">${ICON.flame}${item.Streak}</span>`);
  } else if (!item.Done) {
    if (item.CarryOverCount > 0) meta.push(`<span class="carry">밀림 ${item.CarryOverCount}일</span>`);
    const due = dueBadge(item);
    if (due) meta.push(due);
  }

  return `
    <button class="${classes.join(' ')}" data-act="toggle" data-id="${esc(item.Id)}" data-kind="${item.kind}">
      <span class="ring ${ring}">${ICON.check}</span>
      <span class="title">${esc(item.Title)}</span>
      ${meta.length ? `<span class="meta">${meta.join('')}</span>` : ''}
    </button>`;
}

function dueBadge(task) {
  if (!task.Due && !task.Time) return '';

  const today = logicalToday();
  const overdue = task.Due && task.Due < today;
  let label;

  if (!task.Due || task.Due === today) label = task.Time ? shortTime(task.Time) : '오늘';
  else if (overdue) label = '지남';
  else if (task.Due === addDays(today, 1)) label = '내일';
  else {
    const d = parseDate(task.Due);
    label = `${d.getMonth() + 1}/${d.getDate()}`;
  }

  if (task.Due && task.Due !== today && !overdue && task.Time) label += ' ' + shortTime(task.Time);
  return `<span class="badge${overdue ? ' overdue' : ''}">${esc(label)}</span>`;
}

/** 예정 머리줄에 붙는 가장 가까운 날. "내일" · "10월 9일" */
function upcomingWhen(task) {
  const tomorrow = addDays(logicalToday(), 1);
  if (task.Due === tomorrow) return '내일';
  const d = parseDate(task.Due);
  return `${d.getMonth() + 1}월 ${d.getDate()}일`;
}

function renderToday() {
  const composed = composeView();
  const date = parseDate(composed?.today ?? logicalToday());

  const routines = composed?.routines ?? [];
  const upcoming = composed?.upcoming ?? [];
  const open = (composed?.tasks ?? []).filter(t => !t.Done);
  const done = (composed?.tasks ?? []).filter(t => t.Done);

  const total = routines.length + open.length + done.length;
  const finished = routines.filter(r => r.Done).length + done.length;
  const percent = total ? Math.round(finished / total * 100) : 0;

  const draft = document.getElementById('compose')?.value ?? '';
  const hadFocus = document.activeElement?.id === 'compose';

  app.innerHTML = `
    <header class="head">
      <div class="head-row">
        <div class="head-date">
          <div class="date">${date.getMonth() + 1}월 ${date.getDate()}일</div>
          <div class="weekday">${WEEKDAYS[date.getDay()]}${composed?.stale ? ' · PC가 켜지면 새 하루로 맞춰집니다' : ''}</div>
        </div>
        <button class="icon-btn${state.busy ? ' spinning' : ''}" data-act="refresh" aria-label="새로고침">${ICON.refresh}</button>
      </div>
      ${statusLine()}
      <div class="progress">
        <div class="bar"><i style="width:${percent}%"></i></div>
        <span class="count">${finished} / ${total}</span>
      </div>
    </header>

    <main class="list">
      ${routines.length ? `<div class="section-label">반복 루틴</div>${routines.map(rowHtml).join('')}` : ''}
      ${routines.length && (open.length || done.length) ? '<div class="divider"></div>' : ''}
      ${open.length ? `<div class="section-label">할 일</div>${open.map(rowHtml).join('')}` : ''}
      ${done.length ? `
        <button class="fold${state.showDone ? ' open' : ''}" data-act="fold">${ICON.chevron}완료됨 ${done.length}개</button>
        ${state.showDone ? done.map(rowHtml).join('') : ''}` : ''}
      ${!total ? '<div class="empty">오늘은 비어 있습니다.<br>아래에 적으면 PC가 받아 넣습니다.</div>' : ''}
      ${upcoming.length ? `
        <div class="divider"></div>
        <button class="fold upcoming${state.showUpcoming ? ' open' : ''}" data-act="fold-upcoming">${ICON.chevron}예정 ${upcoming.length}개<span class="fold-note">· 가장 빠른 것 ${esc(upcomingWhen(upcoming[0]))}</span></button>
        ${state.showUpcoming ? upcoming.map(rowHtml).join('') : ''}` : ''}
      ${diagnostics()}
    </main>

    <div class="composer">
      <form data-act="add" autocomplete="off">
        <span class="plus">+</span>
        <input id="compose" enterkeyhint="send" placeholder="빠른 추가 — 내일 오후 3시 회의 !1" value="${esc(draft)}">
        <button class="send" type="submit" aria-label="추가"${draft.trim() ? '' : ' disabled'}>${ICON.send}</button>
      </form>
    </div>
    <div class="toast" id="toast"></div>`;

  if (hadFocus) {
    const input = document.getElementById('compose');
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
  }
}

let toastTimer = 0;

function toast(message) {
  let el = document.getElementById('toast');
  if (!el) {
    el = document.createElement('div');
    el.className = 'toast';
    el.id = 'toast';
    document.body.appendChild(el);
  }
  el.textContent = message;
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), 2200);
}

// ───────── 누르기
//
// 무언가를 누를 때마다 열쇠부터 챙긴다. 구글 로그인 창은 사람이 누른 그 순간에만 열린다.

app.addEventListener('click', async event => {
  const target = event.target.closest('[data-act]');
  if (!target || target.tagName === 'FORM') return;

  switch (target.dataset.act) {
    case 'connect':
      if (DEMO) { state.phase = 'today'; render(); return; }
      if (await requestToken()) {
        state.phase = 'today';
        await refresh();
      }
      return;

    case 'refresh':
      if (!hasToken() && !await requestToken()) return;
      await refresh();
      return;

    case 'fold':
      state.showDone = !state.showDone;
      render();
      return;

    case 'fold-upcoming':
      state.showUpcoming = !state.showUpcoming;
      local.set('showUpcoming', state.showUpcoming);
      render();
      return;

    case 'toggle': {
      const composed = composeView();
      const item = [...(composed?.routines ?? []), ...(composed?.tasks ?? []), ...(composed?.upcoming ?? [])]
        .find(i => i.Id === target.dataset.id);
      if (!item) return;

      if (navigator.vibrate) navigator.vibrate(8);
      pushOp(item.Done ? 'undone' : 'done', item.Id);
      if (!hasToken()) await requestToken();
      if (hasToken()) scheduleFlush();
      return;
    }
  }
});

app.addEventListener('submit', async event => {
  event.preventDefault();
  const input = document.getElementById('compose');
  const text = input.value.trim();
  if (!text) return;

  input.value = '';
  pushOp('add', null, text);
  toast('적어 뒀습니다 · PC가 받아 넣습니다');
  if (!hasToken()) await requestToken();
  if (hasToken()) scheduleFlush();
});

app.addEventListener('input', event => {
  if (event.target.id !== 'compose') return;
  const send = app.querySelector('.send');
  if (send) send.disabled = !event.target.value.trim();
});

// 다른 앱 갔다가 돌아오면, 그리고 켜져 있는 동안 가끔 PC 쪽 변경을 가져온다.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible') refresh();
});
setInterval(() => {
  if (document.visibilityState === 'visible') refresh();
}, REFRESH_EVERY_MS);

// ───────── 시작

function loadDemo() {
  const today = logicalToday();
  const now = new Date();
  const minutesAgo = m => localIso(new Date(now.getTime() - m * 60000));
  const ids = Array.from({ length: 9 }, () => uuid());

  state.view = {
    Version: 1,
    GeneratedAt: minutesAgo(3),
    Today: today,
    DayStartHour: 4,
    Routines: [
      { Id: ids[0], Title: '물 2L 마시기', Done: true, Streak: 12 },
      { Id: ids[1], Title: '스트레칭 10분', Done: false, Time: '22:00:00', Streak: 4 },
      { Id: ids[2], Title: '하루 정산 기록', Done: false, Streak: 0 }
    ],
    Tasks: [
      { Id: ids[3], Title: '분기 매출 정리해서 메일 보내기', Done: false, Priority: 'High', Due: today, Time: '15:00:00' },
      { Id: ids[4], Title: '택배 반품 접수', Done: false, Priority: 'Medium', Due: today, CarryOverCount: 2 },
      { Id: ids[5], Title: '치과 예약 전화', Done: false, Priority: 'None' },
      { Id: ids[6], Title: '은행 서류 제출', Done: true, Priority: 'None', Due: today }
    ],
    Upcoming: [
      { Id: ids[8], Title: '월말 정산 자료 준비', Done: false, Priority: 'Medium', Due: addDays(today, 2) }
    ],
    AppliedOps: []
  };
  state.pending = [
    { Id: ids[7], Kind: 'add', Target: null, Text: '퇴근길에 우유 사기', At: minutesAgo(1), Day: today }
  ];
  state.phase = 'today';
}

if (DEMO) {
  loadDemo();
} else {
  restoreToken();
  state.phase = state.view?.Today ? 'today' : (local.get('connected', false) ? 'waiting-pc' : 'signin');
}

render();
refresh();
