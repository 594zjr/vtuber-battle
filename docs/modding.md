# 自制内容包教程

本教程面向只改 JSON 的内容作者。规则以 [design.md](design.md) 为准；完整起点是仓库中的 [docs/examples/example_pack.json](examples/example_pack.json)，包含六位虚构示例主播、包内技能、人设、状态和直播间效果。先运行原样示例，再逐项改名、调数值或组合已有机制。

## 1. 放在哪里，怎样运行

每个内容包是一个 UTF-8 `.json` 文件。使用标准 JSON：双引号、不写注释、不加末尾逗号，同一对象内不重复键。文件名建议与顶层 `id` 一致；`--pack` 实际匹配的是 `id`。

所有命令从仓库根目录运行，需要 .NET 8 SDK：

```powershell
dotnet run --project Tools/PackCheck -- --pack-dir ./docs/examples --pack example_pack
dotnet run --project Tools/Cli -- --pack-dir ./docs/examples --list-packs
dotnet run --project Tools/Cli -- --pack-dir ./docs/examples --pack example_pack --level normal --seed 1
```

最后一条进入交互式对战；加 `--auto` 可由电脑自动完成双方行动。CLI 会在结束后写入本地对战档案，可用 `--save-dir ./Tools/Cli/obj/example-saves` 指定试验存档目录。

Unity 版在启动时加载 `Application.persistentDataPath/packs/`。把文件直接放在该目录第一层，例如 `<persistentDataPath>/packs/example_pack.json`，重新启动后在组队页选择。`persistentDataPath` 是 Unity 为这个游戏提供的用户数据目录，不是字面目录名，也不是工程中的 `Resources/Packs/`；具体位置取决于平台和 Player Settings 中的公司名、产品名。开发时可在 Unity 中查看 `Application.persistentDataPath` 的值。

加载器只读指定目录第一层的 JSON，按文件名排序。单个坏文件会报告原因；其余合法文件继续加载。外部包同 id 时保留先成功加载的包；与内置包同 id 时保留内置包并提示。目录不存在时不增加外部包。不要把零散片段另存成此目录里的 `.json`，因为每个文件都会被当成完整包。

日常创作可以把示例复制到自己的目录，并改顶层 id，再把命令中的 `--pack-dir` 和 `--pack` 换成自己的值。不同包的定义独立加载，不能通过引用另一个外部包的 id 来取得它的技能。

## 2. 顶层字段

| 字段 | 类型 / 要求 | 用途 |
|---|---|---|
| `id` | 非空字符串，包标识 | 使用稳定、独有的英文标识，例如 `example_pack`；同人包使用 `fan_` 前缀 |
| `name` | 字符串 | 组队页和工具显示的包名 |
| `notice` | 字符串 | 声明；`fan_` 包须包含“非官方同人作品”，界面显示同人包声明 |
| `streamers` | 数组，至少 6 项 | 主播名单，字段见下一节 |
| `skills` | 可选对象，默认无新增 | 键为技能 id，值为技能定义 |
| `personas` | 可选对象，默认无新增 | 键为人设 id，值为人设定义 |
| `fields` | 可选对象，默认无新增 | 键为直播间效果 id，值为效果定义 |
| `statuses` | 可选对象，默认无新增 | 键为状态 id，值为状态定义 |
| `skillNames` | 可选对象，id → 字符串 | 给已有或本包技能换显示名 |
| `fieldNames` | 可选对象，id → 字符串 | 给已有或本包直播间效果换显示名 |
| `statusNames` | 可选对象，id → 字符串 | 给已有或本包状态换显示名 |
| `seasons` | 可选对象，默认无赛季 | 键为赛季 id，值为组队限制，见第 8 节 |

四类包内定义与共用规则合并后，才处理改名、主播绑定和引用校验。它们都可以引用同包新增定义。**同类别 id 不得覆盖共用规则中的 id**；给自己的所有 id 加前缀（示例使用 `ex_`）可减少冲突。显示改名不改变机制。定义的 id 是对象的键，不需要在定义内部再写 `id`。

