# T-99 v3 回合上限样例

两份录像使用上级 `content/StreamDuelRules.json` 和 `content/original.json` 的冻结内容，指纹为 `63a758a4fa663be9`，固定种子 99。均由默认 v3 对战的公开行动接口执行完整 120 回合，没有更改人气、回合数、技能或人设。

| 文件 | 操作与结果 | 终局 StateHash |
|---|---|---|
| `pass-draw-120.json` | 双方始终 Pass；人数、人气比例和累计伤害都相同，平局 | `382710c857b38789` |
| `damage-healed-120.json` | 第 1 回合甲方使用「开嗓」造成 79 点伤害；第 2 回合乙方使用「热修复」回满；其余行动 Pass。双方六人全满，甲方凭累计伤害获胜 | `a1122da58724ba3e` |

`T99_VersionGoldenTests` 固定文件 SHA-256、最后判定日志、结果、哈希和事件数，并比较 fresh、Replay、Clone、ReplayCursor 与随机流。测试只读这些文件；不自动生成或更新黄金数据。原有 v1/v2、T-98 v3 样例及 manifest 保持原样。
