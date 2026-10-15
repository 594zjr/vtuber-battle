# 电脑权重自我对弈（T-95）

## 范围与版本

本实验以 `fe489bc690101a7526c3c7d595f04b4e7a88aa80` 为基点，仅调整电脑选招/补位的策略参数，不改变战斗结算。只在 `original` 原创内容包训练、选择和验证；其他内容包尚无新旧强度提升的证据。最终发布组合为 **Normal r22 + Hard r14**。Easy 的行动与补位固定使用历史权重；Expert 的模拟按当前演员有无风格选择 Hard 基底，不承诺整个 Expert 的旧行为不变。同步主分支后的最终完整测试 1645/1645 通过，训练成绩、模型选择与最终验收分开报告。

## 可复现协议

- Normal 与 Hard 各从旧权重起步，每次训练 30 轮，每轮 8 个候选，各与轮初冻结的当前最佳打 400 局。轮内所有候选使用同一批阵容和种子；全部评完后只接受一个候选，同分取先生成者。
- 候选随机选择 1～12 个不同参数，各乘 `[0,2)` 倍。参数及随机抽取顺序按 ordinal 稳定排序，随机数来自独立 `Battle.Random()`；不使用时间种子。工具默认仍为每轮 4 个候选、1～3 项、±30%；本次显式扩大搜索范围。
- `DamageScoreMultiplier=1`、`PpReduceScore=0` 按既有默认契约冻结。Normal 有 34 个可调字段，Hard 只扰动其中实际消费的 26 个字段；安全斩杀、合法行动、保护收益、换人安全门槛等算法保持不变。
- 每个样本块固定两队阵容和一个战斗种子，枚举阵容左右顺序、候选所在方、选招调用先后这三个二值因子，共 8 局。补位按同一调用侧顺序、各自显式权重执行。每候选 50 个块；实际先后手仍由 Core 的规则结算。
- 胜率为胜局 / 全部局数，平局也计入分母。严格要求胜率 >52%，且双侧 95% Wilson 下界 >52%，400 局时至少 228 胜。该下界是探索筛选门槛：同块对称局相关、每轮同时筛选多候选，不能据此声称总体提升获得独立的 95% 置信保证。
- 每次训练完成后另打 800 局对旧权重，种子区间与该次训练不重叠，不据留出结果回退或筛选候选。T95 的三组固定 200 局验收种子在候选生成前冻结，与训练/留出不同。
- `--jobs` 只并行独立样本块。阵容预先串行抽取，块内 8 局顺序不变，最后按块索引汇总。jobs=1/4 的两档烟测权重逐字相同，报告除 jobs 元数据外一致。

| 运行 | 起始种子 | 训练战斗种子 | 800 局留出种子 | jobs |
|---|---:|---|---|---:|
| Normal v1 | 950001 | 950001–951500 | 951501–951600 | 1 |
| Hard v3 | 960001 | 960001–961500 | 961501–961600 | 8 |

阵容 RNG 种子为各轮战斗起始种子 XOR `0x63D83595`；候选 RNG 种子为整个运行的起始种子 XOR `0xA511E9B3`。内容指纹：`336820d77647481b`。Hard v1 串行运行在首轮完成前主动中断以改为并行；v2 并行版随后为切换进程内 Server GC 中断。最终 v3 仍从同样旧权重、相同种子与原定轮数重新运行，未用中断数据改变候选或筛选门槛。进程环境为 `DOTNET_gcServer=1`、`DOTNET_PROCESSOR_COUNT=8`；相同首候选的权重和400局结果与 v2 一致。这两个环境变量只在运行命令的进程内设置，未改机器或 Unity 全局设置。

## 默认值、历史回归与 API

`AiWeights.Default`、`HardDefault`、`Legacy` 每次返回独立对象。无风格 Normal 使用新 Default；无风格 Hard 使用新 HardDefault。构造器 `new AiWeights()` 保留旧字段初值，以保持现有三种风格预设。有风格 Normal 仍优先采用演员风格，覆盖调用者权重；有风格 Hard 使用 **Legacy 基底 + 原风格偏好**，风格差额固定参照 Legacy。Expert 内部每次 Hard 模拟按当前演员决定基底：有风格用 Legacy，无风格用新 HardDefault，因此不声称整个 Expert 的旧行动序列不变。

