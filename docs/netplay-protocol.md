# 联网对战协议 v2

本文记录 `NetSession.ProtocolVersion = 2` 的现有线路格式与接收行为，供实现对端、审查兼容性和维护固定样例使用。规则以 [design.md 第 10 节](design.md#10-联网对战进行中) 为准；本任务不修改协议。源码入口是 [NetSession](../UnityProject/Assets/StreamDuel/Core/Net/NetSession.cs)、[CommitReveal](../UnityProject/Assets/StreamDuel/Core/Net/NetSession.CommitReveal.cs)、[NetJson](../UnityProject/Assets/StreamDuel/Core/Net/NetJson.cs)、[TcpTransport](../UnityProject/Assets/StreamDuel/Core/Net/TcpTransport.cs) 和 [CLI 收尾](../Tools/Cli/Program.Network.cs)。

v2 适用于可信本机或局域网的单局双人对战，无加密、身份认证、账号、重连或版本协商。主机固定为 0 方，客机为 1 方；队伍、显示名及结算参数始终按此顺序排列。消息不携带 `side`，发送方身份由连接两端确定。禁用和首发选择不在本协议内。

## 1. 传输与通用解析

`INetTransport` 每次传递一条完整字符串，要求每个方向先进先出、不丢失、不重复。调用者在同一线程依次调用 `Poll`、`Submit`、`SubmitReplace`；`Poll` 非阻塞地处理当前已到达的消息。网络消息不能直接替代会话去修改 `Battle`。

TCP 将每条字符串编码为严格 UTF-8，不写 BOM，并追加一个 LF 字节。接收端以 LF 分帧，接受 CRLF（移除 LF 前的一个 CR）。JSON 字符串中的换行必须转义为 `\n`，不能把一条对象拆成多行。发送 API 拒绝消息内的原始 CR/LF；空行不会被跳过，会交给 JSON 解析并报错。完整行先排队交付，随后才报告断开；EOF 前残留半行、非法 UTF-8、连接异常均是错误。当前实现没有行长或接收队列大小上限。

所有消息的顶层必须是 JSON 对象，均有必需的字符串字段 `type`。仅识别大小写精确匹配的 `hello`、`commit`、`reveal`、`hash`、`error`。下文每张字段表中的字段均必需，没有隐式默认值；“必需且为 null”也必须保留该键。

实际解析规则如下，发送方应使用正常、无重复键的 JSON：

- 字段名和枚举字符串区分大小写；字段顺序、JSON 空白和等价字符串转义不影响含义。未认识的字段被忽略，不作为新操作执行，但其值仍须能被 JSON 解析器完整解析。
- [MiniJson](../UnityProject/Assets/StreamDuel/Core/MiniJson.cs) 拒绝尾随内容、尾逗号、数字前导零、非法转义、字符串中未转义的控制符和非有限数值，容器最多嵌套 64 层。重复对象键当前采用最后一次出现的值，不报重复键错误。
- JSON 数字先解析为 `double`，再要求有限、数值等于其截断值、可转换为 `long` 且处于字段范围内。因此 `2`、`2.0`、`2e0` 都可作为版本 2；字符串 `"2"`、布尔、null 不可作为整数。判定针对转换后的数值，不保证保留原始十进制精度。
- 字符串类型检查只拒绝缺键或非字符串，不统一拒绝空串或限制长度。JSON 的 `\uXXXX` 按 UTF-16 码元解码，当前解析器不单独验证转义后的代理项是否配对。发送器会转义引号、反斜杠、控制符、代理项及 U+2028/U+2029。
- 缺少必需字段或类型不对会在 `Poll` 中转为 `Error`，通常带“网络消息缺少字段「…」”或“字段「…」必须是…”等中文原因。状态、回合等检查按源码顺序执行，错误消息不保证总是先报告缺字段。

## 2. `hello`：建立同一局

构造合法的 `NetSession` 后，两端各发送一次 `hello`。在收到并验证对方的 `hello` 前，`Battle` 为 null，状态为 `Handshake`。本方队伍或名字非法时直接进入 `Error` 并尝试发送 `error`，不发送 `hello`。

| 字段 | JSON 类型与约束 | 作用 |
|---|---|---|
| `type` | string，`"hello"` | 消息种类 |
| `version` | number，整数 0..2147483647；必须等于 2 | 严格相等，无降级协商 |
| `packId` | string，与本地 `db.PackId` 完全相等 | 选择同一内容包 |
| `contentHash` | string，与本地发出握手时的内容指纹完全相等 | 当前发送格式为 16 位小写十六进制 |
| `seed` | 主机：number，整数 0..4294967295；客机：null | 只采用主机种子 |
| `name` | string，可为空串 | 对方显示名，不是认证身份 |
| `team` | array，恰好 6 个 string | 按顺序列出本方主播 id |

主机构造参数未给种子时，只在构造时取一次 `unchecked((uint)Environment.TickCount)`；客机构造参数即使给了种子，也发送 null 并采用收到的主机种子。主机接收客机握手时专门检查 `seed` 键存在且值为 null，缺键也报“客机握手的 seed 必须为 null”。两端都自称主机或都自称客机不能完成握手。

`team` 按 `TeamCheck.Validate(db, null, team)` 校验：必须六人、id 存在、无重复；此处不选择赛季，也不附加赛季名单限制。内容指纹发送前保存一次，收到握手时再次计算本地指纹；本地握手途中改动内容，或者对方指纹不等，均在创建 `Battle` 前报“内容不一致”。接收方没有另外检查 `contentHash` 的十六进制语法，而是直接进行 ordinal 字符串比较。开局后调用者仍有责任保持内容库不变。

版本不等、包不同、指纹不同、队伍非法、缺字段、错误种子角色或第二次 `hello` 都进入 `Error` 并尝试通知对方。成功时建立相同的队伍、名字和种子，保存共享 RNG 检查点，进入 `Choose`；不会额外交换开局 `hash`。

## 3. `commit` / `reveal`：锁定再公开选择

`kind = "action"` 用于普通行动阶段，双方都参与；`kind = "replace"` 用于补位阶段，仅需要补位的方参与。两类选择共用同一承诺协议，没有独立的 `action` 或 `replace` 消息类型。

| `commit` 字段 | JSON 类型与约束 |
|---|---|
| `type` | string，`"commit"` |
| `turn` | number，整数 1..2147483647；等于当前阶段保存的回合 |
| `kind` | string，`"action"` 或 `"replace"`，必须与当前阶段一致 |
| `digest` | string，恰好 64 位小写十六进制 |

| `reveal` 字段 | JSON 类型与约束 |
|---|---|
| `type` | string，`"reveal"` |
| `turn` | number，整数 1..2147483647；等于当前阶段保存的回合 |
| `kind` | string，与本阶段及承诺对应的类型一致 |
| `action` | string，严格行动编码；补位只能为有效的 `W0`～`W5` |
| `nonce` | string，恰好 32 位小写十六进制 |

行动编码仅允许 `S0`～`S4`（技能槽）、`W0`～`W5`（己方队伍位置）和 `P`（跳过），均从 0 编号。不允许空白、小写、前导零、额外字符或其他编码，例如 `S00`、`s1`、`W6` 不合法。`Struggle` 和 `PassControlled` 是结算派生动作；本地提交时若确实符合相应条件，会先映射为 `P`，结算仍由 Core 决定。

每次有效本地提交生成 16 字节密码学随机数，编码为 32 位小写十六进制 nonce。摘要计算为：

```text
digest = lowercase_hex(SHA256(UTF8(action + "|" + nonce)))
```

输入没有引号、空格或结尾换行。例如 `action = "S2"` 时，摘要输入是 `S2|` 紧接 32 个 nonce 字符。nonce 不使用 `Battle.Rng`，也不进入日志、回放或状态哈希；它仅隐藏尚未公开的选择。

本地提交发送 `commit`，设置 `HasSubmitted = true` 并进入 `Waiting`。所有本阶段参与方的承诺到齐后，本端自动发送自己的 `reveal`；不等待对方先公开。只有一方需要补位时，该方依次发送自己的 `commit` 和 `reveal`，另一方不发送承诺。收到公开消息时，先检查阶段、回合、承诺已齐及未重复公开，再验证 nonce 与摘要，最后解码行动。任一检查失败均进入 `Error`，未经验证的选择不参与结算。

普通行动阶段仅验证线路编码，当前不能使用的技能或换人不被协议层统一拒绝，最终交给 `Battle.Normalize` 变成合法换人、受控跳过、硬撑、技能或 `Pass`。补位则严格要求该方需要补位，目标在队伍内、未下播且不是当前场上位置；`P` 或 `S0` 即使摘要匹配也不能用作补位。

本地重复提交、阶段不对、不可编码动作或非法补位抛出 `InvalidOperationException`，不通过消息让自己进入 `Error`。远端重复 `commit` / `reveal`、过期或未来回合、承诺未齐便公开、无需补位的一方提交、摘要不匹配等属于协议错误，进入 `Error`。哈希确认开放下一阶段时清空本阶段承诺与 `HasSubmitted`，下一阶段重新提交。

## 4. `hash`：逐次结算，先进先出确认

| 字段 | JSON 类型与约束 |
|---|---|
| `type` | string，`"hash"` |
| `turn` | number，整数 1..2147483647；等于待确认操作调用前的 `Battle.Turn` |
| `hash` | string；发送值为 `Battle.StateHash().ToString("x16", InvariantCulture)` |

双方选择收齐后，各端按固定的主机、客机参数顺序调用 `ResolveTurn`。补位收齐后按 0 方、1 方顺序分别调用 `Replace`，与网络到达次序无关。每次调用后立即保存状态哈希并发送一条 `hash`；本端保存快照队列，收到对方哈希后只与队首比较，不按回合号覆盖或查找。

`turn` 标记操作调用前的回合：第 N 回合 `ResolveTurn` 的哈希标 N，随后补位通常标 N+1。同一补位阶段的两次 `Replace` 可以产生相同 `turn` 的两条不同哈希；后续第 N+1 回合的 `ResolveTurn` 也可以再次使用该回合号。接收方必须保持操作顺序。

当前没有待确认快照时收到 `hash`（包括重复确认），或 `turn` 与队首不同，进入 `Error`。`hash` 缺失或不是字符串也进入 `Error`；字符串值与队首不完全相等则进入 `Desync`，记录本端/对端值和回合，并发送 `error`。**当前接收端不单独校验哈希字符串长度或十六进制格式**：空串、错误长度、大写等只要与本端字符串不同，都走 `Desync`，不是格式 `Error`。

全部待确认哈希通过后才开放下一阶段。等待期间提前收到新选择属于 `Error`。最终一次操作也遵循同一流程；`Battle.Phase == Over` 时，会话可能仍在 `Waiting`，只有最终哈希确认后才成为 `NetState.Over`。

正式结算前及哈希确认开放下一阶段时，会话恢复共享 RNG 检查点。UI/AI 在本端选招或等待时消费的随机数只影响提交选择，不泄漏进两端共享结算；传输比较的是结算瞬间保存的哈希。状态哈希算法、包含字段和内容指纹算法见 [设计文档第 10 节](design.md#10-联网对战进行中)，均用于一致性检查，不提供身份认证。

## 5. 一局中的顺序示例

箭头表示单向发送；两方向可交错，但每个方向必须保持 FIFO。

```text
主机 -> 客机  hello(version=2, seed=42)
客机 -> 主机  hello(version=2, seed=null)
两端验证后进入 Choose

主机 -> 客机  commit(turn=1, kind="action", digest=...)
客机 -> 主机  commit(turn=1, kind="action", digest=...)
主机 -> 客机  reveal(turn=1, kind="action", action="S2", nonce=...)
客机 -> 主机  reveal(turn=1, kind="action", action="P", nonce=...)
两端各自 ResolveTurn(主机行动, 客机行动)
主机 -> 客机  hash(turn=1, hash=H1)
客机 -> 主机  hash(turn=1, hash=H1)
两端确认后，按 Battle.Phase 进入 Choose / Replace / Waiting / Over
```

若此时两方都要补位，下一段是：

```text
两方向各发送 commit(turn=2, kind="replace", digest=...)
承诺齐备后，各发送 reveal(turn=2, kind="replace", action="W...", nonce=...)
两端各自 Replace(0, 主机目标)，立即保存并发送 hash(turn=2, hash=H2a)
两端各自 Replace(1, 客机目标)，立即保存并发送 hash(turn=2, hash=H2b)
按 H2a -> H2b 的顺序确认两条哈希；全部通过后才开放后续选择
```

单方补位时只有需要补位的一方发送 `commit` / `reveal`，两端都会执行该次 `Replace`，也都会发送对应的一条 `hash`。这里的 `...` 和 `H1/H2a/H2b` 是说明占位符，不是可以直接发送的 JSON；可发送的完整固定消息见第 8 节。

## 6. 结束与 TCP 收尾

v2 **没有 `end`、`goodbye` 或独立胜负消息**。胜负由双方相同的结算得到，最后发送的正常 JSON 消息仍为 `hash`。`NetState.Over` 只代表本端已确认全部结算哈希，不代表对方已完成全部检查或已保存结果。

进入 `Over` 后仍要 `Poll`：收到合法 `error` 会转为 `Error`，收到其他协议消息也会报错。只有 `TcpDisconnectedException` 表示的完整消息之后的正常 EOF 在 `Over` 被忽略；RST/其他 I/O 异常、非法 UTF-8、EOF 前半条 JSON 仍会导致 `Error`。未到 `Over` 的正常 EOF 也会导致 `Error`，原因含“对方已断开”。

CLI 的 `FinishNetwork` 在 `Over` 后调用 `CompleteSending()`，仅关闭 TCP 发送方向，再持续 `Poll` 接收对方关闭前的消息。观察到 `PeerClosed` 后再 `Poll` 一次排空消息，检查仍无 `Error` / `Desync` 才打印最终哈希并保存战报、回放和档案。等待对方正常 EOF 超过 10 秒则失败且不保存。收到正常 EOF 的传输端仍保留发送方向，尚未收尾的一端可以报告最终哈希错误；完整 `error` 先于 EOF 被另一端处理。

这不是持久化的双方保存确认，也没有恢复丢失连接的协议。CLI 的手动 q / 输入 EOF 在未完成对战时直接退出并关闭连接，不额外发送退出 JSON，对端按断线处理。连接超时和握手超时在 CLI 各为 10 秒；`NetSession` 本身不负责计时，也不限制等待对方选择的时间。

## 7. `error` 与失败路径

| 字段 | JSON 类型与约束 |
|---|---|
| `type` | string，`"error"` |
| `message` | string，错误原因；当前接收允许空串 |

示例：

```json
{"type":"error","message":"对手公开的行动与承诺不符。"}
```

合法 `error` 在握手、选择、等待、补位和 `Over` 均可接收。本端进入 `Error`，原因前缀为“对方报错：”，**不回复 `error`**。已经处于 `Error` / `Desync` 的会话停止 `Poll` 处理，不再重复报错。坏的 `error`（例如缺少 `message`）仍是解析错误，会尝试发送一次自己的合法 `error`。

| 事件 | 本端状态及通知 |
|---|---|
| JSON、字段、版本、内容、回合、阶段、重复消息、承诺错误 | `Error`；尽力发送一次 `error` |
| 对应状态哈希字符串不等 | `Desync`；尽力发送一次 `error`，另一端收到后为 `Error` |
| 收到合法 `error` | `Error`；不回复，避免相互报错循环 |
| 发送失败 | `Error`；不再尝试通过失败的发送路径报错 |
| 未完成时的正常 TCP EOF | `Error`；不发送通知 |
| 一般接收/处理异常 | `Error`；尽力发送一次 `error`，通知失败被吞掉，不递归报错 |
| 本地误用提交 API | 抛 `InvalidOperationException`，通常保留会话状态；不发送协议错误 |

未知消息类型不被忽略。实际校验顺序为：先读 JSON 与 `type`，优先处理 `error`，再检查 `Over`、`hello`、握手是否完成、`hash`、待确认哈希和 `turn`，最后分派 `commit` / `reveal`。因此未知类型也可能先报告“握手完成前不能发送…”或缺少 `turn`，不保证错误文本总是“未知协议消息类型”。错误原因为诊断文本，对端不能依赖某一完整句子的字节值来驱动状态机。

## 8. 固定样例与兼容性维护

[Engine.Tests/Golden/net/](../Engine.Tests/Golden/net/) 存放由当前发送实现捕获后固定的 JSON；[T79_NetProtocolGoldenTests.cs](../Engine.Tests/T79_NetProtocolGoldenTests.cs) 核对发送字段、当前接收路径、未知字段与必需字段。测试使用冻结的六位原创单位 `ContentDb` 和种子 79，不依赖正式包的平衡数值；分别准备普通首回合、双补位和终局场景。

| 固定 JSON | 所处阶段与内容 |
|---|---|
| [hello-host.json](../Engine.Tests/Golden/net/hello-host.json) | 主机握手，种子 79 |
| [hello-guest.json](../Engine.Tests/Golden/net/hello-guest.json) | 客机握手，种子 null |
| [commit-action.json](../Engine.Tests/Golden/net/commit-action.json) / [reveal-action.json](../Engine.Tests/Golden/net/reveal-action.json) | 普通行动 `S0` 的配套承诺与公开 |
| [hash-turn.json](../Engine.Tests/Golden/net/hash-turn.json) | 首回合结算结果 |
| [commit-replace.json](../Engine.Tests/Golden/net/commit-replace.json) / [reveal-replace.json](../Engine.Tests/Golden/net/reveal-replace.json) | 补位 `W1` 的配套承诺与公开 |
| [hash-replace-first.json](../Engine.Tests/Golden/net/hash-replace-first.json) | 双补位先执行 0 方 Replace 后的快照 |
| [hash-replace-second.json](../Engine.Tests/Golden/net/hash-replace-second.json) | 再执行 1 方 Replace 后的快照，与上一条 turn 相同但顺序不能交换 |
| [hash-final.json](../Engine.Tests/Golden/net/hash-final.json) | 终局最后一条 hash，没有另加 end/goodbye |
| [error.json](../Engine.Tests/Golden/net/error.json) | 当前实现生成的中文错误消息 |

密码学 nonce 每次提交都变化，因此不能要求独立两局产生相同的原始网络文本。测试先独立验证真实 nonce 的格式和真实承诺的 SHA-256 绑定关系，再仅将 nonce 规范化为 `000102030405060708090a0b0c0d0e0f`，并按原行动重算对应 digest，进行固定样例比对。其他字段逐字段严格比较，包括意外新增的字段；样例读取测试不会自动写入或更新文件。这是测试用的熵规范化，不是运行时固定 nonce 的协议。

固定承诺与公开样例必须配套，保持摘要计算关系；测试不得把不一致的摘要当作有效样例。对战的确定性仍由相同内容、种子和行动保证。固定样例也需要在对应会话阶段送入，单独收到一条正确的 `reveal` 或 `hash` 不意味着当时允许接收。

当前版本只接受 `version == 2`。以下变更必须提升协议版本并同步文档、固定样例和兼容性测试：

- 增删或更改必需字段、字段含义/类型/取值域，改变行动编码或承诺摘要输入、算法。
- 新增旧端必须处理的消息，改变承诺/公开/哈希顺序、参与方、同回合哈希配对规则或终局确认流程。
- 改变状态哈希算法或结算语义，使同一合法消息序列在旧端产生不同结果；内容指纹算法变化也需要明确版本边界。
- 收紧当前接收行为，使旧端原来能发送并被接收的消息遭拒；不能仅因新规则“更合理”就静默改变 v2。

只添加可被旧端忽略、且不改变选择或状态推进的可选元数据字段，可以不升版本；字段顺序、紧凑程度或等价转义不构成协议升级。新增不能忽略的行为不能仅靠未知字段绕过版本检查。单纯更新内容包数据通常沿用 v2，由握手 `contentHash` 拒绝内容不同的对局；字段或哈希算法本身的变更另行决定。

## 9. 已知现状与后续决定

以下行为在本任务中保留；未来若调整，必须按上一节确定兼容边界：

1. 承诺摘要只绑定 `action + "|" + nonce`，不绑定回合、kind、发送方或会话身份。外层会校验回合和 kind，但无法阻止一方复制对方 digest，随后复制其相同行动与 nonce。需要另行决定是否给摘要加入上下文。
2. JSON 重复键取最后值，没有重复键错误。需要另行决定后续版本是否拒绝重复键；本任务不通过改解析器改变现有行为。
3. `hash` 没有独立的十六进制格式验证；字符串与本地值不等便进入 `Desync`。需要决定未来是否将非法格式归类为 `Error`，区分它与真正的状态分歧。
