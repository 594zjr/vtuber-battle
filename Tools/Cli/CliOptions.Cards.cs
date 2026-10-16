using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public string CardId;
        public string CardsPack;
        public bool CardMode { get { return CardId != null || CardsPack != null; } }

        private bool ReadCardOption(string[] args, ref int index)
        {
            string option = args[index];
            if (option != "--card" && option != "--cards") return false;
            if ((option == "--card" && CardId != null) || (option == "--cards" && CardsPack != null))
                throw new ArgumentException(option + " 不能重复指定。");
            if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
                || args[index].StartsWith("--", StringComparison.Ordinal) || args[index] == "-h")
                throw new ArgumentException(option + (option == "--card" ? " 缺少主播 id。" : " 缺少内容包 id。"));
            if (args[index].Any(char.IsControl))
                throw new ArgumentException(option + " 的编号不能含换行或控制字符。");
            if (option == "--card") CardId = args[index];
            else CardsPack = args[index];
            return true;
        }

        private static void ValidateCardOptions(CliOptions options, List<string> provided)
        {
            if (!options.CardMode || options.Help) return;
            if (options.CardId != null && options.CardsPack != null)
                throw new ArgumentException("--card 与 --cards 不能同时使用，请选择单张或整包资料卡。");
            var allowed = options.CardId != null
                ? new[] { "--card", "--pack", "--packs", "--pack-dir" }
                : new[] { "--cards", "--pack-dir" };
            var unsupported = provided.Where(option => !allowed.Contains(option)).Distinct().ToArray();
            if (unsupported.Length > 0)
                throw new ArgumentException((options.CardId != null ? "--card 仅支持搭配 --pack、--packs、--pack-dir"
                    : "--cards 仅支持搭配 --pack-dir") + "；不能与 " + string.Join("、", unsupported) + " 同用。");
            if (options.CardsPack != null)
            {
                options.Pack = options.CardsPack;
                options.PackSpecified = true;
            }
        }
    }
}
