$ErrorActionPreference='Stop'
$out=Join-Path $PSScriptRoot 'transfer'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$base='https://mootop.top/wp-content/uploads/vectorforge'
$cross='<p><a href="/software/vectorforge/">软件详情与下载</a> · <a href="/docs/vectorforge/">使用指南</a> · <a href="/vectorforge-v18-0-1-development-story/">开发手记</a></p>'
$css=@'
<style>
body:has(.mootop-vectorforge) .wp-post-image{object-fit:contain!important;max-height:440px;width:100%;padding:36px;box-sizing:border-box;background:#eff4fb}
.mootop-vectorforge{max-width:1120px;margin:auto;overflow-wrap:anywhere}.mootop-vectorforge figure{margin:32px 0}.mootop-vectorforge figure img{display:block;max-width:100%;height:auto;object-fit:contain;margin:auto}.mootop-vectorforge figcaption{font-size:14px;color:#526174;margin-top:12px}.mootop-vectorforge table{display:block;max-width:100%;overflow-x:auto}.mootop-vectorforge h2{margin-top:2em}.mootop-vectorforge .vf-ui{max-height:1250px}.mootop-vectorforge .vf-download{display:flex;flex-wrap:wrap;gap:14px;margin:24px 0}.mootop-vectorforge .vf-download a{background:#142b50;color:white;border-radius:12px;padding:14px 20px;text-decoration:none}
@media(max-width:600px){.mootop-vectorforge{padding:0 4px}.mootop-vectorforge .vf-download a{display:block;width:100%;text-align:center;box-sizing:border-box}}
</style>
'@
foreach($name in @('product','guide','story')){
    $md=Get-Content -LiteralPath (Join-Path $PSScriptRoot "$name.md") -Raw
    $html=(ConvertFrom-Markdown -InputObject $md).Html
    $html=$html -replace '(?s)^<h1\b[^>]*>.*?</h1>',''
    if($name -eq 'product'){
        $download='<div class="vf-download"><a href="'+$base+'/VectorForge-v18.0.1-Windows-x64-Installer.zip">免费下载 Windows 安装版</a><a href="'+$base+'/VectorForge-v18.0.1-Windows-x64-Portable.zip">下载便携版</a></div>'
        $visuals='<figure><img class="vf-ui" src="'+$base+'/vectorforge-v18.0.1-ui.png" alt="VectorForge v18.0.1 真实软件界面，包含 PDF 与 JPG 多路径任务、小时范围、像素宽度及检查更新按钮"/><figcaption>真实界面：PDF 与 JPG 分别设置路径，图片可选择 DPI 或按宽度等比导出。</figcaption></figure><figure><img src="'+$base+'/vectorforge-v18.0.1-flow.png" alt="监控目录、检查源变化和稳定性、导出工作副本的三步流程示意"/><figcaption>流程示意：稳定后检查变化，未变跳过；PDF 包含全部画板，JPG 导出第一画板。</figcaption></figure>'
        $html='<p class="mootop-eyebrow">WINDOWS x64 + ILLUSTRATOR · 免费使用 · v18.0.1</p>'+$download+$html+$visuals
        $sums=Get-Content (Join-Path $PSScriptRoot '../../dist/release-v18.0.1/SHA256SUMS.txt') -Raw
        $html+='<h2>版本与文件校验</h2><p>v18.0.1 · 2026-11-02 · 免费使用。下列摘要对应当前下载包：</p><pre style="white-space:pre-wrap;overflow-wrap:anywhere">'+[Net.WebUtility]::HtmlEncode($sums)+'</pre>'
    }
    if($name -eq 'story'){$class='mootop-editorial-story mootop-vectorforge'}elseif($name -eq 'guide'){$class='mootop-guide mootop-vectorforge'}else{$class='mootop-product-story mootop-vectorforge'}
    $content='<!-- wp:html -->'+$css+'<article class="'+$class+'">'+$html+$cross+'</article><!-- /wp:html -->'
    [IO.File]::WriteAllText((Join-Path $out "$name.html"),$content)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../src/VectorForge/Assets/VectorForge.png') -Destination (Join-Path $out 'vectorforge-v18.0.1-icon.png')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../artifacts/v17-ui.png') -Destination (Join-Path $out 'vectorforge-v18.0.1-ui.png')
