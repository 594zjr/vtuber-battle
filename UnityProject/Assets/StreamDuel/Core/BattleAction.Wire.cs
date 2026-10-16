using System;

namespace StreamDuel.Core
{
    public partial struct BattleAction
    {
        public string ToWire()
        {
            switch (Type)
            {
                case ActionType.Skill:
                    if (Index >= 0 && Index <= 4) return "S" + (char)('0' + Index);
                    break;
                case ActionType.Switch:
                    if (Index >= 0 && Index <= 5) return "W" + (char)('0' + Index);
                    break;
                case ActionType.Pass:
                    return "P";
                case ActionType.Surrender:
                    return "Q";
            }
            throw new FormatException("行动不能编码：只支持技能 S0～S4、换人 W0～W5、跳过 P 或投降 Q");
        }

        public static BattleAction FromWire(string wire)
        {
            if (wire == "P") return Pass();
            if (wire == "Q") return Surrender();
            if (wire != null && wire.Length == 2)
            {
                char index = wire[1];
                if (wire[0] == 'S' && index >= '0' && index <= '4') return Skill(index - '0');
                if (wire[0] == 'W' && index >= '0' && index <= '5') return SwitchTo(index - '0');
            }
            throw new FormatException("行动编码无效：必须是 S0～S4、W0～W5、P 或 Q");
        }
    }
}
