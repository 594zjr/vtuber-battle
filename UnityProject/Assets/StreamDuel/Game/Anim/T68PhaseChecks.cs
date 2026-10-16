using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    // 只在独立舞台和新建内容副本上运行；真实 Core 产生事件，检查器不计算战斗规则。
    internal static class T68PhaseChecks
    {
        const string StatusId = "t68_review_status", FieldId = "t68_review_field";
        const float Epsilon = 0.0001f;

        internal static void Run(BattleStage stage, ICollection<string> passed)
        {
            Need(stage != null && stage.panel != null && stage.worldBound.width > 0,
                "阶段检查需要已挂载且完成布局的独立舞台。");
            Need(stage.Q("timeline-hp") == null, "阶段检查不能复用仍绑定其他播放器的舞台。");
            SwitchStages(stage);
            passed.Add("独立阶段检查：真实 SwitchOut→SwitchIn→Enter，旧/新头像切换、双侧滑动方向及配对 Enter 不二次滑入。");
            DamageAndFaint(stage);
            passed.Add("独立阶段检查：真实伤害扣血中点及残影、日志目标、Faint 中点变灰/下沉/淡出，终态回填前保持隐藏。");
            StatusAndBanner(stage);
            passed.Add("独立阶段检查：真实状态/直播间添加与移除，图标弹出/缩小和横幅滑入/停留/滑出，所有检查均早于终态回填。");
            TruncatedSwitchKeepsOldHidden(stage);
            passed.Add("独立阶段检查：真实 Switch 区间截断为未知 SwitchIn 时，旧 active 不重新出现；Skip 仍显示指定终态。");
        }

        static void SwitchStages(BattleStage stage)
        {
            for (int side = 0; side < 2; side++)
            {
                var battle = Make();
                var before = BattleViewModel.From(battle, 0);
                int from = battle.Events.Count;
                battle.ResolveTurn(side == 0 ? BattleAction.SwitchTo(1) : BattleAction.Pass(),
                    side == 1 ? BattleAction.SwitchTo(1) : BattleAction.Pass());
                var after = BattleViewModel.From(battle, 0);
                var cues = BattleTimeline.Build(battle.Events, from, battle.Events.Count);
                var outgoing = Only(cues, CueKind.SwitchOut, side);
                var incoming = Only(cues, CueKind.SwitchIn, side);
                var entered = Only(cues, CueKind.Enter, side);
                Need(outgoing.Slot == 0 && incoming.Slot == 1 && entered.Slot == 1
                    && incoming.Id == after.Sides[side].Team[1].Id
                    && entered.Id == incoming.Id && incoming.EventIndex < entered.EventIndex,
                    "Core 换人夹具没有生成已知的新旧主播配对。");
                using (var segment = new Segment(stage, battle, before, after, cues))
                {
                    var root = Root(stage, side);
                    var image = root.Q<Image>(StageNames.Portrait);
                    var oldTexture = segment.Portraits.Get(battle.Db, before.Sides[side].Team[0].Id);
                    var newTexture = segment.Portraits.Get(battle.Db, after.Sides[side].Team[1].Id);
                    Need(!ReferenceEquals(oldTexture, newTexture), "头像夹具必须区分每个主播，不能只比较同色图片。");
                    segment.At(outgoing, 0.5);
                    Need(root.Q<Label>("timeline-fighter-name").text == before.Sides[side].Team[0].Name
                        && ReferenceEquals(image.image, oldTexture), "离场中途已经提前换成新人头像。");
                    Sliding(root, side, "旧主播离场");
                    segment.Go(incoming.Start);
                    Need(root.Q<Label>("timeline-fighter-name").text == after.Sides[side].Team[1].Name
                        && ReferenceEquals(image.image, newTexture) && Near(root.style.opacity.value, 0),
                        "SwitchIn 起点没有先替换新人头像再从透明状态滑入。");
                    segment.At(incoming, 0.5);
                    Sliding(root, side, "新人入场");
                    Need(ReferenceEquals(image.image, newTexture), "SwitchIn 中途使用了旧头像。");
                    segment.Go(entered.Start);
                    Settled(root, "配对 Enter 起点重复退回屏外或重新淡入。");
                    Need(ReferenceEquals(image.image, newTexture), "配对 Enter 更换了错误头像。");
                    segment.At(entered, 0.5);
                    Settled(root, "配对 Enter 中点发生了第二次滑入。");
                    segment.Go(entered.Start + entered.Duration + 0.001);
                    Settled(root, "Enter 结束但整条时间线未结束时姿态未归位。");
                    Need(segment.Animator.IsPlaying, "阶段检查误落到了 Render(after) 之后。");
                    segment.Skip();
                }
            }
        }

        static void TruncatedSwitchKeepsOldHidden(BattleStage stage)
        {
            var battle = Make();
            var before = BattleViewModel.From(battle, 0);
            int from = battle.Events.Count;
            battle.ResolveTurn(BattleAction.SwitchTo(1), BattleAction.Pass());
            int switchIndex = Enumerable.Range(from, battle.Events.Count - from)
                .Single(i => battle.Events[i].Kind == BattleEventKind.Switch && battle.Events[i].Side == 0);
            var cues = BattleTimeline.Build(battle.Events, switchIndex, switchIndex + 1);
            var incoming = Only(cues, CueKind.SwitchIn, 0);
            Need(incoming.Slot == -1 && incoming.Id == null && !cues.Any(c => c.Kind == CueKind.Enter),
                "截断夹具必须来自真实时间线且不得包含已知 Enter。");
            // 截断范围没有新人快照；保留旧快照以证明未知 SwitchIn 本身不能重新显示旧主播。
            using (var segment = new Segment(stage, battle, before, before, cues))
            {
                segment.At(incoming, 0.5);
                Need(segment.Animator.IsPlaying && Near(stage.Fighter0.style.opacity.value, 0),
                    "T68 回归：未知 SwitchIn 用 after.Active 重新显示并滑入了旧主播。");
                segment.Go(incoming.Start + incoming.Duration + 0.001);
                Need(segment.Animator.IsPlaying && Near(stage.Fighter0.style.opacity.value, 0),
                    "未知 SwitchIn 结束后、确定 Enter/终态回填前旧主播重新出现。");
                segment.Skip();
                Settled(stage.Fighter0, "Skip 没有恢复调用者指定的终态快照。");
                Need(stage.Fighter0.Q<Label>("timeline-fighter-name").text == before.Sides[0].Team[0].Name,
                    "未知 SwitchIn 的处理破坏了 Skip 终态契约。");
            }
        }

        static void DamageAndFaint(BattleStage stage)
        {
            var battle = Make(new EffectDef { At = "post", Code = "FIXED_DAMAGE", Target = "foe", N = 1000000 });
            var before = BattleViewModel.From(battle, 0);
            int from = battle.Events.Count;
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            var after = BattleViewModel.From(battle, 0);
            var cues = BattleTimeline.Build(battle.Events, from, battle.Events.Count);
            var hp = Only(cues, CueKind.HpBar, 1);
            var faint = Only(cues, CueKind.Faint, 1);
            int target = battle.Log[hp.LogIndex].SnapHp[hp.Side][hp.Slot];
            Need(target == 0 && after.Sides[1].Team[0].Fainted && battle.Phase == Phase.Replace,
                "Core 倒下夹具必须实际扣至零且仍有后排可补位。");
            using (var segment = new Segment(stage, battle, before, after, cues))
            {
                var root = stage.Fighter1;
                var portrait = root.Q<Image>(StageNames.Portrait);
                segment.At(hp, 0.5);
                float fill = root.Q("timeline-hp-fill").style.scale.value.value.x;
                float trail = root.Q("timeline-hp-trail").style.scale.value.value.x;
                float oldRatio = before.Sides[1].Team[0].Hp / (float)before.Sides[1].Team[0].MaxHp;
                Need(fill > 0 && fill < oldRatio && trail > fill && trail < oldRatio,
                    "真实扣血中段没有平滑下降，或浅色残影没有落后于扣血条。");
                Need(Near(portrait.style.opacity.value, 1), "Faint 开始前头像已经被终态快照提前隐藏。");
                segment.Go(hp.Start + hp.Duration + 0.001);
                Need(Near(root.Q("timeline-hp-fill").style.scale.value.value.x, 0)
                    && Near(root.Q("timeline-hp-trail").style.scale.value.value.x, 0)
                    && root.Q<Label>("timeline-hp-value").text == target + " / " + before.Sides[1].Team[0].MaxHp,
                    "HP 提示结束后未精确到达真实日志目标。");
                segment.At(faint, 0.5);
                Color tint = portrait.tintColor;
                Need(tint.r > Color.gray.r && tint.r < Color.white.r
                    && Near(tint.r, tint.g) && Near(tint.g, tint.b)
                    && portrait.style.translate.value.y.value > 0 && portrait.style.translate.value.y.value < 24
                    && portrait.style.opacity.value > 0 && portrait.style.opacity.value < 1,
                    "Faint 中点没有同时变灰、下沉和淡出。");
                segment.Go(faint.Start + faint.Duration + 0.001);
                Need(segment.Animator.IsPlaying && Near(portrait.style.opacity.value, 0)
                    && Near(portrait.style.translate.value.y.value, 24) && Near(portrait.tintColor.r, Color.gray.r),
                    "Faint 段结束但整条时间线未结束时没有保持灰色/下沉/隐藏。");
                segment.Skip();
            }
        }

        static void StatusAndBanner(BattleStage stage)
        {
            var battle = Make(
                new EffectDef { At = "post", Code = "STATUS", Target = "self", Status = StatusId },
                new EffectDef { At = "post", Code = "FIELD", Target = "self", Field = FieldId, Turns = 1 });
            var before = BattleViewModel.From(battle, 0);
            int from = battle.Events.Count;
            battle.ResolveTurn(BattleAction.Skill(0), BattleAction.Pass());
            var after = BattleViewModel.From(battle, 0);
            var cues = BattleTimeline.Build(battle.Events, from, battle.Events.Count);
            var addStatus = EventCue(battle, cues, BattleEventKind.StatusAdd, StatusId);
            var addField = EventCue(battle, cues, BattleEventKind.FieldAdd, FieldId);
            var removeStatus = EventCue(battle, cues, BattleEventKind.StatusRemove, StatusId);
            var removeField = EventCue(battle, cues, BattleEventKind.FieldRemove, FieldId);
            Need(before.Sides[0].Team[0].Statuses.Count == 0 && after.Sides[0].Team[0].Statuses.Count == 0,
                "状态夹具必须同回合真实添加并移除，不能靠终态残留证明动画发生。");
            using (var segment = new Segment(stage, battle, before, after, cues))
            {
                var row = stage.Fighter0.Q(StageNames.StatusRow);
                string statusName = "timeline-icon-status/" + StatusId;
                string fieldName = "timeline-icon-field/" + FieldId;
                segment.Go(addStatus.Start);
                var status = row.Q(statusName);
                Need(status != null && Near(status.style.scale.value.value.x, 0)
                    && Near(status.style.opacity.value, 0), "状态添加起点没有从零缩放/透明开始。");
                segment.At(addStatus, 0.65);
                Need(status.style.scale.value.value.x > 1 && status.style.scale.value.value.x <= 1.2001f,
                    "状态添加中段缺少 1.2 倍弹出。");
                segment.Go(addStatus.Start + addStatus.Duration + 0.001);
                Need(ReferenceEquals(row.Q(statusName), status) && Near(status.style.scale.value.value.x, 1)
                    && Near(status.style.opacity.value, 1), "状态提示结束后没有保持完整图标。");
                Banner(segment, stage, addField, battle.Db.Fields[FieldId].Name, false, fieldName);
                Need(row.Q(fieldName) != null && row.Q(statusName) != null,
                    "直播间添加结束后未保留直播间/状态两个不同类别的图标。");
                segment.At(removeStatus, 0.01);
                Need(ReferenceEquals(row.Q(statusName), status), "状态移除起点立刻删除节点，无法缩小退出。");
                segment.At(removeStatus, 0.5);
                Need(status.parent == row && status.style.scale.value.value.x > 0
                    && status.style.scale.value.value.x < 1 && status.style.opacity.value > 0
                    && status.style.opacity.value < 1, "状态移除中点没有缩小并淡出。");
                segment.Go(removeStatus.Start + removeStatus.Duration + 0.001);
                Need(row.Q(statusName) == null, "状态移除完成后仍保留节点。");
                Banner(segment, stage, removeField, battle.Db.Fields[FieldId].Name, true, fieldName);
                Need(segment.Animator.IsPlaying && row.Q(statusName) == null && row.Q(fieldName) == null,
                    "整条时间线未结束时，已移除的状态/直播间图标仍残留。");
                segment.Skip();
            }
        }

        static void Banner(Segment segment, BattleStage stage, AnimCue cue, string label, bool removing, string chipName)
        {
            // 上一事件的尾后采样可能已经进入本横幅极小一段；此处从稳定的 10% 采样点开始。
            segment.At(cue, 0.10);
            var banner = stage.FxLayer.Q<Label>("timeline-field-" + cue.EventIndex);
            var chip = stage.Fighter0.Q(StageNames.StatusRow).Q(chipName);
            Need(banner != null && banner.text == label && chip != null, "直播间横幅/图标没有使用真实效果名称。");
            float yIn = banner.style.translate.value.y.value;
            Need(yIn > -56 && yIn < 0 && banner.style.opacity.value > 0 && banner.style.opacity.value < 1,
                "横幅滑入阶段没有从上方下移并淡入。");
            segment.At(cue, 0.5);
            Need(Near(banner.style.translate.value.y.value, 0) && Near(banner.style.opacity.value, 1),
                "横幅中段没有完整停留。");
            Need(chip.style.scale.value.value.x > 0 && (removing
                ? chip.style.scale.value.value.x < 1 && chip.style.opacity.value < 1
                : chip.style.scale.value.value.x > 1), "直播间图标中段没有相应弹出/缩小。");
            segment.At(cue, 0.85);
            Need(banner.style.translate.value.y.value < 0 && banner.style.translate.value.y.value > -56
                && banner.style.opacity.value > 0 && banner.style.opacity.value < 1,
                "横幅滑出阶段没有上移并淡出。");
            segment.Go(cue.Start + cue.Duration + 0.001);
            Need(banner.parent == null && stage.FxLayer.Q("timeline-field-" + cue.EventIndex) == null,
                "横幅提示结束后节点没有立即回收。");
            Need(removing ? chip.parent == null : chip.parent != null && Near(chip.style.scale.value.value.x, 1),
                "直播间图标未在提示结束时完成移除/归位。");
        }

        static Battle Make(params EffectDef[] effects)
        {
            var rules = Resources.Load<TextAsset>("StreamDuelRules");
            var pack = Resources.Load<TextAsset>("Packs/original");
            Need(rules != null && pack != null, "阶段夹具缺少原创资源。");
            var db = ContentDb.FromJson(rules.text, pack.text);
            Need(db.Streamers.Count >= 12, "阶段夹具需要十二位原创主播。");
            db.Personas["t68_review_neutral"] = new PersonaDef
                { Id = "t68_review_neutral", Name = "阶段检查人设", Code = "T_LOWHP_POWER" };
            db.Statuses[StatusId] = new StatusDef
                { Id = StatusId, Name = "阶段检查状态", Cat = "weak", Turns = 1, Cleanse = true };
            db.Fields[FieldId] = new FieldDef
                { Id = FieldId, Name = "阶段检查直播间", Kind = "turn", Dispel = true };
            for (int i = 0; i < 5; i++)
            {
                string id = "t68_review_skill_" + i;
                var skill = new SkillDef { Id = id, Name = "阶段检查技能" + i, Type = "talk", Cat = "status", Pp = 20 };
                if (i == 0) skill.Effects.AddRange(effects);
                db.Skills[id] = skill;
            }
            for (int i = 0; i < db.Streamers.Count; i++)
            {
                var definition = db.Streamers[i];
                definition.Persona = "t68_review_neutral";
                definition.Skills = Enumerable.Range(0, 4).Select(n => "t68_review_skill_" + n).ToList();
                definition.Fifth = "t68_review_skill_4";
                definition.Stats["spd"] = 200 - i;
            }
            var teams = new IList<string>[] { db.Streamers.Take(6).Select(s => s.Id).ToArray(),
                db.Streamers.Skip(6).Take(6).Select(s => s.Id).ToArray() };
            return new Battle(db, teams, new[] { "阶段甲方", "阶段乙方" }, 680068);
        }

        static AnimCue Only(IEnumerable<AnimCue> cues, CueKind kind, int side)
        {
            return cues.Single(c => c.Kind == kind && c.Side == side);
        }

        static AnimCue EventCue(Battle battle, IEnumerable<AnimCue> cues, BattleEventKind kind, string id)
        {
            return cues.Single(c => c.EventIndex >= 0 && battle.Events[c.EventIndex].Kind == kind && c.Id == id);
        }

        static VisualElement Root(BattleStage stage, int side) { return side == 0 ? stage.Fighter0 : stage.Fighter1; }
        static bool Near(float a, float b) { return Mathf.Abs(a - b) < Epsilon; }
        static void Need(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Sliding(VisualElement root, int side, string label)
        {
            float x = root.style.translate.value.x.value;
            Need((side == 0 ? x < 0 : x > 0) && root.style.opacity.value > 0 && root.style.opacity.value < 1,
                label + "没有按该侧方向滑动并淡入/淡出。");
        }
        static void Settled(VisualElement root, string message)
        {
            Need(Near(root.style.translate.value.x.value, 0) && Near(root.style.opacity.value, 1), message);
        }

        sealed class Segment : IDisposable
        {
            readonly Battle battle, expected;
            double time;
            public readonly UniquePortraits Portraits = new UniquePortraits();
            public readonly TimelineAnimator Animator;

            public Segment(BattleStage stage, Battle battle, BattleViewModel before, BattleViewModel after, List<AnimCue> cues)
            {
                this.battle = battle;
                expected = battle.Clone();
                // 尾部只加一个展示等待点，保留全部 Core 提示原顺序/元数据/时间；防止终态回填遮住阶段错误。
                var held = new List<AnimCue>(cues);
                held.Add(new AnimCue { Kind = CueKind.End, Side = -1, Slot = -1, EventIndex = -1,
                    LogIndex = -1, Start = BattleTimeline.TotalDuration(cues) + 1, Duration = 0 });
                Animator = new TimelineAnimator(stage, battle.Db, Portraits);
                try
                {
                    Animator.Prepare(battle, before, after);
                    Animator.Play(held);
                    Unchanged();
                }
                catch
                {
                    try { Animator.Dispose(); }
                    finally { Portraits.Dispose(); }
                    throw;
                }
            }

            public void At(AnimCue cue, double fraction) { Go(cue.Start + cue.Duration * fraction); }
            public void Go(double target)
            {
                Need(target >= time - 0.00001, "阶段采样时间倒退。");
                // Tick 接收 float，首次舍入可能停在 double 节点起点之前；补齐残差后再检查起点状态。
                while (time < target)
                {
                    float delta = (float)(target - time);
                    Need(delta > 0 && time + delta > time, "阶段采样无法推进到目标时刻。");
                    Animator.Tick(delta);
                    time += delta;
                }
                Need(Animator.IsPlaying, "阶段采样已落到整条时间线终点。");
                Unchanged();
            }
            public void Skip() { Animator.SkipToEnd(); Need(!Animator.IsPlaying, "Skip 未结束播放。"); Unchanged(); }
            public void Dispose() { try { Animator.Dispose(); Unchanged(); } finally { Portraits.Dispose(); } }

            void Unchanged()
            {
                Need(battle.StateHash() == expected.StateHash() && battle.Record.ToJson() == expected.Record.ToJson()
                    && battle.Clone().Random() == expected.Clone().Random(), "阶段播放改变了规则状态、回放或随机流。");
                Need(battle.Events.Count == expected.Events.Count && battle.Log.Count == expected.Log.Count,
                    "阶段播放增删了事件或日志。");
                for (int i = 0; i < battle.Events.Count; i++)
                {
                    var a = battle.Events[i]; var b = expected.Events[i];
                    Need(a.LogIndex == b.LogIndex && a.Turn == b.Turn && a.Side == b.Side && a.Slot == b.Slot
                        && a.Amount == b.Amount && a.Node == b.Node && a.Id == b.Id && a.Kind == b.Kind,
                        "阶段播放修改了事件条目。");
                }
                for (int i = 0; i < battle.Log.Count; i++)
                {
                    var a = battle.Log[i]; var b = expected.Log[i];
                    Need(a.Turn == b.Turn && a.Side == b.Side && a.Node == b.Node && a.Text == b.Text
                        && a.Kind == b.Kind && Same(a.SnapActive, b.SnapActive)
                        && ((a.SnapHp == null && b.SnapHp == null) || a.SnapHp != null && b.SnapHp != null
                            && a.SnapHp.Length == b.SnapHp.Length && a.SnapHp.Select((row, n) => Same(row, b.SnapHp[n])).All(x => x)),
                        "阶段播放修改了日志条目或快照。");
                }
            }
            static bool Same(int[] a, int[] b) { return a == null ? b == null : b != null && a.SequenceEqual(b); }
        }

        sealed class UniquePortraits : IPortraitProvider, IDisposable
        {
            readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            public Texture2D Get(ContentDb db, string id)
            {
                if (textures.TryGetValue(id, out var texture)) return texture;
                // 每位主播一张自制像素纹理，避免原纯色 provider 共用纹理掩盖换错头像。
                texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
                    { name = "T68Review." + id, hideFlags = HideFlags.HideAndDontSave };
                texture.SetPixel(0, 0, textures.Count % 2 == 0 ? Color.cyan : Color.magenta);
                texture.Apply(false, true);
                textures.Add(id, texture);
                return texture;
            }
            public void Dispose()
            {
                foreach (var texture in textures.Values)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
                    else UnityEngine.Object.DestroyImmediate(texture);
                }
                textures.Clear();
            }
        }
    }
}
