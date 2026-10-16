using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public enum RoomState { Setup, Ready, InGame, BetweenGames, Finished }

    public sealed class MatchRoom
    {
        readonly ContentDb db;
        readonly IList<string>[] teams = { new List<string>(), new List<string>() };
        readonly bool[] ready = new bool[2];
        Ruleset ruleset;
        Match match;

        public RoomState State { get; private set; } = RoomState.Setup;
        public string HostName { get; }
        public string GuestName { get; private set; }
        public bool HasGuest => GuestName != null;
        public Ruleset Ruleset => ruleset.Copy();
        public bool[] Ready => (bool[])ready.Clone();
        public IList<string>[] Teams => new IList<string>[] { new List<string>(teams[0]), new List<string>(teams[1]) };
        public int[] Wins => match == null ? new int[2] : match.Wins;
        public int Draws => match == null ? 0 : match.Draws;
        public Battle CurrentBattle { get; private set; }
        public TurnClock Clock { get; private set; }
        public AiLevel? ComputerLevel { get; private set; }

        public MatchRoom(ContentDb db, Ruleset ruleset, string hostName)
        {
            this.db = db ?? throw new ArgumentNullException(nameof(db), "内容库不能为空。");
            HostName = hostName ?? throw new ArgumentNullException(nameof(hostName), "房主名字不能为空值。");
            this.ruleset = CopyRuleset(ruleset);
        }

        public void Join(string guestName)
        {
            RequireState(RoomState.Setup, "只有设置阶段可以加入房间。");
            if (HasGuest) throw new InvalidOperationException("房间已有一位对手。");
            if (guestName == null) throw new ArgumentNullException(nameof(guestName), "对手名字不能为空值。");
            GuestName = guestName;
        }

        public void SetRuleset(Ruleset r)
        {
            SetRuleset(0, r);
        }

        public void SetRuleset(int side, Ruleset r)
        {
            ValidateSide(side);
            if (side != 0) throw new InvalidOperationException("只有房主可以修改赛制。");
            RequireState(RoomState.Setup, "只有设置阶段可以修改赛制。");
            var next = CopyRuleset(r);
            ruleset = next;
            ready[0] = ready[1] = false;
        }

        public List<string> SubmitTeam(int side, IList<string> team)
        {
            RequireParticipant(side);
            RequireState(RoomState.Setup, "只有设置阶段可以提交队伍，请先取消准备。");
            var candidate = team == null ? null : new List<string>(team);
            var errors = ruleset.CheckTeam(db, candidate);
            if (errors.Count != 0) return errors;
            teams[side] = candidate;
            // 修改自己队伍只撤回自己的准备，保留电脑方在建房时完成的准备。
            ready[side] = false;
            return errors;
        }

        public void SetReady(int side, bool value)
        {
            RequireParticipant(side);
            if (State != RoomState.Setup && State != RoomState.Ready)
                throw new InvalidOperationException("只有开局前可以改变准备状态。");
            if (value)
            {
                ruleset.Validate(db);
                RequireValidTeam(side);
            }
            ready[side] = value;
            State = ready[0] && ready[1] ? RoomState.Ready : RoomState.Setup;
        }

        public Battle Start(uint seed)
        {
            RequireState(RoomState.Ready, "双方准备好后才能开始比赛。");
            RequireValidTeam(0);
            RequireValidTeam(1);
            // 所有构造都成功后再发布，失败开局不改变房间、时钟或种子序号。
            var nextMatch = new Match(db, ruleset, teams, new[] { HostName, GuestName }, seed);
            var nextClock = CreateClock();
            var battle = nextMatch.StartNextGame();
            match = nextMatch;
            CurrentBattle = battle;
            Clock = nextClock;
            State = RoomState.InGame;
            return battle;
        }

        public void ReportGame(Battle battle)
        {
            RequireState(RoomState.InGame, "只有当前对局可以上报结果。");
            match.ReportResult(battle);
            State = match.Over ? RoomState.Finished : RoomState.BetweenGames;
        }

        public Battle NextGame()
        {
            RequireState(RoomState.BetweenGames, "只有局间阶段可以开始下一局。");
            RequireValidTeam(0);
            RequireValidTeam(1);
            var nextClock = CreateClock();
            var battle = match.StartNextGame();
            CurrentBattle = battle;
            Clock = nextClock;
            State = RoomState.InGame;
            return battle;
        }

        public void Rematch()
        {
            RequireState(RoomState.Finished, "比赛结束后才能再来一局。");
            match = null;
            CurrentBattle = null;
            Clock = null;
            ready[0] = ready[1] = false;
            State = RoomState.Setup;
        }

        public static MatchRoom VsAi(ContentDb db, Ruleset r, string playerName, AiLevel level, uint seed)
        {
            if (!Enum.IsDefined(typeof(AiLevel), level))
                throw new ArgumentOutOfRangeException(nameof(level), "电脑难度必须是简单、普通、困难或专家。");
            var room = new MatchRoom(db, r, playerName);
            var team = TeamBuilder.Suggest(db, room.ruleset.Season, Array.Empty<string>(), seed);
            room.Join("电脑");
            var errors = room.SubmitTeam(1, team);
            if (errors.Count != 0) throw new KeyError(string.Join("；", errors));
            room.SetReady(1, true);
            room.ComputerLevel = level;
            return room;
        }

        Ruleset CopyRuleset(Ruleset value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value), "赛制不能为空。");
            var copy = value.Copy();
            copy.Validate(db);
            return copy;
        }

        TurnClock CreateClock()
        {
            return ruleset.TurnSeconds == 0 && ruleset.BankSeconds == 0
                ? null : new TurnClock(ruleset.TurnSeconds, ruleset.BankSeconds);
        }

        void RequireValidTeam(int side)
        {
            var errors = ruleset.CheckTeam(db, teams[side]);
            if (errors.Count > 0) throw new KeyError("第 " + (side + 1) + " 方队伍不合法：" + string.Join("；", errors));
        }

        void RequireParticipant(int side)
        {
            ValidateSide(side);
            if (side == 1 && !HasGuest) throw new InvalidOperationException("对手尚未加入房间。");
        }

        static void ValidateSide(int side)
        {
            if (side != 0 && side != 1) throw new ArgumentOutOfRangeException(nameof(side), "对战方下标必须是 0 或 1。");
        }

        void RequireState(RoomState expected, string message)
        {
            if (State != expected) throw new InvalidOperationException(message);
        }
    }
}
