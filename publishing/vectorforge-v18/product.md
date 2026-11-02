# VectorForge v18.0.1

Windows x64 · 免费使用 · 需要 Adobe Illustrator · 本机自动导出 PDF / JPG

## AI 改完了，旁边的 PDF 和预览图也该更新了

图稿不断修改，发给同事的 PDF 和预览图却可能还停在上一次。VectorForge 监控你指定的文件夹，在已保存的 AI 稳定后自动检查输出：该导出时导出，没有变化就跳过。

PDF 和 JPG 可以分别设置多个路径。输出放在 AI 同目录，方便交接、核对和查找。软件免费使用；Illustrator 需要自行安装并具备合法使用许可。

[免费下载 Windows 安装版 ZIP](https://mootop.top/wp-content/uploads/vectorforge/VectorForge-v18.0.1-Windows-x64-Installer.zip) · [下载便携版 ZIP](https://mootop.top/wp-content/uploads/vectorforge/VectorForge-v18.0.1-Windows-x64-Portable.zip)

无需登录或结账。两个包选择一个即可：安装版解压后运行 `VectorForge-Setup-v18.0.1.exe`，便携版解压后运行 `VectorForge.exe`。免费使用不包含 Adobe Illustrator 的授权。

## 两条路径，各做一件事

- 把需要交付 PDF 的目录加入 PDF 任务，一个 AI 输出一个包含全部画板的 PDF。
- 把需要预览图的目录加入 JPG 任务，一个 AI 输出第一画板的一张 JPG。
- 图片可以按 DPI 导出，也能指定宽度，例如 5000 px，高度按画板比例计算。
- PDF 可以读取本机 Illustrator 的预设，并选择自己事先做好的预设。
- 修改时间范围自行填写小时数；隐藏文件、隐藏文件夹和命中关键词的文件跳过。
- 首次失败先让出队列，空闲时再尝试。三次失败后挂起，并在 AI 所在文件夹生成说明 TXT。

## 开着 Illustrator，也能继续导出

默认“始终导出”模式不等待鼠标、键盘空闲。它读取磁盘上已保存的 AI，用工作副本执行导出，完成后不保存文档更改。未保存的编辑不会出现在输出里。

Illustrator 被弹窗或长操作占用时，调用仍可能等待或超时。软件不会强制结束用户手动启动的 Illustrator；此时需要处理弹窗或恢复 Illustrator，再重试。专用电脑可以选择无人值守模式，由软件管理自己的专用会话。

## 放在本地，也说明白会处理哪些文件

原始 AI 不被软件保存、覆盖、移动或删除。对应 PDF/JPG 则会自动替换；确认源 AI 已删除后，软件也会自动清理清单中能够安全确认的关联输出，无需另行确认。建议先选择一个测试目录熟悉规则。

设置、导出清单和日志保存在 `%LocalAppData%\VectorForge`。图稿导出在本机进行，不需要把 AI 上传到网站。

## 第一次使用

1. 安装并确认 Illustrator 可以正常运行。当前实机验证为 Illustrator 2026；不宣称所有旧版都已兼容。
2. 完全退出旧版 VectorForge 的托盘进程，再运行新版安装包；也可以使用便携版 EXE。
3. 分别添加 PDF/JPG 路径，勾选任务，填写小时范围和扫描间隔。
4. JPG 按需选择 DPI 或像素宽度，PDF 按需读取预设，然后开始监控。

更改图片参数不会让已有最新图片全部重导。确实需要按新尺寸重做时，使用“强制覆盖导出全部 JPG”。

按宽度模式使用 Adobe 原生 JPG 质量档位 20/50/80/100，自动匹配最近档位；DPI 模式仍使用 0–100 设置。JPG 不是无损图片格式，本版本未加入额外无损压缩。

系统要求：Windows 10/11 x64；已安装并可运行的 Illustrator。安装包为自包含程序，无需另外安装 .NET；尚未在全新无 .NET 虚拟机完成安装/卸载验证。

[使用指南](https://mootop.top/docs/vectorforge/) · [开发手记](https://mootop.top/vectorforge-v18-0-1-development-story/) · [GitHub 版本归档](https://github.com/zhangweixsw/VectorForge/releases/tag/v18.0.1) · [文件 SHA-256](https://mootop.top/wp-content/uploads/vectorforge/SHA256SUMS-v18.0.1.txt)

软件底部“检查更新”可查询新版本，确认后下载并验证安装包；验证失败不启动安装，当前软件保留。检查更新会请求 MooTop 的版本清单和下载文件，不上传图稿。

mootop.top
