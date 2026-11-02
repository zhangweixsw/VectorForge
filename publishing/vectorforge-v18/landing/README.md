# VectorForge 独立详情页

参考用户指定的 AmazonProdSeek 详情页：深色首屏、大标题、浅深交替分区、工作流程、格式切换、交互解释、真实截图、FAQ 与收尾下载。使用 VectorForge 深蓝橙色品牌，未复制其他产品能力或数据。

线上地址保留 https://mootop.top/software/vectorforge/ ，不修改原下载、指南与手记入口。WordPress 页面 338 的正文与元数据保留。

部署文件：

- `mootop-vectorforge-landing.php` 放入 `wp-content/mu-plugins/`。
- `index.html`、`render.php` 放入 `wp-content/mu-plugins/vectorforge-landing/`。

上线前保存原页面、核对页面 ID 和路径、确认目标文件不存在，PHP 语法检查后部署。原正文备份通过副本比较验证恢复；页面改版不需要数据库导入。脚本仅对页面 338 生效。

回滚：将 `wp-content/mu-plugins/mootop-vectorforge-landing.php` 重命名为 `.php.disabled`，执行 `wp cache flush`，恢复 WordPress 原正文页面。无需变更安装包或清单。

验证：`node publishing/vectorforge-v18/check-landing.cjs https://mootop.top/software/vectorforge/`。检查 1440/390 视口、图像加载、源状态/格式/使用步骤切换、6000×4000 示例计算、FAQ、规范链接和原下载链接。截图和结果写入 artifacts。

减少动态跟随系统设置；选项支持方向键、Home/End 和键盘操作。交互仅为明确标注的解释示意，不访问本机文件。
