using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using StreamDuel.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public sealed class FxPreviewScreen : IScreen
    {
        public const string ScreenId = ScreenIds.EffectPreview;
        static readonly string[] FxNames = { "斜向刀光", "命中爆开", "行进波纹", "闪烁星点", "两点光束", "扩散圆环", "半透明护盾", "上升治疗光点" };
        static readonly string[] IconNames = { "控制", "弱化", "强化", "保护", "印记", "回合", "回复", "特殊效果" };
        readonly Dictionary<string, ProceduralFx> players = new Dictionary<string, ProceduralFx>(StringComparer.Ordinal);
        readonly Dictionary<string, VisualElement> cards = new Dictionary<string, VisualElement>(StringComparer.Ordinal);
        readonly List<VisualElement> icons = new List<VisualElement>();
        readonly List<Action> unbind = new List<Action>();
        ScreenRouter router;
        ScreenArgs navigation;
        ProceduralPortraitProvider portraits;
        ProceduralIcons iconProvider;
        Label activity, durationLabel;
        bool disposed = true;
        bool unavailable;
        int lastActive = -1;

        public string Id => ScreenId;
        public ContentDb Content { get; private set; }
        public VisualElement Page { get; private set; }
        public IReadOnlyDictionary<string, ProceduralFx> Players => new ReadOnlyDictionary<string, ProceduralFx>(players);
        public IReadOnlyDictionary<string, VisualElement> EffectCards => new ReadOnlyDictionary<string, VisualElement>(cards);
        public bool Paused { get; set; }
        public float Duration { get; private set; } = 1.2f;

        public VisualElement Build(ScreenRouter screenRouter, object args)
        {
            if (screenRouter == null) throw new ArgumentNullException(nameof(screenRouter));
            Dispose();
            router = screenRouter;
            navigation = args as ScreenArgs;
            disposed = false;
            unavailable = false;
            Paused = false;
            Duration = 1.2f;
            lastActive = -1;
            Page = new VisualElement { name = ScreenId + "-screen", userData = this };
            Page.style.flexGrow = 1;
            Page.style.minHeight = 0;
            Page.style.paddingLeft = Page.style.paddingRight = 22;
            Page.style.paddingTop = Page.style.paddingBottom = 12;
            Page.style.backgroundColor = (Color)new Color32(12, 17, 36, 255);

            var header = Row("fx-header");
            header.style.justifyContent = Justify.SpaceBetween;
            var title = Text("特效预览", "fx-title", 30);
            title.style.unityFont = router.Resources.BoldFont;
            title.style.color = (Color)new Color32(255, 181, 71, 255);
            header.Add(title);
            var navigationRow = Row("fx-navigation");
            Button(navigationRow, "fx-open-animation", "动画预览", () => router.Show(ScreenIds.AnimationPreview,
                new ScreenArgs { ReturnScreenId = ScreenId, ReturnArgs = navigation }));
            Button(navigationRow, "fx-back", "返回", ReturnToMenu);
            header.Add(navigationRow);
            Page.Add(header);

            var controls = Row("fx-controls");
            controls.style.flexWrap = Wrap.Wrap;
            Button(controls, "fx-play-all", "同时播放全部", PlayAll);
            Button(controls, "fx-clear", "清空特效", ClearEffects);
            Button(controls, "fx-duration-short", "0.6 秒", () => SetDuration(0.6f));
            Button(controls, "fx-duration-normal", "1.2 秒", () => SetDuration(1.2f));
            Button(controls, "fx-duration-long", "2 秒", () => SetDuration(2));
            durationLabel = Text("当前时长：1.2 秒", "fx-duration", 18);
            durationLabel.style.marginLeft = 12;
            controls.Add(durationLabel);
            Page.Add(controls);
            activity = Text("点击任一卡片播放；再次点击可叠加。颜色跟随施放方主赛道。", "fx-activity", 18);
            activity.style.color = (Color)new Color32(169, 183, 215, 255);
            Page.Add(activity);

            try
            {
                var rules = UnityEngine.Resources.Load<TextAsset>("StreamDuelRules");
                var pack = UnityEngine.Resources.Load<TextAsset>("Packs/original");
                if (rules == null || pack == null) throw new InvalidOperationException("特效预览缺少原创包或规则数据。");
                var loaded = PackLoader.LoadJson(rules.text, pack.text, "original.json", UnityPackPolicy.Load());
                if (loaded.Db == null) throw new InvalidOperationException(loaded.Error);
                Content = loaded.Db;
                if (Content.Streamers.Count == 0) throw new InvalidOperationException("预览内容包没有可显示的主播。");
                portraits = new ProceduralPortraitProvider();
                iconProvider = new ProceduralIcons();

                var scroll = new ScrollView(ScrollViewMode.Vertical) { name = "fx-scroll" };
                scroll.style.flexGrow = 1;
                scroll.style.minHeight = 0;
                scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                Page.Add(scroll);
                var grid = Row("fx-effects-grid");
                grid.style.flexWrap = Wrap.Wrap;
                for (int i = 0; i < ArtManifest.FxIds.Length; i++) AddEffectCard(grid, ArtManifest.FxIds[i], FxNames[i], i);
                scroll.Add(grid);

                var iconHeader = Row("fx-icon-controls");
                iconHeader.Add(Text("状态图标", "fx-icons-title", 24));
                Button(iconHeader, "fx-turns-3", "剩余 3", () => SetRemaining(3));
                Button(iconHeader, "fx-turns-9", "剩余 9", () => SetRemaining(9));
                Button(iconHeader, "fx-turns-none", "不显示回合数", () => SetRemaining(null));
                scroll.Add(iconHeader);
                var iconGrid = Row("fx-icons-grid");
                iconGrid.style.flexWrap = Wrap.Wrap;
                for (int i = 0; i < ArtManifest.IconIds.Length; i++)
                {
                    string id = ArtManifest.IconIds[i];
                    var slot = new VisualElement { name = "fx-icon-slot-" + id };
                    slot.style.width = Length.Percent(25);
                    slot.style.paddingLeft = slot.style.paddingRight = 6;
                    slot.style.paddingTop = slot.style.paddingBottom = 6;
                    var icon = iconProvider.Make(id, IconNames[i], 3);
                    icon.name = "fx-icon-" + id;
                    slot.Add(icon);
                    icons.Add(icon);
                    iconGrid.Add(slot);
                }
                scroll.Add(iconGrid);
                scroll.Add(Text("图形由程序生成；无图片、粒子贴图或音效。角标仅显示传入的剩余数值，不参与结算。", "fx-footer", 17));
            }
            catch (Exception error)
            {
                unavailable = true;
                foreach (var player in players.Values) player.Dispose();
                players.Clear();
                portraits?.Dispose();
                portraits = null;
                controls.SetEnabled(false);
                activity.text = "特效预览暂不可用：" + error.Message;
            }
            return Page;
        }

        void AddEffectCard(VisualElement grid, string id, string title, int index)
        {
            var wrapper = new VisualElement();
            wrapper.style.width = Length.Percent(25);
            wrapper.style.flexShrink = 0;
            wrapper.style.paddingLeft = wrapper.style.paddingRight = 6;
            wrapper.style.paddingTop = wrapper.style.paddingBottom = 6;
            var card = new VisualElement { name = "fx-card-" + id };
            card.style.height = 224;
            card.style.backgroundColor = (Color)new Color32(23, 32, 57, 255);
            card.style.borderTopLeftRadius = card.style.borderTopRightRadius = 12;
            card.style.borderBottomLeftRadius = card.style.borderBottomRightRadius = 12;
            card.style.paddingLeft = card.style.paddingRight = 10;
            card.style.paddingTop = card.style.paddingBottom = 8;
            card.Add(Text(title + " · " + id, "fx-name-" + id, 20));
            var canvas = new VisualElement { name = "fx-canvas-" + id };
            canvas.style.position = Position.Relative;
            canvas.style.flexGrow = 1;
            canvas.style.overflow = Overflow.Hidden;
            card.Add(canvas);
            var caster = Content.Streamers[index % Content.Streamers.Count];
            var target = Content.Streamers[(index + 1) % Content.Streamers.Count];
            Endpoint(canvas, "fx-from-" + id, caster, 12, id);
            Endpoint(canvas, "fx-to-" + id, target, 70, null);
            var layer = new VisualElement { name = "fx-layer-" + id, pickingMode = PickingMode.Ignore };
            layer.style.position = Position.Absolute;
            layer.style.left = layer.style.right = layer.style.top = layer.style.bottom = 0;
            canvas.Add(layer);
            Color color;
            if (!ColorUtility.TryParseHtmlString(Content.Types[caster.Type].Color, out color)) color = Color.cyan;
            var player = new ProceduralFx(layer, ignored => color, false);
            players.Add(id, player);
            cards.Add(id, card);
            Button(card, "fx-play-" + id, "播放", () => PlayEffect(id));
            wrapper.Add(card);
            grid.Add(wrapper);
        }

        VisualElement Endpoint(VisualElement canvas, string name, StreamerDef streamer, float left, string portraitName)
        {
            var endpoint = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            endpoint.style.position = Position.Absolute;
            endpoint.style.left = Length.Percent(left);
            endpoint.style.top = 8;
            endpoint.style.width = 56;
            endpoint.style.height = 88;
            var portrait = new Image { name = portraitName == null ? name + "-portrait" : "fx-portrait-" + portraitName,
                image = portraits.Get(Content, streamer.Id), pickingMode = PickingMode.Ignore };
            portrait.style.width = portrait.style.height = 48;
            portrait.style.flexShrink = 0;
            endpoint.Add(portrait);
            var nameLabel = Text(streamer.Name, name + "-name", 15);
            nameLabel.style.whiteSpace = WhiteSpace.NoWrap;
            nameLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            nameLabel.style.width = 82;
            nameLabel.style.marginLeft = -17;
            endpoint.Add(nameLabel);
            canvas.Add(endpoint);
            return endpoint;
        }

        public void SetDuration(float duration)
        {
            if (float.IsNaN(duration) || float.IsInfinity(duration) || duration <= 0)
                throw new ArgumentOutOfRangeException(nameof(duration), "预览时长必须为有限正数。");
            Duration = duration;
            if (durationLabel != null) durationLabel.text = "当前时长：" + duration.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " 秒";
        }

        public void PlayEffect(string id)
        {
            if (disposed) return;
            if (!players.TryGetValue(id, out var player)) throw new ArgumentException("没有该预览特效。", nameof(id));
            var card = cards[id];
            player.Play(id, card.Q("fx-from-" + id), card.Q("fx-to-" + id), Duration);
            RefreshActivity();
        }

        public void PlayAll()
        {
            if (disposed) return;
            foreach (string id in ArtManifest.FxIds) PlayEffect(id);
        }

        public void ClearEffects()
        {
            foreach (var player in players.Values) player.Clear();
            RefreshActivity();
        }

        public void SetRemaining(int? remaining)
        {
            foreach (var icon in icons) ProceduralIcons.UpdateRemaining(icon, remaining);
        }

        public void Tick(float dt)
        {
            if (disposed || unavailable || Paused || dt < 0 || float.IsNaN(dt) || float.IsInfinity(dt)) return;
            foreach (var player in players.Values) player.Tick(dt);
            RefreshActivity();
        }

        void RefreshActivity()
        {
            if (unavailable) return;
            int active = 0;
            foreach (var player in players.Values) active += player.ActiveCount;
            if (activity == null || active == lastActive) return;
            lastActive = active;
            activity.text = "播放中：" + active + "　｜　再次点击可叠加；颜色取施放方主赛道，结束后自动回收。";
        }

        public void ReturnToMenu()
        {
            if (!disposed) router.Show(navigation?.ReturnScreenId ?? ScreenIds.Menu, navigation?.ReturnArgs);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var remove in unbind) remove();
            unbind.Clear();
            foreach (var player in players.Values) player.Dispose();
            players.Clear();
            cards.Clear();
            icons.Clear();
            portraits?.Dispose();
            portraits = null;
            iconProvider = null;
            Content = null;
        }

        void Button(VisualElement parent, string name, string text, Action action)
        {
            var button = new Button { name = name, text = text };
            button.AddToClassList("screen-button");
            button.style.minWidth = 0;
            button.style.minHeight = 38;
            button.style.marginLeft = button.style.marginRight = 4;
            button.style.marginTop = button.style.marginBottom = 4;
            button.style.paddingLeft = button.style.paddingRight = 12;
            button.style.paddingTop = button.style.paddingBottom = 4;
            button.style.fontSize = 18;
            button.clicked += action;
            unbind.Add(() => button.clicked -= action);
            parent.Add(button);
        }

        static VisualElement Row(string name)
        {
            var row = new VisualElement { name = name };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.flexShrink = 0;
            return row;
        }

        static Label Text(string text, string name, int size)
        {
            var label = new Label(text) { name = name, enableRichText = false };
            label.style.fontSize = size;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexShrink = 0;
            return label;
        }
    }
}
