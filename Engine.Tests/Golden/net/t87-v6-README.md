# T-87 公平种子协议 v6 固定样例

本组新增 `t87-v6-*.json`，原 v3、v4 控制与 v5 JSON 全部保留。固定内容仍为 T-79/T-75 的原创测试内容，指纹 `06cab9cf6b123697`。`T79_NetProtocolGoldenTests` 验证普通线路与旧版本拒绝；`T75_ProtocolGoldenReviewTests` 验证可选重连、终止组合和真实恢复。旧 seed 79 的四种结算哈希另由 T-79 独立 Core 夹具逐项验证，未重录旧预期。

## 固定贡献与摘要

- 主机原值：字节 0～31，hex `000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f`。
- 客机原值：字节 32～63，hex `202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f`。
- 主机承诺：`SHA-256(UTF-8("host:") || 主机原值)` = `29e2b9e1d07c622d5adea64add1072a077d769b9b872f267b3d7479a7b91bdc3`。
- 客机承诺：`SHA-256(UTF-8("guest:") || 客机原值)` = `d45208b2e976df8d670d2dd1e9a73facb3039b8a414b2fd28caf4916f4636fcb`。
- 最终种子：`SHA-256(主机原值 || 客机原值)` 前四字节小端，`0xacb9eafd`（十进制 `2897865469`）。最终摘要不加角色前缀。

上述三个摘要独立按二进制拼接计算，再与真实 NetSession 输出比较。构造器仍传旧参数 79，但公平种子必须等于上述新值，证明旧参数不能覆盖双方贡献。hello 只锁定承诺；测试先断言 Handshake 和 Battle 为 null，再送 seed_reveal，检查最终 Battle.Seed/Record.Seed。

## 状态与消息

| 当前 v6 样例 | 操作 | 状态哈希 |
|---|---|---|
| hash-turn | 双方第 1 回合 S0 | `42cc2dfa4dfd0972` |
| hash-replace-first | 双方场上设为已下播，Pass 进入补位，先补 0 方到下标 1 | `7034bac0fd8dd3bf` |
| hash-replace-second | 同一补位阶段再补 1 方到下标 1 | `f8ebcf66e05ba25f` |
| hash-final | 独立新局到 TurnCap，双方 Pass，正常平局终止 | `56086a91b9ed2557` |

这四值来自当前真实 NetSession 驱动的 T-79 捕获，捕获只输出独立验收目录，不提供自动改写黄金文件的测试开关。两端对战及消息仍由原强断言校验；双补位保留两个相同回合号但不同快照，终局等待对方哈希。

普通 commit/reveal/chat/error 格式未变，继续复用原固定文件。行动 nonce 仍仅在验证真实 32 位小写 hex 及其 SHA-256 绑定后规范化为 `000102030405060708090a0b0c0d0e0f`；公平握手原值已固定，因此 seedCommit/seed_reveal 无需规范化。发送字段全集、中文转义、角色、回合和状态哈希必须一致，接收端仍覆盖每个必需字段缺失与未知字段规则。

`t87-v6-reconnect-*` 保留 T-75 packet、resume、resumed、goodbye 和终止冻结窗口的全部结构与序号。hello 额外声明 `reconnectVersion:1`；仅组合终止能力时带 `terminationVersion:1`，能力存在性和值都须双方一致。sessionId 只在验证真实格式后规范化为 `f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff`。hello/seed_reveal 不进入 journal，首条 packet 仍从 1 开始；S0 后 packet-hash 与 resume 使用新公平种子的 hash-turn，eventIndex 1、Turn 2、received/sent 3 均保持原语义。

测试同时将冻结的 v6 guest hello、seed_reveal、commit/reveal/hash packet 输入真实主机会话，检查只产生一次 S0/S0；另一条路径真实断开再 Attach，检查恢复成功。双向不同终止能力、双向不同重连能力都会在建立 Battle 前拒绝。历史 v3/v5 hello 不改字段来伪装 v6，而是原样送入版本拒绝用例；旧 v4 同样由双向版本门禁覆盖。