`stats` 的显示名称、`categories`、`types`、`chart` 和 `struggle` 属于共用规则，不是内容包可覆盖的顶层字段。`PackDefined` 等 C# 内部标记也不是 JSON 字段。仅修改 `desc`、`when`、`node` 不会增加新机制。

## 3. 主播、属性与双赛道

| `streamers[]` 字段 | 类型 / 要求 | 用途 |
|---|---|---|
| `id` | 唯一字符串 | 本包主播标识，赛季和队伍引用它 |
| `name` | 字符串 | 显示名 |
| `org` | 可选字符串 | 所属团体的显示文字；原创示例可写“示例社团” |
| `blurb` | 非空字符串，最多 40 个 Unicode 字符 | 简介；PackCheck 会检查长度 |
| `type` | 已存在的赛道 id | 主赛道 |
| `type2` | 可选赛道 id 或 `null` | 第二赛道；必须存在且不同于 `type` |
| `stats` | 对象，六项全部填写整数 | `hp` 人气、`atk` 互动、`def` 抗压、`spa` 才艺、`sdef` 定力、`spd` 反应 |
| `persona` | 已存在的人设 id | 每位主播一个永久人设 |
| `skills` | 恰好 4 个技能 id 的数组 | 四个普通技能，数组顺序就是槽位顺序 |
| `fifth` | 已存在的技能 id | 第五技能，使用同一种技能定义格式 |
| `personaName` | 可选字符串 | 仅为这位主播复制并改名人设 |
| `personaDesc` | 可选字符串 | 仅为这位主播复制并改写人设说明，不能改变代码的作用 |
| `personaStatus` | 可选状态 id 或 `null` | 复制人设并替换它引用的状态；只有使用 `status` 的人设码才有对应行为 |
| `aiStyle` | 可选字符串或 `null` | 电脑出招风格：`aggressive`（猛攻）、`defensive`（稳守）、`tricky`（花招）；名称区分大小写，未知值拒绝加载 |

三个人设覆盖字段都会保留原人设的代码和参数；`null` 表示不替换，不是清空原状态。新 id 不要使用加载器为人设副本保留的 `主播id#原人设id` 形式。

例如在主播对象中加入 `"aiStyle": "aggressive"`：猛攻偏重伤害和击倒、较少换人；稳守偏重回复、防护和改善对位的换人；花招偏重状态、驱散和削减 PP。省略 `aiStyle` 或设为 `null` 时保留原有电脑行为；本次不为现有主播配置风格。

普通、困难和专家难度在选招时读取当前场上主播的风格，换人后重新读取；简单难度忽略风格。普通难度的风格预设优先于调用者传入的权重，困难和专家把风格相对默认估值的差额加入原有评分。风格只改变选择偏好，不改变技能、人设、合法行动或实际结算规则。`AiStyles.Get(name)` 每次返回独立权重，修改返回值不会污染预设；未知名称返回 `null`，但内容包校验会拒绝未知的 `aiStyle`。

`Describe.Persona` 对 `T_KO_HEAL/T_FIRST_CONTROL_IMMUNE/T_ENDURE/T_ALLY_PP_ON_ENTER/T_STAGE_ON_HIT` 会按代码和参数生成标准时机与说明；这些标准说明不会被 `personaDesc` 覆盖。填写自定义文字时仍须与真实行为一致。

战斗显示可调用三参数版本 `Describe.Persona(db, persona, battle)`，让说明读取本局规则。启用 `stage_cap_3` 时，`T_STAGE_ON_HIT` 的上限显示为 +3，`T_SPEED_IF_FIRST` 也按实际参数生成回合结束时机、反应提升和 +3 上限说明。双参数版本以及未启用特殊上限的对战保留原有说明行为；这些显示处理不改写内容包中的 `personaDesc`。

现有赛道 id 为：`song` 歌回、`game` 游戏、`talk` 杂谈、`art` 绘画、`meme` 整活、`asmr` 助眠、`horror` 恐怖、`tech` 硬核。不要把中文显示名当成 id。

