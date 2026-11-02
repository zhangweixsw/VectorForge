# VectorForge v18.0.1

Windows x64 · 需要本机 Adobe Illustrator · 免费使用

监控多个 AI 文件夹，按源文件变化更新同目录 PDF 和第一画板 JPG。支持自定义 PDF 预设、小时范围、关键词排除、隐藏条目跳过，以及按像素宽度等比导出。默认已有 Illustrator 或有人操作时也继续提交导出；没有源变化且输出有效时仍跳过。

## 下载与使用

- 安装版 ZIP：解压后运行 `VectorForge-Setup-v18.0.1.exe`。
- 便携版 ZIP：解压后运行 `VectorForge.exe`。
- 两个包选择一个即可。升级前请从托盘退出旧版。
- [软件详情与免费下载](https://mootop.top/software/vectorforge/)
- [使用指南](https://mootop.top/docs/vectorforge/)
- [开发手记](https://mootop.top/vectorforge-v18-0-1-development-story/)

原始 AI 只读，输出 PDF/JPG 自动替换及核对清理。PDF 包含全部画板，JPG 只导第一画板。像素模式的 JPG 质量匹配 Adobe 原生 20/50/80/100 档位，DPI 模式保留 0–100 设置。

当前实机验证为 Illustrator 2026；不宣称所有旧版兼容。47 项自动检查通过，覆盖签名更新和文件摘要拒绝篡改。自包含程序无需额外安装 .NET；未在全新无 .NET 虚拟机完成安装/卸载测试。

免费使用不包含 Adobe Illustrator 许可。源码供查阅，非 MIT 开源授权，具体权利见仓库 LICENSE.txt。每个 ZIP 包含运行程序、使用指南、开发手记及许可证，不包含用户图稿、设置和日志。

文件摘要见本 Release 的 SHA256SUMS.txt。软件底部可检查更新，确认后验证并打开安装程序；更新失败保留当前版本。

mootop.top