旧四参 `Ai.Choose(b, side, level, weights)` 仍只为 Normal 配置权重，Hard/Easy 继续忽略此参数。新 `Ai.ChooseWithWeights` / `ChooseReplacementWithWeights` 是 Normal/Hard 调参、历史复现的显式入口；Easy 忽略传入权重，Expert 不支持显式权重并抛出异常。Hard 补位算法没有新增可调参数。

T33 的五个历史 SHA 与 Golden 文件全部保留。历史回归显式传 Legacy，继续检查原日志和后续 RNG；T33 另保留全部默认重载一致性断言。这样改变的是历史测试的策略输入，不是期望输出或断言强度。新默认的强度由 T95、T06、T12、T35 共同检验。

## Normal v1 每轮结果

下表每项是该候选的胜局数 / 400；平局不剔除。采用列只列满足 Wilson 门槛且在当轮最佳的候选。

| 轮 | 候选 1–8 胜局数 | 采用 |
|---:|---|---|
| 1 | 184 / 213 / 200 / 206 / 211 / 198 / 200 / 212 | — |
| 2 | 190 / 175 / 205 / 207 / 188 / 162 / 200 / 192 | — |
| 3 | 200 / 200 / 197 / 203 / 198 / 205 / 184 / 220 | — |
| 4 | 194 / 199 / 182 / 190 / 212 / 174 / 198 / 187 | — |
| 5 | 216 / 206 / 192 / 201 / 181 / 202 / 196 / 217 | — |
| 6 | 200 / 176 / 200 / 210 / 171 / 234 / 186 / 201 | 6 |
| 7 | 188 / 191 / 192 / 174 / 173 / 205 / 181 / 156 | — |
| 8 | 200 / 203 / 182 / 196 / 209 / 196 / 198 / 199 | — |
| 9 | 196 / 184 / 195 / 181 / 222 / 174 / 200 / 213 | — |
| 10 | 212 / 200 / 208 / 218 / 200 / 193 / 217 / 194 | — |
| 11 | 193 / 198 / 155 / 199 / 217 / 190 / 192 / 227 | — |
| 12 | 198 / 205 / 211 / 204 / 177 / 138 / 173 / 212 | — |
| 13 | 200 / 164 / 171 / 182 / 220 / 180 / 210 / 205 | — |
| 14 | 237 / 189 / 212 / 194 / 146 / 164 / 141 / 181 | 1 |
| 15 | 204 / 192 / 133 / 183 / 208 / 230 / 202 / 168 | 6 |
| 16 | 199 / 202 / 188 / 165 / 198 / 200 / 133 / 207 | — |
| 17 | 181 / 232 / 133 / 200 / 192 / 200 / 204 / 221 | 2 |
| 18 | 198 / 221 / 212 / 200 / 206 / 181 / 204 / 201 | — |
| 19 | 217 / 200 / 148 / 78 / 208 / 193 / 88 / 100 | — |
| 20 | 204 / 200 / 205 / 204 / 177 / 204 / 210 / 218 | — |
| 21 | 209 / 192 / 212 / 209 / 202 / 210 / 177 / 189 | — |
| 22 | 228 / 219 / 198 / 200 / 127 / 200 / 182 / 198 | 1 |
| 23 | 141 / 200 / 199 / 205 / 166 / 200 / 198 / 115 | — |
| 24 | 197 / 183 / 188 / 140 / 208 / 206 / 200 / 156 | — |
| 25 | 176 / 194 / 186 / 200 / 191 / 158 / 168 / 196 | — |
| 26 | 200 / 96 / 194 / 189 / 196 / 204 / 200 / 197 | — |
| 27 | 204 / 199 / 198 / 194 / 210 / 200 / 155 / 149 | — |
| 28 | 201 / 214 / 200 / 79 / 113 / 207 / 195 / 84 | — |
| 29 | 194 / 211 / 203 / 170 / 200 / 114 / 195 / 208 | — |
| 30 | 193 / 210 / 197 / 200 / 206 / 53 / 202 / 224 | — |

