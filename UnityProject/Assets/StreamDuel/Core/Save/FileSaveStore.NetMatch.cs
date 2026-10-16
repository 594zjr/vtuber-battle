using System;
using System.Runtime.CompilerServices;

namespace StreamDuel.Core
{
    public sealed partial class FileSaveStore
    {
        private sealed class NetMatchSaveState
        {
            public bool Saved;
        }

        private static readonly ConditionalWeakTable<NetSession, NetMatchSaveState> netMatchSaves
            = new ConditionalWeakTable<NetSession, NetMatchSaveState>();

        public Profile RecordMatchProfile(NetSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var saved = netMatchSaves.GetValue(session, key => new NetMatchSaveState());
            lock (saved)
            {
                RequireConfirmedMatch(session);
                if (saved.Saved) throw new InvalidOperationException("这场联网比赛已经结算，不能重复记录排位分。");
                var games = session.GameResults;
                var opponent = session.OpponentRating;
                if (games.Count == 0 || opponent == null)
                    throw new InvalidOperationException("联网比赛缺少已确认的对局或对手排位快照。");

                using (OpenLock(".profile.lock"))
                {
                    // 与切季共用锁并读取最新档案，损坏来源不能被空白档案覆盖。
                    var profile = LoadProfileStrict();
                    foreach (var game in games)
                    {
                        var battle = game.Battle;
                        if (battle.IsBranch || battle.Phase != Phase.Over)
                            throw new InvalidOperationException("接手对局或未结束对局不能结算正式联网比赛。");
                        profile.Record(battle, session.MySide);
                    }
                    PlayerRating current;
                    var own = profile.Ratings.TryGetValue(Profile.DefaultRatingFormat, out current)
                        ? current.Clone() : new PlayerRating();
                    int winner = session.MatchWinner;
                    double score = winner < 0 ? 0.5 : winner == session.MySide ? 1.0 : 0.0;
                    RatingSystem.Apply(own, opponent, score);
                    profile.Ratings[Profile.DefaultRatingFormat] = own;

                    // 终局之后收到不同步仍会撤销资格；先完成所有计算，最后一次原子落盘。
                    RequireConfirmedMatch(session);
                    WriteAtomic("profile.json", ".profile.json.tmp", profile.ToJson());
                    // 写入失败不消耗本会话资格，重试不会把成功保存的一场重复计分。
                    saved.Saved = true;
                    return profile;
                }
            }
        }

        private static void RequireConfirmedMatch(NetSession session)
        {
            if (!session.IsRulesetMatch || !session.MatchConfirmed || session.State != NetState.Over
                || session.MatchWinner < -1 || session.MatchWinner > 1)
                throw new InvalidOperationException("只有双方确认完成且未出现不同步的整场联网比赛才能结算排位分。");
        }
    }
}