双赛道写成 `"type": "song", "type2": "talk"`。受到技能伤害时，两条防守赛道的克制倍率相乘；攻击技能匹配自身任意一条赛道就得到一次 ×1.5 的同赛道加成。技能本身只有一个 `type`，固定伤害不吃这些倍率。

属性填写正整数，并参照共用原创包的量级做测试；加载成功不代表任意负数、极大数都合理。能力等级与基础属性分开：技能里的 `changes` 只接受 `atk/def/spa/sdef/spd`，不能写 `hp`，默认等级范围为 −6～+6；对战启用 `stage_cap_3` 修饰器时改为 −3～+3，见第 8 节。

## 4. 技能与效果公共字段

一个完整技能定义可以是：

```json
{
  "name": "示例·清晰开场",
  "type": "song",
  "cat": "talent",
  "power": 70,
  "pp": 15,
  "acc": 100,
  "prio": 0,
  "effects": [
    { "at": "post", "code": "STAGE", "target": "self", "changes": { "spa": 1 }, "chance": 30 }
  ]
}
```

| 技能字段 | 类型 / 默认值 | 含义 |
|---|---|---|
| `name` | 字符串 | 显示名 |
| `type` | 赛道 id | 技能的单一赛道 |
| `cat` | 字符串 | `interact` 互动、`talent` 才艺、`status` 状态 |
| `power` | 整数，默认 0 | 威力；状态技能使用 0，伤害技能填写正数 |
| `pp` | 整数，默认 0 | 可用次数上限；创作时显式填写正数 |
| `acc` | 整数，默认 100 | 命中率，使用 0～100 |
| `prio` | 整数，默认 0 | 技能先制修正，允许负数 |
| `effects` | 可选数组，默认空 | 按数组顺序处理其中对应时机的效果 |

技能命中后依次执行 `pre` → 普通技能伤害 → `post`。界面把这两个时机称为“命中前判定”“命中后判定”：这里的 `pre` 指伤害前，**未命中时不会执行 pre 或 post**。若行动者下播或对战结束，后续结算会中止，不能假设数组最后一项总能执行。

| `effects[]` 字段 | 类型 / 默认值 | 使用方式 |
|---|---|---|
| `at` | 必填 `pre` 或 `post` | 判定与执行时机 |
| `code` | 已支持的效果码 | 见下节，大小写必须一致 |
| `target` | `foe`（默认）或 `self` | 对手或自身；只对支持目标选择的效果码生效 |
| `chance` | 整数，默认 100 | 条件成立后再判概率，使用 0～100 |
| `cond` | 可选对象，默认无条件 | `kind` 为条件码，`value` 为数值参数，默认 0 |
| `status` | 状态 id | `STATUS` 引用的状态 |
| `field` | 直播间效果 id | `FIELD`、`ADD_MARK`、`POWER_PER_MARK` 使用 |
| `turns` | 整数，默认 −1 | `FIELD` 的持续回合；正数为有限回合，−1 为直到下场 |
| `n` | 整数，默认 0 | PP 削减、固定伤害、印记层数等；通常填写正数 |
| `frac` | 数字，默认 0 | 回复、反噬、吸取的比例；0.25 表示 25% |
| `mul` | 数字，默认 1 | `POWER_MUL` 的威力倍率 |
| `per` | 数字，默认 0 | `POWER_PER_MARK` 每层增加的威力比例 |
| `consume` | 布尔，默认 false | `POWER_PER_MARK` 是否消耗全部对应印记 |
| `changes` | 对象，默认空 | `STAGE` 的能力 id → 整数等级变化，例如 `{"atk": 1, "spd": -1}` |

每个效果只填写自己使用的参数。`HEAL/RECOIL/DRAIN/CLEANSE/POWER_MUL/POWER_PER_MARK` 作用于使用者，不能靠 `target` 改成作用于对手。消回合与消强化按设计针对对手，下面明确写 `target: "foe"`。

