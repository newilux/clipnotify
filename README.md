# 剪贴提示 ClipNotify

> 剪贴板变化提示小工具 · Windows

复制内容时立刻弹窗 + 响一声"叮"告诉你：**剪贴板更新成功**。解决"Ctrl+C 到底复制上没有"的困惑。

![icon](clipnotify.ico)

## 特性

- **即复制即提示**：剪贴板内容一变，右下角弹出小浮窗（带内容预览）+ 一声清脆提示音
- **快速连续复制不积压**：自绘浮窗立即刷新内容、1.4 秒自动消失，复制再快也不会排队堆积
- **精确模式（推荐）**：只有真实按下 Ctrl+C 才提示。通过低级键盘钩子区分"真实按键"与"应用模拟注入的按键"，**微信 / 豆包划词等"选中即复制"（模拟 Ctrl+C）不会误报**
- **独立开关**：托盘右键菜单可单独开关「提示音」「浮窗提示」，只留一个也行
- **设置持久化**：配置保存在 `%LOCALAPPDATA%\ClipNotify\settings.ini`
- **零干扰**：轮询系统剪贴板序列号，不接管剪贴板监视链，不与 Ditto、CopyQ 等剪贴板工具冲突
- **单实例**：重复打开不会运行多个
- **无依赖**：仅需 Windows 自带 .NET Framework 4.x，免安装、免联网

## 使用

1. 下载并运行 `clipnotify.exe`（或自行编译，见下），右下角托盘出现程序图标
2. 程序启动 1.5 秒后会自动提示一次，确认提示通路正常
3. 复制任意文字/图片/文件，右下角浮窗立即显示 + 提示音
4. 右键托盘图标可设置：

   | 菜单项 | 说明 |
   |---|---|
   | 提示音 | 勾选=复制时发声 |
   | 浮窗提示 | 勾选=复制时弹浮窗 |
   | 精确模式（仅Ctrl+C时提示） | 勾选后只有真实按下 Ctrl+C 才提示 |
   | 测试提示 | 手动触发一次提示验证效果 |

5. 双击托盘图标或右键菜单「退出」关闭程序

## 开机自启

`Win + R` 输入 `shell:startup` 回车，把 `clipnotify.exe` 的快捷方式放进去即可。

## 常见问题

**Q：微信/豆包里鼠标选中文字（还没按 Ctrl+C）也会提示？**
A：这是微信/豆包等应用的"选中即复制"行为——它们会**模拟按下 Ctrl+C** 把选中内容写入剪贴板。勾选「精确模式」即可过滤（程序能识别模拟注入的按键）。

**Q：远程桌面(RDP)、虚拟机里复制不提示？**
A：这些环境会截断 Windows 剪贴板链路，提示工具同样收不到变化，属系统机制限制。

**Q：首次运行被 Windows Defender/SmartScreen 拦截？**
A：未签名小工具的正常误报：右键 exe → 属性 → 解除锁定，或点"更多信息 → 仍要运行"。

## 自行编译

环境：Windows + .NET Framework 4.x（系统自带）

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /win32icon:clipnotify.ico /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /out:clipnotify.exe clipnotify.cs
```

图标由 `makeicon.cs` 程序化生成（可修改配色后重新生成 `clipnotify.ico`）。

## 运行日志

`%LOCALAPPDATA%\ClipNotify\clipnotify.log` 记录每次剪贴板变化时的真实 Ctrl 状态与判定结果，便于排查。

## 许可

[MIT](LICENSE)
