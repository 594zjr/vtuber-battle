# 原创主播立绘制作说明与 AI 生图提示词

适用于三期原创内容包（`original`、`original_wave2`、`original_wave3`）共 36 位主播。同人包（A-SOUL、VirtuaReal、hololive、彩虹社）的立绘不在本文范围内。

## 1. 游戏现在怎么用立绘

- 每位主播**只读一张图**：`Resources.Load` 加载 `Portraits/<包id>/<主播id>`（见 `docs/design.md` 美术清单一节）。没有图时自动用代码生成的色块头像，所以可以一位一位慢慢补。
- 同一张图会按原比例缩小放进不同位置（不裁切）：对战舞台约 200 px 高，组队卡片约 108×106，详情约 100×128，回看约 140×164，开局选人只有 48×48。所以**脸要大、要清楚**，缩到 48 px 还能认出是谁。
- 目前没有单独的「小头像」文件；以后想要专门的小头像需要改代码，先不做。

## 2. 图片规格

| 项目 | 要求 |
|---|---|
| 尺寸 | 1024×1280（4:5 竖图）；最小 768×960 |
| 构图 | 半身像，头顶到腰部；头顶留约 5% 空白，脸部中心在画面上方三分之一处；人物左右居中 |
| 格式 | PNG，**透明背景**（不要白底、不要场景） |
| 大小 | 单张不超过 2 MB |
| 文件名 | `<主播id>.png`，全小写，和下表「id」一列完全一致 |
| 放置位置 | `UnityProject/Assets/StreamDuel/Resources/Portraits/<包id>/<主播id>.png`，例如 `Resources/Portraits/original/enka.png` |
| Unity 导入设置 | Texture Type 选 Default，勾选 Alpha Is Transparency，Max Size 2048；其余保持默认 |

## 3. 统一画风

所有 36 位共用同一句画风锚定（已写进每条提示词开头）：**清爽赛璐璐上色的原创二次元半身立绘，干净线稿、两层柔和阴影、明亮饱和配色、透明背景**。同一个工具、同一个模型、同一组参数一口气生成，画风才统一；挑出满意的一张后，可以用它作为风格参考图（图生图 / 风格参考）继续生成其余角色。

## 4. 禁止事项

- 图里不能有任何文字、字母、数字、水印、签名或标志。
- 不得参考、模仿任何真实主播（包括虚拟主播）或已有动漫、游戏角色的形象；不要在提示词里加任何真人或已有角色的名字。
- 不使用来源不明的图片做参考图；使用 AI 生成时，确认所用工具的条款允许把生成结果用在免费公开的同人项目中。
- 仓库目前不提交任何头像图片（见 `docs/design.md` 美术清单一节）；图片先放在本机工程里试，要不要提交另行决定。

## 5. 通用负面提示词

工具支持负面提示词时，每张都加上：

```
文字, 字母, 水印, 签名, 标志, 多人, 背景场景, 照片质感, 3D 渲染, 真实主播或已有动漫角色的形象, 多余手指 / text, letters, watermark, signature, logo, multiple people, background scenery, photorealistic, 3d render, likeness of any real streamer or existing character, extra fingers
```

## 6. 每位主播的提示词

每条都已包含统一画风；「主色」取自该主播主赛道的颜色，用作服装或头发的主调。

### 原创主播（包 id：`original`）

#### 绯月炎华（id：`enka`）

