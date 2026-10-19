// Flow 폰 중계 — 구글 Apps Script
//
// 폰이 구글 로그인 없이 PC와 주고받게 해 주는 작은 프로그램이다.
// 내 구글 계정 안에서 '나'로 돌면서, 내 드라이브의 'Flow 폰' 폴더에 있는 두 파일만 읽고 쓴다.
//   phone-view.json  — PC가 쓴 오늘 화면. 읽기만 한다.
//   phone-inbox.json — 폰이 한 일. 폰이 보낸 것을 합쳐 넣는다.
//
// 아래 KEY 를 아는 사람만 쓸 수 있다. 이 글을 남에게 보내지 말 것.

const KEY = '__FLOW_KEY__';

const FOLDER = 'Flow 폰';
const VIEW = 'phone-view.json';
const INBOX = 'phone-inbox.json';

function doGet(e) {
  return handle(e && e.parameter && e.parameter.key, null);
}

function doPost(e) {
  let body = {};
  try { body = JSON.parse(e.postData.contents); } catch (error) { body = {}; }
  return handle(body.key, body);
}

function handle(key, body) {
  if (key !== KEY) return out({ ok: false, error: 'key' });

  const files = findFiles();
  const view = read(files.view, {});

  // 읽기: 오늘 화면과 아직 PC가 안 받은 일
  if (!body) return out({ ok: true, view: view, inbox: read(files.inbox, {}).Ops || [] });

  // 쓰기: 지금 있는 것 + 폰이 보낸 것 − PC가 받아 간 것. 두 폰이 동시에 보내도 하나씩.
  const lock = LockService.getScriptLock();
  lock.waitLock(15000);
  try {
    const current = read(files.inbox, {}).Ops || [];
    const applied = new Set(view.AppliedOps || []);
    const byId = new Map();
    current.concat(body.ops || []).forEach(function (op) {
      if (op && op.Id && !applied.has(op.Id) && !byId.has(op.Id)) byId.set(op.Id, op);
    });
    const merged = Array.from(byId.values()).sort(function (a, b) {
      return String(a.At).localeCompare(String(b.At)) || String(a.Id).localeCompare(String(b.Id));
    });

    const same = merged.length === current.length && current.every(function (op) { return byId.has(op.Id); });
    if (!same) files.inbox.setContent(JSON.stringify({ Version: 1, Ops: merged }));

    return out({ ok: true, inbox: merged });
  } finally {
    lock.releaseLock();
  }
}

/** 'Flow 폰' 폴더와 두 파일. 같은 이름 폴더가 여럿이면 PC가 쓰고 있는 쪽(화면 파일이 있는 쪽). 없으면 만든다. */
function findFiles() {
  let best = null;
  const folders = DriveApp.getFoldersByName(FOLDER);
  while (folders.hasNext()) {
    const folder = folders.next();
    if (folder.isTrashed()) continue;
    const view = first(folder.getFilesByName(VIEW));
    if (!best || (view && !best.view)) best = { folder: folder, view: view, inbox: first(folder.getFilesByName(INBOX)) };
  }

  if (!best) best = { folder: DriveApp.createFolder(FOLDER), view: null, inbox: null };
  if (!best.view) best.view = best.folder.createFile(VIEW, '{}', 'application/json');
  if (!best.inbox) best.inbox = best.folder.createFile(INBOX, JSON.stringify({ Version: 1, Ops: [] }), 'application/json');
  return best;
}

function first(files) {
  while (files.hasNext()) {
    const file = files.next();
    if (!file.isTrashed()) return file;
  }
  return null;
}

function read(file, fallback) {
  try { return JSON.parse(file.getBlob().getDataAsString('UTF-8')) || fallback; } catch (error) { return fallback; }
}

function out(value) {
  return ContentService.createTextOutput(JSON.stringify(value)).setMimeType(ContentService.MimeType.JSON);
}
