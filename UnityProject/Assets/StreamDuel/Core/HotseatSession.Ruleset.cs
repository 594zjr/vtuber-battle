using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed partial class HotseatSession
    {
        readonly Match match;
        readonly Ruleset ruleset;
        bool gameReported;

        public bool UsesRuleset => match != null;
        public Ruleset Ruleset => ruleset == null ? null : ruleset.Copy();
        public bool MatchOver => match == null ? battle.Phase == Phase.Over : match.Over;
        public int[] Wins => match == null ? new int[2] : match.Wins;
        public int Draws => match == null ? 0 : match.Draws;
        public int GameNumber { get; private set; } = 1;
        public TurnClock Clock { get; private set; }
        public long TimerRevision { get; private set; }

        public HotseatSession(ContentDb db, IList<string> teamA, IList<string> teamB,
            string nameA, string nameB, Ruleset ruleset, uint seed)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (ruleset == null) throw new ArgumentNullException(nameof(ruleset), "赛制不能为空。");
            if (string.IsNullOrWhiteSpace(nameA)) throw new ArgumentException("玩家名称不能为空", nameof(nameA));
            if (string.IsNullOrWhiteSpace(nameB)) throw new ArgumentException("玩家名称不能为空", nameof(nameB));
            this.ruleset = ruleset.Copy();
            match = new Match(db, this.ruleset, new[] { teamA, teamB }, new[] { nameA, nameB }, seed);
            Clock = NewRulesetClock();
            battle = match.StartNextGame();
            StartStage(0);
        }

        public void NextGame()
        {
            if (match == null || battle.Phase != Phase.Over || !gameReported || match.Over)
                throw new InvalidOperationException("只有多局比赛的局间阶段可以开始下一局。");
            // 内容校验和构造成功后才替换，失败时上一局仍可导出，种子顺序也不改变。
            var nextClock = NewRulesetClock();
            var nextBattle = match.StartNextGame();
            battle = nextBattle;
            Clock = nextClock;
            gameReported = false;
            GameNumber++;
            StartStage(0);
            // 上局最后持机的可能是另一方；新局必须明确交给 A 方，等待交接不扣时。
            NeedsHandover = true;
        }

        public bool TickRuleset(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
                throw new ArgumentOutOfRangeException(nameof(seconds), "经过时间必须是有限非负秒数。");
            if (Clock == null || NeedsHandover || WhoseTurn < 0
                || (battle.Phase != Phase.Choose && battle.Phase != Phase.Replace)) return false;
            int side = WhoseTurn;
            if (!Clock.Tick(side, seconds)) return false;
            // 超时仍进入同一私密提交路径；多余经过时间不能扣给接手者或新阶段。
            if (battle.Phase == Phase.Choose) Submit(TurnClock.TimeoutAction(battle, side));
            else SubmitReplace(TurnClock.TimeoutReplacement(battle, side));
            return true;
        }

        TurnClock NewRulesetClock()
        {
            return ruleset.TurnSeconds == 0 && ruleset.BankSeconds == 0
                ? null : new TurnClock(ruleset.TurnSeconds, ruleset.BankSeconds);
        }

        void StartRulesetStage()
        {
            if (match == null) return;
            TimerRevision++;
            if (battle.Phase == Phase.Over)
            {
                if (!gameReported)
                {
                    match.ReportResult(battle);
                    gameReported = true;
                }
                if (Clock != null) { Clock.Submit(0); Clock.Submit(1); }
            }
            else if (Clock != null && (battle.Phase == Phase.Choose || battle.Phase == Phase.Replace))
            {
                Clock.StartTurn();
                if (battle.Phase == Phase.Replace)
                    for (int side = 0; side < 2; side++)
                        if (!battle.Need[side]) Clock.Submit(side);
            }
        }
    }
}
