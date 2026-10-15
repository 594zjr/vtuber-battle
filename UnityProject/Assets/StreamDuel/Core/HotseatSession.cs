using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    /// <summary>同一台设备轮流选择；调用方用交接遮挡保护屏幕，战斗规则仍由 Battle 结算。</summary>
    public sealed class HotseatSession
    {
        readonly Battle battle;
        readonly bool[] submitted = new bool[2];
        readonly BattleAction[] actions = new BattleAction[2];
        readonly int[] choices = new int[2];

        public int WhoseTurn { get; private set; }
        public bool NeedsHandover { get; private set; }
        public Phase Phase { get { return battle.Phase; } }
        public int Winner { get { return battle.Winner; } }

        /// <summary>只包含已结算的事件，每次读取返回独立快照。</summary>
        public BattleRecord Record
        {
            get { return BattleRecord.FromJson(battle.Record.ToJson()); }
        }

        /// <summary>只包含已结算的日志，条目和人气快照均与会话隔离。</summary>
        public IReadOnlyList<LogEntry> Log
        {
            get
            {
                return battle.Log.Select(entry => new LogEntry
                {
                    Turn = entry.Turn, Side = entry.Side, Node = entry.Node,
                    Text = entry.Text, Kind = entry.Kind,
                    SnapActive = (int[])entry.SnapActive.Clone(),
                    SnapHp = entry.SnapHp.Select(hp => (int[])hp.Clone()).ToArray()
                }).ToList().AsReadOnly();
            }
        }

        public HotseatSession(ContentDb db, IList<string> teamA, IList<string> teamB,
            string nameA, string nameB, BattleOptions opt)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (opt == null) throw new ArgumentNullException(nameof(opt));
            ValidateTeam(teamA, nameof(teamA), opt.BanOne);
            ValidateTeam(teamB, nameof(teamB), opt.BanOne);
            if (string.IsNullOrWhiteSpace(nameA)) throw new ArgumentException("玩家名称不能为空", nameof(nameA));
            if (string.IsNullOrWhiteSpace(nameB)) throw new ArgumentException("玩家名称不能为空", nameof(nameB));
            battle = new Battle(db, new[] { teamA, teamB }, new[] { nameA, nameB }, opt);
            StartStage(0);
        }

        static void ValidateTeam(IList<string> team, string name, bool ban)
        {
            if (team == null) throw new ArgumentNullException(name);
            if (team.Count == 0 || (ban && team.Count < 2))
                throw new ArgumentException("队伍不能为空，开启禁用时至少需要两位主播", name);
        }

        public void ConfirmHandover()
        {
            if (!NeedsHandover) throw new InvalidOperationException("现在不需要交接");
            NeedsHandover = false;
        }

        public void Submit(BattleAction action)
        {
            RequireStage(Phase.Choose);
            var legal = battle.LegalFor(WhoseTurn);
            bool valid;
            switch (action.Type)
            {
                case ActionType.Pass:
                    valid = true;
                    break;
                case ActionType.Skill:
                    valid = legal.Skills.Any(skill => skill.Index == action.Index && skill.Usable);
                    break;
                case ActionType.Switch:
                    valid = legal.Switches.Contains(action.Index);
                    break;
                case ActionType.Struggle:
                    valid = legal.Struggle;
                    break;
                case ActionType.PassControlled:
                    valid = legal.Controlled != null;
                    break;
                default:
                    valid = false;
                    break;
            }
            if (!valid) throw new InvalidOperationException("现在不能选择这个行动");
            actions[WhoseTurn] = action;
            FinishSubmission();
        }

        public void SubmitReplace(int to)
        {
            RequireStage(Phase.Replace);
            var side = battle.Sides[WhoseTurn];
            if (!battle.Need[WhoseTurn] || to < 0 || to >= side.Team.Count ||
                to == side.Active || side.Team[to].Fainted)
                throw new InvalidOperationException("不能派出这位主播");
            choices[WhoseTurn] = to;
            FinishSubmission();
        }

        public void SubmitBan(int index)
        {
            RequireStage(Phase.Ban);
            if (index < 0 || index >= battle.Sides[1 - WhoseTurn].Team.Count)
                throw new ArgumentOutOfRangeException(nameof(index), "禁用位置不在对手队伍中");
            choices[WhoseTurn] = index;
            FinishSubmission();
        }

        public void SubmitLead(int index)
        {
            RequireStage(Phase.Lead);
            if (index < 0 || index >= battle.Sides[WhoseTurn].Team.Count)
                throw new ArgumentOutOfRangeException(nameof(index), "首发位置不在队伍中");
            choices[WhoseTurn] = index;
            FinishSubmission();
        }

        public BattleViewModel ViewFor(int side)
        {
            var view = BattleViewModel.From(battle, side);
            if (battle.Phase != Phase.Choose)
            {
                view.Skills.Clear();
                view.SwitchTargets.Clear();
                view.Struggle = false;
                view.FreePp = false;
                view.ControlledReason = null;
                // 主动换人的锁定不阻止补位，不能沿用 LegalFor 的 Switches。
                if (battle.Phase == Phase.Replace && battle.Need[side])
                {
                    var state = battle.Sides[side];
                    for (int i = 0; i < state.Team.Count; i++)
                        if (i != state.Active && !state.Team[i].Fainted) view.SwitchTargets.Add(i);
                }
            }
            return view;
        }

        /// <summary>终局后供统计和导出使用；进行中不公开 Battle 或待提交选择。</summary>
        public Battle CloneCompletedBattle()
        {
            if (battle.Phase != Phase.Over) throw new InvalidOperationException("对战尚未结束");
            return battle.Clone();
        }

        void RequireStage(Phase phase)
        {
            if (NeedsHandover) throw new InvalidOperationException("请先确认交接，再进行选择");
            if (WhoseTurn < 0 || battle.Phase != phase)
                throw new InvalidOperationException("现在不是此项选择的阶段");
        }

        void FinishSubmission()
        {
            int holder = WhoseTurn;
            submitted[holder] = true;
            int other = 1 - holder;
            if (!submitted[other] && (battle.Phase != Phase.Replace || battle.Need[other]))
            {
                WhoseTurn = other;
                NeedsHandover = true;
                return;
            }

            WhoseTurn = -1;
            NeedsHandover = false;
            // 禁用和首发也会立即写录像；全部缓存在会话中才能保证先选内容不外泄。
            switch (battle.Phase)
            {
                case Phase.Choose:
                    battle.ResolveTurn(actions[0], actions[1]);
                    break;
                case Phase.Ban:
                    battle.ChooseBan(0, choices[0]);
                    battle.ChooseBan(1, choices[1]);
                    break;
                case Phase.Lead:
                    battle.ChooseLead(0, choices[0]);
                    battle.ChooseLead(1, choices[1]);
                    break;
                case Phase.Replace:
                    for (int side = 0; side < 2 && battle.Phase == Phase.Replace; side++)
                        if (battle.Need[side]) battle.Replace(side, choices[side]);
                    break;
            }
            StartStage(holder);
        }

        void StartStage(int holder)
        {
            Array.Clear(submitted, 0, submitted.Length);
            Array.Clear(actions, 0, actions.Length);
            Array.Clear(choices, 0, choices.Length);
            NeedsHandover = false;
            WhoseTurn = battle.Phase == Phase.Over ? -1 : holder;
            // 单方补位没有另一份秘密选择需要保护，直接让需要补位的玩家操作。
            if (battle.Phase == Phase.Replace && battle.Need[0] != battle.Need[1])
                WhoseTurn = battle.Need[0] ? 0 : 1;
        }
    }
}
