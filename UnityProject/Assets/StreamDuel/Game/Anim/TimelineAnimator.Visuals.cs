using System;
using System.Collections.Generic;
using System.Globalization;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed partial class TimelineAnimator
    {
        public Color TrackColorFor(VisualElement fighter)
        {
            // 使用当前动画快照中的演员，避免换人动画读到引擎已推进后的上场位置。
            for (int side = 0; side < visuals.Length; side++)
                if (ReferenceEquals(visuals[side].Root, fighter) && display != null)
                {
                    string id = display.Team[side][display.Active[side]].Id;
                    string track = db.Streamer(id).Type;
                    if (db.Types.TryGetValue(track, out var definition)
                        && ColorUtility.TryParseHtmlString(definition.Color, out var color)) return color;
                }
            return Color.gray;
        }

        void Render(DisplaySnapshot snapshot)
        {
            display = snapshot.Clone();
            for (int side = 0; side < 2; side++) ShowFighter(side);
        }

        void ShowFighter(int side)
        {
            var visual = visuals[side];
            var fighter = display.Team[side][display.Active[side]];
            visual.Visible = true;
            visual.Fainted = fighter.Fainted;
            visual.Portrait.image = portraits.Get(db, fighter.Id);
            visual.Portrait.tintColor = fighter.Fainted ? Color.gray : Color.white;
            visual.Portrait.style.opacity = fighter.Fainted ? 0 : 1;
            visual.Name.text = fighter.Name;
            visual.Initial.text = string.IsNullOrEmpty(fighter.Name) ? string.Empty : StringInfo.GetNextTextElement(fighter.Name);
            visual.Name.tooltip = fighter.Name;
            visual.Portrait.tooltip = fighter.Name;
            visual.Hp = visual.TrailHp = fighter.Hp;
            visual.MaxHp = fighter.MaxHp;
            visual.Root.style.opacity = 1;
            SetTranslate(visual.Root, 0, 0);
            SetTranslate(visual.Portrait, 0, fighter.Fainted ? 24 : 0);
            foreach (var chip in visual.Chips.Values) chip.RemoveFromHierarchy();
            visual.Chips.Clear();
            foreach (var chip in fighter.Chips)
            {
                var element = MakeChip(chip);
                visual.Chips.Add(chip.Key, element);
                visual.StatusRow.Add(element);
            }
            DrawHp(side);
        }

        VisualElement MakeChip(ChipData data)
        {
            var element = (icons is IStatusDurationIconProvider timed
                ? timed.Make(data.Id, data.Name, data.Remaining) : icons.Make(data.Id, data.Name))
                ?? throw new InvalidOperationException("状态图标提供者必须返回一个节点。");
            element.name = "timeline-icon-" + data.Key;
            element.pickingMode = PickingMode.Ignore;
            element.tooltip = data.Tooltip ?? data.Name;
            return element;
        }

        void DrawHp(int side)
        {
            var visual = visuals[side];
            float denominator = Math.Max(1, visual.MaxHp);
            float ratio = Mathf.Clamp01(visual.Hp / denominator);
            float trailRatio = Mathf.Clamp01(visual.TrailHp / denominator);
            // 两条固定尺寸覆盖层只缩放，不在逐帧动画中改动 ProgressBar 的布局宽度。
            visual.Fill.style.scale = new Scale(new Vector3(ratio, 1, 1));
            visual.Trail.style.scale = new Scale(new Vector3(trailRatio, 1, 1));
            visual.Fill.style.backgroundColor = ratio > 0.5f ? new Color(0.28f, 0.84f, 0.62f)
                : ratio > 0.2f ? new Color(1f, 0.76f, 0.3f) : new Color(1f, 0.35f, 0.4f);
            visual.HpText.text = Mathf.RoundToInt(visual.Hp).ToString(CultureInfo.InvariantCulture)
                + " / " + visual.MaxHp.ToString(CultureInfo.InvariantCulture);
        }

        VisualElement MakeFloat(AnimCue cue)
        {
            var label = new Label(cue.Text ?? string.Empty)
            {
                name = "timeline-float-" + cue.EventIndex.ToString(CultureInfo.InvariantCulture),
                pickingMode = PickingMode.Ignore,
                enableRichText = false
            };
            label.style.position = Position.Absolute;
            label.style.width = 280;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.fontSize = cue.Style == "crit" ? 34 : 26;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = FloatColor(cue.Style);
            Rect portraitBounds = visuals[cue.Side].Portrait.worldBound;
            Vector2 origin = stage.FloatLayer.WorldToLocal(new Vector2(portraitBounds.center.x, portraitBounds.yMin));
            label.style.left = origin.x - 140;
            label.style.top = origin.y - 44;
            stage.FloatLayer.Add(label);
            temporary.Add(label);
            return label;
        }

        VisualElement MakeBanner(AnimCue cue)
        {
            var label = new Label(EffectName(cue.Id, false))
            {
                name = "timeline-field-" + cue.EventIndex.ToString(CultureInfo.InvariantCulture),
                enableRichText = false,
                pickingMode = PickingMode.Ignore
            };
            label.style.position = Position.Absolute;
            label.style.left = Length.Percent(18);
            label.style.right = Length.Percent(18);
            label.style.top = 8;
            label.style.paddingTop = label.style.paddingBottom = 8;
            label.style.paddingLeft = label.style.paddingRight = 12;
            label.style.borderTopLeftRadius = label.style.borderTopRightRadius = 8;
            label.style.borderBottomLeftRadius = label.style.borderBottomRightRadius = 8;
            label.style.backgroundColor = new Color(0.18f, 0.23f, 0.43f, 0.96f);
            label.style.color = Color.white;
            label.style.fontSize = 22;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            stage.FxLayer.Add(label);
            temporary.Add(label);
            return label;
        }

        static Color FloatColor(string style)
        {
            switch (style)
            {
                case "damage": return new Color(1f, 0.3f, 0.36f);
                case "heal": return new Color(0.35f, 1f, 0.65f);
                case "crit": return new Color(1f, 0.82f, 0.25f);
                case "miss": return new Color(0.72f, 0.76f, 0.84f);
                case "stage-up": return new Color(0.42f, 0.76f, 1f);
                case "stage-down": return new Color(0.82f, 0.5f, 1f);
                default: return Color.white;
            }
        }

        static VisualElement Overlay(string name, Color color)
        {
            var element = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            element.style.position = Position.Absolute;
            element.style.left = element.style.right = element.style.top = element.style.bottom = 0;
            element.style.backgroundColor = color;
            return element;
        }

        static void SetTranslate(VisualElement element, float x, float y)
        {
            element.style.translate = new Translate(x, y, 0);
        }

        sealed class SideVisual : IDisposable
        {
            public readonly VisualElement Root, StatusRow, Fill, Trail;
            public readonly Image Portrait;
            public readonly Label Name, Initial, HpText;
            public readonly Dictionary<string, VisualElement> Chips = new Dictionary<string, VisualElement>(StringComparer.Ordinal);
            readonly VisualElement hpOverlay, originalFill;
            readonly ProgressBar hpBar;
            readonly StyleFloat originalFillOpacity, originalPortraitOpacity, originalRootOpacity;
            readonly StyleTranslate originalRootTranslate, originalPortraitTranslate;
            readonly Texture originalImage;
            readonly Color originalTint;
            readonly string originalTooltip;
            public float Hp, TrailHp;
            public int MaxHp;
            public bool Visible = true, Fainted;

            public SideVisual(VisualElement root)
            {
                Root = root;
                Portrait = root.Q<Image>(StageNames.Portrait)
                    ?? throw new ArgumentException("舞台缺少头像节点。");
                hpBar = root.Q<ProgressBar>(StageNames.HpBar)
                    ?? throw new ArgumentException("舞台缺少人气条节点。");
                StatusRow = root.Q<VisualElement>(StageNames.StatusRow)
                    ?? throw new ArgumentException("舞台缺少状态行节点。");
                originalRootOpacity = root.style.opacity;
                originalRootTranslate = root.style.translate;
                originalPortraitOpacity = Portrait.style.opacity;
                originalPortraitTranslate = Portrait.style.translate;
                originalImage = Portrait.image;
                originalTint = Portrait.tintColor;
                originalTooltip = Portrait.tooltip;
                originalFill = hpBar.Q<VisualElement>(className: "unity-progress-bar__progress");
                if (originalFill != null)
                {
                    originalFillOpacity = originalFill.style.opacity;
                    originalFill.style.opacity = 0;
                }
                Initial = new Label { name = "timeline-fighter-initial", enableRichText = false, pickingMode = PickingMode.Ignore };
                Initial.style.position = Position.Absolute;
                Initial.style.left = Initial.style.right = 8;
                Initial.style.top = 8;
                Initial.style.bottom = 44;
                Initial.style.fontSize = 76;
                Initial.style.unityFontStyleAndWeight = FontStyle.Bold;
                Initial.style.color = Color.white;
                Initial.style.unityTextAlign = TextAnchor.MiddleCenter;
                Portrait.Add(Initial);
                Name = new Label { name = "timeline-fighter-name", enableRichText = false, pickingMode = PickingMode.Ignore };
                Name.style.position = Position.Absolute;
                Name.style.left = Name.style.right = 8;
                Name.style.bottom = 8;
                Name.style.fontSize = 24;
                Name.style.whiteSpace = WhiteSpace.Normal;
                Name.style.unityFontStyleAndWeight = FontStyle.Bold;
                Name.style.color = Color.white;
                Name.style.backgroundColor = new Color(0.04f, 0.06f, 0.12f, 0.75f);
                Name.style.unityTextAlign = TextAnchor.MiddleCenter;
                Portrait.Add(Name);
                hpOverlay = Overlay("timeline-hp", new Color(0.08f, 0.11f, 0.2f));
                hpOverlay.style.marginLeft = hpOverlay.style.marginRight = hpOverlay.style.marginTop = hpOverlay.style.marginBottom = 0;
                hpOverlay.style.paddingLeft = hpOverlay.style.paddingRight = hpOverlay.style.paddingTop = hpOverlay.style.paddingBottom = 0;
                hpOverlay.style.top = hpOverlay.style.bottom = 3;
                hpOverlay.style.left = hpOverlay.style.right = 2;
                Trail = Overlay("timeline-hp-trail", new Color(0.92f, 0.96f, 1f, 0.85f));
                Fill = Overlay("timeline-hp-fill", new Color(0.28f, 0.84f, 0.62f));
                Trail.style.transformOrigin = Fill.style.transformOrigin =
                    new TransformOrigin(Length.Percent(0), Length.Percent(50), 0);
                hpOverlay.Add(Trail);
                hpOverlay.Add(Fill);
                HpText = new Label { name = "timeline-hp-value", enableRichText = false, pickingMode = PickingMode.Ignore };
                HpText.style.position = Position.Absolute;
                HpText.style.left = HpText.style.right = HpText.style.top = HpText.style.bottom = 0;
                // Label 默认边距会吃掉人气条的内容高度，固定覆盖层内保留完整的一行中文字体高度。
                HpText.style.marginLeft = HpText.style.marginRight = HpText.style.marginTop = HpText.style.marginBottom = 0;
                HpText.style.paddingLeft = HpText.style.paddingRight = HpText.style.paddingTop = HpText.style.paddingBottom = 0;
                HpText.style.minHeight = 24;
                HpText.style.whiteSpace = WhiteSpace.NoWrap;
                HpText.style.fontSize = 16;
                HpText.style.color = Color.white;
                HpText.style.unityTextAlign = TextAnchor.MiddleCenter;
                hpOverlay.Add(HpText);
                hpBar.Add(hpOverlay);
            }

            public void Dispose()
            {
                foreach (var chip in Chips.Values) chip.RemoveFromHierarchy();
                Chips.Clear();
                Initial.RemoveFromHierarchy();
                Name.RemoveFromHierarchy();
                hpOverlay.RemoveFromHierarchy();
                if (originalFill != null) originalFill.style.opacity = originalFillOpacity;
                Root.style.opacity = originalRootOpacity;
                Root.style.translate = originalRootTranslate;
                Portrait.style.opacity = originalPortraitOpacity;
                Portrait.style.translate = originalPortraitTranslate;
                Portrait.image = originalImage;
                Portrait.tintColor = originalTint;
                Portrait.tooltip = originalTooltip;
            }
        }

        sealed class DisplaySnapshot
        {
            public readonly int[] Active = new int[2];
            public readonly List<DisplayFighter>[] Team = { new List<DisplayFighter>(), new List<DisplayFighter>() };

            public static DisplaySnapshot Copy(BattleViewModel view)
            {
                if (view == null || view.Sides == null || view.Sides.Length != 2)
                    throw new ArgumentException("动画需要包含双方的视图快照。");
                var snapshot = new DisplaySnapshot();
                for (int side = 0; side < 2; side++)
                {
                    var source = view.Sides[side];
                    if (source == null || source.Team == null || source.Active < 0 || source.Active >= source.Team.Count)
                        throw new ArgumentException("动画快照的上场位置无效。");
                    snapshot.Active[side] = source.Active;
                    foreach (var fighter in source.Team)
                    {
                        if (fighter == null) throw new ArgumentException("动画快照的主播不能为空。");
                        var copy = new DisplayFighter { Id = fighter.Id, Name = fighter.Name, Hp = fighter.Hp,
                            MaxHp = fighter.MaxHp, Fainted = fighter.Fainted };
                        if (fighter.Statuses != null)
                            foreach (var chip in fighter.Statuses)
                                copy.Chips.Add(new ChipData { Key = ChipKey(chip.IsStatus, chip.Id), Id = chip.Id,
                                    Name = chip.Name, Tooltip = chip.Tooltip,
                                    Remaining = chip.Turns > 0 ? (int?)chip.Turns : null });
                        snapshot.Team[side].Add(copy);
                    }
                }
                return snapshot;
            }

            public DisplaySnapshot Clone()
            {
                var copy = new DisplaySnapshot();
                for (int side = 0; side < 2; side++)
                {
                    copy.Active[side] = Active[side];
                    foreach (var fighter in Team[side]) copy.Team[side].Add(fighter.Clone());
                }
                return copy;
            }
        }

        sealed class DisplayFighter
        {
            public string Id, Name;
            public int Hp, MaxHp;
            public bool Fainted;
            public readonly List<ChipData> Chips = new List<ChipData>();

            public DisplayFighter Clone()
            {
                var copy = new DisplayFighter { Id = Id, Name = Name, Hp = Hp, MaxHp = MaxHp, Fainted = Fainted };
                // 芯片记录只在创建时赋值，播放时仅替换列表，不修改共享记录。
                copy.Chips.AddRange(Chips);
                return copy;
            }
        }

        sealed class ChipData
        {
            public string Key, Id, Name, Tooltip;
            public int? Remaining;
        }

        sealed class LogSnapshot
        {
            public readonly int[][] Hp;
            public LogSnapshot(LogEntry entry)
            {
                if (entry.SnapHp == null) return;
                Hp = new int[entry.SnapHp.Length][];
                for (int side = 0; side < Hp.Length; side++)
                    Hp[side] = entry.SnapHp[side] == null ? null : (int[])entry.SnapHp[side].Clone();
            }
        }
    }
}