Normal v1 留出：582 胜、218 负、0 平，胜率 72.75%。但将其单独启用时，旧 Hard 在 T12 难度顺序基准仅 126/300 胜（42%）；因此不能仅凭 Normal 的自我对弈提升宣称整个任务通过。

## 发布组合的独立训练约束（结果揭晓前固定）

Normal v1 的单档提升破坏了旧难度顺序，因此仅从两档训练中正式接受过的检查点选择可发布组合。该阶段仍属于模型选择，不称为最终验收。协议在 Hard v3 完整结果与 T95 固定验收结果揭晓前确定：

1. 所有 Normal 已接受检查点在相同的独立 400 局（战斗种子 970001–970050）挑战旧 Normal；所有 Hard 已接受检查点在 980001–980050 挑战旧 Hard。两者均要求胜率 >52% 且 Wilson 下界 >52%，不包含未达到提升的初始旧配置。
2. 所有合格 Hard/Normal 组合在相同的独立 400 局（990001–990050）对战。Hard 胜率至少 58%，且 Wilson 下界 >52%；点估计沿用既有 T12 的较强难度门槛，并要求与自我对弈相同的优势筛选下界。全部比较保留平局分母与八局对称协议。
3. 在可行组合中选择 Normal 对旧版胜率最高者，再比较 Hard 对旧版胜率；仍同分时按较早的已接受轮次稳定取值。不读取 T12/T35/T95 的失败场次来选阵容或权重。若无可行组合，明确报告未找到满足要求的组合，不降低门槛。
4. 选定后另用全新 800 局种子 1000001–1000100（Normal）、1010001–1010100（Hard）对旧版做最终留出；Hard 对 Normal 另用 400 局 1020001–1020050。最终留出不反馈筛选。先前各800局结果仅保留为对应实验的历史结果。

## Hard v3 每轮结果

下表与 Normal 表相同：每项为胜局数，分母 400；例如 232 表示 232/400=58.00%。原始报告另含所有候选的完整权重、扰动字段、种子、胜负平及采用标记。两档各 30×8×400，共各 96,000 局训练。

| 轮 | 候选 1–8 胜局数 | 采用（胜率） |
|---:|---|---|
| 1 | 167 / 200 / 184 / 198 / 170 / 210 / 204 / 192 | — |
| 2 | 218 / 196 / 198 / 198 / 200 / 200 / 190 / 192 | — |
| 3 | 196 / 242 / 212 / 184 / 198 / 224 / 194 / 158 | 2（60.50%） |
| 4 | 216 / 214 / 204 / 190 / 194 / 210 / 204 / 186 | — |
| 5 | 200 / 210 / 206 / 200 / 168 / 190 / 176 / 214 | — |
| 6 | 200 / 196 / 200 / 208 / 200 / 196 / 196 / 180 | — |
| 7 | 208 / 178 / 200 / 218 / 212 / 212 / 198 / 212 | — |
| 8 | 220 / 180 / 210 / 200 / 186 / 216 / 208 / 230 | 8（57.50%） |
| 9 | 202 / 213 / 206 / 172 / 184 / 174 / 214 / 196 | — |
| 10 | 178 / 188 / 212 / 172 / 206 / 200 / 196 / 190 | — |
| 11 | 192 / 192 / 206 / 206 / 228 / 206 / 206 / 208 | 5（57.00%） |
| 12 | 194 / 200 / 200 / 200 / 192 / 214 / 206 / 172 | — |
| 13 | 166 / 200 / 204 / 220 / 200 / 184 / 204 / 186 | — |
| 14 | 200 / 162 / 150 / 217 / 214 / 216 / 232 / 200 | 7（58.00%） |
| 15 | 200 / 186 / 196 / 210 / 200 / 136 / 200 / 218 | — |
| 16 | 216 / 172 / 208 / 204 / 202 / 182 / 190 / 210 | — |
| 17 | 192 / 154 / 196 / 202 / 200 / 192 / 202 / 180 | — |
| 18 | 186 / 172 / 162 / 200 / 210 / 174 / 170 / 170 | — |
| 19 | 204 / 210 / 184 / 216 / 200 / 172 / 220 / 216 | — |
| 20 | 194 / 204 / 204 / 212 / 132 / 196 / 200 / 194 | — |
| 21 | 200 / 200 / 174 / 204 / 138 / 182 / 192 / 204 | — |
| 22 | 216 / 198 / 196 / 192 / 208 / 204 / 204 / 146 | — |
| 23 | 196 / 206 / 182 / 200 / 200 / 188 / 196 / 202 | — |
| 24 | 236 / 201 / 190 / 164 / 200 / 202 / 142 / 190 | 1（59.00%） |
| 25 | 138 / 200 / 184 / 174 / 200 / 224 / 204 / 178 | — |
| 26 | 194 / 208 / 196 / 200 / 192 / 200 / 200 / 200 | — |
| 27 | 204 / 200 / 212 / 212 / 228 / 200 / 208 / 219 | 5（57.00%） |
| 28 | 202 / 182 / 192 / 220 / 140 / 132 / 140 / 146 | — |
| 29 | 202 / 210 / 200 / 194 / 192 / 200 / 212 / 198 | — |
| 30 | 190 / 212 / 214 / 156 / 202 / 210 / 200 / 148 | — |

