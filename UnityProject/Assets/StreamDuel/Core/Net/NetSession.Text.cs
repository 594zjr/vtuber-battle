using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDuel.Core
{
    public sealed partial class NetSession
    {
        /// <summary>握手完成后按房主、客机顺序提供安全昵称与提示；握手前为空。</summary>
        public IReadOnlyList<TextInputResult> NameInputs
        {
            get
            {
                // 原始名字参与既有记录与同步；显示快照不能反向修改这些数据。
                if (Battle == null) return Array.AsReadOnly(Array.Empty<TextInputResult>());
                return Array.AsReadOnly(Battle.Sides.Select(side =>
                    TextInput.Clean(side.Name, TextInputKind.PlayerName)).ToArray());
            }
        }

        public IReadOnlyList<string> DisplayNames
        {
            get { return Array.AsReadOnly(NameInputs.Select(input => input.Text).ToArray()); }
        }

        public BattleViewModel ViewFor(int viewerSide)
        {
            if (Battle == null) throw new InvalidOperationException("握手尚未完成，无法显示对战。");
            return BattleViewModel.From(Battle, viewerSide);
        }
    }
}