赛道：游戏　主色：`#ff8a3d`　简介：FPS 主播，反应快，残血越打越狠。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，红色高马尾带一缕橙色挑染，戴电竞耳机，黑红战术夹克，肩头小火焰徽章，手握发光游戏手柄，眼神锐利自信。主色调 #ff8a3d。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, crimson high ponytail with one orange streak, gaming headset, black and red tactical jacket, small flame badge on the shoulder, holding a glowing game controller, sharp confident eyes. main color #ff8a3d.
```

#### 深海汐汐（id：`shiori`）

赛道：助眠　主色：`#5cc6ff`　简介：助眠主播，慢热耐打，后手反而更痛。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，及腰蓝绿色波浪长发，发梢像浪花，水母形发饰，宽松浅蓝针织开衫，怀抱贝壳形麦克风，半闭眼温柔微笑。主色调 #5cc6ff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, waist-length teal wavy hair with wave-like tips, jellyfish hair ornament, loose pale blue knit cardigan, hugging a seashell-shaped microphone, half-closed eyes, gentle smile. main color #5cc6ff.
```

#### 森野鹿鸣（id：`lumi`）

赛道：杂谈　主色：`#5fd08a`　简介：杂谈主播，消耗型辅助，队友越多越稳。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，栗棕色短卷发，头戴小鹿角发箍，森林绿背带裙配奶白衬衫，斜挎藤编小包，张开一只手热情打招呼。主色调 #5fd08a。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, short curly chestnut hair, small deer antler headband, forest green pinafore dress over a cream blouse, woven rattan crossbody bag, waving warmly with one hand. main color #5fd08a.
```

#### 雷音铃铛（id：`bell`）

赛道：歌回　主色：`#ff7fb6`　简介：歌回主播，全场最快，带消回合。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，金黄色双马尾系着小铃铛，闪电形发卡，黄白相间偶像演出服配短披肩，握着挂铃铛的立式麦克风，单脚轻跳。主色调 #ff7fb6。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, golden twin tails tied with small bells, lightning bolt hair clip, yellow and white idol stage outfit with a short capelet, holding a stand microphone decorated with bells, hopping on one foot. main color #ff7fb6.
```

#### 白羽绘绘（id：`hane`）

赛道：绘画　主色：`#ffd166`　简介：绘画主播，自我净化，专打强化过的对手。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，银白色短发，背后一对小白羽翼，白色画家罩衫沾着金色颜料点，手持大号画笔，笔尖发出金光。主色调 #ffd166。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, short silver-white hair, a pair of small white feathered wings, white artist smock with gold paint dots, holding an oversized paintbrush with a glowing gold tip. main color #ffd166.
```

#### 夜猫墨墨（id：`momo`）

赛道：恐怖　主色：`#9b7dff`　简介：恐怖游戏主播，对手残血时伤害提高。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，墨黑色及肩发，戴猫耳兜帽，紫色眼瞳，黑紫色连帽卫衣，手提一盏小南瓜灯，俏皮坏笑。主色调 #9b7dff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, shoulder-length ink black hair, cat-ear hood, violet eyes, black and purple hoodie, holding a small pumpkin lantern, mischievous grin. main color #9b7dff.
```

#### 铁壁老K（id：`kei`）

赛道：硬核　主色：`#9db4c9`　简介：硬核测评主播，互动类伤害打不动他。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。三十岁左右男性，灰色寸头和短胡茬，黑色方框眼镜，灰蓝工装外套挂着工牌，手里拿着拆开的显卡，沉稳可靠。主色调 #9db4c9。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. man around thirty, gray buzz cut and light stubble, black square glasses, gray-blue work jacket with a lanyard badge, holding a disassembled graphics card, calm and reliable. main color #9db4c9.
```

#### 星砂喵喵（id：`nyako`）

赛道：整活　主色：`#c7a6ff`　简介：整活主播，攒热度放名场面；6 号位队友在就不耗 PP。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，薰衣草紫蓬松短发别着星星发夹，猫耳，宽大的荧光紫卫衣贴满小贴纸，高举星形应援棒，夸张大笑。主色调 #c7a6ff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, fluffy short lavender hair with star clips, cat ears, oversized neon purple hoodie covered in small stickers, raising a star-shaped glow stick, big laugh. main color #c7a6ff.
```

#### 熔岩巴克（id：`buck`）

赛道：游戏　主色：`#ff8a3d`　简介：暴躁老哥，谁跟他互动谁容易过热。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻男性，橙红色刺猬头，额头绑运动头带，橙黑相间熔岩裂纹球衣，耳机挂在脖子上，咬牙握拳。主色调 #ff8a3d。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young man, spiky orange-red hair, sports headband, orange and black jersey with lava crack pattern, headphones around the neck, clenched fist, gritted teeth. main color #ff8a3d.
```

#### 雾隐霞（id：`kasumi`）

赛道：助眠　主色：`#5cc6ff`　简介：开播自带滤镜，憋大招一击。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，淡天蓝色长直发，薄纱面纱遮住下半张脸，水色改良和风长裙，手持半开折扇，身边飘着淡雾。主色调 #5cc6ff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, long straight pale sky blue hair, sheer veil over the lower face, aqua modernized kimono-style dress, holding a half-open folding fan, soft mist around her. main color #5cc6ff.
```

#### 怨灵阿渊（id：`yuan`）

赛道：恐怖　主色：`#9b7dff`　简介：最慢最肉，下播也要拉人下水。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。高个子年轻男性，苍白长发遮住一只眼，深紫色长袍上挂着空白符纸，身后飘着淡紫色鬼火，表情阴郁。主色调 #9b7dff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. tall young man, long pale hair covering one eye, deep purple long robe with blank paper talismans, faint violet will-o-wisps behind him, gloomy expression. main color #9b7dff.
```

#### 齿轮小蜂（id：`bee`）

赛道：硬核　主色：`#9db4c9`　简介：越先手越快，带消强化和减先制。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。少年，蜂蜜色短发带黑色挑染，护目镜推在头顶，黄黑蜂纹机械背带裤，背后一对小机械翅膀，手持扳手。主色调 #9db4c9。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. teenage boy, short honey blond hair with black streaks, goggles pushed up on the head, yellow and black bee-striped mechanic overalls, small mechanical wings on the back, holding a wrench. main color #9db4c9.
```

### 原创主播·第二期（包 id：`original_wave2`）

#### 栖灯短波（id：`og2_qideng`）

赛道：杂谈、助眠　主色：`#5fd08a`　简介：深夜电台的留灯员，把听众寄来的小故事读成晚安。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，墨绿色齐耳短发，戴老式收音机耳机，米色毛衣外套，手提一盏暖黄色小台灯，安静微笑。主色调 #5fd08a。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, dark green ear-length bob, vintage radio headphones, beige sweater coat, carrying a small warm yellow desk lamp, quiet smile. main color #5fd08a.
```

#### 秒针折线（id：`og2_miaozhen`）

赛道：游戏　主色：`#ff8a3d`　简介：给每条速通路线画折线，连失误也认真记进分段表。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻男性，深橙色翘起短发，单边运动护目镜，印着折线纹的运动夹克，手腕戴大号秒表，摆出冲刺起跑姿势。主色调 #ff8a3d。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young man, messy short dark orange hair, single-lens sport visor, track jacket with zigzag line pattern, large stopwatch on the wrist, sprint start pose. main color #ff8a3d.
```

#### 纸榫团子（id：`og2_zhisun`）

赛道：绘画　主色：`#ffd166`　简介：不用胶水搭纸屋的手工主播，最爱演示小小榫卯。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，奶黄色丸子头插着折纸小屋发饰，牛皮纸色围裙，双手捧着纸做的小房子模型，圆脸开心笑。主色调 #ffd166。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, cream yellow hair bun with a folded paper house ornament, kraft paper colored apron, holding a small paper house model in both hands, round face, happy smile. main color #ffd166.
```

#### 星尺豆灯（id：`og2_xingchi`）

赛道：硬核、绘画　主色：`#9db4c9`　简介：用自制星尺讲天文，把观测笔记画成一页页小灯图。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，深蓝渐变长发点缀星点，圆框眼镜，灰蓝色天文学者斗篷，手握带刻度的星尺，身边飘着几颗小灯泡。主色调 #9db4c9。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, long navy gradient hair with star sparkles, round glasses, gray-blue astronomer cloak, holding a ruler with star markings, a few tiny floating light bulbs around her. main color #9db4c9.
```

#### 汤勺咕轮（id：`og2_tangshao`）

赛道：整活、助眠　主色：`#c7a6ff`　简介：把面团捏成小车的料理主播，等出锅时直播咕嘟声。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻男性，浅紫色蓬松卷发，歪戴小厨师帽，白色厨师服配紫色领巾，一手拿汤勺一手托着面团捏的小车。主色调 #c7a6ff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young man, fluffy light purple curly hair, small tilted chef hat, white chef coat with a purple neckerchief, soup ladle in one hand, a tiny car made of dough in the other. main color #c7a6ff.
```

#### 弦扣斜拍（id：`og2_xiankou`）

赛道：歌回　主色：`#ff7fb6`　简介：爱写错拍小曲的乐队主唱，用衣扣标记排练节奏。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻男性，粉色狼尾发，乐队主唱皮夹克上缀满彩色纽扣，斜挎电吉他，帅气歪头。主色调 #ff7fb6。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young man, pink wolf-cut hair, band vocalist leather jacket covered in colorful buttons, electric guitar slung across the body, cool head tilt. main color #ff7fb6.
```

#### 雾罐夜游（id：`og2_wuguan`）

赛道：恐怖、游戏　主色：`#9b7dff`　简介：玩恐怖实况先画出口地图，把惊吓剪成雾罐小剧场。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，灰紫色侧编发，探险背心挂着地图筒，手提装着雾气的玻璃罐，罐里透出淡紫光。主色调 #9b7dff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, gray-purple side braid, explorer vest with a map tube, holding a glass jar full of swirling mist glowing faint violet. main color #9b7dff.
```

#### 焊点松饼（id：`og2_handian`）

赛道：硬核　主色：`#9db4c9`　简介：给旧键盘做测评的小工匠，把测试台布置得像早餐桌。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻男性，浅灰蓝卷发，头戴放大镜目镜，焦糖色皮围裙，手持电烙铁和迷你键盘，围裙口袋插着松饼形键帽。主色调 #9db4c9。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young man, light gray-blue curly hair, magnifier goggles, caramel leather apron, holding a soldering iron and a mini keyboard, waffle-shaped keycap in the apron pocket. main color #9db4c9.
```

#### 竹筒转音（id：`og2_zhutong`）

赛道：杂谈　主色：`#5fd08a`　简介：收集日常方言韵脚，用自制竹筒麦排练轻快绕口令。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，翠绿色麻花辫，竹叶发簪，浅绿立领短上衣，手持竹筒做的麦克风，正在说绕口令的活泼表情。主色调 #5fd08a。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, emerald green braids, bamboo leaf hairpin, light green mandarin-collar short top, holding a bamboo tube microphone, lively expression mid-tongue-twister. main color #5fd08a.
```

#### 格点绒笔（id：`og2_gedian`）

赛道：绘画　主色：`#ffd166`　简介：逐格绘制像素小镇，喜欢邀请观众决定下一扇窗的颜色。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，柠檬黄短波波头别着像素方格发卡，方格背带裤，手握毛绒笔头的画笔，身旁漂浮几个小像素方块。主色调 #ffd166。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, lemon yellow short bob with pixel grid hair clip, checkered overalls, holding a paintbrush with a fluffy tip, a few small floating pixel cubes nearby. main color #ffd166.
```

#### 枕叶砂钟（id：`og2_zhenye`）

赛道：助眠　主色：`#5cc6ff`　简介：用干叶和砂钟录制助眠声，每晚整理一小盒安静。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，雾蓝色长发里编着干叶片，宽松浅蓝睡袍，怀抱小沙漏，困倦柔和的眼神。主色调 #5cc6ff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, long misty blue hair braided with dried leaves, loose pale blue sleep robe, hugging a small hourglass, sleepy soft eyes. main color #5cc6ff.
```

#### 鼓芽节拍（id：`og2_guya`）

赛道：歌回、整活　主色：`#ff7fb6`　简介：把锅盖和种子罐当鼓的节拍主播，给日常物件编小合奏。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。少年，粉色短发头顶长出一片嫩芽，背带短裤，头戴锅盖当帽子，手拿鼓棒敲着种子罐。主色调 #ff7fb6。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. teenage boy, short pink hair with a small sprout growing on top, suspender shorts, a pot lid worn as a hat, drumsticks tapping a seed jar. main color #ff7fb6.
```

### 原创主播·第三期（包 id：`original_wave3`）

#### 栗铃踏拍（id：`og3_liling`）

赛道：歌回、游戏　主色：`#ff7fb6`　简介：快攻：戴栗子铃跳节奏游戏，把每次踩拍唱成小曲。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，栗色短双马尾系着栗子形铃铛，粉色运动背心配百褶短裙，跳舞踩拍的动态姿势，脚下有发光的节拍方块。主色调 #ff7fb6。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, short chestnut twin tails with chestnut-shaped bells, pink sports top and pleated skirt, dynamic dance step pose, glowing rhythm tiles under her feet. main color #ff7fb6.
```

#### 芽页邮筒（id：`og3_yaye`）

赛道：杂谈、助眠　主色：`#5fd08a`　简介：消耗：住在纸邮筒里的读信主播，用折页声慢慢续航。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，嫩绿色长发别着信封发夹，邮差帽和绿色邮差外套，从红褐色纸邮筒里探出半身，手拿一封折好的信。主色调 #5fd08a。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, long fresh green hair with an envelope hair clip, mail carrier cap and green postal coat, leaning out of a reddish-brown paper mailbox, holding a folded letter. main color #5fd08a.
```

#### 绒角投影（id：`og3_rongjiao`）

赛道：恐怖、绘画　主色：`#9b7dff`　简介：控制：画软绒小怪演灯影剧，让惊喜在眨眼间停一拍。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，紫色蓬松头发上有一对毛绒小角，深紫色斗篷，举着手电在身后投出软绒小怪物的影子。主色调 #9b7dff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, fluffy purple hair with a pair of plush little horns, deep purple cape, holding up a flashlight that casts the shadow of a fluffy little monster behind her. main color #9b7dff.
```

#### 泡尺工坊（id：`og3_paochi`）

赛道：硬核　主色：`#9db4c9`　简介：强化：直播制作泡泡测量仪，调好刻度再展示彩色花火。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻男性，银灰色短发，工坊护目镜，灰蓝背带工作服，手持透明的泡泡测量仪，周围飘着彩色泡泡。主色调 #9db4c9。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young man, short silver-gray hair, workshop goggles, gray-blue work overalls, holding a transparent bubble measuring gadget, colorful bubbles floating around. main color #9db4c9.
```

#### 软垫咚咚（id：`og3_ruandian`）

赛道：整活、硬核　主色：`#c7a6ff`　简介：反击：给小发明装软垫的整活主播，慢半拍也能弹回惊喜。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻男性，薰衣草紫圆寸头，穿圆滚滚像软垫一样的羽绒外套，怀抱一个装着弹簧的小机器人，呆萌表情。主色调 #c7a6ff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young man, round lavender buzz cut, puffy cushion-like down jacket, hugging a small robot with a spring, goofy cute expression. main color #c7a6ff.
```

#### 绵糖茶帽（id：`og3_miantang`）

赛道：助眠　主色：`#5cc6ff`　简介：辅助：戴茶杯帽直播温柔拟音，为队友接力备好茶点。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，天蓝色棉花糖般蓬松长发，头戴茶杯形小礼帽，奶白色围裙连衣裙，端着放茶点的托盘。主色调 #5cc6ff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, long fluffy cotton-candy sky blue hair, small teacup-shaped top hat, cream white apron dress, carrying a tray of tea and snacks. main color #5cc6ff.
```

#### 纸鸢卷卷（id：`og3_zhiyuan`）

赛道：绘画　主色：`#ffd166`　简介：强化：边画风筝边收集纸星，攒好灵感再铺开长空画卷。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，金黄色长卷发随风飘起，宽袖画师外衣，手牵彩色纸风筝的线，身边漂浮着纸星星。主色调 #ffd166。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, long golden curly hair blowing in the wind, wide-sleeved painter coat, holding the string of a colorful paper kite, floating paper stars around her. main color #ffd166.
```

#### 果冻转盘（id：`og3_guodong`）

赛道：游戏、整活　主色：`#ff8a3d`　简介：快攻：用果冻按键直播跳跃游戏，过线后抛开彩纸再出发。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻男性，橙色蓬松短发发梢像果冻一样半透明，运动外套，手持果冻按键的游戏手柄，身后抛撒彩纸。主色调 #ff8a3d。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young man, fluffy short orange hair with translucent jelly-like tips, sporty jacket, holding a game controller with jelly buttons, confetti bursting behind him. main color #ff8a3d.
```

#### 星罐慢音（id：`og3_xingguan`）

赛道：歌回、助眠　主色：`#ff7fb6`　简介：消耗：把晚安小调装进星罐，留住余音等待温柔回合。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，粉色长发渐变成星空蓝，柔纱长款演出裙，双手捧着装满星光的玻璃罐，闭眼轻声歌唱。主色调 #ff7fb6。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, long pink hair fading into starry blue, flowing chiffon stage gown, holding a glass jar full of starlight in both hands, eyes closed, singing softly. main color #ff7fb6.
```

#### 云扣拼拼（id：`og3_yunkou`）

赛道：硬核、绘画　主色：`#9db4c9`　简介：反击：直播拼装会变色的云窗，记住来招再整理对手强化。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻男性，灰白色云朵般蓬松头发，工匠马甲上挂满拼图扣件，手持一块会变色的云朵形窗框。主色调 #9db4c9。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young man, fluffy cloud-like off-white hair, craftsman vest hung with puzzle clasps, holding a color-shifting cloud-shaped window frame. main color #9db4c9.
```

#### 蘑笛巡游（id：`og3_modi`）

赛道：恐怖　主色：`#9b7dff`　简介：控制：提蘑菇灯直播木偶探秘，用好奇停拍接上轻快谢幕。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻女性，深紫色短发戴着蘑菇帽，一手提蘑菇灯，一手吹小木笛，肩上坐着一个木偶小人。主色调 #9b7dff。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young woman, short dark purple hair under a mushroom cap hat, mushroom lantern in one hand, playing a small wooden flute with the other, a tiny wooden puppet sitting on her shoulder. main color #9b7dff.
```

#### 橘帆飞票（id：`og3_jufan`）

赛道：游戏　主色：`#ff8a3d`　简介：快攻：折橘色纸帆直播竞速，口袋里总留一张再试票。

```
原创二次元虚拟主播角色立绘，半身像（头顶到腰部），正面略侧的 3/4 视角，人物居中、脸部占画面上部且清晰，干净线稿，清爽赛璐璐上色，两层柔和阴影，明亮饱和的配色，透明背景，单人，无文字，无水印，无标志。年轻男性，橘色短发，竞速护目镜，赛车风夹克，手持一只橘色纸折帆船，夹克口袋露出一张空白纸票。主色调 #ff8a3d。
```

```
original anime-style virtual streamer character portrait, half body from head to waist, slight 3/4 front view, centered, face large and clear in the upper part of the frame, clean line art, crisp cel shading with two soft shadow tones, bright saturated colors, transparent background, solo, no text, no watermark, no logo. young man, short tangerine hair, racing goggles, racing-style jacket, holding an orange folded paper sailboat, a blank paper ticket peeking from the jacket pocket. main color #ff8a3d.
```
