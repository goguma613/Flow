using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Flow.Models;
using Flow.Services;
using Flow.ViewModels;

namespace Flow.Tests;

internal static partial class Program
{
    // ───────────────────────── 반복 · 뒤에 고친 것

    /// <summary>v1.13.0 검토에서 미뤄 두었다가 고친 작은 것들.</summary>
    private static void RepeatFixTests()
    {
        var today = D(2026, 11, 23);   // 월요일

        // ── 문장: 날짜 여럿에 '일'을 끝에 한 번만 적어도 읽는다
        QuickAddResult P(string s) => QuickAddParser.Parse(s, today);
        string R(string s) => P(s).Repeat is { } r ? $"{RepeatEngine.Describe(r)} @{r.Anchor:yyyy-MM-dd} | {P(s).Title}" : "없음 | " + P(s).Title;

        Check("10,25일", R("매월 10,25일 송금"), "매월 10·25일 @2026-11-25 | 송금");
        Check("10·25일(줄에 보이는 글 그대로)", R("매월 10·25일 송금"), "매월 10·25일 @2026-11-25 | 송금");
        Check("1, 15, 말일", R("매월 1, 15, 말일 급여"), "매월 1·15일·말일 @2026-11-30 | 급여");
        Check("3개월마다 5,20일", R("3개월마다 5,20일 점검"), "3개월마다 5·20일 @2026-12-05 | 점검");
        Check("금액은 날짜가 아니다", R("매월 5,000원 저축"), "매월 23일 @2026-11-23 | 5,000원 저축");
        Check("쉼표로 이은 맨 숫자뿐이면 날짜가 아니다", R("매월 10, 20 정산"), "매월 23일 @2026-11-23 | 10, 20 정산");
        Check("날짜 뒤의 개수는 제목", R("매월 10일 3개 주문"), "매월 10일 @2026-12-10 | 3개 주문");
        Check("없는 날짜는 반복이 아니다", P("매월 10,45일 송금").Repeat, null);

        // ── n일마다: 문장이 읽는 만큼(세 자리) 반복 창도 받는다
        string Text(RepeatEditorState s) => RepeatEngine.Describe(s.Build());

        var far = new RepeatRule { Unit = RepeatUnit.EveryDays, Every = 180, Anchor = today, Sticky = true };
        var farEditor = new RepeatEditorState(far, today);
        Check("180일마다를 열면 180", farEditor.Every, 180);
        Check("그대로 저장하면 그대로", (Text(farEditor), farEditor.Build().Anchor), ("180일마다", today));
        farEditor.Every = 5000;
        Check("n일마다의 위 끝은 999", farEditor.Every, 999);
        farEditor.SetTab(RepeatEditorState.TabWeekly);
        farEditor.Every = 5000;
        Check("n주마다의 위 끝은 99 그대로", farEditor.Every, 99);

        // ── 매년: 간격이 글에 나오고, 반복 창이 말없이 1 로 되돌리지 않는다
        var twoYears = new RepeatRule { Unit = RepeatUnit.Yearly, Every = 2, Month = 5, Day = 31, Anchor = D(2027, 5, 31), Sticky = true };
        Check("2년마다", RepeatEngine.Describe(twoYears), "2년마다 5/31");
        Check("매년은 그대로", RepeatEngine.Describe(new RepeatRule { Unit = RepeatUnit.Yearly, Month = 5, Day = 31 }), "매년 5/31");
        var yearEditor = new RepeatEditorState(twoYears, today);
        var yearSaved = yearEditor.Build();
        Check("창에서 그대로 저장하면 2년마다 그대로", (RepeatEngine.Describe(yearSaved), yearSaved.Every, yearSaved.Anchor), ("2년마다 5/31", 2, D(2027, 5, 31)));
        yearEditor.SetTab(RepeatEditorState.TabMonthly);
        yearEditor.SetTab(RepeatEditorState.TabYearly);
        Check("다른 탭에 다녀와도 그대로", yearEditor.Build().Every, 2);
        var newYear = new RepeatEditorState(RepeatEditorState.DefaultFor(today), today);
        newYear.SetTab(RepeatEditorState.TabYearly);
        Check("새로 고르는 매년은 1년마다", (Text(newYear), newYear.Build().Every), ("매년 11/23", 1));

        // ── 더 새 판(Version)이 적은 규칙은 아는 척하지 않는다
        var newer = M25();
        newer.Version = 2;
        Check("새 판의 규칙은 모르는 것", (RepeatEngine.IsKnown(newer), RepeatEngine.Describe(newer)), (false, "반복 정보 없음"));
        Check("지금 판은 안다", RepeatEngine.IsKnown(M25()), true);
        var held = new Routine { Archived = true, Repeat = newer };
        Check("오늘에 내놓지 않는다", (DayEngine.HasRule(held), DayEngine.IsRepeatMissing(held), DayEngine.IsDue(held, D(2026, 11, 25))), (false, true, false));
        var newerEditor = new RepeatEditorState(newer, today);
        Check("반복 창은 기본값에서 시작", Text(newerEditor), "매월 23일");

        // ── 칩으로 반복을 골라 둔 채 문장에 요일을 적었다
        var chip = M25();
        QuickAddResult Tab(string s) => QuickAddParser.Parse(s, today, assumeRoutine: true);

        var withDay = ItemFactory.WithChip(Tab("월 운동"), "월 운동", chip, today);
        Check("문장이 요일을 말하면 칩을 입히지 않는다",
            (withDay.Repeat is null, string.Join(",", withDay.Days), withDay.Title), (true, "Monday", "운동"));

        var plainTitle = ItemFactory.WithChip(Tab("관리비"), "관리비", chip, today);
        Check("요일 없는 문장에는 칩", (RepeatEngine.Describe(plainTitle.Repeat!), plainTitle.Days.Count, plainTitle.Title), ("매월 25일", 0, "관리비"));
        Check("칩의 규칙은 사본", ReferenceEquals(plainTitle.Repeat, chip), false);
        Check("'매일'이라 적으면 문장이 이긴다", ItemFactory.WithChip(Tab("매일 물"), "매일 물", chip, today).Repeat, null);
        Check("문장의 반복이 칩을 이긴다",
            RepeatEngine.Describe(ItemFactory.WithChip(Tab("3일마다 화분"), "3일마다 화분", chip, today).Repeat!), "3일마다");
        Check("칩을 안 골랐으면 그대로", ItemFactory.WithChip(Tab("관리비"), "관리비", null, today).Repeat, null);
        Check("칩을 고른 뒤 날이 넘어갔으면 첫 차례를 당긴다",
            ItemFactory.WithChip(Tab("관리비"), "관리비", chip, D(2026, 12, 1)).Repeat!.Anchor, D(2026, 12, 25));

        var weeklyChip = new RepeatRule { Unit = RepeatUnit.Weekly, Days = { 2, 4 } };   // 지나가는 화 · 목(옛 방식)
        var legacyChip = ItemFactory.WithChip(Tab("청소"), "청소", weeklyChip, today);
        Check("옛 방식 칩은 요일만", (legacyChip.Repeat is null, string.Join(",", legacyChip.Days)), (true, "Tuesday,Thursday"));

        // ── 폰: 반복 정보를 아직 못 받은 루틴에 온 체크는 버리지 않고 남겨 둔다
        var day28 = D(2026, 11, 28);
        {
            var data = new AppData { LastLogicalDate = day28 };
            var pay = new Routine { Title = "정산", Archived = true };   // 규칙 파일이 아직 안 내려왔다
            data.Routines.Add(pay);

            var done = Op(PhoneOp.RepeatDone, day28, 10, target: pay.Id);
            var rename = Op(PhoneOp.Rename, day28, 10, 1, target: pay.Id, text: "카드값 정산");

            var first = PhoneLinkEngine.Apply(data, [done, rename]);
            Check("못 받은 동안의 체크: 건너뛰지도 적지도 않는다",
                (first.Checked, first.Skipped, data.PhoneOpsApplied.Any(a => a.Id == done.Id)), (0, 0, false));
            Check("이름 바꾸기 같은 다른 일은 들어간다", (first.Edited, pay.Title), (1, "카드값 정산"));

            pay.Repeat = M25();   // 규칙 파일이 내려왔다
            var second = PhoneLinkEngine.Apply(data, [done, rename]);
            Check("받은 뒤에 반영된다", (second.Checked, pay.DoneToday, data.PhoneOpsApplied.Any(a => a.Id == done.Id)), (1, true, true));
        }

        // 하루가 넘어간 뒤에 규칙이 내려와도, 남는 항목은 그 날 끝낸 것으로 들어간다
        {
            var data = new AppData { LastLogicalDate = day28 };
            var pay = new Routine { Title = "정산", Archived = true };
            data.Routines.Add(pay);

            var late = Op(PhoneOp.RepeatDone, D(2026, 11, 27), 21, target: pay.Id);
            var waiting = PhoneLinkEngine.Apply(data, [late]);
            Check("지난 날 체크도 기다린다", (waiting.Skipped, data.PhoneOpsApplied.Count), (0, 0));

            pay.Repeat = M25();
            var applied = PhoneLinkEngine.Apply(data, [late]);
            Check("받은 뒤: 그 날 끝낸 것으로", (applied.Checked, pay.LastDoneDate, DayEngine.IsDue(pay, day28)), (1, (DateOnly?)D(2026, 11, 27), false));
        }

        // 체크했다가 푼 것도 같이 기다렸다가 순서대로 들어간다
        {
            var data = new AppData { LastLogicalDate = day28 };
            var pay = new Routine { Title = "정산", Archived = true };
            data.Routines.Add(pay);

            var done = Op(PhoneOp.RepeatDone, day28, 10, target: pay.Id);
            var undone = Op(PhoneOp.RepeatUndone, day28, 10, 5, target: pay.Id);
            PhoneLinkEngine.Apply(data, [done, undone]);
            Check("체크 · 풀기 둘 다 남는다", data.PhoneOpsApplied.Count, 0);

            pay.Repeat = M25();
            var both = PhoneLinkEngine.Apply(data, [done, undone]);
            Check("받은 뒤: 체크했다가 푼 것", (both.Checked, both.Unchecked, pay.DoneToday), (1, 1, false));
        }

        // 끝내 안 오는 규칙을 영영 기다리지는 않는다(반영 기록을 들고 있는 기간만큼)
        {
            var data = new AppData { LastLogicalDate = D(2027, 2, 1) };
            var pay = new Routine { Title = "정산", Archived = true };
            data.Routines.Add(pay);

            var old = Op(PhoneOp.RepeatDone, day28, 10, target: pay.Id);
            var result = PhoneLinkEngine.Apply(data, [old]);
            Check("45일이 지나면 건너뜀으로 적는다", (result.Skipped, data.PhoneOpsApplied.Any(a => a.Id == old.Id)), (1, true));
        }

        // ── 파일
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLOW_DATA_DIR")))
        {
            Console.WriteLine("   SKIP 반복 파일 시험 · 뒤에 고친 것 (FLOW_DATA_DIR 미지정)");
            return;
        }

        var root = Path.Combine(DataStore.Directory, "repeattest-fix");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        try
        {
            var store = new RepeatStore(root, () => true);
            var monthly = new Routine { Title = "정산", Archived = true, Repeat = M25() };
            var data = new AppData();
            data.Routines.Add(monthly);
            store.Sync(data);
            var path = Path.Combine(store.Folder, monthly.Id.ToString("D") + ".json");
            Check("규칙 파일이 있다", File.Exists(path), true);

            var gone = new AppData();   // 그 루틴을 지운 뒤의 data.json

            // 루틴을 지워도 규칙 파일은 data.json 이 저장된 뒤에 지운다 —
            // 먼저 지웠다가 그 사이 꺼지면 data.json 에는 루틴이 남고 규칙만 없어 "반복 정보 없음"이 된다
            store.DeleteLater(monthly.Id);
            Check("나중에 지우기: 아직 안 지운다", (File.Exists(path), store.HasPendingDeletes), (true, true));
            store.CompleteDeletes(gone);
            Check("저장된 뒤에 지운다", (File.Exists(path), store.HasPendingDeletes, store.Get(monthly.Id) is null), (false, false, true));

            // 지우기로 해 둔 사이 그 루틴이 다시 새 반복이 됐으면(매일로 바꿨다가 도로 매월) 지우지 않는다
            store.Sync(data);
            store.DeleteLater(monthly.Id);
            store.Sync(data);
            Check("다시 쓰는 규칙이면 지우기를 거둔다", store.HasPendingDeletes, false);
            store.DeleteLater(monthly.Id);
            store.CompleteDeletes(data);
            Check("저장된 데이터가 그 규칙을 쓰면 안 지운다", (File.Exists(path), store.HasPendingDeletes), (true, false));

            // 못 쓰는 동안(폴더를 기다림)에는 미뤄 두었다가 다시 한다
            var canWrite = false;
            var blocked = new RepeatStore(root, () => canWrite);
            blocked.Scan();
            blocked.DeleteLater(monthly.Id);
            blocked.CompleteDeletes(gone);
            Check("못 쓰는 동안엔 미룬다", (File.Exists(path), blocked.HasPendingDeletes), (true, true));
            canWrite = true;
            blocked.CompleteDeletes(gone);
            Check("쓸 수 있게 되면 지운다", (File.Exists(path), blocked.HasPendingDeletes), (false, false));

            // 못 쓴 채 들고 있던 규칙도, 지우기로 했으면 버린다
            canWrite = false;
            Check("못 쓴 규칙을 들고 있다", (blocked.Sync(data), blocked.HasUnsynced), (false, true));
            blocked.DeleteLater(monthly.Id);
            Check("지우기로 하면 들고 있던 것도 버린다", blocked.HasUnsynced, false);
            canWrite = true;
            blocked.CompleteDeletes(gone);

            // 손으로 만들었거나 덜 적힌 파일: 첫 차례(Anchor)가 없으면 셀 수 없다 — "74만 일 밀림"이 되면 안 된다
            var handId = Guid.NewGuid();
            var handPath = Path.Combine(store.Folder, handId.ToString("D") + ".json");
            const string handJson = "{ \"Unit\": 3, \"MonthDays\": [25], \"Sticky\": true }";
            File.WriteAllText(handPath, handJson);

            var reader = new RepeatStore(root, () => true);
            reader.Scan();
            var hand = new Routine { Id = handId, Title = "손으로 만든 것", Archived = true };
            var handData = new AppData();
            handData.Routines.Add(hand);
            reader.Attach(handData);
            Check("첫 차례 없는 파일은 못 읽은 것", (reader.Get(handId) is null, DayEngine.IsRepeatMissing(hand), DayEngine.OverdueDays(hand, today)), (true, true, 0));
            Check("그 파일을 덮어쓰지 않는다", (reader.Sync(handData), File.ReadAllText(handPath)), (true, handJson));

            // 더 새 판이 적은 파일: 읽어 두되 계산하지 않고, 덮어쓰지도 않는다
            var newerId = Guid.NewGuid();
            var newerPath = Path.Combine(store.Folder, newerId.ToString("D") + ".json");
            const string newerJson = "{ \"Version\": 2, \"Unit\": 3, \"MonthDays\": [25], \"Anchor\": \"2026-11-25\", \"Sticky\": true }";
            File.WriteAllText(newerPath, newerJson);

            reader.Scan();
            var future = new Routine { Id = newerId, Title = "새 판이 만든 것", Archived = true };
            var futureData = new AppData();
            futureData.Routines.Add(future);
            reader.Attach(futureData);
            Check("새 판의 파일: 반복 정보 없음", (DayEngine.IsRepeatMissing(future), DayEngine.IsDue(future, D(2026, 11, 25))), (true, false));
            Check("새 판의 파일을 덮어쓰지 않는다", (reader.Sync(futureData), File.ReadAllText(newerPath)), (true, newerJson));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