Hard 训练末尾为 r27，训练末尾独立留出为 508 胜、292 负、0 平（63.50%）。它不是发布权重；发布的 r14 由上述预声明约束选择，并以全新种子验证。Normal v1 在 jobs 字段加入前串行完成；两份正式训练报告状态均为 completed。

## 检查点约束与发布组合

全部 5 个 Normal、6 个 Hard 已接受检查点对同难度旧版的约束结果如下。每项 400 局，平局均为 0。Hard r03 的 Wilson 下界不足 52%，不进入组合矩阵，其余全部合格。

| 检查点 | 胜 / 负 / 平（400 局） | 胜率 | Wilson 下界 | 进入组合比较 |
|---|---|---:|---:|---|
| normal-r06 | 235 / 165 / 0 | 58.75% | 53.86% | 是 |
| normal-r14 | 235 / 165 / 0 | 58.75% | 53.86% | 是 |
| normal-r15 | 244 / 156 / 0 | 61.00% | 56.14% | 是 |
| normal-r17 | 268 / 132 / 0 | 67.00% | 62.25% | 是 |
| normal-r22 | 272 / 128 / 0 | 68.00% | 63.28% | 是 |
| hard-r03 | 220 / 180 / 0 | 55.00% | 50.10% | 否 |
| hard-r08 | 238 / 162 / 0 | 59.50% | 54.62% | 是 |
| hard-r11 | 230 / 170 / 0 | 57.50% | 52.61% | 是 |
| hard-r14 | 258 / 142 / 0 | 64.50% | 59.69% | 是 |
| hard-r24 | 246 / 154 / 0 | 61.50% | 56.64% | 是 |
| hard-r27 | 256 / 144 / 0 | 64.00% | 59.18% | 是 |

全部合格组合矩阵如下，每格为 Hard 胜局 / 400（胜率）。✓ 同时满足胜率至少 58% 和 Wilson 下界 >52%，✗ 未达到门槛；全部 25 格均已运行，没有按先出现的成绩提前停止。

| Hard / Normal | r06 | r14 | r15 | r17 | r22 |
|---|---|---|---|---|---|
| r08 | 263/400（65.75%）✓ | 281/400（70.25%）✓ | 240/400（60.00%）✓ | 233/400（58.25%）✓ | 221/400（55.25%）✗ |
| r11 | 285/400（71.25%）✓ | 291/400（72.75%）✓ | 246/400（61.50%）✓ | 239/400（59.75%）✓ | 223/400（55.75%）✗ |
| r14 | 292/400（73.00%）✓ | 273/400（68.25%）✓ | 241/400（60.25%）✓ | 258/400（64.50%）✓ | 268/400（67.00%）✓ |
| r24 | 272/400（68.00%）✓ | 256/400（64.00%）✓ | 236/400（59.00%）✓ | 247/400（61.75%）✓ | 253/400（63.25%）✓ |
| r27 | 268/400（67.00%）✓ | 280/400（70.00%）✓ | 262/400（65.50%）✓ | 261/400（65.25%）✓ | 272/400（68.00%）✓ |

