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

function isQuotaError(error) {
  return error?.name === 'QuotaExceededError' || error?.name === 'NS_ERROR_DOM_QUOTA_REACHED' || error?.code === 22 || error?.code === 1014;
}

const local = DEMO ? {
  get(key, fallback) { return fallback; },
  set() { return true; },
  remove() {}
} : {
  get(key, fallback) {
    try {
      const raw = localStorage.getItem('flow.' + key);
      return raw == null ? fallback : JSON.parse(raw);
    } catch { return fallback; }
  },
  /**
   * 남겼으면 true. 자리가 모자라면(큰 메모 글·임시 글) 다시 받으면 되는 PC 화면(view)을 비우고 한 번 더 해 본다.
   * 그래도 못 남기면(사생활 모드 등) false — 부른 쪽이 '남겼다'고 믿고 글을 버리지 않게.
   */
  set(key, value) {
    const raw = JSON.stringify(value);
    try {
      localStorage.setItem('flow.' + key, raw);
      return true;
    } catch (error) {
      if (key === 'view' || !isQuotaError(error)) return false;
      try {
        localStorage.removeItem('flow.view');
        localStorage.setItem('flow.' + key, raw);
        return true;
      } catch { return false; }
    }
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
  relay: DEMO ? null : local.get('relay', null), // 로그인 없이 쓰기: { url, key }
  editing: null,                       // 고치기 창에 띄운 항목의 Id
  memoMenu: null,                      // … 메뉴를 띄운 메모의 Id
  confirmDelete: false,                // 삭제를 한 번 눌렀다
  fetchedAt: local.get('fetchedAt', 0),
  tab: local.get('tab', 'today') === 'memo' ? 'memo' : 'today', // 위 탭(오늘 · 메모). 연 메모는 기억하지 않는다
  memoOpen: null,                      // 읽기 화면에 띄운 메모의 Id
  memoFrom: 'memo',                    // 메모를 연 탭(오늘의 📝 · 메모 목록). ‹ 가 그리로 돌아간다
  draft: '',                           // 빠른 추가 칸에 적던 글. 탭을 바꾸거나 메모를 열었다 와도 남는다
  memoSearch: '',                      // 메모 탭 검색칸
  memoTag: null,                       // 메모 탭에서 고른 태그 칩
  memoListScroll: 0                    // 메모를 열 때 연 화면이 내려가 있던 자리. 돌아오면 그 자리로
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

/** 드라이브와 주고받을 수 있는지. 중계를 쓰면 열쇠가 필요 없으니 언제나 그렇다. */
function hasToken() {
  return DEMO || !!state.relay || (state.token && Date.now() < state.tokenExpires - 60_000);
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
  if (state.relay) return null;        // 중계가 폴더와 파일을 찾는다
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

// ───────── 중계 (로그인 없이 쓰기)
//
// 내 구글 계정에 둔 Apps Script(docs/relay.gs)가 '나'로 돌며 드라이브 파일을 대신 읽고 쓴다.
// 폰은 그 주소와 KEY 만 알면 된다. 구글 로그인 창이 뜰 일이 없다.

const RELAY_ERRORS = {
  key: '중계 열쇠가 맞지 않습니다',
  parse: '중계 응답을 못 읽었습니다 — 배포의 액세스 권한이 \'모든 사용자\'인지 확인하세요'
};

async function relayCall(relay, body) {
  const response = body
    ? await fetch(relay.url, {
        method: 'POST',
        // text/plain 이어야 브라우저가 사전 확인(CORS preflight) 없이 보낸다. Apps Script 는 그걸 못 받는다.
        headers: { 'Content-Type': 'text/plain;charset=utf-8' },
        body: JSON.stringify({ key: relay.key, ...body }),
        cache: 'no-store'
      })
    : await fetch(`${relay.url}?key=${encodeURIComponent(relay.key)}&t=${Date.now()}`, { cache: 'no-store' })
        .catch(() => { throw new Error('중계에 닿지 못했습니다 — 액세스 권한이 \'모든 사용자\'인지, 권한 승인을 끝냈는지 확인하세요'); });

  if (!response.ok) throw new Error('중계 ' + response.status + (response.status === 404 ? ' — 주소가 틀렸거나 배포가 지워졌습니다' : ''));
  let json;
  try { json = await response.json(); } catch { throw new Error(RELAY_ERRORS.parse); }
  if (!json?.ok) throw new Error(RELAY_ERRORS[json?.error] ?? ('중계: ' + (json?.error || '알 수 없음')));
  return json;
}

function applyView(view) {
  if (!view?.Today) return;
  state.view = view;
  state.fetchedAt = Date.now();
  local.set('view', view);
  local.set('fetchedAt', state.fetchedAt);
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
/** inbox 를 올리는 중인지. 올리는 중인 일은 이미 저쪽에 닿았을 수 있어 합치지(queueMemoSave) 않는다. */
let uploading = 0;

async function syncInbox(remoteGiven) {
  const rev = state.rev;
  const applied = new Set(state.view?.AppliedOps ?? []);
  // 중계는 받은 것을 저쪽에서 합치므로 먼저 읽어 올 필요가 없다. 보내기만 하면 된다.
  const remote = remoteGiven ?? (state.relay ? null : await readInbox());

  const merged = mergeOps([remote ?? [], local.get('pending', []), state.pending], applied);
  state.pending = merged;
  local.set('pending', merged);

  let onDrive = merged.map(op => op.Id);
  if (!remote || !sameIds(remote, merged)) {
    uploading++;
    try {
      if (state.relay) {
        const json = await relayCall(state.relay, { ops: merged });
        onDrive = (json.inbox ?? []).map(op => op.Id);
      } else {
        await drive(`/upload/drive/v3/files/${state.ids.inbox}?uploadType=media`, {
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ Version: 1, Ops: merged })
        });
      }
    } finally {
      uploading--;
    }
  }

  // 여기까지 왔으면 이 일들은 드라이브에 있다. '아직 폰에만 있음'과 'PC가 받기를 기다림'을 가르는 표시.
  state.sent = new Set(onDrive);
  local.set('sent', [...state.sent]);
  pruneMemoDrafts();   // 자리가 모자라 남겨 둔 저장 글이 이제 드라이브에 갔으면 버린다

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
      if (state.relay) {
        // 한 번에 화면과 inbox 를 같이 받는다.
        const json = await relayCall(state.relay, null);
        applyView(json.view);
        await syncInbox(json.inbox ?? []);
      } else {
        await withLink(async () => {
          applyView(await readView());

          // 새로고침할 때마다 inbox 를 맞춘다. PC가 받아 간 것은 빠지고,
          // 이 폰에 남아 있는데 드라이브에 없는 것(전에 못 보낸 것)은 이때 다시 간다.
          await syncInbox();
        });
      }

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
    if (state.relay) await syncInbox();
    else await withLink(() => syncInbox());
    state.error = null;
  } catch (error) {
    state.error = error instanceof AuthError ? 'auth' : 'net';
    state.errorDetail = error instanceof AuthError ? '' : String(error?.message || error);
  }
  render();
}

/**
 * 폰에서 한 일 하나를 쌓는다. 화면에는 바로 반영하고, 드라이브에는 모아서 보낸다.
 * extra 는 갈래마다 더 붙는 칸(메모 체크의 Line · Checked).
 */
function pushOp(kind, target, text, extra) {
  const now = new Date();
  let at = localIso(now);

  // 메모 일은 1초 안에 여럿이 생긴다(체크했다 바로 풀기, 새 메모를 만들고 바로 연결, 저장 두 번).
  // PC 는 At 순으로 넣고 같은 초면 번호 순이라 거꾸로 들어갈 수 있다. 앞의 메모 일보다 언제나 뒤의 시각을 쓴다.
  if (isMemoOp({ Kind: kind })) {
    const last = state.pending.filter(isMemoOp).map(op => op.At).sort().pop();
    if (last && last >= at) at = localIso(new Date(new Date(last).getTime() + 1000));
  }

  const op = {
    Id: uuid(),
    Kind: kind,
    Target: target ?? null,
    Text: text ?? null,
    At: at,
    Day: logicalToday(now),
    ...(extra ?? {})
  };
  state.pending.push(op);
  return { op, saved: commitPending() };
}

/** 쌓인 일을 이 폰에 남기고 보낼 차례로 둔다. 남기지 못했으면(자리 모자람) false — 일은 메모리에 있어 연결되면 간다. */
function commitPending() {
  state.rev++;
  state.dirty = true;
  const saved = local.set('pending', state.pending);
  local.set('dirty', true);
  render();
  scheduleFlush();
  return saved;
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
  const deleted = new Set();

  for (const op of [...state.pending].sort((a, b) => a.At.localeCompare(b.At))) {
    if (applied.has(op.Id)) continue;

    if (op.Kind === 'add') {
      const item = { Id: op.Id, Title: op.Text, Done: false, kind: 'task', waiting: true, Priority: 'None' };
      tasks.push(item);
      byId.set(op.Id, item);
    } else {
      const item = byId.get(op.Target);
      if (!item) continue;

      if (op.Kind === 'done' || op.Kind === 'undone') item.Done = op.Kind === 'done';
      else if (op.Kind === 'rename') item.Title = op.Text;
      else if (op.Kind === 'priority') item.Priority = op.Text;
      else if (op.Kind === 'delete') deleted.add(item.Id);
      else if (op.Kind === 'memo-link') item.MemoId = op.Text || null;   // 빈 글은 끊기
      else continue;

      item.waiting = true;
    }
  }

  if (deleted.size) {
    const keep = item => !deleted.has(item.Id);
    routines = routines.filter(keep);
    tasks = tasks.filter(keep);
    upcoming = upcoming.filter(keep);
  }

  // 예정에서 체크한 것은 오늘 끝낸 것이 되므로 오늘 쪽 '완료됨'으로 옮긴다.
  tasks.push(...upcoming.filter(t => t.Done));
  upcoming = upcoming.filter(t => !t.Done);

  return { today: stale ? today : view.Today, stale, routines, tasks, upcoming };
}

// ───────── 메모
//
// 글은 PC 가 phone-view.json 의 Memos 에 통째로 넣어 준다. 폰은 쓰고 고친 것을 '일'로 보낸다:
//   memo-save(본문 전체, Base = 보고 고친 판) · memo-check · memo-pin · memo-daily · memo-uncheck-all · memo-delete · memo-link
// 줄 나누기·검색·태그·판·서식 단추는 PC 의 MemoFormat · MemoQuery · MemoEditing 과 같은 규칙이어야 한다. 한쪽을 바꾸면 여기도 맞출 것.

const MEMO_RX = {
  lineBreak: /\r\n|\n|\r/,
  heading2: /^##\s+/,
  heading1: /^#\s+/,
  check: /^[ \t]*(?:-\s+)?\[([ xX]?)\]\s?/,
  numbered: /^[ \t]*(\d{1,3}\.)\s+/,
  bullet: /^[ \t]*[-*]\s+/,
  indent: /^[ \t]*/,
  // 목록에 보일 때 떼는 줄 머리 기호(MemoFormat.PlainLine)
  plainCheck: /^(?:[-*]\s+)?\[[ xX]?\](?:\s+|$)/,
  plainMark: /^(?:#{1,2}|\d{1,3}\.|[-*])(?:\s+|$)/
};

/** 줄 앞 공백·탭의 들여쓰기 단계. 2칸이 한 단계, 탭은 4칸, 많아도 6단계. */
function memoIndent(line) {
  let columns = 0;
  for (const ch of MEMO_RX.indent.exec(line)[0]) columns += ch === '\t' ? 4 : 1;
  return Math.min(Math.floor(columns / 2), 6);
}

/** 한 줄의 갈래. MemoFormat.Classify 와 같은 순서로 본다(## 를 # 보다 먼저). */
function classifyMemoLine(index, line) {
  const make = (kind, text, extra) => ({ index, kind, text, number: null, checked: false, indent: 0, ...extra });
  if (!line.trim()) return make('blank', '');

  let m = MEMO_RX.heading2.exec(line);
  if (m) return make('h2', line.slice(m[0].length));
  m = MEMO_RX.heading1.exec(line);
  if (m) return make('h1', line.slice(m[0].length));

  m = MEMO_RX.check.exec(line);
  if (m) return make('check', line.slice(m[0].length), { checked: m[1] === 'x' || m[1] === 'X', indent: memoIndent(line) });

  m = MEMO_RX.numbered.exec(line);
  if (m) return make('numbered', line.slice(m[0].length), { number: m[1], indent: memoIndent(line) });

  m = MEMO_RX.bullet.exec(line);
  if (m) return make('bullet', line.slice(m[0].length), { indent: memoIndent(line) });

  return make('text', line);
}

function memoLines(text) {
  return String(text ?? '').split(MEMO_RX.lineBreak).map((line, i) => classifyMemoLine(i, line));
}

/** 줄 머리 기호를 뗀 한 줄(MemoFormat.PlainLine). */
function plainMemoLine(line) {
  const trimmed = line.trim();
  const check = MEMO_RX.plainCheck.exec(trimmed);
  if (check) return trimmed.slice(check[0].length).trim();
  const mark = MEMO_RX.plainMark.exec(trimmed);
  return mark ? trimmed.slice(mark[0].length).trim() : trimmed;
}

/** 목록의 흐린 첫머리: 제목 줄 다음 본문을 기호 떼고 한 줄로 접어 앞 80자(MemoFormat.PreviewOf). */
function memoPreview(text) {
  const lines = String(text ?? '').split(MEMO_RX.lineBreak);
  const first = lines.findIndex(l => l.trim());
  if (first < 0) return '';
  const folded = lines.slice(first + 1).map(plainMemoLine).join(' ').replace(/\s+/g, ' ').trim();
  if (folded.length <= 80) return folded;
  const cut = folded.slice(0, 80);
  return /[\uD800-\uDBFF]$/.test(cut) ? cut.slice(0, -1) : cut;
}

const lower = s => String(s ?? '').toLocaleLowerCase();
const bareTag = t => String(t ?? '').replace(/^#+/, '');

/** 고정 먼저, 최근에 고친 순, 같으면 제목 순(MemoQuery.Filter 의 정렬). */
function memoOrder(a, b) {
  return (b.Pinned ? 1 : 0) - (a.Pinned ? 1 : 0)
    || (Date.parse(b.Updated) || 0) - (Date.parse(a.Updated) || 0)
    || lower(a.Title).localeCompare(lower(b.Title), 'ko');
}

/**
 * 검색칸의 낱말이 모두 (제목 또는 본문)에 들어 있는 메모. 대소문자는 무시한다.
 * '#' 로 시작하는 낱말과 고른 태그 칩은 태그 조건이다(MemoQuery.Filter 와 같다).
 */
function filterMemos(memos, search, tag) {
  const words = [];
  const tags = [];
  for (const raw of String(search ?? '').split(/\s+/).filter(Boolean)) {
    if (raw.startsWith('#')) {
      const t = bareTag(raw);
      if (t) tags.push(lower(t));
    } else words.push(lower(raw));
  }
  const chosen = bareTag(String(tag ?? '').trim());
  if (chosen) tags.push(lower(chosen));

  return memos.filter(m => {
    const have = (m.Tags ?? []).map(t => lower(bareTag(t)));
    if (!tags.every(t => have.includes(t))) return false;
    const title = lower(m.Title);
    const body = lower(m.Text);
    return words.every(w => title.includes(w) || body.includes(w));
  }).sort(memoOrder);
}

/** 모든 태그(대소문자 무시로 합침). 메모 수 많은 순, 같으면 이름순(MemoQuery.AllTags). */
function allMemoTags(memos) {
  const counts = new Map();
  for (const m of memos) {
    const seen = new Set();
    for (const raw of m.Tags ?? []) {
      const name = bareTag(raw);
      const key = lower(name);
      if (!name || seen.has(key)) continue;
      seen.add(key);
      const entry = counts.get(key) ?? { name, count: 0 };
      entry.count++;
      counts.set(key, entry);
    }
  }
  return [...counts.values()]
    .sort((a, b) => b.count - a.count || lower(a.name).localeCompare(lower(b.name), 'ko'))
    .map(e => e.name);
}

/** PC 가 Memos 를 보내 주는지. 옛 PC 는 이 칸이 없다. */
function memosOfView() {
  return Array.isArray(state.view?.Memos) ? state.view.Memos : null;
}

/** 체크 일이 가리키는 줄: Line 줄이 같은 글의 체크 줄이면 그 줄, 아니면 같은 글의 첫 체크 줄(PC 와 같다). */
function findCheckLine(lines, line, text) {
  const want = String(text ?? '').trim();
  const matches = l => l.kind === 'check' && l.text.trim() === want;
  return (Number.isInteger(line) && lines[line] && matches(lines[line]) ? lines[line] : null) ?? lines.find(matches) ?? null;
}

/** 메모 갈래의 일인지(memo-check · memo-save · memo-pin · memo-daily · memo-uncheck-all · memo-delete · memo-link). */
function isMemoOp(op) {
  return String(op?.Kind ?? '').startsWith('memo-');
}

const MEMO_UNTITLED = '제목 없음';

/** 한 번에 보낼 수 있는 본문 크기(UTF-8). 본문 전체가 inbox 에 들어가 중계·드라이브를 거치므로 넉넉히 막는다. */
const MEMO_MAX_BYTES = 200 * 1024;

/** 목록·제목 줄에 보일 제목(MemoFormat.PlainTitle(TitleOf)): 첫 비어 있지 않은 줄에서 기호를 뗀 것. */
function memoTitleOf(text) {
  const first = String(text ?? '').split(MEMO_RX.lineBreak).find(l => l.trim());
  if (first == null) return MEMO_UNTITLED;
  return plainMemoLine(first) || first.trim();
}

/**
 * 본문의 #태그(MemoFormat.TagsOf): 나온 순서, 중복 없이, # 뺀 이름.
 * 앞 글자는 잡아서 버린다 — 뒤돌아보기 정규식(lookbehind)은 iOS 16.4 보다 옛 사파리가 읽지 못해 앱 전체가 안 뜬다.
 */
function memoTagsOf(text) {
  return [...new Set([...String(text ?? '').matchAll(/(^|\s)#([^\s#]+)/gm)].map(m => m[2]))];
}

/** 체크 칸 하나를 바꾼 줄(MemoFormat.SetMark). 체크 줄이 아니면 null. */
function setMemoMark(line, checked) {
  const m = /^([ \t]*(?:-\s+)?\[)([ xX]?)\]/.exec(line);
  if (!m) return null;
  if (!m[2] && !checked) return line;   // [] 는 이미 빈 칸
  return line.slice(0, m[1].length) + (checked ? 'x' : ' ') + line.slice(m[1].length + m[2].length);
}

/** 그 줄의 체크를 바꾼 본문(MemoFormat.SetChecked). 줄바꿈 문자는 그대로 둔다. */
function setMemoChecked(text, index, checked) {
  const parts = String(text ?? '').split(/(\r\n|\n|\r)/);
  const changed = index >= 0 && index * 2 < parts.length ? setMemoMark(parts[index * 2], checked) : null;
  if (changed == null) return text;
  parts[index * 2] = changed;
  return parts.join('');
}

/** 체크를 모두 푼 본문(MemoFormat.UncheckAll). */
function uncheckAllMemo(text) {
  const parts = String(text ?? '').split(/(\r\n|\n|\r)/);
  for (let i = 0; i < parts.length; i += 2) parts[i] = setMemoMark(parts[i], false) ?? parts[i];
  return parts.join('');
}

/**
 * 본문의 판(MemoFormat.RevOf): 줄바꿈을 \n 으로 맞춘 UTF-8 의 SHA-256, 16진 소문자 앞 16자.
 * 웹 암호(https 에서만 있음)가 없으면 null.
 */
async function memoRev(text) {
  if (!globalThis.crypto?.subtle) return null;
  const normalized = String(text ?? '').replace(/\r\n/g, '\n').replace(/\r/g, '\n');
  const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(normalized));
  return [...new Uint8Array(hash)].slice(0, 8).map(b => b.toString(16).padStart(2, '0')).join('');
}

function utf8Bytes(text) {
  return new TextEncoder().encode(String(text ?? '')).length;
}

let memoCache = null;

/**
 * 화면에 보일 메모들 = PC 가 보낸 메모 + 아직 PC 가 못 받은 메모 일(composeView 와 같은 생각).
 * 일은 PC 가 넣는 순서(At)대로 얹는다: 새 메모는 목록에, 고친 글은 그 메모에, 체크·모두 풀기는 그 줄에,
 * 고정·매일은 표시에, 삭제는 목록에서 뺀다. PC 가 받아 간 일(AppliedOps)은 얹지 않는다 —
 * 넣었든 건너뛰었든 충돌 사본을 만들었든 PC 의 글이 맞다(충돌 사본은 따로 한 메모로 온다).
 * PC 가 꺼진 채 하루가 지났으면, 매일 초기화 메모는 PC 가 켜지면 풀릴 체크를 미리 풀어 보여 준다.
 * 메모마다: Text(얹은 글) · lines · preview · waiting(못 받은 일 수) · waitOther(그중 체크가 아닌 일 — 체크는 줄마다 따로 보인다)
 * · edited(고친 글이 기다림) · created(폰에서 만든 새 메모).
 */
function composeMemos() {
  const memos = memosOfView();
  if (!memos) return null;
  const today = logicalToday();
  const c = memoCache;
  if (c && c.view === state.view && c.pending === state.pending && c.count === state.pending.length && c.today === today) return c.list;

  const stale = !!state.view?.Today && state.view.Today < today;
  const applied = new Set(state.view?.AppliedOps ?? []);
  const byId = new Map();
  for (const memo of memos) {
    const preview = stale && !!memo.Daily && (!memo.Reset || memo.Reset < today);
    byId.set(memo.Id, {
      ...memo, Text: preview ? uncheckAllMemo(memo.Text) : memo.Text,
      preview, waiting: 0, waitOther: 0, edited: false, created: false, deleted: false, waitLines: new Set()
    });
  }

  const ops = state.pending
    .filter(op => isMemoOp(op) && op.Kind !== 'memo-link' && !applied.has(op.Id))
    .sort((a, b) => a.At.localeCompare(b.At) || a.Id.localeCompare(b.Id));

  for (const op of ops) {
    let memo = byId.get(op.Target);
    if (op.Kind === 'memo-save') {
      if (typeof op.Text !== 'string') continue;
      if (!memo) {
        memo = { Id: op.Target, Pinned: false, Daily: false, Reset: null, Tags: [], Rev: '', Text: '', preview: false,
          waiting: 0, waitOther: 0, edited: false, created: true, deleted: false, waitLines: new Set() };
        byId.set(op.Target, memo);
      }
      // 머리말 태그(본문에 없는 태그)는 그대로 두고 본문 #태그만 새 글로 바꾼다.
      const oldBody = new Set(memoTagsOf(memo.Text).map(lower));
      const front = (memo.Tags ?? []).filter(t => !oldBody.has(lower(bareTag(t))));
      const seen = new Set(front.map(t => lower(bareTag(t))));
      memo.Tags = [...front, ...memoTagsOf(op.Text).filter(t => !seen.has(lower(t)))];
      memo.Text = op.Text;
      memo.Title = memoTitleOf(op.Text);
      memo.Updated = op.At;
      memo.edited = true;
      memo.deleted = false;
      memo.waitLines.clear();
      memo.waiting++;
      memo.waitOther++;
      continue;
    }
    if (!memo || memo.deleted) continue;

    switch (op.Kind) {
      case 'memo-check': {
        if (memo.Daily) {
          // PC 는 초기화보다 앞선 날의 체크를 건너뛴다. 미리 풀어 보이는 중이면 오늘 누른 것만 남는다.
          if (memo.preview && op.Day < today) continue;
          if (memo.Reset && op.Day < memo.Reset) continue;
        }
        const line = findCheckLine(memoLines(memo.Text), op.Line, op.Text);
        if (!line) continue;
        memo.Text = setMemoChecked(memo.Text, line.index, !!op.Checked);
        memo.waitLines.add(line.index);
        break;
      }
      case 'memo-pin': memo.Pinned = op.Text === 'true'; break;
      case 'memo-daily': {
        const on = op.Text === 'true';
        if (on && !memo.Daily) memo.Reset = op.Day;   // 켠 날은 안 풀린다(PC 와 같다)
        memo.Daily = on;
        break;
      }
      case 'memo-uncheck-all': memo.Text = uncheckAllMemo(memo.Text); break;
      case 'memo-delete': memo.deleted = true; break;
      default: continue;   // 이 폰이 모르는 메모 일
    }
    memo.waiting++;
    if (op.Kind !== 'memo-check') memo.waitOther++;
  }

  const list = [];
  for (const memo of byId.values()) {
    if (memo.deleted) continue;
    memo.lines = memoLines(memo.Text);
    for (const i of memo.waitLines) if (memo.lines[i]) memo.lines[i].waiting = true;
    list.push(memo);
  }
  memoCache = { view: state.view, pending: state.pending, count: state.pending.length, today, list };
  return list;
}

/** 화면에 보일 메모 하나(얹은 일 포함). 지웠거나 없으면 null. */
function findMemo(id) {
  return composeMemos()?.find(m => m.Id === id) ?? null;
}

/** PC 가 보낸 그대로의 메모(얹은 일 없음). */
function sentMemo(id) {
  return memosOfView()?.find(m => m.Id === id) ?? null;
}

/**
 * 고치기를 시작할 때의 판(memo-save 의 Base). 얹은 일이 없으면 PC 가 보낸 판 그대로,
 * 있으면 보이는 글(= PC 가 이 일에 닿을 때 가질 글)의 판을 계산한다 — 저장을 두 번 하면 두 번째 Base 는 첫 글의 판.
 */
async function memoBaseOf(memo) {
  const sent = sentMemo(memo.Id);
  if (sent && sent.Text === memo.Text && sent.Rev) return sent.Rev;
  // 판을 못 계산하면(웹 암호가 없는 http) 맞지 않는 판을 보낸다 — PC 는 덮지 않고 충돌 사본으로 남긴다.
  // null 을 보내면 '새 메모'로 읽혀, 이미 있는 메모라 건너뛰어 글이 사라진다.
  return await memoRev(memo.Text) ?? sent?.Rev ?? '';
}

/** PC 메모 창이 지금 쓰는 중으로 들고 있는 메모인지. 그 메모의 일은 PC 에서 쓰기를 마쳐야 들어간다. */
function isHeldMemo(id) {
  return Array.isArray(state.view?.HeldMemos) && state.view.HeldMemos.includes(id);
}

/** 대기 표시. PC 가 쓰는 중인 메모면 '1분 안에'가 아니라 쓰기를 마치면 들어간다고 알린다. */
function waitBadge(memoId) {
  return isHeldMemo(memoId)
    ? '<span class="badge wait held">PC에서 쓰는 중</span>'
    : '<span class="badge wait">PC 반영 대기</span>';
}

// ───────── 그리기

const ICON = {
  check: '<svg viewBox="0 0 16 16" fill="none" stroke="#17171A" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M4.5 8.5 7 11l4.5-5.5"/></svg>',
  flame: '<svg viewBox="0 0 16 16" fill="currentColor"><path d="M8 1.4c1.2 3.2 4 4.8 4 8a4 4 0 0 1-8 0c0-2 1.2-3.2 1.8-4.8.6 1.4 1.4 1 1.6 0 .2-1 0-2 .6-3.2Z"/></svg>',
  refresh: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 11a8 8 0 1 0-2.3 5.7"/><path d="M20 4v7h-7"/></svg>',
  plus: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 5v14"/><path d="M5 12h14"/></svg>',
  more: '<svg viewBox="0 0 24 24" fill="currentColor"><circle cx="5" cy="12" r="1.8"/><circle cx="12" cy="12" r="1.8"/><circle cx="19" cy="12" r="1.8"/></svg>',
  send: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 19V5"/><path d="m5 12 7-7 7 7"/></svg>',
  chevron: '<svg viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="m3.5 2 3 3-3 3"/></svg>',
  drive: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3h8l6 10-4 7H6l-4-7z"/><path d="m8 3 6 10H2"/><path d="m16 3-6 10 4 7"/></svg>'
};

const app = document.getElementById('app');

function render() {
  // 설정 화면은 적는 칸이 있어 30초마다 다시 그리면 적던 것이 날아간다. 들어갈 때 한 번만 그린다.
  if (state.phase === 'setup') { if (!app.querySelector('.setup')) renderSetup(); return; }
  if (state.phase === 'signin' || (state.phase === 'boot' && !state.view)) return renderSignin();
  if (state.phase === 'waiting-pc' && !state.view?.Today) return renderWaiting();
  if (state.tab === 'memo') return state.memoOpen ? renderMemoView() : renderMemoList();
  renderToday();
}

/** 위의 '오늘 | 메모' 탭. */
function tabsHtml(active) {
  const tab = (name, label) =>
    `<button class="tab${active === name ? ' on' : ''}" role="tab" aria-selected="${active === name}" data-act="tab" data-tab="${name}">${label}</button>`;
  return `<nav class="tabs" role="tablist">${tab('today', '오늘')}${tab('memo', '메모')}</nav>`;
}

function renderSignin() {
  app.innerHTML = `
    <div class="center">
      <div class="brand"><img src="icon-192.png" alt=""><b>Flow</b></div>
      <p class="lead">PC의 Flow와 이어서 씁니다.</p>
      <p class="sub">오늘 할 일을 보고, 체크하고, 생각난 것을 적어 두면 PC가 받아 넣습니다.</p>
      <button class="primary" data-act="connect">${ICON.drive}구글 계정으로 연결</button>
      <button class="secondary" data-act="setup">로그인 없이 쓰기 (중계 설정)</button>
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
    // PC 가 그 메모를 쓰는 중이면(쓰기 모드) 1분이 지나도 안 들어간다. 왜 기다리는지 말해 준다.
    const held = open.filter(op => state.sent.has(op.Id) && isMemoOp(op) && op.Kind !== 'memo-link' && isHeldMemo(op.Target)).length;
    const when = held === waiting ? 'PC에서 쓰는 중 · 쓰기를 마치면 들어갑니다'
      : held > 0 ? `PC가 켜져 있으면 1분 안에 · ${held}개는 PC에서 쓰는 중 · 쓰기를 마치면 들어갑니다`
      : 'PC가 켜져 있으면 1분 안에';
    return `<div class="status pending"><span class="dot"></span>PC 반영 대기 ${waiting}개 · ${when}</div>`;
  }
  return `<div class="status live"><span class="dot"></span>${generated || '최신'}</div>`;
}

/** 맨 아래 작은 글씨. 문제가 생겼을 때 캡처 한 장으로 사정을 알 수 있게 한다. */
function diagnostics() {
  const standalone = matchMedia('(display-mode: standalone)').matches || navigator.standalone;
  const minutes = hasToken() && !DEMO ? Math.max(0, Math.round((state.tokenExpires - Date.now()) / 60000)) : 0;
  const parts = [
    'v13',
    standalone ? '앱' : '브라우저',
    DEMO ? '보기용' : state.relay ? '중계' : (hasToken() ? `연결 ${minutes}분 남음` : '연결 끊김'),
    `대기 ${state.pending.length}`
  ];
  const relayLink = DEMO ? '' : state.relay
    ? '<button class="link" data-act="relay-off">로그인 없이 쓰는 중 · 끄기</button>'
    : '<button class="link" data-act="setup">로그인 창이 번거로우면 · 로그인 없이 쓰기</button>';
  return `<div class="hint">길게 누르면 이름·중요도를 고치거나 메모를 연결하거나 지울 수 있습니다</div>${relayLink}<div class="diag">${parts.join(' · ')}</div>`;
}

/** PC가 보내 온 화면에 이미 있는 항목인지. 없으면 폰에서 적고 아직 PC가 못 받은 것이다. */
function inView(id) {
  const view = state.view;
  return !!view && [...(view.Routines ?? []), ...(view.Tasks ?? []), ...(view.Upcoming ?? [])].some(i => i.Id === id);
}

function rowHtml(item) {
  const classes = ['row'];
  if (item.Done) classes.push('done');
  if (item.waiting) classes.push('waiting');

  const ring = item.kind === 'task' && !item.Done
    ? { High: 'high', Medium: 'medium', Low: 'low' }[item.Priority] ?? ''
    : '';

  const meta = [];
  if (item.waiting && item.kind === 'task' && item.Id && !inView(item.Id)) {
    meta.push('<span class="badge wait">PC 반영 대기</span>');
  }
  if (item.MemoId && findMemo(item.MemoId)) {
    // 줄 안의 작은 단추. 누르면 메모를 연다 — 줄 체크·길게 누르기와는 따로 논다(누르기 처리에서 가른다).
    meta.push(`<span class="memo-link" role="button" aria-label="메모 열기" data-act="memo-open" data-memo="${esc(item.MemoId)}">📝</span>`);
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

  const draft = state.draft;
  const hadFocus = document.activeElement?.id === 'compose';

  app.innerHTML = `
    <header class="head">
      ${tabsHtml('today')}
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
    </div>`;

  if (hadFocus) {
    const input = document.getElementById('compose');
    input.focus();
    input.setSelectionRange(input.value.length, input.value.length);
  }
}

// ───────── 메모 탭 · 읽기 화면

/** 메모 화면 맨 아래 작은 글씨(오늘 화면의 진단 줄과 같은 것). */
function memoDiag() {
  const parts = ['v13', DEMO ? '보기용' : state.relay ? '중계' : (hasToken() ? '연결' : '연결 끊김'), `대기 ${state.pending.length}`];
  return `<div class="diag">${parts.join(' · ')}</div>`;
}

/**
 * 메모 목록. 검색칸은 처음 한 번만 그리고 그 아래만 다시 그린다 —
 * 30초마다 통째로 다시 그리면 적던 검색어(한글 조합 중인 글자)가 날아간다.
 */
function renderMemoList() {
  if (!app.querySelector('.memo-page')) {
    app.innerHTML = `
      <header class="head memo-page">
        ${tabsHtml('memo')}
        <div class="head-row search-row">
          <input id="memo-search" type="search" enterkeyhint="search" autocomplete="off" spellcheck="false"
            placeholder="검색 — 제목·본문, #태그" value="${esc(state.memoSearch)}">
          <button class="icon-btn plus-btn" data-act="memo-new" aria-label="새 메모"${memosOfView() ? '' : ' hidden'}>${ICON.plus}</button>
          <button class="icon-btn" data-act="refresh" aria-label="새로고침">${ICON.refresh}</button>
        </div>
        <div id="memo-status"></div>
      </header>
      <main class="list memo-list" id="memo-body"></main>`;
  }
  updateMemoList();
}

function updateMemoList() {
  const body = document.getElementById('memo-body');
  if (!body) return;
  app.querySelector('.memo-page [data-act="refresh"]')?.classList.toggle('spinning', state.busy);
  document.getElementById('memo-status').innerHTML = statusLine();

  const memos = composeMemos();
  app.querySelector('.memo-page [data-act="memo-new"]')?.toggleAttribute('hidden', !memos);
  if (!memos) {
    body.innerHTML = `<div class="empty">PC의 Flow를 1.8.0 이상으로 올리면<br>메모가 여기에 보입니다.</div>${memoDiag()}`;
    return;
  }

  const tags = allMemoTags(memos);
  if (state.memoTag && !tags.some(t => lower(t) === lower(state.memoTag))) state.memoTag = null;
  const found = filterMemos(memos, state.memoSearch, state.memoTag);
  const searching = !!state.memoSearch.trim() || !!state.memoTag;

  const chips = tags.length ? `
    <div class="chips">${tags.map(t => {
      const on = state.memoTag && lower(t) === lower(state.memoTag);
      return `<button class="chip${on ? ' on' : ''}" data-act="memo-tag" data-tag="${esc(t)}">#${esc(t)}</button>`;
    }).join('')}</div>` : '';

  const rows = found.map(m => {
    const preview = memoPreview(m.Text);
    return `
      <button class="memo-row" data-act="memo-open" data-memo="${esc(m.Id)}">
        <span class="memo-row-head">
          ${m.Pinned ? '<span class="pin" aria-label="고정">📌</span>' : ''}
          <span class="memo-row-title">${esc(m.Title)}</span>
          ${m.Daily ? '<span class="daily" title="매일 초기화">↻</span>' : ''}
          ${m.waiting ? waitBadge(m.Id) : ''}
        </span>
        ${preview ? `<span class="memo-row-preview">${esc(preview)}</span>` : ''}
      </button>`;
  }).join('');

  const label = searching ? `찾은 메모 ${found.length}개` : `메모 ${memos.length}개`;
  const empty = !memos.length
    ? '<div class="empty">아직 메모가 없습니다.<br>오른쪽 위 + 를 누르거나 PC의 메모 창에서 쓰면 여기에 보입니다.</div>'
    : !found.length ? '<div class="empty">찾는 메모가 없습니다.</div>' : '';

  body.innerHTML = `${chips}${memos.length ? `<div class="section-label">${label}</div>` : ''}${rows}${empty}${memoDiag()}`;
}

/** 읽기 화면 한 줄. PC 읽기 모드와 같은 모양: 소제목 · 번호 · 점 · 체크 · 보통 글, 들여쓰기. */
function memoLineHtml(line, memoId) {
  const indent = line.indent ? ` style="--indent:${line.indent}"` : '';
  switch (line.kind) {
    case 'blank': return '<div class="m-blank"></div>';
    case 'h1': return `<div class="m-h1">${esc(line.text)}</div>`;
    case 'h2': return `<div class="m-h2">${esc(line.text)}</div>`;
    case 'numbered':
      return `<div class="m-item"${indent}><span class="m-mark m-num">${esc(line.number)}</span><span class="m-body">${esc(line.text)}</span></div>`;
    case 'bullet':
      return `<div class="m-item"${indent}><span class="m-mark m-dot">•</span><span class="m-body">${esc(line.text)}</span></div>`;
    case 'check':
      return `
        <button class="m-item m-check${line.checked ? ' on' : ''}${line.waiting ? ' waiting' : ''}"${indent}
          role="checkbox" aria-checked="${line.checked}" data-act="memo-check" data-line="${line.index}">
          <span class="m-mark"><span class="m-box">${ICON.check}</span></span>
          <span class="m-body">${esc(line.text)}</span>
          ${line.waiting ? waitBadge(memoId) : ''}
        </button>`;
    default: return `<div class="m-text">${esc(line.text)}</div>`;
  }
}

/** 메모 읽기 화면. 30초마다 다시 그려도 읽던 자리(스크롤)는 그대로 둔다. */
function renderMemoView() {
  const memo = findMemo(state.memoOpen);
  const y = window.scrollY;

  let main;
  if (!memo) {
    main = '<div class="empty">이 메모는 이제 PC에 없습니다.<br>지워졌거나 휴지통으로 갔습니다.</div>';
  } else {
    // 첫 비어 있지 않은 줄은 제목으로 위에 따로 보인다. 제목 바로 아래 빈 줄은 뺀다(PC 와 같다).
    const titleAt = memo.lines.findIndex(l => l.kind !== 'blank');
    const lines = titleAt < 0 ? [] : memo.lines.slice(titleAt + 1);
    while (lines.length && lines[0].kind === 'blank') lines.shift();

    const meta = [];
    // 체크 말고도 기다리는 일(고친 글 · 새 메모 · 고정 · 매일 · 모두 풀기)이 있으면 위에 한 번 알린다.
    if (memo.waitOther > 0) meta.push(waitBadge(memo.Id));
    if (memo.Pinned) meta.push('<span class="chip static">📌 고정</span>');
    if (memo.Daily) meta.push(`<span class="chip static daily">↻ 매일 초기화${memo.preview ? ' · 새 하루라 풀어 보임' : ''}</span>`);
    for (const t of memo.Tags ?? []) meta.push(`<span class="chip static">#${esc(bareTag(t))}</span>`);

    const held = memo.waiting && isHeldMemo(memo.Id)
      ? '<div class="memo-note held">PC에서 쓰는 중 · 쓰기를 마치면 들어갑니다</div>' : '';

    main = `
      <h1 class="memo-title">${esc(memo.Title)}</h1>
      ${meta.length ? `<div class="chips memo-meta">${meta.join('')}</div>` : ''}
      ${held}
      <div class="memo-lines">${lines.map(l => memoLineHtml(l, memo.Id)).join('')}</div>`;
  }

  app.innerHTML = `
    <header class="head memo-head">
      <div class="head-row">
        <button class="back-btn" data-act="memo-back">‹ ${state.memoFrom === 'today' ? '오늘' : '메모'}</button>
        <span class="grow"></span>
        ${memo ? `<button class="text-btn" data-act="memo-edit">고치기</button>
        <button class="icon-btn" data-act="memo-menu" aria-label="메모 메뉴">${ICON.more}</button>` : ''}
        <button class="icon-btn${state.busy ? ' spinning' : ''}" data-act="refresh" aria-label="새로고침">${ICON.refresh}</button>
      </div>
      ${statusLine()}
    </header>
    <main class="memo-view" data-memo="${esc(state.memoOpen)}">${main}${memoDiag()}</main>`;

  window.scrollTo(0, y);
}

/**
 * 메모를 연다. 안드로이드 뒤로 단추로도 닫히게 기록을 하나 쌓는다.
 * 연 탭(오늘의 📝 · 메모 목록)을 기억해 ‹ 와 뒤로 단추가 그리로 돌아간다. 다시 열 때 볼 탭도 연 탭 그대로 둔다.
 */
function openMemo(id, fromList) {
  if (!state.memoOpen) {
    state.memoFrom = fromList || state.tab === 'memo' ? 'memo' : 'today';
    state.memoListScroll = window.scrollY;
  }
  state.memoOpen = id;
  state.tab = 'memo';
  closeEditor();
  if (history.state?.memo == null) history.pushState({ memo: id }, '');
  else history.replaceState({ memo: id }, '');
  app.innerHTML = '';
  render();
  window.scrollTo(0, 0);
}

function closeMemo() {
  if (!state.memoOpen) return;
  state.memoOpen = null;
  state.tab = state.memoFrom;
  local.set('tab', state.tab);
  app.innerHTML = '';
  render();
  window.scrollTo(0, state.memoListScroll);
}


let toastTimer = 0;

/**
 * 짧은 알림. #app 바깥에 하나만 두어 다시 그리기·화면 바꾸기·글 칸 위에서도 보인다.
 * warn 이면 붉게, 화면 폭 안에서 줄을 바꿔, 더 오래 보인다(놓치면 안 되는 것).
 */
function toast(message, warn) {
  let el = document.getElementById('toast');
  if (!el) {
    el = document.createElement('div');
    el.className = 'toast';
    el.id = 'toast';
    document.body.appendChild(el);
  }
  el.textContent = message;
  el.classList.toggle('warn', !!warn);
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), warn ? 5000 : 2200);
}

// ───────── 로그인 없이 쓰기 설정
//
// 한 번만 한다. PC 브라우저에서 하는 게 편하다(스크립트를 붙여넣어야 하니까).
// 끝나면 '연결 링크'가 나오고, 폰에서 그 링크를 열면 폰도 같은 중계를 쓴다.

const SCRIPT_NEW_URL = 'https://script.google.com/home/projects/create';
const QR_LIB = 'https://cdn.jsdelivr.net/npm/qrcode-generator@1.4.4/qrcode.min.js';

let relayScript = null;

function draftKey() {
  let key = local.get('relayDraftKey', null);
  if (!key) {
    const bytes = crypto.getRandomValues(new Uint8Array(18));
    key = btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    local.set('relayDraftKey', key);
  }
  return key;
}

function relayLink(relay) {
  return `${location.origin}${location.pathname}#relay=${encodeURIComponent(relay.url)}&key=${encodeURIComponent(relay.key)}`;
}

/** 붙여넣은 것: 연결 링크(…#relay=…&key=…) 또는 웹 앱 주소(…/exec). */
function parseRelayInput(text) {
  text = (text || '').trim();
  const hash = text.indexOf('#');
  if (hash >= 0 && text.includes('relay=')) {
    const params = new URLSearchParams(text.slice(hash + 1));
    const url = params.get('relay');
    const key = params.get('key');
    if (url && key) return { url, key };
  }
  // 회사 계정이면 …/a/macros/회사주소/s/…/exec 꼴이다. 뒤에 붙은 ?… 나 / 는 떼어 낸다.
  const match = text.match(/^https:\/\/script\.google\.com\/(?:a\/macros\/[^/\s]+|macros)\/s\/[\w-]+\/exec/);
  if (match) return { url: match[0], key: draftKey() };
  return null;
}

/** 붙여넣은 것이 왜 안 되는지 사람 말로. */
function explainRelayInput(text) {
  text = (text || '').trim();
  if (!text) return '웹 앱 URL 을 붙여넣어 주세요';
  if (/script\.google\.com\/.*\/dev\b/.test(text)) return '…/dev 주소는 테스트용입니다. 배포 → 새 배포 후 나오는 …/exec 주소를 붙여넣어 주세요';
  if (/script\.google\.com\/(home|d)\//.test(text)) return '이건 편집 화면 주소입니다. 배포 → 새 배포 → 배포를 누른 뒤 나오는 웹 앱 URL(…/exec)을 붙여넣어 주세요';
  if (!/^https?:\/\//.test(text)) return '배포 ID 가 아니라, 그 아래 \'웹 앱 URL\'(https://script.google.com/…/exec)을 복사해 주세요';
  return '…/exec 로 끝나는 웹 앱 URL 또는 연결 링크를 붙여넣어 주세요';
}

/** 폰에서 연결 링크로 열었을 때. 주소창에서 열쇠를 바로 지운다. */
function takeRelayFromLink() {
  if (!location.hash.includes('relay=')) return;
  const relay = parseRelayInput(location.href);
  history.replaceState(null, '', location.pathname + location.search);
  if (!relay) return;
  state.relay = relay;
  local.set('relay', relay);
  setTimeout(() => toast('로그인 없이 쓰기로 연결했습니다'), 300);
}

function renderSetup() {
  const key = draftKey();
  app.innerHTML = `
    <div class="setup">
      <button class="back" data-act="setup-back">${ICON.chevron}돌아가기</button>
      <h2>로그인 없이 쓰기</h2>
      <p class="sub">내 구글 계정 안에 작은 중계 프로그램을 하나 두면, 폰은 로그인 없이 그걸 통해 주고받습니다.
        처음 한 번만 하면 됩니다. <b>PC에서 하는 게 편합니다</b> — PC 브라우저로 이 주소를 여세요:
        <code>${esc(location.origin + location.pathname)}</code></p>

      <ol class="steps setup-steps">
        <li><button class="secondary" data-act="setup-copy">스크립트 복사</button>
          <details><summary>복사가 안 되면 여기서 직접</summary><textarea id="setup-script" readonly rows="6"></textarea></details></li>
        <li><a href="${SCRIPT_NEW_URL}" target="_blank" rel="noopener">새 Apps Script 프로젝트 열기</a> →
          원래 있던 글을 모두 지우고 붙여넣기 → 저장(💾 또는 Ctrl+S)</li>
        <li>오른쪽 위 <b>배포</b> → <b>새 배포</b> → 톱니바퀴 → <b>웹 앱</b><br>
          실행: <b>나</b> · 액세스 권한: <b>모든 사용자</b> → <b>배포</b></li>
        <li><b>액세스 승인</b> → 내 계정 선택 → "확인되지 않은 앱"이 뜨면 <b>고급</b> → <b>(제목 없는 프로젝트)(으)로 이동</b> → <b>허용</b><br>
          <span class="muted">내가 방금 만든 스크립트라서 뜨는 경고입니다. 이 스크립트는 'Flow 폰' 폴더의 두 파일만 씁니다.</span></li>
        <li>나온 <b>웹 앱 URL</b>(…/exec 로 끝남)을 아래에 붙여넣고 연결 확인</li>
      </ol>

      <input id="relay-input" class="setup-input" placeholder="https://script.google.com/macros/s/…/exec 또는 연결 링크" autocomplete="off">
      <button class="primary" data-act="setup-test">연결 확인</button>
      <div id="setup-result" class="setup-result"></div>
      <p class="sub muted">이 기기의 열쇠: <code>${esc(key.slice(0, 6))}…</code> — 스크립트에 함께 들어갑니다.</p>
    </div>`;

  fetch('relay.gs?v=13', { cache: 'no-store' })
    .then(r => r.ok ? r.text() : Promise.reject(r.status))
    .then(text => {
      relayScript = text.replace('__FLOW_KEY__', key);
      const area = document.getElementById('setup-script');
      if (area) area.value = relayScript;
    })
    .catch(() => toast('스크립트를 못 불러왔습니다. 새로고침해 주세요'));
}

async function copySetupScript() {
  if (!relayScript) { toast('스크립트를 불러오는 중입니다. 잠시 뒤 다시'); return; }
  try {
    await navigator.clipboard.writeText(relayScript);
    toast('복사했습니다 · 다음 단계로');
  } catch {
    const area = document.getElementById('setup-script');
    area?.closest('details')?.setAttribute('open', '');
    area?.select();
    toast('자동 복사가 막혔습니다. 칸을 길게 눌러 복사하세요');
  }
}

async function testRelay() {
  const result = document.getElementById('setup-result');
  const typed = document.getElementById('relay-input')?.value;
  const relay = parseRelayInput(typed);
  if (!relay) {
    result.innerHTML = `<div class="status error"><span class="dot"></span>${esc(explainRelayInput(typed))}</div>`;
    return;
  }

  result.innerHTML = '<div class="status pending"><span class="dot"></span>확인 중… (처음엔 몇 초 걸립니다)</div>';
  try {
    const json = await relayCall(relay, null);
    state.relay = relay;
    local.set('relay', relay);
    applyView(json.view);
    state.error = null;

    const link = relayLink(relay);
    result.innerHTML = `
      <div class="status live"><span class="dot"></span>연결됐습니다. 이 기기는 이제 로그인 없이 씁니다.</div>
      <p class="sub">폰에서도 쓰려면 <b>이 링크를 폰에서 여세요</b> — 카톡 '나와의 채팅'으로 보내거나 아래 QR을 폰 카메라로 찍으면 됩니다.
        홈 화면에 추가한 Flow 앱을 쓴다면, 앱을 열고 이 설정 화면의 칸에 링크를 붙여넣으세요.</p>
      <div class="link-box"><code id="relay-link">${esc(link)}</code><button class="secondary" data-act="setup-copy-link">링크 복사</button></div>
      <div id="relay-qr" class="qr"></div>
      <p class="sub muted">이 링크에는 열쇠가 들어 있습니다. 남에게 보내지 마세요.</p>`;
    drawQr(link);
  } catch (error) {
    result.innerHTML = `<div class="status error"><span class="dot"></span>연결 못 함 — ${esc(error?.message || error)}</div>
      <p class="sub muted">스크립트를 붙여넣은 뒤 <b>저장</b>했는지, 배포의 액세스 권한이 <b>모든 사용자</b>인지 확인하세요.
      이 화면에서 복사한 스크립트여야 합니다(열쇠가 들어 있음).</p>`;
  }
}

function drawQr(text) {
  const draw = () => {
    const el = document.getElementById('relay-qr');
    if (!el || !window.qrcode) return;
    const qr = window.qrcode(0, 'M');
    qr.addData(text);
    qr.make();
    el.innerHTML = qr.createSvgTag({ cellSize: 4, margin: 4, scalable: true });
  };
  if (window.qrcode) return draw();
  const script = document.createElement('script');
  script.src = QR_LIB;
  script.onload = draw;
  document.head.appendChild(script);
}

function leaveSetup() {
  state.phase = state.view?.Today ? 'today' : (local.get('connected', false) || state.relay ? 'waiting-pc' : 'signin');
  app.innerHTML = '';
  render();
  refresh();
}

// ───────── 고치기
//
// 고치기 창은 #app 바깥에 둔다. #app 은 30초마다 통째로 다시 그려지는데,
// 그 안에 두면 이름을 고치는 도중에 입력칸이 사라진다.

const PRIORITIES = [
  { value: 'None', label: '없음' },
  { value: 'Low', label: '낮음' },
  { value: 'Medium', label: '보통' },
  { value: 'High', label: '높음' }
];

const sheet = document.createElement('div');
sheet.id = 'sheet';
document.body.appendChild(sheet);

function findItem(id) {
  const composed = composeView();
  return [...(composed?.routines ?? []), ...(composed?.tasks ?? []), ...(composed?.upcoming ?? [])].find(i => i.Id === id);
}

function openEditor(id) {
  if (state.editing === id) return;
  const item = findItem(id);
  if (!item) return;

  state.editing = id;
  state.confirmDelete = false;
  if (navigator.vibrate) navigator.vibrate(15);

  const isTask = item.kind === 'task';
  // 메모 연결: PC 가 메모를 보내 주고(1.8.0 이상), PC 에 이미 있는 항목일 때만(폰에서 적고 아직 안 간 할 일은 번호가 아직 없다).
  const linked = item.MemoId ? findMemo(item.MemoId) : null;
  const linkRow = memosOfView() && inView(item.Id) ? `
      <button type="button" class="link-row" data-sheet="link">
        <span>📝 메모 연결</span><span class="link-now">${linked ? esc(linked.Title) : '없음'}</span>${ICON.chevron}
      </button>` : '';
  sheet.innerHTML = `
    <div class="sheet-backdrop" data-sheet="close"></div>
    <form class="sheet" data-sheet="save" autocomplete="off">
      <div class="sheet-head">${isTask ? '할 일 고치기' : '루틴 고치기'}</div>
      <input id="edit-title" enterkeyhint="done" value="${esc(item.Title)}">
      ${isTask ? `
        <div class="sheet-label">중요도</div>
        <div class="prio" role="radiogroup">
          ${PRIORITIES.map(p => `<button type="button" class="prio-btn ${p.value.toLowerCase()}${(item.Priority || 'None') === p.value ? ' on' : ''}" data-sheet="prio" data-value="${p.value}">${p.label}</button>`).join('')}
        </div>` : ''}
      ${linkRow}
      <div class="sheet-actions">
        <button type="button" class="danger" data-sheet="delete">삭제</button>
        <span class="grow"></span>
        <button type="button" class="ghost" data-sheet="close">취소</button>
        <button type="submit" class="save">저장</button>
      </div>
      <div class="sheet-note" id="sheet-note">${isTask ? '' : '루틴을 지우면 연속 기록도 함께 사라집니다.'}</div>
    </form>`;
  sheet.classList.add('open');
}

/** 메모 읽기 화면의 … 메뉴: 고정 · 매일 초기화 · 체크 모두 풀기 · 삭제(두 번 눌러야). */
function openMemoMenu(id) {
  const memo = findMemo(id);
  if (!memo) return;
  state.editing = null;
  state.memoMenu = id;
  state.confirmDelete = false;
  const hasChecked = memo.lines.some(l => l.kind === 'check' && l.checked);
  sheet.innerHTML = `
    <div class="sheet-backdrop" data-sheet="close"></div>
    <div class="sheet menu-sheet">
      <div class="sheet-head">${esc(memo.Title)}</div>
      <button type="button" class="menu-item" data-sheet="memo-pin">📌 ${memo.Pinned ? '고정 풀기' : '고정'}</button>
      <button type="button" class="menu-item" data-sheet="memo-daily">↻ 매일 초기화 ${memo.Daily ? '끄기' : '켜기'}</button>
      <button type="button" class="menu-item" data-sheet="memo-uncheck"${hasChecked ? '' : ' disabled'}>○ 체크 모두 풀기</button>
      <button type="button" class="menu-item danger" data-sheet="memo-delete">삭제</button>
      <div class="sheet-note" id="sheet-note">${memo.Daily ? '매일 초기화: 새 하루가 되면 체크가 모두 풀립니다' : '매일 초기화를 켜면 새 하루가 될 때 체크가 모두 풀립니다'}</div>
    </div>`;
  sheet.classList.add('open');
}

/** 길게 누르기 창의 '메모 연결' → 메모 고르기(고정 먼저 · 최근 순) / 새 메모로 / 연결 끊기. */
function openLinkPicker(id) {
  const item = findItem(id);
  if (!item) return;
  const memos = [...(composeMemos() ?? [])].sort(memoOrder);
  const rows = memos.map(m => `
        <button type="button" class="menu-item pick${m.Id === item.MemoId ? ' on' : ''}" data-sheet="link-pick" data-memo="${esc(m.Id)}">
          ${m.Pinned ? '<span class="pin">📌</span>' : ''}<span class="pick-title">${esc(m.Title)}</span>${m.Id === item.MemoId ? '<span class="pick-on">연결됨</span>' : ''}
        </button>`).join('');
  sheet.innerHTML = `
    <div class="sheet-backdrop" data-sheet="close"></div>
    <div class="sheet menu-sheet">
      <div class="sheet-head">메모 연결 · ${esc(item.Title)}</div>
      <button type="button" class="menu-item accent" data-sheet="link-new">＋ 새 메모로</button>
      ${item.MemoId ? '<button type="button" class="menu-item" data-sheet="link-clear">연결 끊기</button>' : ''}
      <div class="pick-list">${rows || '<div class="sheet-note">아직 메모가 없습니다</div>'}</div>
      <div class="sheet-actions"><span class="grow"></span><button type="button" class="ghost" data-sheet="close">취소</button></div>
    </div>`;
  sheet.classList.add('open');
}

function closeEditor() {
  state.editing = null;
  state.memoMenu = null;
  state.confirmDelete = false;
  sheet.classList.remove('open');
  sheet.innerHTML = '';
}

/** 고친 것을 쌓고 보낸다. 사람이 누른 처리 안이라 열쇠가 없으면 이때 받는다. */
async function sendEdits() {
  if (!hasToken()) await requestToken();
  if (hasToken()) scheduleFlush();
}

sheet.addEventListener('click', async event => {
  const target = event.target.closest('[data-sheet]');
  if (!target || target.tagName === 'FORM') return;
  const id = state.editing;
  const memoId = state.memoMenu;
  const memo = memoId ? findMemo(memoId) : null;

  switch (target.dataset.sheet) {
    case 'close':
      closeEditor();
      return;

    case 'prio':
      sheet.querySelectorAll('.prio-btn').forEach(b => b.classList.toggle('on', b === target));
      return;

    case 'delete': {
      if (!state.confirmDelete) {
        state.confirmDelete = true;
        target.textContent = '한 번 더 누르면 삭제';
        target.classList.add('armed');
        return;
      }
      closeEditor();
      pushOp('delete', id);
      toast('지웠습니다 · PC에서도 지워집니다');
      await sendEdits();
      return;
    }

    // ── 메모 연결
    case 'link':
      openLinkPicker(id);
      return;

    case 'link-pick': {
      const item = findItem(id);
      closeEditor();
      if (!item || item.MemoId === target.dataset.memo) return;
      pushOp('memo-link', id, target.dataset.memo);
      toast('메모를 연결했습니다 · PC가 받아 넣습니다');
      await sendEdits();
      return;
    }

    case 'link-clear':
      closeEditor();
      pushOp('memo-link', id, '');
      toast('연결을 끊었습니다 · PC가 받아 넣습니다');
      await sendEdits();
      return;

    case 'link-new': {
      // PC 의 '새로 만들기'와 같다: 항목 제목을 본문으로 새 메모를 만들어 잇고 연다.
      // 만드는 일(memo-save)을 먼저, 잇는 일(memo-link)을 뒤에 — PC 는 메모가 생긴 뒤에 잇는다.
      const item = findItem(id);
      closeEditor();
      if (!item) return;
      const newId = uuid();
      pushOp('memo-save', newId, item.Title, { Base: null });
      pushOp('memo-link', id, newId);
      openMemo(newId, false);
      toast('새 메모를 만들어 연결했습니다 · PC가 받아 넣습니다');
      await sendEdits();
      return;
    }

    // ── 메모 … 메뉴
    case 'memo-pin':
    case 'memo-daily': {
      closeEditor();
      if (!memo) return;
      const pin = target.dataset.sheet === 'memo-pin';
      const on = !(pin ? memo.Pinned : memo.Daily);
      pushOp(target.dataset.sheet, memo.Id, on ? 'true' : 'false');
      toast(pin ? (on ? '고정했습니다' : '고정을 풀었습니다') : (on ? '매일 초기화를 켰습니다' : '매일 초기화를 껐습니다'));
      await sendEdits();
      return;
    }

    case 'memo-uncheck':
      closeEditor();
      if (!memo) return;
      pushOp('memo-uncheck-all', memo.Id);
      toast('체크를 모두 풀었습니다');
      await sendEdits();
      return;

    case 'memo-delete': {
      if (!memo) { closeEditor(); return; }
      if (!state.confirmDelete) {
        state.confirmDelete = true;
        target.textContent = '한 번 더 누르면 삭제';
        target.classList.add('armed');
        const note = document.getElementById('sheet-note');
        if (note) note.textContent = '지우면 PC의 메모 휴지통으로 갑니다';
        return;
      }
      closeEditor();
      pushOp('memo-delete', memo.Id);
      dropMemoDraft(memo.Id);
      if (history.state?.memo != null) history.back();   // popstate 가 메모를 닫는다
      else closeMemo();
      toast('지웠습니다 · PC에서 휴지통으로 갑니다');
      await sendEdits();
      return;
    }
  }
});

sheet.addEventListener('submit', async event => {
  event.preventDefault();
  const id = state.editing;
  const item = findItem(id);
  if (!item) { closeEditor(); return; }

  const title = document.getElementById('edit-title').value.trim();
  const priority = sheet.querySelector('.prio-btn.on')?.dataset.value;
  closeEditor();

  let changed = false;
  if (title && title !== item.Title) { pushOp('rename', id, title); changed = true; }
  if (priority && priority !== (item.Priority || 'None')) { pushOp('priority', id, priority); changed = true; }
  if (!changed) return;

  toast('고쳤습니다 · PC가 받아 넣습니다');
  await sendEdits();
});

// 길게 누르기. 손가락이 움직이면(스크롤) 취소하고, 길게 누른 뒤의 클릭은 체크로 치지 않는다.
const LONG_PRESS_MS = 480;
let press = null;
let suppressClick = false;

app.addEventListener('pointerdown', event => {
  suppressClick = false;
  const row = event.target.closest('.row[data-id]');
  if (!row || event.target.closest('.memo-link')) return;   // 📝 는 길게 눌러도 고치기 창을 열지 않는다
  const id = row.dataset.id;
  press = {
    x: event.clientX,
    y: event.clientY,
    timer: setTimeout(() => { suppressClick = true; press = null; openEditor(id); }, LONG_PRESS_MS)
  };
});

function cancelPress() {
  if (press) clearTimeout(press.timer);
  press = null;
}

app.addEventListener('pointermove', event => {
  if (press && Math.hypot(event.clientX - press.x, event.clientY - press.y) > 10) cancelPress();
});
app.addEventListener('pointerup', cancelPress);
app.addEventListener('pointercancel', cancelPress);

// 안드로이드는 길게 누르면 contextmenu 도 보낸다. PC 브라우저의 우클릭도 같은 길로 온다.
app.addEventListener('contextmenu', event => {
  const row = event.target.closest('.row[data-id]');
  if (!row) return;
  event.preventDefault();
  if (event.target.closest('.memo-link')) return;
  cancelPress();
  suppressClick = true;
  openEditor(row.dataset.id);
});

// ───────── 누르기
//
// 무언가를 누를 때마다 열쇠부터 챙긴다. 구글 로그인 창은 사람이 누른 그 순간에만 열린다.

app.addEventListener('click', async event => {
  if (suppressClick) { suppressClick = false; event.preventDefault(); return; }
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

    case 'setup':
      state.phase = 'setup';
      app.innerHTML = '';
      render();
      window.scrollTo(0, 0);
      return;

    case 'setup-back':
      leaveSetup();
      return;

    case 'setup-copy':
      await copySetupScript();
      return;

    case 'setup-copy-link':
      try {
        await navigator.clipboard.writeText(document.getElementById('relay-link').textContent);
        toast('링크를 복사했습니다');
      } catch { toast('링크를 길게 눌러 복사하세요'); }
      return;

    case 'setup-test':
      await testRelay();
      return;

    case 'relay-off':
      if (!confirm('로그인 없이 쓰기를 끕니다. 이 기기는 다시 구글 로그인으로 연결합니다.')) return;
      state.relay = null;
      local.remove('relay');
      state.phase = state.view?.Today ? 'today' : 'signin';
      render();
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

    case 'tab':
      if (state.tab === target.dataset.tab && !state.memoOpen) return;
      state.tab = target.dataset.tab === 'memo' ? 'memo' : 'today';
      local.set('tab', state.tab);
      if (state.memoOpen && history.state?.memo != null) history.back();   // 쌓아 둔 기록을 걷는다
      state.memoOpen = null;
      app.innerHTML = '';
      render();
      window.scrollTo(0, 0);
      return;

    case 'memo-open':
      // 오늘 줄 안의 📝 도 여기로 온다. 가장 가까운 data-act 가 이것이라 줄 체크는 일어나지 않는다.
      if (!findMemo(target.dataset.memo)) return;
      openMemo(target.dataset.memo, !!target.closest('.memo-list'));
      return;

    case 'memo-back':
      if (history.state?.memo != null) history.back();   // popstate 가 닫는다
      else closeMemo();
      return;

    case 'memo-new':
      if (memosOfView()) await openMemoEditor(null);
      return;

    case 'memo-edit':
      await openMemoEditor(state.memoOpen);
      return;

    case 'memo-menu':
      openMemoMenu(state.memoOpen);
      return;

    case 'memo-tag':
      state.memoTag = state.memoTag && lower(state.memoTag) === lower(target.dataset.tag) ? null : target.dataset.tag;
      updateMemoList();
      return;

    case 'memo-check': {
      const memo = findMemo(state.memoOpen);
      if (!memo) return;
      const line = memo.lines[Number(target.dataset.line)];
      if (line?.kind !== 'check') return;

      if (navigator.vibrate) navigator.vibrate(8);
      pushOp('memo-check', memo.Id, line.text.trim(), { Line: line.index, Checked: !line.checked });
      if (!hasToken()) await requestToken();
      if (hasToken()) scheduleFlush();
      return;
    }

    case 'toggle': {
      const item = findItem(target.dataset.id);
      if (!item) return;

      if (navigator.vibrate) navigator.vibrate(8);
      pushOp(item.Done ? 'undone' : 'done', item.Id);
      if (!hasToken()) await requestToken();
      if (hasToken()) scheduleFlush();
      return;
    }
  }
});

// 검색칸에서 엔터(키보드의 '검색')를 누르면 자판만 내린다.
app.addEventListener('keydown', event => {
  if (event.target.id === 'memo-search' && event.key === 'Enter' && !event.isComposing) event.target.blur();
});

app.addEventListener('submit', async event => {
  event.preventDefault();
  const input = document.getElementById('compose');
  const text = input.value.trim();
  if (!text) return;

  input.value = '';
  state.draft = '';
  pushOp('add', null, text);
  toast('적어 뒀습니다 · PC가 받아 넣습니다');
  if (!hasToken()) await requestToken();
  if (hasToken()) scheduleFlush();
});

app.addEventListener('input', event => {
  if (event.target.id === 'memo-search') {
    state.memoSearch = event.target.value;
    updateMemoList();
    return;
  }
  if (event.target.id !== 'compose') return;
  state.draft = event.target.value;
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

// ───────── 메모 쓰기 · 고치기
//
// 글 칸은 #app 바깥(#memo-editor)에 둔다. #app 은 30초마다 다시 그려지는데, 그 안에 두면
// 쓰던 글 · 커서 · 한글 조합 중인 글자가 날아간다.
// 쓰는 글은 이 폰에 임시로 남긴다(memoDrafts) — 다른 앱에 갔다 와도, 앱을 닫았다 열어도 이어서 쓴다.
// 저장은 memo-save 하나: 본문 전체 + Base(고치기를 시작할 때 본 판, 새 메모면 null).
// PC 가 판을 대 보고 덮거나, 그 사이 PC 에서 고쳤으면 '(휴대폰 충돌 사본)' 새 메모로 남긴다. 어느 쪽 글도 사라지지 않는다.

// 서식 단추·Enter 이어 쓰기 — PC 의 MemoEditing 과 같은 규칙이다.
const EDIT_RX = {
  h2: /^##\s+/,
  h1: /^#\s+/,
  check: /^([ \t]*)(-\s+)?\[[ xX]?\]\s?/,
  numbered: /^([ \t]*)(\d{1,3})\.\s+/,
  bullet: /^([ \t]*)([-*])\s+/,
  indent: /^[ \t]*/
};

const LIST_KINDS = ['check', 'numbered', 'bullet'];

/** 줄 머리(MemoEditing.HeadOf). length 는 들여쓰기까지 넣은 기호 전체 길이, 기호가 없으면 들여쓰기 길이. */
function lineHead(line) {
  const indent = EDIT_RX.indent.exec(line)[0];
  const make = (kind, ind, length, number, prefix) => ({ kind, indent: ind, length, number, prefix });
  if (!line.trim()) return make('blank', indent, indent.length, 0, '');
  let m = EDIT_RX.h2.exec(line);
  if (m) return make('h2', '', m[0].length, 0, '## ');
  m = EDIT_RX.h1.exec(line);
  if (m) return make('h1', '', m[0].length, 0, '# ');
  m = EDIT_RX.check.exec(line);
  if (m) return make('check', m[1], m[0].length, 0, m[1] + (m[2] ? '- ' : '') + '[ ] ');   // 이어 쓸 때는 빈 칸
  m = EDIT_RX.numbered.exec(line);
  if (m) return make('numbered', m[1], m[0].length, parseInt(m[2], 10), m[1] + m[2] + '. ');
  m = EDIT_RX.bullet.exec(line);
  if (m) return make('bullet', m[1], m[0].length, 0, m[1] + m[2] + ' ');
  return make('text', indent, indent.length, 0, '');
}

function markerOf(kind, number) {
  return { h1: '# ', h2: '## ', numbered: Math.max(1, number) + '. ', bullet: '- ', check: '[ ] ' }[kind] ?? '';
}

/** 줄바꿈(\r\n · \n · \r)으로 나눈 줄들의 { start, length }. 글이 비어도 줄 하나는 있다. */
function lineSpans(text) {
  const list = [];
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (ch !== '\r' && ch !== '\n') continue;
    list.push({ start, length: i - start });
    if (ch === '\r' && text[i + 1] === '\n') i++;
    start = i + 1;
  }
  list.push({ start, length: text.length - start });
  return list;
}

/** 그 자리가 들어 있는 줄. 줄바꿈 위의 자리는 그 앞 줄로 친다. */
function lineIndexAt(lines, pos) {
  let at = 0;
  for (let i = 0; i < lines.length && lines[i].start <= pos; i++) at = i;
  return at;
}

const spanText = (text, span) => text.substr(span.start, span.length);

/** 바로 위로 이어진 목록에서 같은 들여쓰기의 번호 줄 다음 번호. 빈 줄·글·소제목에서 끊긴다. 없으면 1. */
function nextListNumber(text, lines, index, indent) {
  for (let i = index - 1; i >= 0; i--) {
    const head = lineHead(spanText(text, lines[i]));
    if (!LIST_KINDS.includes(head.kind)) break;
    if (head.kind === 'numbered' && head.indent === indent) return Math.min(head.number + 1, 999);
  }
  return 1;
}

/**
 * 서식 단추(MemoEditing.Toggle): 커서 줄(골랐으면 걸친 줄들)에 그 기호를 켜고 끈다.
 * 고른 줄이 모두 이미 그 종류면 떼고, 아니면 다른 기호를 이 기호로 바꿔 붙인다. 여러 줄이면 빈 줄은 건너뛴다.
 * 돌려주는 것: { text, start, end } (뒤에서 앞으로 고른 방향은 그대로).
 */
function toggleMemoFormat(text, selectionStart, selectionEnd, kind) {
  const s = Math.max(0, Math.min(Math.min(selectionStart, selectionEnd), text.length));
  const e = Math.max(0, Math.min(Math.max(selectionStart, selectionEnd), text.length));
  const lines = lineSpans(text);
  const first = lineIndexAt(lines, s);
  let last = lineIndexAt(lines, e);
  if (last > first && e === lines[last].start) last--;   // 다음 줄 맨 앞까지 고른 것은 그 줄을 고른 것이 아니다

  const targets = [];
  for (let i = first; i <= last; i++) if (first === last || spanText(text, lines[i]).trim()) targets.push(i);
  if (!targets.length) targets.push(first);

  const remove = targets.every(i => lineHead(spanText(text, lines[i])).kind === kind);
  const firstHead = lineHead(spanText(text, lines[targets[0]]));
  let number = kind === 'numbered'
    ? nextListNumber(text, lines, targets[0], firstHead.kind === 'h1' || firstHead.kind === 'h2' ? '' : firstHead.indent) : 0;

  const changes = new Map();
  for (const i of targets) {
    const head = lineHead(spanText(text, lines[i]));
    const newHead = remove ? head.indent
      : kind === 'h1' || kind === 'h2' ? markerOf(kind, 0)
      : head.indent + markerOf(kind, number++);
    changes.set(i, { oldLength: head.length, newHead });
  }

  let out = '';
  const newStarts = [];
  let pos = 0;
  lines.forEach((line, i) => {
    out += text.slice(pos, line.start);
    newStarts[i] = out.length;
    const c = changes.get(i);
    out += c ? c.newHead + text.substr(line.start + c.oldLength, line.length - c.oldLength) : spanText(text, line);
    pos = line.start + line.length;
  });
  out += text.slice(pos);

  const map = (at, keepLineStart) => {
    const i = lineIndexAt(lines, at);
    const col = Math.min(at - lines[i].start, lines[i].length);
    const c = changes.get(i);
    if (!c) return newStarts[i] + col;
    if (keepLineStart && col === 0) return newStarts[i];   // 줄 맨 앞에서 시작한 선택은 새 기호까지 품는다
    if (col >= c.oldLength) return newStarts[i] + c.newHead.length + (col - c.oldLength);
    return newStarts[i] + c.newHead.length;                 // 기호 안에 있던 커서는 새 기호 뒤로
  };
  const has = s !== e;
  const newStart = map(s, has);
  const newEnd = has ? map(e, false) : newStart;
  return selectionStart <= selectionEnd
    ? { text: out, start: newStart, end: newEnd }
    : { text: out, start: newEnd, end: newStart };
}

/**
 * 체크·번호·점 줄에서 Enter(MemoEditing.ContinueList). 커서 뒤 글을 새 줄로 넘기며 같은 기호
 * (체크는 빈 칸, 번호는 다음 번호, 들여쓰기 그대로)를 붙인다. 기호만 있는 줄이면 기호를 지워 목록을 끝낸다.
 * 목록 줄이 아니거나 커서가 기호 앞·안이면 null(보통 줄바꿈). 돌려주는 것: { text, caret }.
 */
function continueMemoList(text, caret) {
  caret = Math.max(0, Math.min(caret, text.length));
  const lines = lineSpans(text);
  const line = lines[lineIndexAt(lines, caret)];
  const lineText = spanText(text, line);
  const head = lineHead(lineText);
  if (!LIST_KINDS.includes(head.kind)) return null;

  const col = caret - line.start;
  if (col < head.length || col > line.length) return null;

  if (!lineText.slice(head.length).trim()) {
    return { text: text.slice(0, line.start) + text.slice(line.start + line.length), caret: line.start };
  }
  const next = head.kind === 'numbered' ? head.indent + Math.min(head.number + 1, 999) + '. ' : head.prefix;
  const insert = (text.includes('\r\n') ? '\r\n' : '\n') + next;
  return { text: text.slice(0, caret) + insert + text.slice(caret), caret: caret + insert.length };
}

const FORMATS = [
  { kind: 'h1', mark: '#', label: '큰 소제목' },
  { kind: 'h2', mark: '##', label: '작은 소제목' },
  { kind: 'numbered', mark: '1.', label: '번호' },
  { kind: 'bullet', mark: '-', label: '점' },
  { kind: 'check', mark: '[ ]', label: '체크' }
];

const editorEl = document.createElement('div');
editorEl.id = 'memo-editor';
document.body.appendChild(editorEl);

/**
 * 글 칸을 보이는 영역(visualViewport)에 맞춘다. iOS 사파리는 자판이 떠도 화면 높이를 줄이지 않아
 * 바닥에 붙은 서식 줄이 자판 뒤로 숨는다. 안드로이드(interactive-widget=resizes-content)는 화면이 같이 줄어
 * 보이는 영역 = 화면이라 전과 같다.
 */
function fitEditor() {
  const vv = window.visualViewport;
  if (!vv || !editorEl.classList.contains('open')) return;
  editorEl.style.top = Math.max(0, vv.offsetTop) + 'px';
  editorEl.style.height = vv.height + 'px';
  editorEl.style.bottom = 'auto';
}
window.visualViewport?.addEventListener('resize', fitEditor);
window.visualViewport?.addEventListener('scroll', fitEditor);

let memoEdit = null;          // 쓰는 중인 글 { key, id, isNew, base, pcRev, start, startedAt, text }
let draftTimer = 0;
let shiftEnter = false;       // Shift+Enter 는 이어 쓰지 않고 그냥 줄을 바꾼다
let afterEditorPop = null;    // 글 칸을 닫으며 걷어 낸 기록이 돌아오면 할 일

const memoArea = () => document.getElementById('memo-text');

function memoDrafts() {
  const all = local.get('memoDrafts', {});
  return all && typeof all === 'object' ? all : {};
}

/** 쓰는 글을 이 폰에 남긴다. 다시 열 때(앱을 닫았다 열어도) 이어서 쓴다. 남기지 못했으면(자리 모자람) false. */
function saveMemoDraft() {
  clearTimeout(draftTimer);
  if (!memoEdit) return false;
  const area = memoArea();
  if (area) {
    memoEdit.text = area.value;
    memoEdit.caret = area.selectionEnd;
  }
  const all = memoDrafts();
  // 자리가 모자라 남겨 둔 저장 글(savedOp)은 그 일이 이 폰에 남거나 드라이브에 갈 때까지 덮지 않는다 —
  // 덮으면 앱이 닫혔을 때 그 저장 글이 사라진다.
  if (unpersistedSave(all[memoEdit.key])) return false;
  all[memoEdit.key] = { ...memoEdit, at: localIso(new Date()) };
  const saved = local.set('memoDrafts', all);
  local.set('memoEditing', memoEdit.key);
  return saved;
}

/**
 * 임시 글이 든 저장(savedOp)의 일이 아직 메모리에만 있는지(이 폰에도 · 드라이브에도 없고 PC 도 안 받음).
 * 일이 메모리에서도 사라졌으면(앱이 닫힘) 그 임시 글은 보통 임시 글이다 — 이어 쓰고 덮고 버릴 수 있다.
 */
function unpersistedSave(draft) {
  const id = draft?.savedOp;
  if (!id || !state.pending.some(op => op.Id === id)) return false;
  if (state.sent.has(id) || (state.view?.AppliedOps ?? []).includes(id)) return false;
  return !local.get('pending', []).some(op => op?.Id === id);
}

/**
 * 저장은 했는데 일(pending)을 이 폰에 남기지 못했을 때(자리 모자람): 일은 메모리에 있어 연결되면 가지만,
 * 그 전에 앱이 닫히면 사라진다. 그래서 임시 글을 버리지 않고 그 일의 번호(savedOp)를 붙여 둔다 —
 * 드라이브에 가거나 PC 가 받으면 버리고(pruneMemoDrafts), 일이 사라졌으면 다시 열 때 이어서 쓴다.
 */
function keepSavedDraft(ed, text, opId) {
  const all = memoDrafts();
  all[ed.key] = { ...ed, text, caret: text.length, savedOp: opId, at: localIso(new Date()) };
  local.set('memoDrafts', all);
  local.remove('memoEditing');
}

/** 저장 글(savedOp)의 일이 아직 이 폰의 메모리에 있거나 드라이브에 갔거나 PC 가 받았는지 — 그러면 임시 글은 쓸모없다. */
function draftSaved(draft) {
  const id = draft?.savedOp;
  if (!id) return false;
  return state.sent.has(id) || (state.view?.AppliedOps ?? []).includes(id) || state.pending.some(op => op.Id === id);
}

const DRAFT_KEEP_DAYS = 14;

/**
 * 쓸모없어진 임시 글을 버린다: 저장 글이 드라이브에 갔거나 PC 가 받은 것, 그리고 지금 없는 메모(지워짐)의 글 중
 * 14일 넘게 안 연 것. 새 메모('new')의 글은 메모가 없는 게 당연하니 날짜로 버리지 않는다.
 */
function pruneMemoDrafts() {
  const memos = composeMemos();
  const all = memoDrafts();
  const applied = new Set(state.view?.AppliedOps ?? []);
  const cutoff = Date.now() - DRAFT_KEEP_DAYS * 86400000;
  let changed = false;
  for (const [key, draft] of Object.entries(all)) {
    if (key === memoEdit?.key) continue;
    const sent = !!draft?.savedOp && (state.sent.has(draft.savedOp) || applied.has(draft.savedOp));
    const orphan = !!memos && key !== 'new' && !memos.some(m => m.Id === key) && !(new Date(draft?.at).getTime() > cutoff);
    if (sent || orphan) {
      delete all[key];
      changed = true;
    }
  }
  if (changed) local.set('memoDrafts', all);
}

/** 임시 글을 버린다. 아직 어디에도 안 남은 저장 글(savedOp)은 force 가 아니면 두고 간다(취소 · 뒤로 · 고친 것 없음). */
function dropMemoDraft(key, force) {
  const all = memoDrafts();
  if (key in all && (force || !unpersistedSave(all[key]))) {
    delete all[key];
    local.set('memoDrafts', all);
  }
  local.remove('memoEditing');
}

function editorNote(text, warn) {
  const el = document.getElementById('editor-note');
  if (!el) return;
  el.textContent = text;
  el.hidden = !text;
  el.classList.toggle('warn', !!warn);
}

/** 남겨 둔 글 중 이어 쓸 만한 것. 고친 것 없이 남은 것, 저장해서 일이 살아 있는 것은 버리고 지금 글로 연다. */
function usableDraft(key) {
  const draft = memoDrafts()[key];
  if (!draft || draft.text === draft.start || draftSaved(draft)) return null;
  return draft;
}

const HELD_NOTE = 'PC에서 이 메모를 쓰는 중입니다 · 저장하면 PC에서 쓰기를 마친 뒤 들어갑니다';

/**
 * 남겨 둔 글로 이어 쓸 때 Base 를 다시 본다. Base 는 고치기를 시작할 때 본 글의 판인데, 그 뒤에
 * - 그 메모에 체크 · 모두 풀기 같은 일만 더 있고(아직 PC 가 안 받은 다른 글 저장은 없음) PC 글은 그대로면
 *   (매일 초기화 메모를 새 하루라 풀어 보이는 것도 같다 — PC 는 초기화를 먼저 하고 이 저장을 넣는다):
 *   PC 가 이 저장에 닿을 때 가질 글은 지금 보이는 글이다 → 그 판으로 바꾼다(안 그러면 제 체크 때문에 충돌 사본이 생긴다).
 *   지금 보이는 글이 고치기의 시작 글이 된다 — 저장하면 이 글로 덮인다.
 * - PC 글이 바뀌었으면(PC 에서 고침 · 앞의 일을 받음): Base 는 그대로 두고 저장하면 충돌 사본이 될 수 있다고 알린다.
 * 돌려주는 것: { draft, note }. PC 가 쓰는 중이면 그것도 알린다(처음 열 때와 같다).
 */
async function resumeDraft(draft) {
  const notes = ['쓰던 글을 이어서 씁니다'];
  if (!draft.isNew) {
    const memo = findMemo(draft.id);
    const sent = sentMemo(draft.id);
    // PC 글이 그대로인지: 시작할 때의 PC 판이거나, 그때 얹어 계산한 판(앞의 일을 PC 가 예상대로 받음).
    // pcRev 가 없는 것은 v10 이 남긴 글 — Base 와만 견준다.
    const pcSame = !sent || sent.Rev === draft.pcRev || sent.Rev === draft.base;
    if (!memo) {
      notes.push('이 메모는 PC에 없습니다 · 저장하면 새 메모로 남습니다');
    } else if (!pcSame) {
      notes.push('그 사이 PC에서 이 메모가 바뀌었습니다 · 저장하면 PC 글은 두고 충돌 사본으로 남습니다');
    } else if (memo.Text !== draft.start) {
      // PC 글은 그대로인데 보이는 글이 바뀌었다. 체크 · 모두 풀기(어느 탭 · 기기에서든)나 새 하루의 매일 초기화뿐이면
      // Base 를 지금 글로 옮긴다. 아직 PC 가 안 받은 다른 글 전체(memo-save — 다른 탭 · 기기에서 고친 것)가 끼어 있으면
      // 옮기지 않는다 — 옮기면 PC 가 그 글을 넣은 뒤 이 저장이 판이 맞아 그 글을 사본 없이 덮는다.
      // 고치기를 시작한 뒤(startedAt 부터)의 저장만 센다 — 그 전 저장(이 폰의 앞 저장 등)은 이미 시작 글에 들어 있다.
      // startedAt 이 없는 것(v12 까지 남긴 글)은 모두 센다.
      const applied = new Set(state.view?.AppliedOps ?? []);
      const otherSave = state.pending.some(op => op.Kind === 'memo-save' && op.Target === draft.id
        && !applied.has(op.Id) && op.Text !== draft.start && (!draft.startedAt || op.At >= draft.startedAt));
      if (otherSave) {
        notes.push('그 사이 다른 곳에서 이 메모를 고쳤습니다 · 저장하면 충돌 사본이 생길 수 있습니다');
      } else {
        draft = { ...draft, base: await memoBaseOf(memo), pcRev: sent?.Rev ?? null, start: memo.Text, startedAt: localIso(new Date()) };
        notes.push('그 뒤 바꾼 체크 등은 저장하면 이 글대로 됩니다');
      }
    }
    if (isHeldMemo(draft.id)) notes.push(HELD_NOTE);
  }
  return { draft, note: notes.join(' · ') };
}

/**
 * 글 칸을 연다. id 가 없으면 새 메모. 남겨 둔 글이 있으면 그것으로 이어 쓴다(Base 는 resumeDraft 가 다시 본다 —
 * 그 사이 PC 에서 고쳤으면 저장할 때 충돌 사본으로 남는다).
 */
async function openMemoEditor(id) {
  if (memoEdit) return;
  const key = id ?? 'new';
  let draft = usableDraft(key);
  let note = '';

  if (draft) {
    ({ draft, note } = await resumeDraft(draft));
  } else if (id) {
    const memo = findMemo(id);
    if (!memo) return;
    draft = { key, id, isNew: false, base: await memoBaseOf(memo), pcRev: sentMemo(id)?.Rev ?? null,
      start: memo.Text, startedAt: localIso(new Date()), text: memo.Text };
    if (isHeldMemo(id)) note = HELD_NOTE;
  } else {
    draft = { key, id: uuid(), isNew: true, base: null, pcRev: null, start: '', text: '' };
  }
  if (memoEdit) return;   // 기다리는 사이 두 번 눌렀다
  showMemoEditor(draft, note);
}

function showMemoEditor(draft, note) {
  memoEdit = { ...draft };
  delete memoEdit.savedOp;
  closeEditor();
  editorEl.innerHTML = `
    <div class="editor">
      <header class="editor-head">
        <button class="text-btn" data-ed="cancel">취소</button>
        <div class="editor-title">${draft.isNew ? '새 메모' : '메모 고치기'}</div>
        <button class="save-btn" data-ed="save">저장</button>
      </header>
      <div class="editor-note" id="editor-note"${note ? '' : ' hidden'}>${esc(note)}</div>
      <textarea id="memo-text" autocomplete="off" spellcheck="false" placeholder="첫 줄이 제목이 됩니다"></textarea>
      <div class="format-bar" role="toolbar" aria-label="서식">
        ${FORMATS.map(f => `<button data-fmt="${f.kind}" aria-label="${f.label}">${esc(f.mark)}</button>`).join('')}
      </div>
    </div>`;
  const area = memoArea();
  area.value = draft.text ?? '';
  document.body.classList.add('memo-editing');
  editorEl.classList.add('open');
  fitEditor();
  // 안드로이드 뒤로 단추가 글 칸을 닫게 기록을 하나 쌓는다(쓰던 글은 남는다).
  history.pushState({ ...(history.state ?? {}), editor: true }, '');
  saveMemoDraft();
  const caret = Math.min(draft.caret ?? (draft.isNew ? 0 : area.value.length), area.value.length);
  area.focus();
  area.setSelectionRange(caret, caret);
}

function hideMemoEditor() {
  clearTimeout(draftTimer);
  memoEdit = null;
  editorEl.classList.remove('open');
  editorEl.style.top = editorEl.style.height = editorEl.style.bottom = '';
  editorEl.innerHTML = '';
  document.body.classList.remove('memo-editing');
  local.remove('memoEditing');
}

/** 글 칸을 닫고 쌓아 둔 기록을 걷는다. then 은 기록이 돌아온 뒤에 한다(그 사이 새 기록을 쌓으면 꼬인다). */
function leaveMemoEditor(then) {
  hideMemoEditor();
  render();
  if (history.state?.editor) {
    afterEditorPop = then ?? (() => {});
    history.back();
  } else then?.();
}

/**
 * 저장 하나를 일로 쌓는다. 바로 앞의 같은 메모 일이 아직 어디에도 안 올라간 저장이고 이 글이 그 글을 이어 고친 것이면
 * (Base = 그 글의 판) 둘을 하나로 합친다 — 본문 전체가 inbox 에 들어가므로 저장을 여러 번 하면 그만큼 커진다.
 * 합친 일은 앞 일의 Base · At 그대로, 글만 새 글로, 번호는 새로 뽑는다: 앞 일이 사실은 올라갔다면(답만 못 받음 ·
 * 다른 탭이 보냄) 같은 번호의 옛 글이 이겨 새 글이 사라지지만, 새 번호면 둘 다 가서 PC 가 뒤의 것을 충돌 사본으로 남긴다.
 * 돌려주는 것: pushOp 와 같은 { op, saved }.
 */
async function queueMemoSave(ed, text) {
  const base = ed.isNew ? null : ed.base ?? '';
  const applied = new Set(state.view?.AppliedOps ?? []);
  const last = state.pending
    .filter(op => op.Target === ed.id && isMemoOp(op) && !applied.has(op.Id))
    .sort((a, b) => a.At.localeCompare(b.At) || a.Id.localeCompare(b.Id))
    .pop();
  const candidate = !!last && !ed.isNew && !!base && last.Kind === 'memo-save' && typeof last.Text === 'string';
  const continues = candidate && base === await memoRev(last.Text);
  // 판을 재는 사이 올리기가 시작됐거나 끝났을 수 있어 그 뒤에 본다.
  if (!continues || uploading || state.sent.has(last.Id) || !state.pending.includes(last)) {
    return pushOp('memo-save', ed.id, text, { Base: base });
  }

  const op = { ...last, Id: uuid(), Text: text, Day: logicalToday() };
  state.pending = state.pending.map(o => (o === last ? op : o));
  return { op, saved: commitPending() };
}

let savingMemo = false;      // 저장 단추를 두 번 눌러도 한 번만(합치기는 판을 재느라 잠깐 기다린다)

async function saveMemoEditor() {
  const ed = memoEdit;
  const area = memoArea();
  if (!ed || !area || savingMemo) return;
  const text = area.value;

  // 고친 것이 없으면 크기와 상관없이 그냥 닫는다(큰 메모를 열었다 닫기만 해도 막히지 않게).
  if (ed.isNew ? !text.trim() : text === ed.start) {
    dropMemoDraft(ed.key);
    leaveMemoEditor(() => toast(ed.isNew ? '빈 메모는 만들지 않았습니다' : '바뀐 것이 없습니다'));
    return;
  }
  const bytes = utf8Bytes(text);
  if (bytes > MEMO_MAX_BYTES) {
    saveMemoDraft();
    editorNote(`글이 너무 깁니다(${Math.ceil(bytes / 1024)} KB) · 한 메모는 200 KB 까지 보낼 수 있습니다. 나눠서 써 주세요`, true);
    return;
  }

  savingMemo = true;
  let queued;
  try {
    queued = await queueMemoSave(ed, text);
  } finally {
    savingMemo = false;
  }
  const { op, saved } = queued;
  // 일을 이 폰에 남겼을 때만 임시 글을 버린다. 못 남겼으면(자리 모자람) 앱이 닫혀도 글이 남게 임시 글을 둔다.
  if (saved) dropMemoDraft(ed.key, true);   // 새 일이 남았다(합쳐서 옛 저장 글의 일이 없어졌어도 버린다)
  else keepSavedDraft(ed, text, op.Id);
  leaveMemoEditor(() => {
    if (ed.isNew) openMemo(ed.id, true);
    if (!saved) toast('휴대폰 저장 공간이 모자라 임시로만 들고 있습니다 · 연결되면 바로 보냅니다', true);
    else toast(ed.isNew ? '새 메모를 적어 뒀습니다 · PC가 받아 넣습니다' : '저장했습니다 · PC가 받아 넣습니다');
  });
  await sendEdits();
}

function cancelMemoEditor() {
  const ed = memoEdit;
  const area = memoArea();
  if (!ed) return;
  if (area && area.value !== ed.start && !confirm(ed.isNew ? '쓰던 새 메모를 버릴까요?' : '고친 것을 버릴까요?')) return;
  dropMemoDraft(ed.key);
  leaveMemoEditor();
}

editorEl.addEventListener('click', event => {
  const fmt = event.target.closest('[data-fmt]');
  if (fmt) {
    const area = memoArea();
    const r = toggleMemoFormat(area.value, area.selectionStart, area.selectionEnd, fmt.dataset.fmt);
    area.value = r.text;
    area.focus();
    area.setSelectionRange(Math.min(r.start, r.end), Math.max(r.start, r.end), r.start > r.end ? 'backward' : 'forward');
    saveMemoDraft();
    return;
  }
  const act = event.target.closest('[data-ed]')?.dataset.ed;
  if (act === 'save') saveMemoEditor();
  else if (act === 'cancel') cancelMemoEditor();
});

// 서식 단추를 눌러도 글 칸이 커서·자판을 놓지 않게 한다.
editorEl.addEventListener('pointerdown', event => {
  if (event.target.closest('[data-fmt], [data-ed]')) event.preventDefault();
});

editorEl.addEventListener('keydown', event => {
  if (event.target.id === 'memo-text' && event.key === 'Enter') shiftEnter = event.shiftKey;
});

/**
 * Enter 이어 쓰기. 줄바꿈이 들어간 뒤에 고친다 — 안드로이드 자판은 keydown 을 제대로 안 주는 일이 많아서다.
 * 한글을 조합하는 중(isComposing)에는 손대지 않고, Shift+Enter 는 그냥 줄바꿈이다.
 */
editorEl.addEventListener('input', event => {
  const area = event.target;
  if (area.id !== 'memo-text') return;
  const shift = shiftEnter;
  shiftEnter = false;
  const lineBreak = event.inputType === 'insertLineBreak' || event.inputType === 'insertParagraph'
    || (event.inputType === 'insertText' && event.data === '\n');
  if (lineBreak && !event.isComposing && !shift) {
    const caret = area.selectionStart;
    if (caret === area.selectionEnd && caret > 0 && area.value[caret - 1] === '\n') {
      const r = continueMemoList(area.value.slice(0, caret - 1) + area.value.slice(caret), caret - 1);
      if (r) {
        area.value = r.text;
        area.setSelectionRange(r.caret, r.caret);
      }
    }
  }
  clearTimeout(draftTimer);
  draftTimer = setTimeout(saveMemoDraft, 400);
});

// 다른 앱으로 가면 바로 남긴다(안드로이드는 뒤에 있는 앱을 말없이 닫기도 한다).
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') saveMemoDraft();
});
window.addEventListener('pagehide', () => saveMemoDraft());

window.addEventListener('popstate', () => {
  if (afterEditorPop) {
    const then = afterEditorPop;
    afterEditorPop = null;
    then();
    return;
  }
  if (memoEdit) {
    // 안드로이드 뒤로: 글 칸만 닫는다. 고친 것이 있으면 남겨 두고 다시 열 때 이어서 쓴다.
    const area = memoArea();
    const key = memoEdit.key;
    const changed = !!area && area.value !== memoEdit.start;
    if (changed && !saveMemoDraft()) {
      // 남기지 못했다(자리 모자람) — 닫으면 글이 사라진다. 글 칸을 그대로 두고 알린다.
      history.pushState({ ...(history.state ?? {}), editor: true }, '');
      editorNote('휴대폰 저장 공간이 모자라 쓰던 글을 남겨 둘 수 없습니다 · 저장하거나 취소해 주세요', true);
      return;
    }
    if (!changed) dropMemoDraft(key);
    hideMemoEditor();
    render();
    if (changed) toast('쓰던 글은 남겨 뒀습니다 · 다시 열면 이어서 씁니다');
    return;
  }
  closeMemo();
});

// ───────── 시작

function loadDemo() {
  const today = logicalToday();
  const now = new Date();
  const minutesAgo = m => localIso(new Date(now.getTime() - m * 60000));
  const ids = Array.from({ length: 9 }, () => uuid());
  const memoIds = Array.from({ length: 4 }, () => uuid());

  state.view = {
    Version: 1,
    GeneratedAt: minutesAgo(3),
    Today: today,
    DayStartHour: 4,
    Routines: [
      { Id: ids[0], Title: '물 2L 마시기', Done: true, Streak: 12 },
      { Id: ids[1], Title: '스트레칭 10분', Done: false, Time: '22:00:00', Streak: 4, MemoId: memoIds[1] },
      { Id: ids[2], Title: '하루 정산 기록', Done: false, Streak: 0 }
    ],
    Tasks: [
      { Id: ids[3], Title: '분기 매출 정리해서 메일 보내기', Done: false, Priority: 'High', Due: today, Time: '15:00:00', MemoId: memoIds[2] },
      { Id: ids[4], Title: '택배 반품 접수', Done: false, Priority: 'Medium', Due: today, CarryOverCount: 2 },
      { Id: ids[5], Title: '치과 예약 전화', Done: false, Priority: 'None' },
      { Id: ids[6], Title: '은행 서류 제출', Done: true, Priority: 'None', Due: today }
    ],
    Upcoming: [
      { Id: ids[8], Title: '월말 정산 자료 준비', Done: false, Priority: 'Medium', Due: addDays(today, 2) }
    ],
    AppliedOps: [],
    Memos: [
      {
        Id: memoIds[0], Title: 'AK넷 일별 정책서 배포', Pinned: true, Daily: false, Reset: null, Tags: ['AK넷', '업무'],
        Rev: 'demo0', Updated: minutesAgo(40),
        Text: '# AK넷 일별 정책서 배포\n#AK넷 #업무\n\n## 아침\n1. 정책서 파일 받기\n2. 단가표 확인\n  - 지난주와 다른 칸 표시\n\n## 보내기\n[x] 대리점 단톡방 공지\n[ ] 본사 메일 회신\n- [ ] 정산 시트 갱신\n    [ ] 바뀐 단가만 따로 표시\n\n늦어도 10시 전에 끝낸다.'
      },
      {
        Id: memoIds[1], Title: '아침 루틴', Pinned: false, Daily: true, Reset: today, Tags: ['루틴'],
        Rev: 'demo1', Updated: minutesAgo(90),
        Text: '아침 루틴 #루틴\n[x] 물 한 컵\n[ ] 비타민\n[ ] 스트레칭 5분\n[ ] 오늘 할 일 훑기'
      },
      {
        Id: memoIds[2], Title: '정산 절차', Pinned: false, Daily: false, Reset: null, Tags: ['정산', '업무'],
        Rev: 'demo2', Updated: minutesAgo(60 * 26),
        Text: '정산 절차 #정산\n매달 말일 전에 끝낸다.\n\n1. 판매 내역 내려받기\n2. 반품 빼기\n3. 시트에 붙여넣기\n* 영수증은 따로 모아 둔다\n\n[ ] 이번 달 영수증 사진'
      },
      {
        Id: memoIds[3], Title: '읽을 책', Pinned: false, Daily: false, Reset: null, Tags: [],
        Rev: 'demo3', Updated: minutesAgo(60 * 24 * 5),
        Text: '읽을 책\n- 아주 작은 습관의 힘\n- 딥 워크\n- 몰입'
      }
    ]
  };
  state.pending = [
    { Id: ids[7], Kind: 'add', Target: null, Text: '퇴근길에 우유 사기', At: minutesAgo(1), Day: today }
  ];
  state.phase = 'today';
}

// 메모를 연 채 새로고침했으면 그 기록이 남아 있다. 열린 메모는 기억하지 않으므로 지운다.
if (history.state?.memo != null || history.state?.editor) history.replaceState(null, '');

if (DEMO) {
  loadDemo();
} else {
  takeRelayFromLink();
  restoreToken();
  state.phase = state.view?.Today ? 'today' : (local.get('connected', false) || state.relay ? 'waiting-pc' : 'signin');
}

render();

// 쓰던 메모 글 칸을 연 채 앱이 닫혔으면 그대로 다시 연다(열 때와 같이 Base · PC 쓰는 중을 다시 본다).
{
  const key = DEMO ? null : local.get('memoEditing', null);
  const draft = key ? memoDrafts()[key] : null;
  if (draft && state.view?.Today && !draftSaved(draft)) {
    resumeDraft(draft).then(r => { if (!memoEdit) showMemoEditor(r.draft, r.note); });
  } else if (key) local.remove('memoEditing');
  if (!DEMO) pruneMemoDrafts();
}

refresh();
