using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    // 只检查展示层契约；人工视图和提示不回写真实对局，也不宣称是规则或人工视觉验收。
    internal static class T68AnimatorChecks
    {
        internal static IReadOnlyList<string> Run(ContentDb db, Battle source, BattleStage stage)
        {
            if (db == null || source == null || stage == null) throw new ArgumentNullException("动画检查参数不能为空。");
            Require(stage.panel != null && Finite(stage.worldBound.width) && stage.worldBound.width > 0
                && Finite(stage.Fighter0.worldBound.width) && stage.Fighter0.worldBound.width > 0,
                "定向检查的独立舞台必须先挂到面板并完成布局。");
            Require(stage.Q("timeline-hp") == null, "定向检查需要未绑定其他播放器的独立舞台。");
            string sourceRecord = source.Record.ToJson();
            ulong sourceHash = source.StateHash();
            var checks = new List<string>();
            var foreignFx = new VisualElement { name = "t68-check-existing-fx" };
            var foreignFloat = new VisualElement { name = "t68-check-existing-float" };
            stage.FxLayer.Add(foreignFx);
            stage.FloatLayer.Add(foreignFloat);
            var portraits = new BorrowedPortraits();
            var effects = new BorrowedFx(stage.FxLayer);
            var icons = new BorrowedIcons();
            var resolutions = new List<string>();
            TimelineAnimator animator = null;
            try
            {
                animator = new TimelineAnimator(stage, db, portraits, effects, icons, id =>
                {
                    resolutions.Add(id);
                    return "resolved/" + id;
                });
                Concurrent(source, stage, animator, foreignFx, foreignFloat);
                checks.Add("展示定向：相同起点的闪白与跳字同时开始、同时推进，结束后清理临时节点。");
                DelayedEnter(source, stage, animator, foreignFx, foreignFloat);
                checks.Add("展示定向：第二方延迟 Enter 在起点前隐藏，第一方先入场，起点后第二方才开始滑入。");
                RecordedHp(source, stage, animator, foreignFx, foreignFloat);
                checks.Add("展示定向：HP 中段平滑且残影落后，终点来自真实日志快照而不是提示 Amount。");
                LargeTickAndSkip(source, stage, animator, foreignFx, foreignFloat);
                checks.Add("展示定向：一次大 Tick 跨过施放、换人、状态和倒下节点，与 Skip 的完整展示终态相同。");
                InputIsolation(source, stage, animator, foreignFx, foreignFloat);
                checks.Add("展示定向：Prepare/Play 后修改原视图、芯片、提示内容和列表，不影响已准备的展示。");
                Projectiles(source, stage, animator, effects, resolutions, foreignFx, foreignFloat);
                checks.Add("展示定向：Projectile 使用注入资源映射，各提示仅调用一次 IFxPlayer，方向和时长正确；重播清理自有临时节点。");
                animator.Dispose();
                animator.Dispose();
                Require(portraits.DisposeCount == 0 && effects.DisposeCount == 0 && icons.DisposeCount == 0,
                    "播放器释放了调用方借出的 provider。");
                Require(stage.Q("timeline-hp") == null && stage.Q("timeline-fighter-name") == null,
                    "Dispose 后仍残留播放器的头像标签或人气覆盖层。");
                Clean(stage, foreignFx, foreignFloat);
                Require(sourceHash == source.StateHash() && sourceRecord == source.Record.ToJson(),
                    "展示定向检查改变了真实对局或回放。");
                checks.Add("展示定向：重复 Dispose 不释放借用 provider，移除自有覆盖层，保留调用方节点，真实状态哈希及回放不变。");
                return checks.AsReadOnly();
            }
            finally
            {
                animator?.Dispose();
                effects.Dispose();
                icons.Dispose();
                portraits.Dispose();
                foreignFx.RemoveFromHierarchy();
                foreignFloat.RemoveFromHierarchy();
            }
        }

        static void Concurrent(Battle source, BattleStage stage, TimelineAnimator animator, VisualElement foreignFx, VisualElement foreignFloat)
        {
            var before = Fixture(source, false);
            var after = Fixture(source, true);
            animator.Prepare(source, before, after);
            animator.Play(new[]
            {
                Cue(CueKind.Flash, 0, 1, 0),
                Cue(CueKind.FloatText, 0, 1, 0, text: "并行展示", style: "damage")
            });
            var flash = stage.Q("timeline-flash");
            var floating = stage.FloatLayer.Children().OfType<Label>().Single();
            Require(animator.IsPlaying && flash != null && floating.text == "并行展示", "同起点提示没有同时创建。");
            Require(Mathf.Abs(flash.style.opacity.value - 0.8f) < 0.0001f, "闪白没有从指定透明度开始。");
            animator.Tick(0.25f);
            Require(animator.IsPlaying && flash.parent != null && floating.parent == stage.FloatLayer
                && flash.style.opacity.value > 0 && flash.style.opacity.value < 0.8f
                && floating.style.opacity.value > 0 && floating.style.opacity.value < 1
                && floating.style.translate.value.y.value < 0,
                "同起点的闪白或跳字未在同一时间段推进。");
            animator.Tick(1);
            Require(!animator.IsPlaying, "提示超过时长后没有结束。");
            Final(stage, after);
            Clean(stage, foreignFx, foreignFloat);
        }

        static void DelayedEnter(Battle source, BattleStage stage, TimelineAnimator animator, VisualElement foreignFx, VisualElement foreignFloat)
        {
            var before = Fixture(source, false);
            var after = Fixture(source, true);
            animator.Prepare(source, before, after);
            animator.Play(new[]
            {
                Cue(CueKind.Enter, 0, 0.4, 0, id: before.Sides[0].Team[0].Id),
                Cue(CueKind.Enter, 0.4, 0.4, 1, id: before.Sides[1].Team[0].Id)
            });
            Require(stage.Fighter1.style.opacity.value == 0, "延迟 Enter 在播放起点已提前显示。");
            animator.Tick(0.2f);
            Require(stage.Fighter1.style.opacity.value == 0
                && stage.Fighter0.style.opacity.value > 0 && stage.Fighter0.style.opacity.value < 1
                && stage.Fighter0.style.translate.value.x.value < 0,
                "第一方入场期间，延迟 Enter 没有保持隐藏或第一方没有正常滑入。");
            animator.Tick(0.25f);
            Require(stage.Fighter0.style.opacity.value == 1
                && stage.Fighter1.style.opacity.value > 0 && stage.Fighter1.style.opacity.value < 1
                && stage.Fighter1.style.translate.value.x.value > 0,
                "延迟 Enter 经过自己的起点后没有开始从右侧滑入。");
            animator.Tick(1);
            Final(stage, after);
            Clean(stage, foreignFx, foreignFloat);
        }

        static void RecordedHp(Battle source, BattleStage stage, TimelineAnimator animator, VisualElement foreignFx, VisualElement foreignFloat)
        {
            int logIndex = -1, recordedHp = 0;
            for (int i = 0; i < source.Log.Count; i++)
            {
                var hp = source.Log[i].SnapHp;
                if (hp == null || hp.Length == 0 || hp[0] == null || hp[0].Length == 0 || hp[0][0] <= 0) continue;
                logIndex = i;
                recordedHp = hp[0][0];
                break;
            }
            Require(logIndex >= 0, "完整对局没有可用于展示检查的真实 HP 快照。");
            var before = Fixture(source, false);
            var after = Fixture(source, true);
            before.Sides[0].Team[0].Hp = 0;
            after.Sides[0].Team[0].Hp = recordedHp;
            var cue = Cue(CueKind.HpBar, 0, 1, 0);
            cue.LogIndex = logIndex;
            cue.Amount = int.MinValue;
            animator.Prepare(source, before, after);
            animator.Play(new[] { cue, Cue(CueKind.End, 2, 0, -1) });
            animator.Tick(0.5f);
            float fill = stage.Fighter0.Q("timeline-hp-fill").style.scale.value.value.x;
            float trail = stage.Fighter0.Q("timeline-hp-trail").style.scale.value.value.x;
            float target = recordedHp / (float)after.Sides[0].Team[0].MaxHp;
            Require(fill > 0 && fill < target && trail >= 0 && trail < fill,
                "HP 没有按日志目标平滑变化，或残影没有延迟跟随。");
            animator.Tick(0.5f);
            Require(animator.IsPlaying
                && Mathf.Abs(stage.Fighter0.Q("timeline-hp-fill").style.scale.value.value.x - target) < 0.0001f
                && Mathf.Abs(stage.Fighter0.Q("timeline-hp-trail").style.scale.value.value.x - target) < 0.0001f
                && stage.Fighter0.Q<Label>("timeline-hp-value").text == recordedHp + " / " + after.Sides[0].Team[0].MaxHp,
                "HP 段结束但整条时间线尚未结束时，没有精确到达真实日志目标。");
            animator.Tick(1);
            Final(stage, after);
            Clean(stage, foreignFx, foreignFloat);
        }

        static void LargeTickAndSkip(Battle source, BattleStage stage, TimelineAnimator animator, VisualElement foreignFx, VisualElement foreignFloat)
        {
            var before = Fixture(source, false);
            var after = Fixture(source, true);
            Require(after.Sides[0].Team.Count > 1, "展示夹具需要至少两个队员。");
            after.Sides[0].Active = 1;
            after.Sides[1].Team[0].Hp = 0;
            after.Sides[1].Team[0].Fainted = true;
            var incoming = Cue(CueKind.SwitchIn, 0.5, 0.35, 0, 1, id: after.Sides[0].Team[1].Id);
            incoming.EventIndex = 1;
            var enter = Cue(CueKind.Enter, 0.85, 0.4, 0, 1, id: incoming.Id);
            enter.EventIndex = 2;
            var chip = Cue(CueKind.StatusIcon, 1.25, 0.3, 0, 1, id: "t68-display-status");
            chip.Amount = 1;
            var cues = new[]
            {
                Cue(CueKind.Cast, 0, 0.2, 0),
                Cue(CueKind.SwitchOut, 0.2, 0.3, 0), incoming, enter, chip,
                Cue(CueKind.FloatText, 1.25, 0.4, 1, text: "展示伤害", style: "damage"),
                Cue(CueKind.Faint, 1.65, 0.5, 1), Cue(CueKind.End, 2.15, 0, -1)
            };
            animator.Prepare(source, before, after);
            animator.Play(cues);
            animator.Tick(100);
            Require(!animator.IsPlaying, "大 Tick 跨过终点后仍在播放。");
            Final(stage, after);
            Clean(stage, foreignFx, foreignFloat);
            string natural = VisibleState(stage);
            animator.Prepare(source, before, after);
            animator.Play(cues);
            animator.SkipToEnd();
            Require(!animator.IsPlaying, "Skip 未立即结束。");
            Final(stage, after);
            Clean(stage, foreignFx, foreignFloat);
            Require(natural == VisibleState(stage), "大 Tick 与 Skip 的展示终态不一致。");
        }

        static void InputIsolation(Battle source, BattleStage stage, TimelineAnimator animator, VisualElement foreignFx, VisualElement foreignFloat)
        {
            var before = Fixture(source, false);
            var after = Fixture(source, true);
            var expected = Fixture(source, true);
            string originalName = before.Sides[0].Team[0].Name;
            animator.Prepare(source, before, after);
            before.Sides[0].Team[0].Name = "不应显示的前视图修改";
            before.Sides[0].Team.Clear();
            after.Sides[0].Team[0].Name = "不应显示的后视图修改";
            after.Sides[0].Team[0].Statuses[0].Name = "不应显示的芯片修改";
            after.Sides[0].Team[0].Statuses.Clear();
            after.Sides[0].Team.Clear();
            var flash = Cue(CueKind.Flash, 0, 0.8, 0);
            var floating = Cue(CueKind.FloatText, 0.1, 0.7, 0, text: "已复制的提示", style: "heal");
            var cues = new List<AnimCue> { flash, floating };
            animator.Play(cues);
            Require(stage.Fighter0.Q<Label>("timeline-fighter-name").text == originalName,
                "Play 使用了外部修改后的 before 视图。");
            flash.Start = 100;
            flash.Duration = 100;
            floating.Kind = CueKind.End;
            floating.Start = 100;
            floating.Text = "不应显示的提示修改";
            cues.Clear();
            animator.Tick(0.25f);
            Require(stage.Q("timeline-flash") != null
                && stage.FloatLayer.Children().OfType<Label>().Single().text == "已复制的提示",
                "Play 未复制提示列表或延后开始提示的内容。");
            animator.Tick(1);
            Require(!animator.IsPlaying, "外部修改提示时长影响了正在播放的副本。");
            Final(stage, expected);
            Clean(stage, foreignFx, foreignFloat);
        }

        static void Projectiles(Battle source, BattleStage stage, TimelineAnimator animator, BorrowedFx effects,
            List<string> resolutions, VisualElement foreignFx, VisualElement foreignFloat)
        {
            var before = Fixture(source, false);
            var after = Fixture(source, true);
            int previous = effects.Calls.Count;
            int previousResolutions = resolutions.Count;
            animator.Prepare(source, before, after);
            var cues = new[]
            {
                Cue(CueKind.Projectile, 0.2, 0.3, 0, id: "display-left"),
                Cue(CueKind.Projectile, 0.2, 0.4, 1, id: "display-right")
            };
            animator.Play(cues);
            Require(effects.Calls.Count == previous, "Projectile 在 Start 之前调用了 provider。");
            animator.Tick(0.25f);
            Require(effects.Calls.Count == previous + 2 && resolutions.Count == previousResolutions + 2,
                "同起点 Projectile 未各调用一次资源映射和 provider。");
            var left = effects.Calls[previous];
            var right = effects.Calls[previous + 1];
            Require(left.Id == "resolved/display-left" && ReferenceEquals(left.From, stage.Fighter0)
                && ReferenceEquals(left.To, stage.Fighter1) && Mathf.Abs(left.Duration - 0.3f) < 0.0001f,
                "左方 Projectile 的资源、目标或时长错误。");
            Require(right.Id == "resolved/display-right" && ReferenceEquals(right.From, stage.Fighter1)
                && ReferenceEquals(right.To, stage.Fighter0) && Mathf.Abs(right.Duration - 0.4f) < 0.0001f,
                "右方 Projectile 的资源、目标或时长错误。");
            animator.Tick(0.05f);
            Require(effects.Calls.Count == previous + 2, "Projectile 在多个 Tick 重复调用 provider。");
            animator.Play(Array.Empty<AnimCue>());
            Require(!animator.IsPlaying, "空重播未结束正在播放的片段。");
            Final(stage, after);
            Clean(stage, foreignFx, foreignFloat);
            animator.Play(cues);
            animator.Tick(0.25f);
            Require(effects.Calls.Count == previous + 4, "重复 Play 未重新按提示调用 provider。");
            animator.SkipToEnd();
            Final(stage, after);
            Clean(stage, foreignFx, foreignFloat);
        }

        static BattleViewModel Fixture(Battle source, bool final)
        {
            var view = BattleViewModel.From(source, 0);
            for (int side = 0; side < view.Sides.Length; side++)
            {
                view.Sides[side].Active = 0;
                foreach (var fighter in view.Sides[side].Team)
                {
                    fighter.Name = (final ? "终态展示·" : "前态展示·") + fighter.Id;
                    fighter.Hp = Math.Max(1, fighter.MaxHp / (final ? 3 : 2));
                    fighter.Fainted = false;
                    fighter.Statuses = new List<ChipView>();
                    if (final) fighter.Statuses.Add(new ChipView
                    {
                        Id = "t68-display-status", Name = "展示状态", IsStatus = true, Turns = 2, Tooltip = "仅用于展示检查"
                    });
                }
            }
            return view;
        }

        static AnimCue Cue(CueKind kind, double start, double duration, int side, int slot = 0,
            string id = null, string text = null, string style = null)
        {
            return new AnimCue { Kind = kind, Start = start, Duration = duration, Side = side, Slot = slot,
                Id = id, Text = text, Style = style, EventIndex = -1, LogIndex = -1 };
        }

        static void Final(BattleStage stage, BattleViewModel expected)
        {
            for (int side = 0; side < 2; side++)
            {
                var fighter = expected.Sides[side].Team[expected.Sides[side].Active];
                var root = side == 0 ? stage.Fighter0 : stage.Fighter1;
                var portrait = root.Q<Image>(StageNames.Portrait);
                Require(root.Q<Label>("timeline-fighter-name").text == fighter.Name
                    && portrait.tooltip == fighter.Name && portrait.image != null, "终态没有使用目标上场主播。");
                Require(root.Q<Label>("timeline-hp-value").text == fighter.Hp + " / " + fighter.MaxHp,
                    "终态人气文字与视图不一致。");
                float expectedHp = fighter.Hp / (float)fighter.MaxHp;
                foreach (string name in new[] { "timeline-hp-fill", "timeline-hp-trail" })
                    Require(Mathf.Abs(root.Q(name).style.scale.value.value.x - expectedHp) < 0.0001f,
                        "终态人气或残影条未归位。");
                Require(Mathf.Abs(root.style.translate.value.x.value) < 0.0001f && root.style.opacity.value == 1
                    && Mathf.Abs(portrait.style.translate.value.x.value) < 0.0001f
                    && Mathf.Abs(portrait.style.translate.value.y.value - (fighter.Fainted ? 24 : 0)) < 0.0001f
                    && portrait.style.opacity.value == (fighter.Fainted ? 0 : 1), "终态头像或上场位仍有临时姿态。");
                var actualChips = root.Q(StageNames.StatusRow).Children().Select(chip => chip.name).OrderBy(name => name, StringComparer.Ordinal);
                var expectedChips = fighter.Statuses.Select(chip => "timeline-icon-" + (chip.IsStatus ? "status/" : "field/") + chip.Id)
                    .OrderBy(name => name, StringComparer.Ordinal);
                Require(actualChips.SequenceEqual(expectedChips), "终态状态集合与目标视图不同。");
                foreach (var chip in fighter.Statuses)
                    Require(root.Q<Label>("timeline-icon-" + (chip.IsStatus ? "status/" : "field/") + chip.Id).text == chip.Name,
                        "终态状态显示受到外部快照修改影响。");
            }
        }

        static void Clean(BattleStage stage, VisualElement foreignFx, VisualElement foreignFloat)
        {
            Require(stage.FxLayer.childCount == 1 && ReferenceEquals(stage.FxLayer[0], foreignFx)
                && stage.FloatLayer.childCount == 1 && ReferenceEquals(stage.FloatLayer[0], foreignFloat)
                && stage.Q("timeline-flash") == null, "清理遗留临时节点或删除了调用方原有节点。");
        }

        static string VisibleState(BattleStage stage)
        {
            var text = new StringBuilder();
            foreach (var root in new[] { stage.Fighter0, stage.Fighter1 })
            {
                var portrait = root.Q<Image>(StageNames.Portrait);
                text.Append(root.Q<Label>("timeline-fighter-name").text).Append('|')
                    .Append(root.Q<Label>("timeline-hp-value").text).Append('|')
                    .Append(portrait.style.opacity.value.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                    .Append(portrait.style.translate.value.y.value.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                    .Append(ColorUtility.ToHtmlStringRGBA(portrait.tintColor)).Append('|');
                foreach (var chip in root.Q(StageNames.StatusRow).Children()) text.Append(chip.name).Append(';');
            }
            return text.ToString();
        }

        static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        sealed class BorrowedPortraits : IPortraitProvider, IDisposable
        {
            readonly SolidColorPortraitProvider inner = new SolidColorPortraitProvider();
            public int DisposeCount;
            public Texture2D Get(ContentDb db, string id) { return inner.Get(db, id); }
            public void Dispose() { DisposeCount++; inner.Dispose(); }
        }

        sealed class BorrowedIcons : IStatusIconProvider, IDisposable
        {
            public int DisposeCount;
            public VisualElement Make(string id, string label)
            {
                return new Label(label) { enableRichText = false, pickingMode = PickingMode.Ignore };
            }
            public void Dispose() { DisposeCount++; }
        }

        sealed class BorrowedFx : IFxPlayer, IDisposable
        {
            public sealed class Call
            {
                public string Id;
                public VisualElement From, To;
                public float Duration;
            }
            readonly VisualElement layer;
            readonly List<VisualElement> nodes = new List<VisualElement>();
            public readonly List<Call> Calls = new List<Call>();
            public int DisposeCount;
            public BorrowedFx(VisualElement layer) { this.layer = layer; }
            public void Play(string id, VisualElement from, VisualElement to, float duration)
            {
                Calls.Add(new Call { Id = id, From = from, To = to, Duration = duration });
                var node = new VisualElement { name = "t68-provider-owned-fx" };
                nodes.Add(node);
                layer.Add(node);
            }
            public void Dispose()
            {
                DisposeCount++;
                foreach (var node in nodes) node.RemoveFromHierarchy();
                nodes.Clear();
            }
        }
    }
}
