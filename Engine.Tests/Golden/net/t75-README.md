# T-75 协议版本 5 固定样例

T-87 合并后本组 JSON 保持历史字节，不再发送给当前 v6 作为成功握手。历史 hello 由 T-79 验证明确版本拒绝，seed 79 的四种状态仍由独立 Core 夹具复核；当前真实重连发收验收已迁移到 [T-87 v6 样例](t87-v6-README.md)。下文保留 v5 建立时的输入、规范化和序号说明。

本组仅新增 `t75-*.json`，不替换现有 v3/v4 样例。验收入口是 `T75_ProtocolGoldenReviewTests`，冻结内容由 `T75ReviewProtocol.FrozenContent()` 构造，数值与 T-79 的原创测试内容一致：内容指纹 `06cab9cf6b123697`，种子 79，双方第一回合使用 S0 后状态哈希 `e72531a46e767db7`。

仅规范化密码学随机数据：sessionId 固定为 `f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff`，公开 nonce 固定为 `000102030405060708090a0b0c0d0e0f`，commit 摘要对应 `SHA-256(S0|nonce)`。测试先验证真实随机串格式与真实承诺绑定，再规范化；字段集合、角色、序号、能力、内容指纹、回合和状态哈希都必须与固定文件匹配。packet.payload 是承载原协议 JSON 的字符串，比较时也逐字段检查内层对象。

普通 v5 hello 增加 sessionId；主机为 32 位小写十六进制，客机为 null。计时仍使用 timerVersion 1 及主机 timer。显式启用终止协议时带 terminationVersion 1，未启用则省略；同为 v5 但这项能力不一致的两端必须在建立 Battle 前互相拒绝。

packet-commit/reveal/hash 是客机同一回合的连续序号 1/2/3。resume 来自主机，已收到 3 条客机 packet，最后结算检查点为 eventIndex 1、Turn 2；resumed 来自客机，其已重发边界 sent 为 3。goodbye 使用独立终局样例，三条行动/哈希 packet 后序号为 4。termination-freeze 是另一独立启用终止协议的会话首条主机 packet，不与普通示例串联。

测试同时把冻结的 guest hello 和 commit/reveal/hash packet 输入真实 NetSession，校验仅产生一次 S0/S0 回合及同一个冻结状态哈希。没有环境变量或自动覆盖样例的生成入口。