条件在各自 `at` 时机读取当时的状态；条件不满足时不掷概率。给效果加上 `"cond": {"kind": "foeHpAbove", "value": 0.5}`，表示此时对手人气严格高于最大值的 50% 才判定它。

| `cond.kind` | 条件 |
|---|---|
| `foeHpAbove` | 对手当前人气比例严格大于 `value`（比例用 0～1） |
| `foeWeak` | 对手有弱化类状态 |
| `actedFirst` | 当前在先手行动节点 |
| `actedSecond` | 当前在后手行动节点 |
| `foeBuffed` | 对手至少一项能力等级大于 0 |

## 5. 全部效果码的最小例子

以下每一行的 JSON 都是**一个 `effects` 数组元素**，不是完整内容包。引用的 `stall/shield/hype` 来自共用规则，复制到任意合法技能即可；伤害相关效果应搭配有威力的技能。每项都给出了 `at`，消除规则由实际产生的状态或直播间效果决定，没有通用的技能 `dispel` 开关。

| 效果码及最小 JSON | 判定 / 作用 | 能否消除 |
|---|---|---|
| `{"at":"pre","code":"STATUS","target":"foe","status":"stall"}` | 伤害前施加控制；次数由状态的 `skip` 决定 | 此例不能净化；弱化类应定义 `cleanse: true` |
| `{"at":"post","code":"STAGE","target":"self","changes":{"atk":1}}` | 伤害后互动等级 +1 | 正等级可被消强化；正负等级均在下场清零 |
| `{"at":"post","code":"HEAL","frac":0.25}` | 伤害后回复自身最大人气的 25%，不超过上限 | 即时结算，无持续效果可驱散 |
| `{"at":"post","code":"RECOIL","frac":0.25}` | 伤害后自身承受普通技能主伤害实际扣血的 25%；有伤害时至少 1 | 即时结算 |
| `{"at":"post","code":"DRAIN","frac":0.5}` | 伤害后回复普通技能主伤害实际扣血的 50%，不能复活 | 即时结算 |
| `{"at":"post","code":"PP_REDUCE","target":"foe","n":2}` | 伤害后削减目标本次上场最后使用技能的 PP，最低 0；尚未使用技能则无效 | 即时结算，不可驱散 |
| `{"at":"post","code":"FIXED_DAMAGE","target":"foe","n":40}` | 伤害后追加固定 40 点，不吃普通伤害倍率 | 即时结算，不可驱散 |
| `{"at":"post","code":"FIELD","target":"self","field":"shield","turns":2}` | 伤害后获得或刷新 2 回合护盘；施加当回合结束也扣 1 回合 | 此例 `shield.dispel=true`，可消回合；下场清空 |
| `{"at":"post","code":"ADD_MARK","target":"self","field":"hype","n":1}` | 伤害后热度 +1 层，受 `maxStacks` 限制 | 热度是不可消回合的印记；可被消费，下场清空 |
| `{"at":"pre","code":"POWER_MUL","mul":1.5}` | 仅限 pre，本次威力 ×1.5 | 只影响本次技能，不留可驱散效果 |
| `{"at":"pre","code":"POWER_PER_MARK","field":"hype","per":0.2,"consume":true}` | 仅限 pre，本次威力 ×(1+0.2×自身层数)，再耗尽该印记；没有层数则 ×1 | 倍率只影响本次，印记已按参数消耗 |
| `{"at":"pre","code":"DISPEL_TURNS","target":"foe"}` | 伤害前清除对手所有 `dispel=true` 的直播间效果 | 不清印记、保护类、状态或能力等级 |
| `{"at":"pre","code":"DISPEL_BOOST","target":"foe"}` | 伤害前把对手所有正能力等级归零 | 负等级与直播间效果保留 |
| `{"at":"pre","code":"STEAL_BOOST","target":"foe"}` | 伤害前取走对手正能力等级并加到自身，每项默认上限 +6，启用 `stage_cap_3` 时为 +3；对手负等级保留 | 转移后的正等级仍可消强化，下场清零；以自身为目标无效 |
| `{"at":"post","code":"CLEANSE"}` | 伤害后净化自身全部 `cleanse=true` 的弱化状态 | 即时移除；不清控制、直播间效果或能力等级 |