按预声明排序，Normal r22 对旧版的约束胜率 68.00% 最高；其合格 Hard 搭档中，r14 对旧版 64.50%，高于 r27 的 64.00% 和 r24 的 61.50%，故选 r22/r14。该组合的约束成绩为 Hard 268/400（67.00%）。r27 的组合胜率虽更高，但协议优先比较 Hard 对旧版成绩，未事后改动排序。

原始文件 SHA-256（包含原文件格式）：

| 文件 | SHA-256 |
|---|---|
| `legacy.json` | `efccf3f606870bcc72f4a1424996986b0dfbdc87e324c08b19b7e5882e759416` |
| `normal-r22.json` | `29aa7625bf9bf8827f5455061130a4e671c2c1351f34b367773c3abf9228e6dc` |
| `hard-r14.json` | `93b6015e0fe9f3c5af3b8206ffa1dc535db610e799828d8d603c18edb70e633a` |

`selection-manifest.json` 还保存全部 11 个已接受检查点的 SHA。已逐份核对旧权重及检查点，共 12 份文件均与 manifest 一致。重新格式化 JSON 会改变文件 SHA，不代表数值变化。

## 最终留出与验收

组合固定后才运行以下全新样本。风格修复后以最终代码复核，三份 `*-final-code.json` 与原最终留出报告逐字一致；original 没有配置演员风格，修复不改变这些对局。

| 比较 | 战斗种子 | 胜 / 负 / 平 | 胜率 | Wilson 下界 |
|---|---|---|---:|---:|
| 新 Normal r22 对旧 Normal | 1000001–1000100 | 581 / 219 / 0（800 局） | 72.625% | 69.43% |
| 新 Hard r14 对旧 Hard | 1010001–1010100 | 517 / 283 / 0（800 局） | 64.625% | 61.25% |
| 新 Hard r14 对新 Normal r22 | 1020001–1020050 | 250 / 150 / 0（400 局） | 62.50% | 57.66% |

T95 固定验收在训练前冻结：阵容种子为 `0x95A00000 + suite*0x10000 + sample`，战斗种子为 `0x95B00000 + suite*0x10000 + sample`，sample=0..49。每组覆盖候选所在方和选招调用顺序四种组合，共 200 局，区别于工具的八局块。这些种子没有用于生成候选、选择检查点或组合排序。

| T95 suite | 比较 | 胜 / 负 / 平 | 胜率 | 断言要求 |
|---:|---|---|---:|---|
| 0 | 新 Normal 对旧 Normal | 144 / 56 / 0 | 72.00% | ≥104/200（52%） |
| 1 | 新 Hard 对旧 Hard | 127 / 73 / 0 | 63.50% | ≥104/200（52%） |
| 2 | 新 Hard 对新 Normal | 125 / 75 / 0 | 62.50% | ≥110/200（55%） |

既有 T12 固定 300 局：Hard 胜 180、负 120、平 0（60.00%），达到原 ≥58% 断言；0 侧 86/150，1 侧 94/150。T35 专家对困难测试通过。上一轮完整 1594 项仅 T53 风格一项失败，随后修复有风格 Hard 的 Legacy 基底并补充覆盖。风格修复后完整测试 1597/1597 通过。随后将主分支快进至 `fd724b8`，纳入 T68、T86、T89、T92（未修改本实验 AI、规则或原创包），再次运行 `dotnet test Engine.Tests`：**1645/1645 通过、0 失败、0 跳过**，耗时 2.6519 分钟，包含 18 项 T95 测试。Core 与 Sim 的 warnings-as-errors 构建各 0 警告、0 错误，同步后也再次构建通过。

