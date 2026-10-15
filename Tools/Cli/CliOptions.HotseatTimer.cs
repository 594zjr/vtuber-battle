using System;
using System.Globalization;

namespace StreamDuel.Cli
{
    internal sealed partial class CliOptions
    {
        public int TurnTimeLimitSeconds;
        public bool TurnTimeLimitSpecified;

        private void SetHotseatTurnLimit(string value)
        {
            if (TurnTimeLimitSpecified) throw new ArgumentException("--turn-limit 不能重复指定。");
            if (value != "0" && value != "10" && value != "20" && value != "30" && value != "60")
                throw new ArgumentException("--turn-limit 只能是 0、10、20、30 或 60 秒，0 表示不限时。");
            TurnTimeLimitSeconds = int.Parse(value, CultureInfo.InvariantCulture);
            TurnTimeLimitSpecified = true;
        }
    }
}
