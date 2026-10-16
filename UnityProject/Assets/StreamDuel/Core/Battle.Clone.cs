// 为避免模拟局修改真实对战，新增下列类型的字段时必须同步本文件的复制逻辑：
// Battle：Db（共享）、Seed、Sides、Log、Turn、Phase、Winner、Need、ActedFirst、rng、
// leadChoices、banChoices、secondBanChoices、banCount、leadPickAfterBan、modifiers、drawOffers、lastDrawOfferTurns、Record、fighterStats、battleEvents；eventsView 按本对象列表重新建立。
// SideState：Name、Active、Team；Fighter：Def（共享 Db 定义）、Slot、Side、Hp、MaxHp、
// EnteredTurn、Fainted、LastSkillIndex、FirstControlImmunityUsed、EndureUsed、Stages、Skills、Statuses、Fields。
// SkillSlot：Id、Pp、Max、Fifth；StatusInst：Id、Left；FieldInst：Id、Turns、Stacks。
// LogEntry：Turn、Side、Node、Text、Kind、SnapActive、SnapHp。
// BattleRecord：Version、ContentHash、Seed、PackId、LeadPick、BanOne、BanCount、Modifiers、Names、Teams、Events；
// BattleRecordEvent：Type、A0、A1、Side、To、Accepted、RngState、Branch（不可变元数据共享）。
// BattleEvent：LogIndex、Turn、Side、Slot、Amount、Node、Id、Kind。
// FighterStats：StreamerId、DamageDealt、DamageTaken、Kos、TurnsActive、Fainted、SkillUses。
using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed partial class Battle
    {
        /// <summary>复制当前对战及历史，不触发开场、不推进随机数；规则和内容定义仍共享 Db。</summary>
        public Battle Clone()
        {
            return new Battle(this, CloneRecord(Record));
        }

        // 携带完整历史，区别于困难 AI 只用于局部估值的复制构造。
        Battle(Battle source, BattleRecord record)
        {
            Db = source.Db;
            Seed = source.Seed;
            Turn = source.Turn;
            Phase = source.Phase;
            Winner = source.Winner;
            CopyTerminationFrom(source);
            rng = new Rng(source.rng.State);
            leadPickAfterBan = source.leadPickAfterBan;
            banCount = source.banCount;
            modifiers = new List<string>(source.modifiers).AsReadOnly();
            Array.Copy(source.Need, Need, Need.Length);
            Array.Copy(source.ActedFirst, ActedFirst, ActedFirst.Length);
            Array.Copy(source.leadChoices, leadChoices, leadChoices.Length);
            Array.Copy(source.banChoices, banChoices, banChoices.Length);
            Array.Copy(source.secondBanChoices, secondBanChoices, secondBanChoices.Length);

            // 统计以 Fighter 对象为键，必须沿用队伍复制时建立的身份映射。
            var fighters = new Dictionary<Fighter, Fighter>();
            Fighter CopyFighter(Fighter original)
            {
                Fighter copy;
                if (!fighters.TryGetValue(original, out copy))
                {
                    copy = CloneFighter(original);
                    fighters.Add(original, copy);
                }
                return copy;
            }

            for (int side = 0; side < Sides.Length; side++)
            {
                var original = source.Sides[side];
                Sides[side] = new SideState
                {
                    Name = original.Name,
                    Active = original.Active,
                    Team = original.Team.Select(CopyFighter).ToList()
                };
            }
            foreach (var pair in source.fighterStats)
            {
                var stats = pair.Value;
                fighterStats.Add(CopyFighter(pair.Key), new FighterStats
                {
                    StreamerId = stats.StreamerId,
                    DamageDealt = stats.DamageDealt,
                    DamageTaken = stats.DamageTaken,
                    Kos = stats.Kos,
                    TurnsActive = stats.TurnsActive,
                    Fainted = stats.Fainted,
                    SkillUses = new Dictionary<string, int>(stats.SkillUses, stats.SkillUses.Comparer)
                });
            }

            // 首发重排会原地修改旧禁用日志的快照，所以连日志条目和内层数组也要独立。
            foreach (var entry in source.Log)
            {
                Log.Add(new LogEntry
                {
                    Turn = entry.Turn,
                    Side = entry.Side,
                    Node = entry.Node,
                    Text = entry.Text,
                    Kind = entry.Kind,
                    SnapActive = (int[])entry.SnapActive.Clone(),
                    SnapHp = entry.SnapHp.Select(hp => (int[])hp.Clone()).ToArray()
                });
            }
            CopyEventsFrom(source);
            Record = record;
        }

        static Fighter CloneFighter(Fighter source)
        {
            return new Fighter
            {
                Def = source.Def,
                Slot = source.Slot,
                Side = source.Side,
                Hp = source.Hp,
                MaxHp = source.MaxHp,
                EnteredTurn = source.EnteredTurn,
                Fainted = source.Fainted,
                LastSkillIndex = source.LastSkillIndex,
                FirstControlImmunityUsed = source.FirstControlImmunityUsed,
                EndureUsed = source.EndureUsed,
                Stages = new Dictionary<string, int>(source.Stages, source.Stages.Comparer),
                Skills = source.Skills.Select(slot => new SkillSlot
                {
                    Id = slot.Id, Pp = slot.Pp, Max = slot.Max, Fifth = slot.Fifth
                }).ToList(),
                Statuses = source.Statuses.Select(status => new StatusInst
                {
                    Id = status.Id, Left = status.Left
                }).ToList(),
                Fields = source.Fields.Select(field => new FieldInst
                {
                    Id = field.Id, Turns = field.Turns, Stacks = field.Stacks
                }).ToList()
            };
        }

        static BattleRecord CloneRecord(BattleRecord source)
        {
            var copy = new BattleRecord
            {
                Version = source.Version,
                ContentHash = source.ContentHash,
                Seed = source.Seed,
                PackId = source.PackId,
                LeadPick = source.LeadPick,
                BanOne = source.BanOne,
                BanCount = source.BanCount,
                Modifiers = source.Modifiers == null ? null : new List<string>(source.Modifiers),
                Names = source.Names == null ? null : (string[])source.Names.Clone(),
                Teams = source.Teams.Select(team => (IList<string>)new List<string>(team)).ToArray()
            };
            foreach (var entry in source.Events)
            {
                copy.Events.Add(new BattleRecordEvent
                {
                    Type = entry.Type,
                    A0 = entry.A0,
                    A1 = entry.A1,
                    Side = entry.Side,
                    To = entry.To,
                    Accepted = entry.Accepted,
                    Branch = entry.Branch,
                    RngState = entry.RngState
                });
            }
            return copy;
        }
    }
}
