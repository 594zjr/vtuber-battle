using System;
using System.Collections.Generic;

namespace StreamDuel.Core
{
    public sealed class CodeInfo
    {
        public string Code { get; }
        public string Timing { get; }
        public string DispelRule { get; }
        public IReadOnlyList<string> ParameterNames { get; }
        public string DescriptionSource { get; }

        internal CodeInfo(string code, string timing, string dispelRule, string descriptionSource,
            params string[] parameterNames)
        {
            Code = code;
            Timing = timing;
            DispelRule = dispelRule;
            DescriptionSource = descriptionSource;
            // 不暴露原数组，避免调用方改写所有内容共用的代码契约。
            ParameterNames = Array.AsReadOnly((string[])parameterNames.Clone());
        }
    }
}
