using System;
using System.Collections.Generic;
using System.Linq;
using StreamDuel.Core;

namespace StreamDuel.Game
{
    public sealed class TournamentEntrant
    {
        public string Name;
        public string TeamCode;
        public AiLevel? ComputerLevel;
        public Profile Profile;
    }

    public sealed partial class TournamentSession
    {
        readonly string packId, contentHash;
        readonly Dictionary<int, Battle> activeBattles = new Dictionary<int, Battle>();
        readonly AiLevel?[] controllers;
        string savedText;

        public ContentDb Db { get; }
        public Tournament Tournament { get; private set; }
        public IReadOnlyList<AiLevel?> Controllers { get; }
        public string SavePath { get; }
        public string ArchivePath { get { return SavePath + ".replay-library"; } }

        TournamentSession(ContentDb db, string path, Tournament tournament, AiLevel?[] controls, string saved)
        {
            Db = db;
            SavePath = FullSavePath(path);
            Tournament = tournament;
            controllers = (AiLevel?[])controls.Clone();
            Controllers = Array.AsReadOnly(controllers);
            packId = db.PackId;
            contentHash = db.ContentHash();
            savedText = saved;
        }

        public static TournamentSession Create(ContentDb db, string path, string format,
            IList<TournamentEntrant> entrants, int rounds = 0, TournamentOptions options = null)
        {
            if (db == null) throw new ArgumentNullException(nameof(db), "赛事内容不能为空。");
            if (entrants == null || entrants.Count < 2 || entrants.Count > 16)
                throw new ArgumentException("本地赛事需要 2～16 位选手。", nameof(entrants));
            var tournament = new Tournament(db, format, rounds, options);
            var controls = new AiLevel?[entrants.Count];
            for (int i = 0; i < entrants.Count; i++)
            {
                var entrant = entrants[i] ?? throw new ArgumentException("参赛选手不能为空。", nameof(entrants));
                ValidateController(entrant.ComputerLevel);
                tournament.AddPlayer(entrant.Name, entrant.TeamCode, entrant.Profile, entrant.ComputerLevel);
                controls[i] = entrant.ComputerLevel;
            }
            var result = new TournamentSession(db, path, tournament, controls, null);
            result.SaveCandidate(tournament);
            return result;
        }

        public static TournamentSession Load(ContentDb db, string path)
        {
            if (db == null) throw new ArgumentNullException(nameof(db), "赛事内容不能为空。");
            string fullPath = FullSavePath(path);
            CheckPath(fullPath);
            string text = ReadSave(fullPath);
            ParseSave(db, text, out var tournament, out var controls);
            return new TournamentSession(db, fullPath, tournament, controls, text);
        }

        public IReadOnlyList<Pairing> NextRound()
        {
            var candidate = Candidate();
            var pairingIds = candidate.NextRound().Select(pairing => pairing.Id).ToArray();
            SaveCandidate(candidate);
            return Array.AsReadOnly(pairingIds.Select(id => Tournament.Pairings[id]).ToArray());
        }

        public Battle CreateBattle(int pairingId)
        {
            EnsureContent();
            var pairing = Pending(Tournament, pairingId);
            Battle battle;
            if (Tournament.AutoTiebreak) battle = Tournament.CreateGame(pairing);
            else
            {
                var first = Tournament.Players[pairing.Player0];
                var second = Tournament.Players[pairing.Player1];
                battle = new Battle(Db, new IList<string>[] { first.Team.ToArray(), second.Team.ToArray() },
                    new[] { first.Name, second.Name }, Tournament.GameSeed(pairing, 0));
            }
            activeBattles[pairingId] = battle;
            return battle;
        }

        public string CompleteBattle(int pairingId, Battle battle)
        {
            if (battle == null) throw new ArgumentNullException(nameof(battle), "对战不能为空。");
            if (!activeBattles.TryGetValue(pairingId, out var active) || !ReferenceEquals(active, battle))
                throw new InvalidOperationException("请登记本赛事为该对阵创建的对局。");
            var candidate = Candidate();
            var pairing = Pending(candidate, pairingId);
            // 与 Core 的赛事归档入口共用验证；写入失败时保留对局，允许原样重试。
            string archiveId;
            using (var saveLock = OpenSaveLock())
            {
                RequireUnchangedSave();
                var library = new ReplayLibrary(ArchivePath, policy: Db.Policy);
                if (candidate.AutoTiebreak)
                {
                    archiveId = library.SaveTournamentGame(candidate, pairing, battle, pairing.Games.Count);
                    candidate.ReportGame(pairing, battle);
                }
                else
                {
                    archiveId = library.SaveTournament(candidate, pairing, battle);
                    candidate.ReportResult(pairing, battle.Winner);
                }
                SaveCandidateLocked(candidate);
            }
            activeBattles.Remove(pairingId);
            return archiveId;
        }

        public void ReportResult(int pairingId, int winnerSide)
        {
            var candidate = Candidate();
            candidate.ReportResult(Pending(candidate, pairingId), winnerSide);
            SaveCandidate(candidate);
            activeBattles.Remove(pairingId);
        }

        public TournamentReport BuildReport()
        {
            EnsureContent();
            return TournamentReport.Build(Tournament, ReplayLibrary.OpenReadOnly(ArchivePath, Db.Policy));
        }

        Tournament Candidate()
        {
            EnsureContent();
            return StreamDuel.Core.Tournament.FromJson(Db, Tournament.ToJson());
        }

        void EnsureContent()
        {
            if (Db.PackId != packId || Db.ContentHash() != contentHash)
                throw new InvalidOperationException("赛事内容包或规则已改变，请恢复相同内容后续赛。");
        }

        static Pairing Pending(Tournament tournament, int pairingId)
        {
            if (pairingId < 0 || pairingId >= tournament.Pairings.Count)
                throw new ArgumentOutOfRangeException(nameof(pairingId), "对阵编号无效。");
            var pairing = tournament.Pairings[pairingId];
            if (pairing.IsBye || pairing.WinnerSide.HasValue)
                throw new InvalidOperationException("轮空或已登记结果的对阵不能再次开打或报分。");
            return pairing;
        }

        static void ValidateController(AiLevel? level)
        {
            if (level.HasValue && level != AiLevel.Normal && level != AiLevel.Hard && level != AiLevel.Expert)
                throw new ArgumentException("赛事电脑难度只支持普通、困难或专家；简单难度不提供排位参考分。");
        }
    }
}
