using UnityEngine.UIElements;

namespace StreamDuel.Game
{
    public static class StageNames
    {
        public const string Fighter0 = "fighter-0";
        public const string Fighter1 = "fighter-1";
        public const string Portrait = "portrait";
        public const string HpBar = "hp-bar";
        public const string StatusRow = "status-row";
        public const string FxLayer = "fx-layer";
        public const string FloatLayer = "float-layer";
        public const string FieldBanner = "field-banner";
    }

    public sealed class BattleStage : VisualElement
    {
        public VisualElement Fighter0 { get; }
        public VisualElement Fighter1 { get; }
        public VisualElement FxLayer { get; }
        public VisualElement FloatLayer { get; }
        public Label FieldBanner { get; }

        public BattleStage()
        {
            AddToClassList("battle-stage");
            style.position = Position.Relative;
            style.flexGrow = 1;

            var fighters = new VisualElement();
            fighters.AddToClassList("battle-stage__fighters");
            fighters.style.flexDirection = FlexDirection.Row;
            fighters.style.justifyContent = Justify.SpaceAround;
            fighters.style.alignItems = Align.Center;
            fighters.style.flexGrow = 1;
            Fighter0 = MakeFighter(StageNames.Fighter0);
            Fighter1 = MakeFighter(StageNames.Fighter1);
            fighters.Add(Fighter0);
            fighters.Add(Fighter1);
            Add(fighters);

            FxLayer = MakeLayer(StageNames.FxLayer, "battle-stage__fx-layer");
            FloatLayer = MakeLayer(StageNames.FloatLayer, "battle-stage__float-layer");
            Add(FxLayer);
            Add(FloatLayer);

            FieldBanner = new Label
            {
                name = StageNames.FieldBanner,
                enableRichText = false,
                pickingMode = PickingMode.Ignore
            };
            FieldBanner.AddToClassList("battle-stage__field-banner");
            Add(FieldBanner);
        }

        static VisualElement MakeFighter(string name)
        {
            var fighter = new VisualElement { name = name };
            fighter.AddToClassList("battle-stage__fighter");

            var portrait = new Image { name = StageNames.Portrait };
            portrait.AddToClassList("battle-stage__portrait");
            fighter.Add(portrait);

            var hpBar = new ProgressBar { name = StageNames.HpBar, lowValue = 0, highValue = 100 };
            hpBar.AddToClassList("battle-stage__hp-bar");
            fighter.Add(hpBar);

            var statusRow = new VisualElement { name = StageNames.StatusRow };
            statusRow.AddToClassList("battle-stage__status-row");
            statusRow.style.flexDirection = FlexDirection.Row;
            statusRow.style.flexWrap = Wrap.Wrap;
            fighter.Add(statusRow);
            return fighter;
        }

        static VisualElement MakeLayer(string name, string className)
        {
            // 覆盖舞台的表现层不截获下层按钮或头像的输入。
            var layer = new VisualElement { name = name, pickingMode = PickingMode.Ignore };
            layer.AddToClassList(className);
            layer.style.position = Position.Absolute;
            layer.style.left = 0;
            layer.style.top = 0;
            layer.style.right = 0;
            layer.style.bottom = 0;
            return layer;
        }
    }
}
