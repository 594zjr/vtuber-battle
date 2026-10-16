using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public static class T133TournamentSessionChecks
    {
        public static IReadOnlyList<string> RunChecks(ContentDb db, string directory)
        {
            Require(Environment.GetCommandLineArgs().Contains("--t133-smoke-test"), "存储检查仅可在 T133 隔离验收中运行。");
            string root = Path.GetFullPath(directory);
            Require(!Directory.Exists(root) && !File.Exists(root), "存储检查需要新的独占验收目录。");
            Directory.CreateDirectory(root);
            var checks = new List<string>();
            var preset = new TeamPreset { Name = "赛事存储检查", PackId = db.PackId,
                Ids = TeamBuilder.Suggest(db, null, Array.Empty<string>(), 133) };
            string code = TeamCode.Encode(preset);
            var profile = new Profile();
            profile.Ratings[Profile.DefaultRatingFormat] = new PlayerRating { Rating = 1777, Games = 5 };
            var entrants = new List<TournamentEntrant>
            {
                new TournamentEntrant { Name = "玩家", TeamCode = code, Profile = profile },
                new TournamentEntrant { Name = "普通电脑", TeamCode = code, ComputerLevel = AiLevel.Normal },
                new TournamentEntrant { Name = "困难电脑", TeamCode = code, ComputerLevel = AiLevel.Hard },
                new TournamentEntrant { Name = "专家电脑", TeamCode = code, ComputerLevel = AiLevel.Expert }
            };
            string path = Path.Combine(root, "roundrobin.json");
            var session = TournamentSession.Create(db, path, "roundrobin", entrants);
            Require(session.Tournament.Round == 0, "创建不得隐式推进首轮。");
            var pairings = session.NextRound();
            var restored = TournamentSession.Load(db, path);
            Require(restored.Controllers.SequenceEqual(new AiLevel?[] { null, AiLevel.Normal, AiLevel.Hard, AiLevel.Expert }), "控制类型与难度须逐位续存。");
            Throws<NotSupportedException>(() => ((IList<AiLevel?>)restored.Controllers)[0] = AiLevel.Expert);
            Require(session.Tournament.ToJson() == restored.Tournament.ToJson(), "赛事检查点应完整恢复。");
            checks.Add("创建、排轮、控制类型/难度与只读列表续存");

            var pairing = pairings.First(item => !item.IsBye);
            var battle = session.CreateBattle(pairing.Id);
            ulong initial = battle.StateHash();
            battle.ResolveTurn(BattleAction.Pass(), BattleAction.Pass());
            var restart = restored.CreateBattle(pairing.Id);
            Require(restart.StateHash() == initial && restart.Seed == battle.Seed,
                "未登记对局重启应从相同队伍、种子与初始状态开始。");
            battle.Surrender(1);
            string replayId = session.CompleteBattle(pairing.Id, battle);
            Require(!string.IsNullOrEmpty(replayId), "完整对局必须先归档再登记。");
            restored = TournamentSession.Load(db, path);
            Require(restored.Tournament.Pairings[pairing.Id].WinnerSide == 0, "实际胜负必须进入续赛检查点。");
            var report = (Dictionary<string, object>)MiniJson.Parse(restored.BuildReport().ToJson());
            var coverage = (Dictionary<string, object>)report["coverage"];
            Require((double)coverage["archivedGames"] == 1, "战报必须能严格复算实际归档。");
            Throws<InvalidOperationException>(() => session.CompleteBattle(pairing.Id, battle));
            checks.Add("中途退出重开同种子；完整对局归档、报分、复算战报与拒绝双报");

            var stale = TournamentSession.Load(db, path);
            string staleCore = stale.Tournament.ToJson();
            int remaining = session.Tournament.Pairings.First(item => !item.IsBye && !item.WinnerSide.HasValue).Id;
            session.ReportResult(remaining, -1);
            byte[] committed = File.ReadAllBytes(path);
            Throws<IOException>(() => stale.ReportResult(remaining, 0));
            Require(stale.Tournament.ToJson() == staleCore && File.ReadAllBytes(path).SequenceEqual(committed),
                "旧窗口报分冲突时必须保留检查点与原内存。");
            checks.Add("并行窗口旧快照拒绝覆盖；手工平局交 Core 判定");

            foreach (string format in new[] { "roundrobin", "swiss", "single", "double" })
            {
                string ratingPath = Path.Combine(root, "rating-" + format + ".json");
                var rated = TournamentSession.Create(db, ratingPath, format, entrants,
                    options: new TournamentOptions { SeedMode = TournamentSeedMode.ByRating });
                rated.NextRound();
                var reloaded = TournamentSession.Load(db, ratingPath);
                Require(reloaded.Tournament.EffectiveSeedOrder.SequenceEqual(new[] { 3, 0, 2, 1 }), "各赛制排位顺序须由 Core 当前快照确定。");
                Require(reloaded.Tournament.ToJson() == rated.Tournament.ToJson(), "各赛制排位检查点必须逐字恢复。");
                if (reloaded.Tournament.IsElimination)
                {
                    byte[] original = File.ReadAllBytes(ratingPath);
                    Throws<ArgumentException>(() => reloaded.ReportResult(reloaded.Tournament.Pairings.First(item => !item.IsBye).Id, -1));
                    Require(File.ReadAllBytes(ratingPath).SequenceEqual(original), "淘汰赛非法平局不得改档。");
                }
            }
            checks.Add("四赛制排位快照续赛、淘汰赛平局拒绝且文件不变");

            string randomPath = Path.Combine(root, "random.json");
            var random = TournamentSession.Create(db, randomPath, "single", entrants, options: new TournamentOptions { Seed = 133 });
            random.NextRound();
            Require(TournamentSession.Load(db, randomPath).Tournament.EffectiveSeedOrder.SequenceEqual(random.Tournament.EffectiveSeedOrder),
                "随机种子续赛必须保留 Core 排序。");
            Throws<ArgumentException>(() => TournamentSession.Create(db, Path.Combine(root, "bad-random.json"), "swiss", entrants,
                options: new TournamentOptions { Seed = 133 }));
            var easy = new List<TournamentEntrant> { entrants[0], new TournamentEntrant { Name = "不支持", TeamCode = code, ComputerLevel = AiLevel.Easy } };
            Throws<ArgumentException>(() => TournamentSession.Create(db, Path.Combine(root, "easy.json"), "single", easy));
            checks.Add("淘汰随机种子续存；非淘汰随机与简单难度明确拒绝");

            string damaged = Path.Combine(root, "damaged.json");
            byte[] broken = { 0xff, 0xfe, 0x7b };
            File.WriteAllBytes(damaged, broken);
            var timestamp = File.GetLastWriteTimeUtc(damaged);
            Throws<FormatException>(() => TournamentSession.Load(db, damaged));
            Throws<FormatException>(() => TournamentSession.Create(db, damaged, "roundrobin", entrants));
            Require(File.ReadAllBytes(damaged).SequenceEqual(broken) && File.GetLastWriteTimeUtc(damaged) == timestamp,
                "坏 UTF-8 读取/新建失败不得改变字节与修改时间。");
            string coreBefore = session.Tournament.ToJson();
            File.WriteAllText(path, "{损坏", new UTF8Encoding(false));
            byte[] changed = File.ReadAllBytes(path);
            Throws<IOException>(() => session.NextRound());
            Require(session.Tournament.ToJson() == coreBefore && File.ReadAllBytes(path).SequenceEqual(changed),
                "读取后的外部改档不得覆盖，也不得推进内存。");
            Throws<FormatException>(() => TournamentSession.Load(new ContentDb { PackId = "t133-other" }, randomPath));
            string packId = db.PackId;
            try
            {
                db.PackId = packId + "-changed";
                Throws<InvalidOperationException>(() => random.CreateBattle(random.Tournament.Pairings.First(item => !item.IsBye).Id));
            }
            finally { db.PackId = packId; }
            checks.Add("坏档/外部编辑不覆盖；异包与创建后内容变更拒绝");

            string autoPath = Path.Combine(root, "tiebreak.json");
            var automatic = TournamentSession.Create(db, autoPath, "single", entrants.Take(2).ToList(),
                options: new TournamentOptions { AutoTiebreak = true });
            int matchId = automatic.NextRound().Single().Id;
            Throws<InvalidOperationException>(() => automatic.ReportResult(matchId, 0));
            var drawn = automatic.CreateBattle(matchId);
            drawn.OfferDraw(0);
            drawn.RespondDraw(1, true);
            automatic.CompleteBattle(matchId, drawn);
            var continued = TournamentSession.Load(db, autoPath);
            Require(continued.Tournament.Pairings[matchId].Games.Count == 1 && !continued.Tournament.Pairings[matchId].WinnerSide.HasValue,
                "自动加赛平局必须保存为真实逐局记录，不能提前晋级。");
            var deciding = continued.CreateBattle(matchId);
            Require(deciding.Seed == automatic.Tournament.GameSeed(automatic.Tournament.Pairings[matchId], 1), "续赛必须使用 Core 下一加赛种子。");
            deciding.Surrender(1);
            string temporary = autoPath + ".tmp";
            File.WriteAllText(temporary, "独立写入中的文件", new UTF8Encoding(false));
            byte[] checkpoint = File.ReadAllBytes(autoPath);
            Throws<IOException>(() => continued.CompleteBattle(matchId, deciding));
            Require(!continued.Tournament.Pairings[matchId].WinnerSide.HasValue && File.ReadAllBytes(autoPath).SequenceEqual(checkpoint),
                "归档后存档写入失败不得推进检查点。");
            Require(File.ReadAllText(temporary) == "独立写入中的文件", "不得删除不属于本次写入的临时文件。");
            File.Delete(temporary);
            continued.CompleteBattle(matchId, deciding);
            Require(TournamentSession.Load(db, autoPath).Tournament.IsFinished, "失败后同一对局必须可幂等重试并完成赛事。");
            Require(new ReplayLibrary(continued.ArchivePath, policy: db.Policy).List().Count == 2, "重试不可重复归档或丢失平局。");
            checks.Add("自动加赛逐局恢复；归档后存档失败可重试、录像幂等且不删除外来临时文件");
            return checks.AsReadOnly();
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("T133 存储检查：" + message);
        }

        static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("T133 存储检查：预期拒绝并抛出 " + typeof(T).Name + "。");
        }
    }
}