检查时 `origin/main` 的 T-85 仍未合并，因此没有更新尚未启用的 `docs/ai-baseline.json`。以上结果只针对所列版本、内容、种子与采样协议；表中 Wilson 数值不提供对任意对手或其它包的独立 95% 优势保证。

## 完整 36 项参数对照

Hard 列为发布 r14，而非训练末尾 r27；未使用或未变化的字段也完整保留。这些数值用于策略评分和阈值，不修改战斗属性或结算公式。

| 参数 | Legacy 旧版 | Normal r22 | Hard r14 |
|---|---:|---:|---:|
| CleanseScore | 0.4 | 0.02423097948387277 | 0.049886425584554674 |
| ControlledSwitchMinHpRatio | 0.3 | 0.3 | 0.3 |
| ControlledSwitchProbability | 0.35 | 0.3950236379401758 | 0.35 |
| ControlStatusScore | 0.5 | 0.5 | 2.429317989741801 |
| DamageScoreMultiplier | 1 | 1 | 1 |
| DispelBoostScore | 0.12 | 0.11334177173674106 | 0.06763436414301395 |
| DispelFieldScore | 0.2 | 0.2906405037388181 | 0.08074422972276807 |
| DispelTrapScore | -0.2 | -0.15242178002372386 | -0.1935823234729469 |
| FieldMaxThreatRatio | 0.6 | 0.023707336247747294 | 0.0619278185069561 |
| FieldSafeScore | 0.32 | 0.4061274227499962 | 0.3589015503227711 |
| FieldThreatenedScore | 0.12 | 0.12 | 0.12 |
| FirstKnockoutBonus | 0.6 | 0.6 | 0.7162418285384774 |
| FoeStageScore | 0.18 | 0.27323309727944434 | 0.3594077047985047 |
| HealMaxHpRatio | 0.5 | 0.5 | 0.9378378209657967 |
| HealMissingHpScore | 0.9 | 0.5042902272048184 | 0.9 |
| KnockoutScore | 1.6 | 1.6110160760163208 | 1.6 |
| MatchupSlowerPenalty | 0.2 | 0.1234392335638404 | 0.2 |
| PpReduceScore | 0 | 0 | 0 |
| RandomScoreRange | 0.08 | 0.018714221035685166 | 0.08 |
| ReflectDamageScoreMultiplier | 2 | 3.638111127540469 | 2.972268307581544 |
| ReflectScoreCap | 0.7 | 1.3452172131697242 | 0.5476419548504055 |
| StageBoostTarget | 2 | 1.2371078999713063 | 0.08502477686852217 |
| StageMaxThreatRatio | 0.5 | 0.13156853383406997 | 0.7602220241025459 |
| StageMinHpRatio | 0.6 | 0.1318834670819342 | 1.204126205329767 |
| StageScore | 0.22 | 0.36439369353465734 | 0.19832700082109678 |
| StealBoostBaseScore | 0.12 | 0.1905173104070127 | 0.12 |
| StealBoostPerStageScore | 0.08 | 0.07360851850360632 | 0.1460947121307254 |
| SwitchMaxActionScore | 0.5 | 0.8061283407732844 | 0.5 |
| SwitchMinMatchupGain | 0.5 | 0.9996142959577979 | 0.5 |
| SwitchMinThreatRatio | 0.9 | 0.40619952888228 | 0.9 |
| SwitchProbability | 0.6 | 0.9869777296632414 | 0.6 |
| ThreatenedFirstKnockoutBonus | 1 | 0.26332098292186856 | 2.167632253910571 |
| TrapAfflictedScore | 0.4 | 0.07886135518028639 | 0.4668673884123564 |
| TrapScore | 0.22 | 0.22 | 0.22 |
| WeakImmunityScore | 0.4 | 0.29198976811021565 | 0.34047039952129127 |
| WeakStatusScore | 0.3 | 0.07546347704913973 | 0.5736655336339026 |

## 可复制复现命令

