# 第三方组件与字体

## Noto Sans SC

- 文件：`UnityProject/Assets/StreamDuel/Fonts/NotoSansSC-Regular.otf`、`NotoSansSC-Bold.otf`，未经修改的简体中文子集，Regular 与 Bold 两个字重。
- 来源：[notofonts/noto-cjk 官方仓库](https://github.com/notofonts/noto-cjk/tree/f8d157532fbfaeda587e826d4cd5b21a49186f7c/Sans/SubsetOTF/SC)，固定提交 `f8d157532fbfaeda587e826d4cd5b21a49186f7c`。
- 许可：SIL Open Font License 1.1；完整许可随字体保存在 `Fonts/OFL.txt`，复制自同一提交的 `Sans/LICENSE`。字体许可独立于项目代码许可。
- Regular SHA256：`faa6c9df652116dde789d351359f3d7e5d2285a2b2a1f04a2d7244df706d5ea9`。
- Bold SHA256：`c6cb5a93abaa9edc8ee7463b7ebb7f42d618d40e6ed2f7a5371c97b0b64767c0`。
- 不复制或打包系统字体；界面使用上述字体资源。

## Unity Input System

- 包名 `com.unity.inputsystem`，版本 1.20.1；由 Unity 6000.6.5f1 的 Package Manager 返回为 recommended。
- 来源：[Unity 官方包文档](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.20/manual/index.html)，遵循包附带的 Unity Companion License。
- 通过 Package Manager 解析，仓库只保存依赖声明与包锁，不复制包源码。
