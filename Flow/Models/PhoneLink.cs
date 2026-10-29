using System;
using System.Collections.Generic;

namespace Flow.Models;

// 폰과 주고받는 파일 두 개의 모양.
//
// 원칙: 파일마다 쓰는 쪽은 하나뿐이다.
//   phone-inbox.json — 폰만 쓴다. PC는 읽기만 한다.
//   phone-view.json  — PC만 쓴다. 폰은 읽기만 한다.
//   data.json        — PC만 쓴다. 폰은 아예 보지 않는다.
// 두 쪽이 같은 파일을 쓰면 나중에 쓴 쪽이 앞의 것을 통째로 덮는다.
// 쓰는 쪽을 하나로 못박으면 그 일이 구조적으로 일어날 수 없다.
//
// 갈래(Kind)를 enum 이 아니라 문자열로 두는 이유: enum 에 값을 늘리면
// 그 값을 모르는 옛 버전이 파일 전체를 못 읽는다. 문자열이면 모르는 갈래만 건너뛴다.

/// <summary>폰에서 한 일 하나. 폰이 inbox 에 쌓아 두면 PC가 반영한다.</summary>
public sealed class PhoneOp
{
    public const string Add = "add";
    public const string Done = "done";
    public const string Undone = "undone";
    public const string Rename = "rename";
    public const string SetPriority = "priority";
    public const string Delete = "delete";

    /// <summary>
    /// 메모의 체크 줄 하나를 맞춘다. <see cref="Target"/> 은 메모 id, <see cref="Text"/> 는 그 줄의 글(체크 기호·앞 들여쓰기 뺀 것),
    /// <see cref="Line"/> 은 본문 몇째 줄(0부터), <see cref="Checked"/> 는 맞출 상태. 이 갈래를 모르는 옛 PC 는 손대지 않고 남긴다.
    /// </summary>
    public const string MemoCheck = "memo-check";

    /// <summary>
    /// 이 일의 고유 번호. PC는 반영한 번호를 기억해 두 번 하지 않는다.
    /// '추가'라면 새로 생기는 항목의 Id 로도 쓴다 — 같은 추가가 두 번 와도 하나만 생긴다.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// <see cref="Add"/> · <see cref="Done"/> · <see cref="Undone"/> · <see cref="Rename"/> ·
    /// <see cref="SetPriority"/> · <see cref="Delete"/> · <see cref="MemoCheck"/>. 모르는 값은 건너뛴다.
    /// </summary>
    public string Kind { get; set; } = "";

    /// <summary>체크·체크 해제·고치기·지우기할 항목의 Id(메모 체크라면 메모 id). 추가에는 쓰지 않는다.</summary>
    public Guid? Target { get; set; }

    /// <summary>
    /// 추가할 한 줄(PC의 빠른 추가와 똑같이 읽는다, "내일 3시 회의 !1"),
    /// 이름 바꾸기라면 새 이름 그대로, 중요도라면 None · Low · Medium · High.
    /// </summary>
    public string? Text { get; set; }

    /// <summary>폰에서 누른 순간(폰의 벽시계). 같은 항목에 대한 일의 순서를 정하고, "9시"를 오전·오후로 읽는 데 쓴다.</summary>
    public DateTime At { get; set; }

    /// <summary>
    /// 그 순간의 논리적 날짜. 새벽 4시 기준으로 폰이 계산해 둔다.
    /// 월요일 밤에 체크한 것을 PC가 화요일 아침에야 받아도 월요일 몫으로 넣을 수 있게 한다.
    /// </summary>
    public DateOnly Day { get; set; }

    /// <summary>메모 체크에만: 폰이 본 본문의 몇째 줄(0부터). 그 사이 PC 에서 줄이 밀렸으면 같은 글을 찾는다.</summary>
    public int? Line { get; set; }

    /// <summary>메모 체크에만: 그 줄을 체크된 상태로 맞출지(true) 풀린 상태로 맞출지(false).</summary>
    public bool? Checked { get; set; }
}

