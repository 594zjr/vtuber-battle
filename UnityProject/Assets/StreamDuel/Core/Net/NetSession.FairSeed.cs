using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace StreamDuel.Core
{
    public sealed partial class NetSession
    {
        private const double SeedWaitSeconds = 10;
        private byte[] seedLocalValue;
        private string seedLocalCommit, seedPeerCommit, seedPeerName;
        private IList<string> seedPeerTeam;
        private bool seedHelloSent, seedHelloReceived, seedRevealSent, seedRevealReceived;
        private double seedWaitElapsed;

        private bool InitializeFairSeed(byte[] contribution)
        {
            if (contribution != null && contribution.Length != 32)
            {
                Fault(NetState.Error, "种子原值必须恰好包含 32 字节。");
                return false;
            }
            try
            {
                // 单独生成握手熵，不读取时间，也不消费尚未创建的 Battle 随机流。
                seedLocalValue = contribution == null ? new byte[32] : (byte[])contribution.Clone();
                if (contribution == null)
                    using (var random = RandomNumberGenerator.Create()) random.GetBytes(seedLocalValue);
                seedLocalCommit = SeedDigest(seedLocalValue);
                return true;
            }
            catch (Exception ex)
            {
                Fault(NetState.Error, "生成种子承诺失败：" + ex.Message);
                return false;
            }
        }

        private void BeginSeedWait()
        {
            seedWaitElapsed = 0;
            TimerRevision++;
        }

        private void TickSeedHandshake(double seconds)
        {
            seedWaitElapsed += Math.Min(seconds, SeedWaitSeconds - seedWaitElapsed);
            if (seedWaitElapsed >= SeedWaitSeconds)
                Fault(NetState.Error, seedHelloReceived
                    ? "等待对方公开种子已达 10 秒，握手超时。"
                    : "等待对方种子承诺已达 10 秒，握手超时。");
        }

        private void RevealSeed()
        {
            if (!seedHelloSent || !seedHelloReceived || seedRevealSent || Faulted) return;
            seedRevealSent = true;
            if (Send("{\"type\":\"seed_reveal\",\"value\":" + NetJson.Quote(Hex(seedLocalValue)) + "}"))
                BeginSeedWait();
        }

        private void ReceiveSeedReveal(Dictionary<string, object> obj)
        {
            if (seedRevealReceived || Battle != null) throw new FormatException("对方重复公开种子原值。");
            if (!seedHelloSent || !seedHelloReceived || !seedRevealSent)
                throw new FormatException("双方种子承诺尚未齐全，不能提前公开种子原值。");
            string text = NetJson.Text(obj, "value");
            if (!LowerHex(text, 64)) throw new FormatException("种子原值必须是 64 位小写十六进制字符串。");
            var peerValue = new byte[32];
            var combined = new byte[64];
            byte[] digest = null;
            try
            {
                for (int i = 0; i < peerValue.Length; i++)
                    peerValue[i] = (byte)((HexDigit(text[i * 2]) << 4) | HexDigit(text[i * 2 + 1]));
                if (!string.Equals(seedPeerCommit, SeedDigest(peerValue), StringComparison.Ordinal))
                    throw new FormatException("对方公开的种子原值与承诺不符。");
                seedRevealReceived = true;
                // 按角色拼接，不能按收包顺序拼接，否则两端会算出不同种子。
                Array.Copy(seedLocalValue, 0, combined, MySide * 32, 32);
                Array.Copy(peerValue, 0, combined, (1 - MySide) * 32, 32);
                using (var sha = SHA256.Create()) digest = sha.ComputeHash(combined);
                uint sharedSeed = (uint)digest[0] | (uint)digest[1] << 8
                    | (uint)digest[2] << 16 | (uint)digest[3] << 24;
                // hello 后还有一次网络往返，须再次拒绝这段等待期间被改写的本地内容。
                string currentContentHash = db.ContentHash();
                if (!string.Equals(contentHash, currentContentHash, StringComparison.Ordinal))
                    throw new FormatException("内容不一致：公开种子期间本端内容已变化，已发送指纹 "
                        + contentHash + "，当前指纹 " + currentContentHash + "。");
                var teams = new IList<string>[2];
                var names = new string[2];
                teams[MySide] = myTeam;
                teams[1 - MySide] = seedPeerTeam;
                names[MySide] = myName;
                names[1 - MySide] = seedPeerName;
                Battle = new Battle(db, teams, names, sharedSeed);
                rngCheckpoint = Battle.NetRngState;
                TimerRevision++;
                BeginPhase();
            }
            finally
            {
                Array.Clear(peerValue, 0, peerValue.Length);
                Array.Clear(combined, 0, combined.Length);
                if (digest != null) Array.Clear(digest, 0, digest.Length);
                ClearSeedSecrets();
            }
        }

        private void ClearSeedSecrets()
        {
            if (seedLocalValue != null) Array.Clear(seedLocalValue, 0, seedLocalValue.Length);
            seedLocalValue = null;
            seedLocalCommit = seedPeerCommit = seedPeerName = null;
            seedPeerTeam = null;
        }

        private static int HexDigit(char value)
        {
            return value <= '9' ? value - '0' : value - 'a' + 10;
        }

        private static string SeedDigest(byte[] value)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(value));
        }
    }
}
