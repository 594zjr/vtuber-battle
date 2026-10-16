# 冻结的历史录像

来源基线：`a5e3360147312ed7601f39e28dec51547e80f674`。11 份录像均由真实 `Battle` 运行到 `Phase.Over` 后导出；最终 `StateHash` 在生成时固定，普通测试只读文件，绝不重新生成期望值。

`content/` 保存该基线的规则、原创包、A-SOUL × VirtuaReal 同人包和原创第二期包快照。快照只把换行统一为 LF，不改变 JSON 内容。这样正式包今后的数值调整不会冒充录像格式回归；引擎结算或格式的变化仍会使冻结哈希测试失败。`manifest.json` 保存快照与录像的 SHA-256、内容指纹、版本、种子、最终状态哈希、胜方、回合数和事件数，测试还固定 manifest 自身的 SHA-256。

内容快照来自本项目，并保留原许可：内容包使用 [Packs/LICENSE.md](../../../UnityProject/Assets/StreamDuel/Resources/Packs/LICENSE.md) 中的 **CC BY-NC-SA 4.0**；规则与生成源码沿用仓库许可。同人包只复制仓库已收录的公开艺名及公开人设数据，没有图片、音频等官方素材。

| 文件 | 覆盖 |
|---|---|
| `ordinary-v2.json` | 普通完整对局，AI 选择及随机流检查点 |
| `lead-v2.json` | 客方先确认、双方选择非默认首发 |
| `ban-lead-v2.json` | 禁用原始队伍成员后选首发，六人记录恢复为五人对局 |
| `modifiers-v2.json` | 禁用、首发、半 PP、开场热度、猝死修饰器 |
| `series-game-1-v2.json`～`series-game-3-v2.json` | 同两队三份独立完整对局，胜方依次为 0、1、0 |
| `network-v2.json` | 基线协议 v3 的两个真实 `NetSession` 经 `LoopbackPair` 完成承诺、公开、终局哈希确认；双方记录和最终哈希相同 |
| `fan-v2.json` | 同人包自有技能、人设与显示名 |
| `wave2-v2.json` | 原创第二期包自有内容与双赛道 |
| `legacy-v1.json` | 无 `version`、`contentHash`、`names`、可选开局字段和事件 `rng` 的旧格式 |

v1 样例由不调用 AI、不额外消耗 RNG 的手动选择对局生成，再仅移除后来增加的版本、指纹、名字与 RNG 字段；不修改行动或结算结果。新版样例保留完整的随机流检查点。联网样例冻结的是 `BattleRecord`，不是网络消息，因此后续网络协议升级不会改变这份历史录像。

**多局覆盖边界：T-54 尚未合并到此基线。** 三份独立单局由测试 manifest 关联，同队、不同种子、合法单侧进攻与另一侧跳过产生交替胜方。没有实现或验证 `Series` 容器、比分累计、下一局种子或三局两胜流程。T-54 后合并的审查员需要追加真实 `Series` 产出的样例和验收，不能把本组样例当作完整多局制验收。

## 生成与审查

生成源码位于 `Tools/ReplayGolden/`，读取本目录冻结的内容快照。使用匹配基线的引擎和 AI 版本，在仓库根目录执行：

```powershell
$env:TEMP = (Resolve-Path Build/T93/temp).Path
$env:TMP = $env:TEMP
dotnet run --project Tools/ReplayGolden -c Release -- --output Build/T93/golden-candidate
dotnet test Engine.Tests --filter FullyQualifiedName~T93_ReplayGoldenTests
```

先创建项目内的 `Build/T93/temp`。输出目录必须不存在，生成器拒绝覆盖任何既有目录；测试不会调用生成器。初次冻结时两次独立生成的全部 16 个 JSON 文件逐字节一致，网络承诺 nonce 的随机性不影响对战记录。

今后升录像格式版本时保留旧样例，新增新版本样例并审查生成源码、快照、manifest 和测试中的 manifest SHA-256。不要为了变绿重新生成并替换旧录像或最终哈希；如果结算有意变更，应明确说明旧版本能否严格重放、是否仅能尽力重放，再更新兼容策略。
