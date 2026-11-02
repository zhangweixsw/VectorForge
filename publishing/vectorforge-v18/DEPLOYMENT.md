# v18.0.1 发布记录

2026-11-02 发布至 mootop.top，免费直接下载，无需账号或购物车。

- 详情：https://mootop.top/software/vectorforge/ （页面 338）
- 指南：https://mootop.top/docs/vectorforge/ （页面 339）
- 手记：https://mootop.top/vectorforge-v18-0-1-development-story/ （文章 340）
- 归档：https://github.com/zhangweixsw/VectorForge/releases/tag/v18.0.1
- 更新清单：https://mootop.top/wp-content/uploads/vectorforge/update.json

安装版和便携版 ZIP 经服务器 SHA-256 校验。匿名下载便携包与本地摘要一致；更新实测完成线上签名、当前版本比较、安装包下载与摘要校验，不执行安装。安装器 `--verify` 返回 0。47 项自动检查通过；Illustrator 2026 实机按宽度导出 5000×3333，保留原已打开文档。

网站当前由 mootop-site-refresh 的 `mootop_public_catalog` 生成目录；`vectorforge-catalog.php` 作为独立 MU 插件接入，同时保留 WordPress 原软件页中的卡片。未修改其他产品。原软件页内容可通过 WordPress 修订版本恢复。

`render.ps1` 生成三个 core/html 区块；`publish.php` 拒绝覆盖未登记的同名页面。`upload.sh` 拒绝覆盖内容不同的既有版本文件。上传前应将脚本转换为 LF；校验脚本兼容 Windows 校验清单的 CRLF。将生成 HTML、三张 PNG、ZIP、SHA256SUMS.txt、update.json、publish.php、upload.sh、vectorforge-catalog.php 放入任务暂存目录后执行发布脚本。

更新私钥仅保留在本机用户目录的独立密钥文件中，不进入仓库或发布包；客户端嵌入公钥。

未在全新无 .NET 虚拟机完成安装/卸载验收；未宣称所有 Illustrator 旧版本兼容。源码仅供查阅，具体使用权见 LICENSE.txt。
