using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StreamDuel.Core
{
    public sealed partial class NetSession
    {
        private readonly string[] commitments = new string[2];
        private readonly bool[] revealed = new bool[2];
        private string localWire;
        private string localNonce;
        private bool revealSent;

        private void CommitSelection(string kind, string wire)
        {
            // nonce 是唯一不经过 Battle.Rng 的随机数例外：只隐藏网络选择，不参与对战结算。
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            string nonce = Hex(bytes);
            string digest = CommitmentDigest(wire, nonce);
            localWire = wire;
            localNonce = nonce;
            commitments[MySide] = digest;
            HasSubmitted = true;
            Clock?.Submit(MySide);
            State = NetState.Waiting;
            if (Send("{\"type\":\"commit\",\"turn\":" + Number(turn) + GameField
                + ",\"kind\":" + NetJson.Quote(kind) + ",\"digest\":" + NetJson.Quote(digest) + "}"))
            {
                BeginRevealTimerIfReady();
                Advance();
            }
        }

        private void ReceiveCommit(Dictionary<string, object> obj)
        {
            ValidateSelectionKind(obj);
            int peer = 1 - MySide;
            if (commitments[peer] != null) throw new FormatException("对方重复发送承诺。");
            string digest = NetJson.Text(obj, "digest");
            if (!LowerHex(digest, 64)) throw new FormatException("承诺摘要必须是 64 位小写十六进制字符串。");
            commitments[peer] = digest;
            Clock?.Submit(peer);
            BeginRevealTimerIfReady();
        }

        private void ReceiveReveal(Dictionary<string, object> obj)
        {
            ValidateSelectionKind(obj);
            int peer = 1 - MySide;
            if (!AllCommitmentsReady()) throw new FormatException("本阶段所需承诺尚未齐全，不能提前公开行动。");
            if (revealed[peer]) throw new FormatException("对方重复公开承诺的行动。");
            string wire = NetJson.Text(obj, "action");
            string nonce = NetJson.Text(obj, "nonce");
            if (!LowerHex(nonce, 32)) throw new FormatException("承诺随机串必须是 32 位小写十六进制字符串。");
            if (!string.Equals(commitments[peer], CommitmentDigest(wire, nonce), StringComparison.Ordinal))
                throw new FormatException("对手公开的行动与承诺不符。");
            StoreRevealedSelection(peer, wire);
        }

        private void ValidateSelectionKind(Dictionary<string, object> obj)
        {
            string kind = NetJson.Text(obj, "kind");
            if ((phase != Phase.Choose && phase != Phase.Replace && (!rulesetProtocol || phase != Phase.Ban && phase != Phase.Lead)) || kind != SelectionKind)
                throw new FormatException("承诺或公开的类型与当前阶段不匹配。");
            if (phase == Phase.Replace && !needsReplacement[1 - MySide])
                throw new FormatException("对方本阶段无需补位，不能发送补位承诺或公开。");
        }

        private string SelectionKind
        {
            get { return phase == Phase.Choose ? "action" : phase == Phase.Ban ? "ban" : phase == Phase.Lead ? "lead" : "replace"; }
        }

        private bool AllCommitmentsReady()
        {
            for (int side = 0; side < 2; side++)
                if ((phase == Phase.Choose || phase == Phase.Ban || phase == Phase.Lead || needsReplacement[side]) && commitments[side] == null) return false;
            return true;
        }

        private void RevealIfReady()
        {
            if (!HasSubmitted || revealSent || !AllCommitmentsReady()) return;
            revealSent = true;
            // 先发公开消息再登记本方选择，单方补位也必须遵守 commit → reveal → hash 的顺序。
            if (Send("{\"type\":\"reveal\",\"turn\":" + Number(turn) + GameField
                + ",\"kind\":" + NetJson.Quote(SelectionKind) + ",\"action\":" + NetJson.Quote(localWire)
                + ",\"nonce\":" + NetJson.Quote(localNonce) + "}")) StoreRevealedSelection(MySide, localWire);
        }

        private void StoreRevealedSelection(int side, string wire)
        {
            var action = BattleAction.FromWire(wire);
            if (action.Type == ActionType.Surrender)
                throw new FormatException("投降必须通过已协商的终止协议提交，不能伪装成普通行动公开。");
            if (phase == Phase.Choose) actions[side] = action;
            else if (phase == Phase.Ban || phase == Phase.Lead)
            {
                if (action.Type != ActionType.Switch || !ValidOpening(side, action.Index))
                    throw new FormatException("公开的禁用或首发必须是当前名单中的有效 W 位置编码。");
                openingChoices[side] = action.Index;
            }
            else
            {
                if (action.Type != ActionType.Switch || !ValidReplacement(side, action.Index))
                    throw new FormatException("公开的补位必须是有效的 W 位置编码。");
                replacements[side] = action.Index;
            }
            revealed[side] = true;
        }

        private void ResetCommitReveal()
        {
            for (int side = 0; side < 2; side++)
            {
                commitments[side] = null;
                revealed[side] = false;
            }
            localWire = null;
            localNonce = null;
            revealSent = false;
        }

        private static string CommitmentDigest(string wire, string nonce)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(wire + "|" + nonce)));
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        private static bool LowerHex(string text, int length)
        {
            if (text == null || text.Length != length) return false;
            foreach (char c in text)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }
    }
}