/// <summary>phone-inbox.json. 폰만 쓴다.</summary>
public sealed class PhoneInbox
{
    public int Version { get; set; } = 1;
    public List<PhoneOp> Ops { get; set; } = new();
}

/// <summary>PC가 반영을 끝낸 폰의 일. data.json 에 남겨 다른 PC도 두 번 하지 않게 한다.</summary>
public sealed class AppliedPhoneOp
{
    public Guid Id { get; set; }

    /// <summary>반영한 논리적 날짜. 오래된 것은 지운다.</summary>
    public DateOnly Day { get; set; }
}

/// <summary>phone-view.json 의 줄 하나.</summary>
public sealed class PhoneViewItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public bool Done { get; set; }

    /// <summary>루틴의 알림 시각 또는 할 일의 마감 시각.</summary>
    public TimeOnly? Time { get; set; }

    // ── 할 일에만
    public Priority Priority { get; set; }
    public DateOnly? Due { get; set; }
    public int CarryOverCount { get; set; }

    // ── 루틴에만
    public int Streak { get; set; }

    /// <summary>연결된 메모. 그 메모가 <see cref="PhoneView.Memos"/> 에 있을 때만 채운다(없으면 null).</summary>
    public Guid? MemoId { get; set; }
}

/// <summary>phone-view.json 의 메모 하나. 휴지통·읽지 못한 메모는 오지 않는다.</summary>
public sealed class PhoneMemo
{
    public Guid Id { get; set; }

    /// <summary>줄 머리 기호를 뗀 제목(첫 비어 있지 않은 줄). 비면 "제목 없음".</summary>
    public string Title { get; set; } = "";

    /// <summary>머리말을 뺀 본문 전체. 줄바꿈은 \n 으로 맞춘다. 체크의 Line 은 이 글의 줄 번호다.</summary>
    public string Text { get; set; } = "";

    public bool Pinned { get; set; }

    /// <summary>매일 초기화 메모인지.</summary>
    public bool Daily { get; set; }

    /// <summary>마지막으로 매일 초기화한 날(yyyy-MM-dd). 없으면 null.</summary>
    public DateOnly? Reset { get; set; }

    /// <summary>머리말 tags 와 본문 #태그를 합친 것(# 없이).</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>본문의 판. 본문이 바뀌면 바뀐다.</summary>
    public string Rev { get; set; } = "";

    /// <summary>파일을 고친 시각.</summary>
    public DateTime Updated { get; set; }
}

/// <summary>phone-view.json. PC만 쓴다. 폰이 보여줄 오늘 화면 그대로.</summary>
public sealed class PhoneView
{
    public int Version { get; set; } = 1;

    /// <summary>PC가 이것을 만든 순간. 폰은 "몇 분 전 PC 기준"이라고 알려 줄 수 있다.</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>이 화면이 가리키는 논리적 날짜.</summary>
    public DateOnly Today { get; set; }

    /// <summary>폰이 자기 쪽에서 논리적 날짜를 계산할 때 쓰는 기준 시각.</summary>
    public int DayStartHour { get; set; } = 4;

    public List<PhoneViewItem> Routines { get; set; } = new();
    public List<PhoneViewItem> Tasks { get; set; } = new();

    /// <summary>
    /// 내일 이후 마감인 할 일(PC의 '예정' 탭). 가까운 날짜부터.
    /// 이 필드를 모르는 옛 폰은 그냥 건너뛴다.
    /// </summary>
    public List<PhoneViewItem> Upcoming { get; set; } = new();

    /// <summary>
    /// 반영이 끝난 폰의 일 번호. 폰은 이걸 보고 inbox 에서 지운다.
    /// 여기 없는 일은 아직 PC가 못 받은 것이니, 폰은 화면에 '반영 대기'로 얹어 보여 준다.
    /// </summary>
    public List<Guid> AppliedOps { get; set; } = new();

    /// <summary>
    /// 메모(휴지통·읽지 못함 제외). 고정 먼저, 그다음 최근에 고친 순.
    /// 이 필드를 모르는 옛 폰은 그냥 건너뛴다.
    /// </summary>
    public List<PhoneMemo> Memos { get; set; } = new();
}