在仓库根目录使用 PowerShell 7 按顺序运行。产物进入新的 `Engine.Tests/obj/T95-reproduce-*`；需要 .NET 8 SDK。完整训练较耗时，不是普通测试的前置步骤。

**必须显式传入旧权重。** 当前 Default/HardDefault 已变，省略 `--weights` 会从新默认起步，无法复现本文训练。以下提供完整旧 36 字段，同时显式指定 `--weights` 和 `--baseline`；对比模式也必须提供双方完整 JSON。文件排版可以变化，数值不可变。

```powershell
$ErrorActionPreference = 'Stop'
$run = Join-Path $PWD ('Engine.Tests/obj/T95-reproduce-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
$oldEnvironment = @{}
foreach ($name in @('TEMP','TMP','DOTNET_CLI_HOME','DOTNET_gcServer','DOTNET_PROCESSOR_COUNT')) {
    $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$legacyJson = @'
{
  "CleanseScore": 0.4,
  "ControlStatusScore": 0.5,
  "ControlledSwitchMinHpRatio": 0.3,
  "ControlledSwitchProbability": 0.35,
  "DamageScoreMultiplier": 1,
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
  "PpReduceScore": 0,
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
'@
try {
    $tempDir = Join-Path $run 'temp'
    $dotnetHome = Join-Path $run 'dotnet-home'
    New-Item -ItemType Directory -Path $tempDir,$dotnetHome | Out-Null
    $env:TEMP = $tempDir
    $env:TMP = $tempDir
    $env:DOTNET_CLI_HOME = $dotnetHome
    $legacy = Join-Path $run 'legacy.json'
    [IO.File]::WriteAllText($legacy, $legacyJson, [Text.UTF8Encoding]::new($false))
    $simDir = Join-Path $run 'bin'
    dotnet build Tools/Sim -c Release -o $simDir
    if ($LASTEXITCODE -ne 0) { throw 'Sim 构建失败' }
    $sim = Join-Path $simDir 'Sim.dll'

    # 环境只影响随后启动的进程；Normal 原始运行采用单工作线程。
    $env:DOTNET_gcServer = '0'
    $env:DOTNET_PROCESSOR_COUNT = '8'
    dotnet $sim --tune --pack original --level normal --rounds 30 --candidates 8 --games 400 --seed 950001 --perturbation 1 --changes 12 --holdout-games 800 --jobs 1 --weights $legacy --baseline $legacy --report "$run/normal-report.json" 1> "$run/normal-weights.json" 2> "$run/normal.log"
    if ($LASTEXITCODE -ne 0) { throw 'Normal 训练失败' }
    $env:DOTNET_gcServer = '1'
    dotnet $sim --tune --pack original --level hard --rounds 30 --candidates 8 --games 400 --seed 960001 --perturbation 1 --changes 12 --holdout-games 800 --jobs 8 --weights $legacy --baseline $legacy --report "$run/hard-report.json" 1> "$run/hard-weights.json" 2> "$run/hard.log"
    if ($LASTEXITCODE -ne 0) { throw 'Hard 训练失败' }

    # 导出所有被正式接受的检查点，不仅导出训练末尾。
    $checkpoints = @{}
    foreach ($level in @('normal','hard')) {
        $report = Get-Content "$run/$level-report.json" -Raw | ConvertFrom-Json
        foreach ($round in $report.rounds) {
            foreach ($candidate in @($round.candidates | Where-Object accepted)) {
                $name = '{0}-r{1:00}' -f $level,$round.round
                $file = Join-Path $run ($name + '.json')
                $candidate.weights | ConvertTo-Json -Depth 5 | Set-Content $file -Encoding utf8
                $checkpoints[$name] = $file
            }
        }
    }
    function Compare-Weights($name, $level, $opponentLevel, $weights, $baseline, $seed, $games = 400) {
        $reportPath = Join-Path $run ($name + '.report.json')
        dotnet $sim --compare --pack original --level $level --opponent-level $opponentLevel --weights $weights --baseline $baseline --games $games --seed $seed --jobs 8 --report $reportPath 1> "$run/$name.stdout.json" 2> "$run/$name.log"
        if ($LASTEXITCODE -ne 0) { throw "比较失败：$name" }
        return (Get-Content $reportPath -Raw | ConvertFrom-Json)
    }
    $qualified = @{normal=@(); hard=@()}
    foreach ($name in ($checkpoints.Keys | Sort-Object)) {
        $level = $name.Split('-')[0]
        $seed = if ($level -eq 'normal') {970001} else {980001}
        $result = Compare-Weights "$name-v-legacy" $level $level $checkpoints[$name] $legacy $seed
        if ($result.result.winRate -gt .52 -and $result.result.wilson95Lower -gt .52) {
            $qualified[$level] += $name
        }
    }
    foreach ($hardName in $qualified.hard) {
        foreach ($normalName in $qualified.normal) {
            $null = Compare-Weights "$hardName-v-$normalName" 'hard' 'normal' $checkpoints[$hardName] $checkpoints[$normalName] 990001
        }
    }
    # 按本文预声明排序核对矩阵，发布组合应为 normal-r22 + hard-r14。
    $normalFinal = $checkpoints['normal-r22']
    $hardFinal = $checkpoints['hard-r14']
    if (-not $normalFinal -or -not $hardFinal) { throw '接受检查点与记录不一致，停止复现' }
    $null = Compare-Weights 'final-normal-v-old' 'normal' 'normal' $normalFinal $legacy 1000001 800
    $null = Compare-Weights 'final-hard-v-old' 'hard' 'hard' $hardFinal $legacy 1010001 800
    $null = Compare-Weights 'final-hard-v-normal' 'hard' 'normal' $hardFinal $normalFinal 1020001 400
} finally {
    foreach ($name in $oldEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process')
    }
}
```

