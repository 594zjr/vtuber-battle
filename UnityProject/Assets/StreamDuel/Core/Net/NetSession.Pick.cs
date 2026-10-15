using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed partial class NetSession
    {
        private int configuredPickCount;
        private bool configuredBanOne;
        private readonly int?[] bans = new int?[2];
        private readonly int[][] picks = new int[2][];

        private bool ConfigurePick(bool host, int pickCount, bool banOne)
        {
            if (pickCount != 0 && pickCount != 3 && pickCount != 4)
            {
                Fault(NetState.Error, "选人数必须为 0（关闭）、3 或 4。");
                return false;
            }
            if (host)
            {
                configuredPickCount = pickCount;
                configuredBanOne = banOne;
            }
            return true;
        }

        private string PickHello()
        {
            string hello = ",\"pickVersion\":1";
            if (MySide == 0 && (configuredPickCount != 0 || configuredBanOne))
                hello += ",\"pickCount\":" + Number(configuredPickCount)
                    + ",\"banOne\":" + (configuredBanOne ? "true" : "false");
            return hello;
        }

        private void ReceivePickHello(Dictionary<string, object> obj)
        {
            bool hasPick = obj.ContainsKey("pickCount"), hasBan = obj.ContainsKey("banOne");
            if (MySide == 0)
            {
                if (hasPick || hasBan) throw new FormatException("禁用和选人规则由主机决定，客机不能声明或更改。");
                if (configuredPickCount != 0 || configuredBanOne) RequirePickCapability(obj);
                return;
            }
            if (!hasPick && !hasBan) return;
            if (!hasPick || !hasBan) throw new FormatException("主机必须同时声明选人数 pickCount 和禁用开关 banOne。");
            RequirePickCapability(obj);
            int count = (int)NetJson.Integer(obj, "pickCount", 0, 4);
            if (count != 0 && count != 3 && count != 4) throw new FormatException("主机选人数必须为 0、3 或 4。");
            if (!(obj["banOne"] is bool ban)) throw new FormatException("主机禁用开关 banOne 必须是布尔值。");
            configuredPickCount = count;
            configuredBanOne = ban;
        }

        private static void RequirePickCapability(Dictionary<string, object> obj)
        {
            if (!obj.TryGetValue("pickVersion", out var version) || !(version is double number) || number != 1)
                throw new FormatException("对方未声明支持禁用和选人协议 pickVersion 1，无法启用开局选择。");
        }

        public void SubmitBan(int originalFoeIndex)
        {
            if (State != NetState.Ban || HasSubmitted || !ValidBan(MySide, originalFoeIndex))
                throw new InvalidOperationException("现在不能提交该禁用位置，或本阶段已经提交。");
            CommitSelection("ban", "B" + Number(originalFoeIndex));
        }

        public void SubmitPick(IList<int> indices)
        {
            if (State != NetState.Pick || HasSubmitted || !ValidPick(MySide, indices))
                throw new InvalidOperationException("现在不能提交该选人列表：人数、原始号位或重复/被禁位置无效，或本阶段已经提交。");
            CommitSelection("pick", "K" + string.Join(",", indices.Select(Number)));
        }

        private bool ValidBan(int side, int index)
        {
            return Battle != null && phase == Phase.Ban && !Battle.HasChosenBan(side)
                && index >= 0 && index < Battle.Sides[1 - side].Team.Count;
        }

        private bool ValidPick(int side, IList<int> indices)
        {
            if (Battle == null || phase != Phase.Pick || Battle.HasChosenPick(side)
                || indices == null || indices.Count != Battle.PickCount) return false;
            var available = new HashSet<int>(Battle.AvailablePicks(side));
            foreach (int index in indices)
                if (!available.Remove(index)) return false;
            return true;
        }

        private void StoreRevealedOpening(int side, string wire)
        {
            if (phase == Phase.Ban)
            {
                if (wire.Length != 2 || wire[0] != 'B' || wire[1] < '0' || wire[1] > '5'
                    || !ValidBan(side, wire[1] - '0'))
                    throw new FormatException("公开的禁用必须是有效的 B0～B5 原始位置编码。");
                bans[side] = wire[1] - '0';
                return;
            }
            if (wire.Length != Battle.PickCount * 2 || wire[0] != 'K')
                throw new FormatException("公开的选人必须为 K 加逗号分隔的原始位置编码，人数须与主机设置一致。");
            var indices = new int[Battle.PickCount];
            for (int i = 0; i < indices.Length; i++)
            {
                int position = 1 + i * 2;
                if (wire[position] < '0' || wire[position] > '5' || i > 0 && wire[position - 1] != ',')
                    throw new FormatException("公开的选人只允许 0～5 的原始号位与逗号，不允许空格或前导零。");
                indices[i] = wire[position] - '0';
            }
            if (!ValidPick(side, indices)) throw new FormatException("公开的选人包含重复、被禁用或不可选的原始号位。");
            picks[side] = indices;
        }
    }
}
