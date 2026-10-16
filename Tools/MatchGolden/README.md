# T-54 多局黄金录像

生成器用同一个真实 `Core.Match` 完成 ranked 三局两胜，逐局调用 `StartNextGame()`，合法禁用、选择首发并出招，最后把当前 Battle 原实例交回 `ReportResult()`。预定胜方为 0、1、0，比分为 1:0、1:1、2:1；生成器不改写 HP、PP、Phase、Winner 或随机状态，也不使用三个独立 Battle 冒充多局赛制。每局仍保存普通版本 2 单局录像，不新增 Match 存档或协议。

内容直接引用 `Engine.Tests/Golden/replays/content/StreamDuelRules.json` 与 `original.json`；生成前核对固定 SHA-256，不复制、不修改旧快照或 T-93 的黄金录像。manifest 记录内容来源提交、T-54 初始功能提交、整合主线基点、每局种子、初末态哈希、录像字节 SHA-256、事件数、比分及结束状态。基点是来源说明，不要求未来生成时检出旧提交。

复测时指定一个**尚不存在**的目录，随后比较全部四个 JSON 文件的 SHA-256：

```powershell
dotnet run --project Tools/MatchGolden -- --output Build/review2/T54/match-golden-repeat
```

输出目录已存在时生成器拒绝运行，不能自动覆盖验收基准。测试直接读取冻结文件，在当前 Match 的 Battle 上逐条执行同样输入并上报结果，同时独立 `Replay.Run` 核对末态；每局开始检查六人原名单、全新人气和 PP、空状态及独立对象，禁止跨局继承状态。旧 T-93 `series-game-*` 的“独立单局”说明和断言原样保留。

冻结 manifest SHA-256：`64513330ce1e9aad23b634a4434c6099002697f4ff0ff5e97247345b23b30430`。三局均按版本 2 现行规则在第 120 回合正常结束，包含禁用、非零首发、实际伤害、PP 消耗及补位；不是投降或人工改写终局状态。种子依次为 540001、540002、540003，末态哈希依次为 `dad44f55fb5f56cd`、`b7ca542f520556a1`、`20e7eea357404ba8`。计时配置随赛制冻结，但本工具不等待真实时间；它验证 Core 多局流程，不声称验收调用层倒计时。
