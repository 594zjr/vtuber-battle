using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed class BattleViewModel
    {
        public int Turn;
        public Phase Phase;
        public string Notice, ControlledReason, StruggleName;
        public bool Struggle, FreePp;
        public SideView[] Sides;
        public List<SkillView> Skills = new List<SkillView>();
        public List<int> SwitchTargets = new List<int>();
        public List<NodeView> NodeStrip = new List<NodeView>();

        public static BattleViewModel From(Battle b, int viewerSide)
        {
            if (b == null) throw new ArgumentNullException(nameof(b), "对战不能为空");
            if (viewerSide < 0 || viewerSide >= b.Sides.Length)
                throw new ArgumentOutOfRangeException(nameof(viewerSide), "观看方必须是有效的对战方下标");

            var legal = b.LegalFor(viewerSide);
            var view = new BattleViewModel
            {
                Turn = b.Turn,
                Phase = b.Phase,
                Notice = b.Db.PackId != null && b.Db.PackId.StartsWith("fan_", StringComparison.Ordinal)
                    ? b.Db.PackNotice ?? "" : "",
                ControlledReason = legal.Controlled,
                Struggle = legal.Struggle,
                FreePp = legal.FreePp,
                StruggleName = b.Db.Struggle.Name,
                Sides = new SideView[b.Sides.Length],
                SwitchTargets = new List<int>(legal.Switches)
            };

            if (b.Phase == Phase.Replace)
            {
                // 主动换人限制不能让下播后的补位界面失去合法候选。
                view.SwitchTargets.Clear();
                if (b.Need[viewerSide])
                {
                    var side = b.Sides[viewerSide];
                    for (int index = 0; index < side.Team.Count; index++)
                        if (index != side.Active && !side.Team[index].Fainted) view.SwitchTargets.Add(index);
                }
            }

            for (int side = 0; side < b.Sides.Length; side++)
            {
                var source = b.Sides[side];
                var sideView = new SideView { Name = source.Name, Active = source.Active };
                foreach (var fighter in source.Team) sideView.Team.Add(FighterSnapshot(b, fighter));
                view.Sides[side] = sideView;
            }

            foreach (var option in legal.Skills)
            {
                var skill = b.Db.Skills[option.Id];
                var damage = b.PreviewDamage(viewerSide, option.Index);
                var skillView = new SkillView
                {
                    Index = option.Index,
                    Id = option.Id,
                    Name = skill.Name,
                    Track = b.Db.Types[skill.Type].Name,
                    TrackId = skill.Type,
                    Category = b.Db.CatNames[skill.Cat],
                    CategoryId = skill.Cat,
                    Power = skill.Power,
                    Pp = option.Pp,
                    MaxPp = option.Max,
                    Priority = option.Prio.Value,
                    BasePriority = skill.Prio,
                    PriorityParts = new List<string>(option.Prio.Parts),
                    Usable = option.Usable,
                    Fifth = option.Fifth,
                    Effectiveness = damage.Eff,
                    DamageMin = damage.Min,
                    DamageMax = damage.Max
                };
                foreach (var effect in skill.Effects)
                {
                    var text = Describe.Effect(b.Db, effect, b);
                    skillView.Effects.Add(new SkillEffectView
                    {
                        Timing = Describe.EffectTiming(effect.At),
                        Text = text.Text,
                        Dispel = text.Dispel
                    });
                }
                view.Skills.Add(skillView);
            }

            var populated = new HashSet<string>(b.Log.Where(entry => entry.Turn == b.Turn)
                .Select(entry => entry.Node), StringComparer.Ordinal);
            foreach (string node in new[] { Node.Start, Node.Switch, Node.TurnStart, Node.First,
                Node.Second, Node.TurnEnd, Node.Replace, Node.Over })
                view.NodeStrip.Add(new NodeView { Name = node, HasContent = populated.Contains(node) });
            return view;
        }

        static FighterView FighterSnapshot(Battle battle, Fighter fighter)
        {
            var persona = battle.PersonaOf(fighter);
            var view = new FighterView
            {
                Id = fighter.Def.Id,
                Name = fighter.Name,
                Tracks = Describe.Tracks(battle.Db, fighter.Def),
                Hp = fighter.Hp,
                MaxHp = fighter.MaxHp,
                Pct = fighter.Pct,
                Fainted = fighter.Fainted,
                PersonaId = persona.Id,
                PersonaName = persona.Name,
                PersonaDescription = Describe.PersonaDescription(battle.Db, persona, battle),
                // 快照允许画面自行整理展示，不能与引擎共享可变字典。
                Stages = new Dictionary<string, int>(fighter.Stages, fighter.Stages.Comparer)
            };
            foreach (var status in fighter.Statuses)
            {
                var definition = battle.Db.Statuses[status.Id];
                view.Statuses.Add(new ChipView
                {
                    Id = status.Id,
                    Name = definition.Name,
                    IsStatus = true,
                    IsControl = definition.IsControl,
                    Turns = status.Left,
                    Stacks = 0,
                    CanDispel = definition.Cleanse,
                    Duration = Describe.StatusDuration(definition, status.Left),
                    Category = Describe.StatusKind(definition),
                    Description = definition.Desc,
                    Timing = Describe.StatusTiming(definition),
                    Tooltip = Describe.StatusTooltip(definition, status.Left)
                });
            }
            foreach (var field in fighter.Fields)
            {
                var definition = battle.Db.Fields[field.Id];
                view.Statuses.Add(new ChipView
                {
                    Id = field.Id,
                    Name = definition.Name,
                    IsStatus = false,
                    IsControl = false,
                    Turns = field.Turns,
                    Stacks = field.Stacks,
                    CanDispel = definition.Dispel,
                    Duration = Describe.FieldDuration(definition, field.Turns, field.Stacks),
                    Category = Describe.FieldKind(definition),
                    Description = definition.Desc,
                    Timing = definition.When,
                    Tooltip = Describe.FieldTooltip(definition, field.Turns, field.Stacks)
                });
            }
            return view;
        }
    }

    public sealed class SideView
    {
        public string Name;
        public int Active;
        public List<FighterView> Team = new List<FighterView>();
    }

    public sealed class FighterView
    {
        public string Id, Name, Tracks, PersonaId, PersonaName, PersonaDescription;
        public int Hp, MaxHp, Pct;
        public bool Fainted;
        public Dictionary<string, int> Stages = new Dictionary<string, int>();
        public List<ChipView> Statuses = new List<ChipView>();
    }

    public sealed class ChipView
    {
        public string Id, Name, Tooltip, Duration, Category, Description, Timing;
        public bool IsStatus, IsControl, CanDispel;
        public int Turns, Stacks;
    }

    public sealed class SkillView
    {
        public int Index, Power, Pp, MaxPp, Priority, BasePriority, DamageMin, DamageMax;
        public string Id, Name, Track, TrackId, Category, CategoryId;
        public bool Usable, Fifth;
        public double Effectiveness;
        public List<string> PriorityParts = new List<string>();
        public List<SkillEffectView> Effects = new List<SkillEffectView>();
    }

    public sealed class SkillEffectView
    {
        public string Timing, Text, Dispel;
    }

    public sealed class NodeView
    {
        public string Name;
        public bool HasContent;
    }
}