复现时比较完整权重、候选成绩、采用轮次和约束矩阵。当前工具比 Normal v1 原报告多了 jobs 字段，JSON 排版或输出目录也可不同；这些元数据差异不改变策略。数值不一致时先检查代码版本与内容指纹，不更换种子寻找更好结果。八工作线程要求可用逻辑处理器数不少于 8；其它机器可降低 jobs，但不能改变种子或样本数。进程环境变量由 finally 恢复。

## 本地原始证据

以下路径相对本工作树 `D:\vtuber-battle-wt\T-95`；obj 产物不提交仓库，本文保留必要参数、结果和复现方法。

- `Engine.Tests/obj/T95-training/normal-v1-report.json`、`hard-v3-report.json`：完整 30×8 候选、权重、扰动、种子、胜负平、采用标记及训练末尾留出；同名 `.log` 为运行记录。
- `Engine.Tests/obj/T95-training/selection-manifest.json`、`chosen.json`、`checkpoints/*.json`：选择协议、全部检查点 SHA、最终发布组合。
- `Engine.Tests/obj/T95-training/constraints/*.json`：11 份同档旧版约束和 25 份组合比较；`.stdout.json` 为报告副本，不是额外样本。
- `Engine.Tests/obj/T95-training/final-holdout/{normal-v-old,hard-v-old,hard-v-normal}.json`：最终新种子验证；同目录 `*-final-code.json` 为风格修复后最终代码复核，三份与原报告逐字一致。
- `Engine.Tests/obj/T95-training/hard-v1-report.json`、`hard-v2-report.json`、`hard-server-gc-probe.json`：中断运行及执行环境探针，只用于排障和一致性核验，不用于选择参数。
- `Engine.Tests/obj/T95-test-before-style-fix.log`：1594 项中 1 项风格失败的历史记录；`Engine.Tests/obj/T95-test.log`：风格修复后 1597/1597；`Engine.Tests/obj/T95-test-integrated.log`：主分支同步后最终 1645/1645，以及 T95/T12/T35 的输出。
- `Engine.Tests/obj/T95-core-build.log`、`T95-sim-build.log`、`T95-integrated-build.log`：构建证据；`Engine.Tests/obj/T95-sim/`：固定种子、串并行、compare、参数边界等验证脚本和结果。
