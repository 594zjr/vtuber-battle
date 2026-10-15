# 普通电脑权重与调参实验

T-33 将普通难度的 34 项策略参数集中在 `Core/AiWeights.cs`。默认数值和原有决策、随机数抽取顺序保持一致。伤害规则中的同赛道倍率、能力上限、百分比单位与“伤害足够击倒”的数学边界不属于策略权重。

## 使用权重

```csharp
var weights = AiWeights.Default.Copy();
weights.Set("ControlStatusScore", 0.6);
BattleAction action = Ai.Choose(battle, side, AiLevel.Normal, weights);
string json = weights.ToJson();
AiWeights loaded = AiWeights.FromJson(json);
```

`Default` 每次返回独立对象；`Copy()` 复制当前值。`Names()` 按英文名的 Ordinal 顺序枚举，`Get/Set` 对未知名字抛出 `KeyNotFoundException`。JSON 是字段名到数值的对象，缺失字段使用默认值，未知字段或非数值拒绝载入；输出与系统数字文化无关。`Set` 和 JSON 输出拒绝 NaN、Infinity。

旧的两个 `Choose` 重载继续使用默认权重。四参数重载仅影响普通难度，简单和困难继续使用各自原来的策略；首发、禁用和补位选择不接受自定义权重。调整 `RandomScoreRange` 为 0 也不省略原来的随机抽取。

## 调参协议

```text
dotnet run --project Tools/Sim -- --tune --rounds 20 --games 400 --seed 1
dotnet run --project Tools/Sim -- --tune --rounds 100 --games 400 --seed 1
```

- 调参默认 20 轮、每轮 400 局、种子 1、`original` 包；`--pack` 和 `--pack-dir` 同样适用。普通模拟原有默认值保持不变。
- 每轮复制当前最好权重，从稳定排序的名字中不重复选择 1～3 项，每项乘以 `[0.7, 1.3)` 的随机倍率。负值保持符号；没有改写 `Default`。
- 每两局使用完全相同的阵容和种子，仅交换候选与当前最好权重所在方。`--games` 必须为正偶数。12 位以上时双方从同一次洗牌各取 6 位；不足 12 位时双方分别抽取 6 位，允许跨方重复。
- 轮次从 0 开始，起始种子为 `seed + round * games`，一对比赛使用 `起始种子 + pairIndex`；uint 溢出按模 2^32 处理。候选生成、阵容抽取和实际比赛使用独立的 `Battle.Random()`，没有引入系统随机数或时间种子。
- 胜率为候选胜局数 / 总局数，平局计入分母。严格大于 55% 才替换当前最好；等于 55% 保留。最终对 Default 的验证批从 `seed + rounds * games` 开始，不参与候选选拔。
- 标准输出仅为完整的最终权重 JSON，标准错误为各轮变化、胜负与最终验证结果。工具不会保存文件或自动启用权重，可自行重定向输出。

## 已运行结果

2026-10-15，.NET SDK 8.0.300，基于 `e692be0c` 的规则和 `original` 包运行本任务实现。规则文件 SHA256：`18F0C70A4FF09B6011055CB5B6C87F9A5E9F5996BC947D91877D63D4BA0DA057`；内容包 SHA256：`D0B8D729F4CE3B2C8AB62BA84F90ACA651232FCBA9C6E5E0341322DBD09308BC`。

| 轮数 | 每轮局数 | 种子 | 超过 55% 并接受的候选 | 最终验证起始种子 | 对 Default 胜 / 负 / 平 | 胜率 |
|---|---:|---:|---:|---:|---|---:|
| 20 | 400 | 1 | 0 | 8001 | 200 / 200 / 0 | 50.00% |
| 100 | 400 | 1 | 0 | 40001 | 200 / 200 / 0 | 50.00% |

20 轮的两次运行，其权重 JSON 与逐轮报告逐字一致；最大 uint 种子的短程运行也重复一致。扩展到 100 轮仍没有达到接受门槛，因此以下最终保留的一组与 Default 相同。这次搜索没有证明普通电脑得到提升，也没有自动采用实验参数。独立验证中的 50% 同时符合相同策略成对交换双方时的预期。

```json
{
  "CleanseScore": 0.4,
  "ControlStatusScore": 0.5,
  "ControlledSwitchMinHpRatio": 0.3,
  "ControlledSwitchProbability": 0.35,
  "DispelBoostScore": 0.12,
  "DispelFieldScore": 0.2,
  "DispelTrapScore": -0.2,
  "FieldMaxThreatRatio": 0.6,
  "FieldSafeScore": 0.32,
  "FieldThreatenedScore": 0.12,
  "FirstKnockoutBonus": 0.6,
  "FoeStageScore": 0.18,
  "HealMaxHpRatio": 0.5,
  "HealMissingHpScore": 0.9,
  "KnockoutScore": 1.6,
  "MatchupSlowerPenalty": 0.2,
  "RandomScoreRange": 0.08,
  "ReflectDamageScoreMultiplier": 2,
  "ReflectScoreCap": 0.7,
  "StageBoostTarget": 2,
  "StageMaxThreatRatio": 0.5,
  "StageMinHpRatio": 0.6,
  "StageScore": 0.22,
  "StealBoostBaseScore": 0.12,
  "StealBoostPerStageScore": 0.08,
  "SwitchMaxActionScore": 0.5,
  "SwitchMinMatchupGain": 0.5,
  "SwitchMinThreatRatio": 0.9,
  "SwitchProbability": 0.6,
  "ThreatenedFirstKnockoutBonus": 1,
  "TrapAfflictedScore": 0.4,
  "TrapScore": 0.22,
  "WeakImmunityScore": 0.4,
  "WeakStatusScore": 0.3
}
```