固定伤害可以解除受伤即醒的控制、导致下播，并可能触发反弹；不触发“被普通技能击中后”的人设。`T_ENDURE` 的一次性保命仍可生效。PP 削减不会恢复目标本回合已选但行动前被削到 0 的技能：该次行动会失败（满足免扣 PP 的人设条件时例外），具体顺序见 [设计文档 3.4 节](design.md#34-一次行动内部)。

`DRAIN/RECOIL` 所说的“本次伤害”仅指普通技能主伤害实际扣除的人气，不包含追加的 `FIXED_DAMAGE`。因此仅用固定伤害的状态技能再接吸取或反噬，不会按固定伤害数值回复或自伤。

## 6. 包内状态与直播间效果

这两种定义都写在内容包顶层，以自定义 id 为键。`STATUS` 引用 `statuses`；`FIELD/ADD_MARK/POWER_PER_MARK` 引用 `fields`。显示名可以相近，引用仍须区分 id。

### 状态 `statuses.<id>`

| 字段 | 类型 / 默认值 | 含义 |
|---|---|---|
| `name`、`desc` | 字符串 | 名称与准确说明 |
| `cat` | `control` 或 `weak` | 控制类或弱化类 |
| `skip` | 整数，默认 0 | 控制跳过的行动次数；控制状态显式填正数 |
| `turns` | 整数，默认 0 | 弱化持续回合；弱化状态显式填正数 |
| `dot` | 数字，默认 0 | 弱化在回合结束造成的最大人气比例伤害 |
| `interactMul` | 数字，默认 1 | 状态持有者造成互动伤害的倍率 |
| `leech` | 布尔，默认 false | 该持续伤害为对手场上主播回复对应人气 |
| `cleanse` | 布尔，默认 false | 必须按规则明确写：控制 false、弱化 true |
| `wakeOnHit` | 布尔，默认 false | 控制是否受到伤害便解除 |

控制持续时间取自状态的 `skip`，弱化持续时间取自状态的 `turns`；给 `STATUS` 元素加 `turns` 不会覆盖它们。控制在持有者自己的行动时扣次数；弱化在回合结束结算并扣回合。同一弱化重复施加刷新，控制同时最多一个。状态下场保留，后排暂停结算。不要用与规则分类相反的 `cleanse` 值制造不可净化弱化或可净化控制。

### 直播间效果 `fields.<id>`

| 字段 | 类型 / 默认值 | 含义与生效时机 |
|---|---|---|
| `name`、`desc` | 字符串 | 名称和作用说明 |
| `kind` | `turn` / `mark` / `protect` | 回合类 / 印记 / 保护类 |
| `when` | 字符串 | 给玩家看的触发时机说明；实际行为由下列字段决定 |
| `dispel` | 布尔，默认 false | 消回合的最终依据；按规则 turn 为 true，mark/protect 为 false |
| `dmgTakenMul` | 数字，默认 1 | 普通伤害结算时，持有者受到的伤害倍率 |
| `talentMul` | 数字，默认 1 | 持有者才艺技能造成的伤害倍率 |
| `consume` | 布尔，默认 false | 与非 1 的 `talentMul` 配合，才艺攻击使用后移除；不是通用的“任意触发后消费” |
| `prio` | 整数，默认 0 | 排行动顺序时修正技能先制 |
| `heal` | 数字，默认 0 | 回合结束回复自身最大人气的比例 |
| `dot` | 数字，默认 0 | 回合结束损失自身最大人气的比例 |
| `maxStacks` | 整数，默认 0 | `ADD_MARK` 层数上限；建议印记显式填写正整数，当前未设正上限时使用 99 |
| `immuneControl` | 布尔，默认 false | 阻止新施加的控制，不净化已有控制 |
| `immuneWeak` | 布尔，默认 false | 阻止弱化施加或刷新，不移除已有弱化 |
| `preventSwitch` | 布尔，默认 false | 禁止主动换人，不阻止下播后的补位 |
| `reflectDamage` | 数字，默认 0 | 受到对手技能直接伤害后的反弹比例；普通与固定伤害均可触发 |

持续时间放在施加它的 `FIELD.turns` 或 `T_ENTER_FIELD` 的 `params.turns`，不放在 `fields.<id>` 中。所有直播间效果下场清空，有限回合在回合结束扣减。反弹按实际扣除人气乘以比例，向下取整且至少 1；不连锁，不触发被反弹者人设，持续伤害、自伤和反噬不触发反弹；任一方全灭后立即结束后续结算。

## 7. 写人设

每个人设只有一个代码。要改变触发行为应选已有 `code`；`node` 是显示文字，不是脚本或任意事件名。

```json
{
  "name": "示例·登场护罩",
  "code": "T_ENTER_FIELD",
  "node": "登场时",
  "field": "shield",
  "params": { "turns": 2 },
  "desc": "登场时获得 2 回合护盘。护盘可被消回合，下场清除；人设本身不可消除。"
}
```

| 人设字段 | 类型 / 用途 |
|---|---|
| `name` | 显示名 |
| `code` | 下表中的一个人设代码 |
| `node` | 触发时机的中文显示文字，须与代码一致 |
| `desc` | 中文规则说明，包括参数和消除规则 |
| `params` | 可选数字对象，缺项按 0 处理；应填齐代码需要的参数，无参数时用 `{}` |
| `status` | `T_CONTACT_STATUS` 必填状态 id |
| `field` | `T_ENTER_FIELD` 必填直播间效果 id |

| 代码 | 实际时机与行为 | `params` 示例 |
|---|---|---|
| `T_LOWHP_POWER` | 造成伤害时，自身人气比例低于 below 则伤害乘 mul | `{"below":0.3333,"mul":1.5}` |
| `T_EXECUTE` | 造成伤害时，对手人气比例低于 below 则伤害乘 mul | `{"below":0.5,"mul":1.3}` |
| `T_REGEN` | 回合结束，回复最大人气 ×frac | `{"frac":0.1}` |
| `T_BENCH_GUARD` | 受伤时，按己方后排在播人数减伤，倍率为 1−人数×per | `{"per":0.05}` |
| `T_INTERACT_REDUCE` | 受到互动伤害时乘 mul | `{"mul":0.8}` |
| `T_FIRST_TURN_PRIO` | 排先手时，上场后的首个回合增加先制 | `{"prio":1}` |
| `T_TURNSTART_CLEANSE` | 回合开始，移除自身一个弱化状态 | `{}` |
| `T_FREE_PP_SLOT6` | 使用技能时，己方第 6 号位仍在播且不是自己，则不扣 PP | `{}` |
| `T_CONTACT_STATUS` | 被互动技能击中后，按 chance 给攻击方施加 `status` | `{"chance":30}` |
| `T_ENTER_FIELD` | 登场获得 `field`，持续 turns 回合 | `{"turns":2}` |
| `T_FAINT_CURSE` | 下播时，对手场上主播五项能力各变动 n；使用负数 | `{"n":-1}` |
| `T_SPEED_IF_FIRST` | 回合结束，本回合先手则反应等级 +n | `{"n":1}` |
| `T_KO_HEAL` | 本方普通或固定技能直接击倒后，回复最大人气 ×frac；已结束则不触发 | `{"frac":0.2}` |
| `T_FIRST_CONTROL_IMMUNE` | 每次登场后，挡下首次本来能施加的控制；下场再登场重置 | `{}` |
| `T_ENDURE` | 每场一次，致命伤害保留 1 人气；下场不重置，反弹不触发 | `{}` |
| `T_ALLY_PP_ON_ENTER` | 登场时，后排在播队友全部技能各回复 n PP，不超过上限 | `{"n":1}` |
| `T_STAGE_ON_HIT` | 受普通技能直接伤害且存活后，提升参数指定的能力；仅用正整数 | `{"def":1}` |

人设本体永久且不可消除；它产生的能力强化仍可被消强化，赋予的直播间效果仍看 `dispel`。直接回复为即时结算。`T_STAGE_ON_HIT` 不由固定、持续、反噬或反弹伤害触发；`T_KO_HEAL` 不由持续、反噬或反弹归因触发。第一回合先制的“首个回合”计算及其余细节见 [设计文档 6.2 节](design.md#62-人设-personasid)。

## 8. 赛季、规则修饰器与赛制边界

赛季描述**组队限制**，可选地写在内容包顶层：

```json
{
  "seasons": {
    "ex_open": {
      "name": "示例公开赛季",
      "banned": [],
      "limited": []
    }
  }
}
```

这是顶层片段，应合并到完整包，不单独保存成待加载文件。`name` 是显示名；`banned` 是禁止入队的主播 id 数组；`limited` 是限制池数组，每项形如 `{"max":1,"ids":["主播甲id","主播乙id"]}`，表示这两人最多选一人。`max` 为非负整数，`ids` 中的主播必须存在；每个限制池分别检查。省略 `banned/limited` 时为空。

六人示例名单只够组成一队，禁用任何一人或把两人设成限选一人都会导致凑不齐六人。需要这种限制时先扩展名单，再检查是否仍有合法队伍。两边可以使用同一主播，但同一队不能重复。

组队代码使用 `TeamCheck.Validate(db, seasonId, team)` 检查正好六人、不重复、引用有效和赛季限制；`seasonId=null` 仅做基础组队检查。内容包加载只检查赛季引用，不能代替具体队伍的合法性检查。CLI/Sim 目前没有 `--season` 选项，不会因 JSON 里有赛季便自动套用它。

T-46 规则修饰器已实现，入口是构造对战时的 `BattleOptions.Modifiers`，**不是内容包 JSON 字段**。接入 Core 的代码在已有 `db` 和合法的两队 `teams` 上可以这样启用：

```csharp
var options = new BattleOptions
{
    Seed = 1,
    Modifiers = new System.Collections.Generic.List<string> { "half_pp", "stage_cap_3" }
};
var battle = new Battle(db, teams, new[] { "甲方", "乙方" }, options);
```

`BattleModifiers.All` 提供 id、中文名称和说明；省略、`null` 或空列表沿用默认规则。id 区分大小写，未知 id 在构造时抛出 `KeyError`；列表会复制、去重并排序。可同时启用多项，规则整场有效且不可驱散，只影响本局实例，不改共享内容包。

| id | 本局作用 |
|---|---|
| `half_pp` | 开局双方全队普通和第五技能的 PP 上限减半、向上取整，当前 PP 同步；后续回复遵守新上限。 |
| `stage_cap_3` | 技能、人设和偷强化等能力变化统一限制在 −3～+3；消强化与下场清零规则不变。 |
| `sudden_death` | 从第 10 回合（含）起，按原回合结束顺序，在各自状态、直播间效果和人设结算后，场上未下播者失去最大人气的 1/16，向下取整且至少 1；一方全灭立即结束后续步骤。 |
| `hype_start` | 禁用、首发选择及开场登场人设结算后，双方实际首发各获 1 层热度；换人或补位不再授予，印记下场清除。 |
| `no_switch` | 禁止主动换人，禁用、首发选择和下播后的补位不受影响。 |
| `hp_x2` | 开局双方全队最大人气和当前人气翻倍；比例回复、持续伤害和骤降使用新上限，普通伤害公式与固定伤害数值不变。 |

修饰器写入对战记录，回放会还原；开场日志列出启用项，状态哈希也包含规则列表。骤降的人设响应、翻倍溢出检查等细节见 [设计文档“规则修饰器”](design.md#11-规则修饰器)。CLI/Sim 目前没有设置修饰器的命令行参数，本教程的工具命令均使用默认规则；在包内添加 `modifiers` 不会启用它们。

T-54 赛制与多局制仍待合并，目前没有内容包赛制字段。不要把自造的 `formats`、`rules` 写进包并期待生效。后续正式合并后，以 [设计文档](design.md) 和工具实际支持的参数为准。

## 9. 检查与平衡试跑

先运行第 1 节的 PackCheck。它检查 JSON、加载引用、主播人数、槽位数、双赛道、人设和状态引用、简介、同人声明等；报告中“错误”会使退出码为 1，“提醒”需要阅读但不阻止通过。小包未使用全部共用技能会产生未使用技能提醒，这不等于自定义技能引用错误。检查输出中的内容指纹可用于核对双方加载的版本。

PackCheck 不是完整的机制证明：它尚未对全部效果码、人设码、参数范围及字段拼写做白名单校验。写错未知字段可能被忽略，未知代码可能直到战斗才失败。因此只用本教程和设计文档里的代码，加载通过后还要实际出招观察 `Battle.Log`，确认时机、次数和消除行为。

需要生成名单说明时，可以额外运行：

```powershell
dotnet run --project Tools/PackCheck -- --pack-dir ./docs/examples --pack example_pack --write-docs
```

这条命令会写入或更新 `docs/roster/example_pack.md`；只做检查时不要加 `--write-docs`。

用 Sim 批量对战，先编译，再把仅包含 CSV 的标准输出存到忽略目录。以下使用 PowerShell：

```powershell
dotnet build Tools/Sim
dotnet run --no-build --project Tools/Sim -- --pack-dir ./docs/examples --pack example_pack --games 100 --seed 1 --level normal | Out-File -Encoding utf8 ./Tools/Sim/obj/example-balance.csv
```

总局数、平局数与耗时写到标准错误，不要用 `2>&1` 混进 CSV。`--games` 默认 5000，`--seed` 默认 1，`--level` 支持 `easy/normal/hard/expert`。首次先跑小批次，扩大规模时保持代码、规则、包版本、AI 难度和种子可追溯。

CSV 有两个区块：主播统计和技能使用次数。主播的 `games` 是被选入阵容的次数，`wins/winrate` 是所在队伍获胜的次数与比例；`avg_turns` 是这些对局的平均回合，`avg_damage_dealt/avg_kos` 是其平均伤害和击倒数。它们不能直接解释为个人胜率或因果贡献。技能区块列 `skill_id/skill_name/uses`；使用次数包含未命中、免扣 PP 的执行和硬撑，不含受控跳过或行动前 PP 耗尽失败，未使用技能输出 0。

特别是本示例只有六人，每局两队必然都包含全部六位主播，因此六人的队伍胜率无法区分强弱。它主要用于确认内容能够加载和结算；扩展到更多主播、换种子、检查技能实际使用率与对战日志之后，再讨论平衡。也应关注是否频繁打到回合上限，而不是只看“对战结束”。

同一版本同参数重跑应得到相同 CSV；耗时不保证相同。改一个数值就保存一次独立结果，避免把不同版本混在一起。提交前运行：

```powershell
dotnet test Engine.Tests
```

若新想法无法用已有代码表达，应作为引擎任务提出，同时更新设计文档的代码表和 `Describe.cs`，不能只给 JSON 增加一个代码名。

## 10. 发布边界

本项目免费、非商业。原创示例不对应任何真人。制作同人包前阅读 [FANWORK.md](../FANWORK.md)，只使用公开艺名与公开人设；**不得放入官方素材**，包括官方立绘、Live2D/3D 模型、音频和标志，也不得放入没有使用权的图片、字体、音频或代码。

形象使用程序生成的图形或自己绘制的图；不写现实事件、中之人信息、负面传闻、擦边或贬损内容。遵守 [内容包许可](../UnityProject/Assets/StreamDuel/Resources/Packs/LICENSE.md)，不加入收费、内购、广告、打赏或数据收集。PackCheck 的文本提醒不能代替作者对内容及权利的人工检查。
