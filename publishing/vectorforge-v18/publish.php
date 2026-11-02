<?php
if (!defined('ABSPATH')) exit;
$root=__DIR__;
$software=get_page_by_path('software');$docs=get_page_by_path('docs');
if(!$software||!$docs)throw new Exception('Missing parent pages');
$definitions=[
 ['product','page','vectorforge',$software->ID,'VectorForge v18.0.1'],
 ['guide','page','vectorforge',$docs->ID,'VectorForge v18.0.1 使用指南'],
 ['story','post','vectorforge-v18-0-1-development-story',0,'AI 改完之后，为什么 PDF 和预览图还要再手动导一次？'],
];
foreach($definitions as $d){
 $found=get_posts(['post_type'=>$d[1],'name'=>$d[2],'post_parent'=>$d[3],'post_status'=>'any','numberposts'=>1]);
 if($found && get_post_meta($found[0]->ID,'_vectorforge_publication',true)!=='18.0.1')throw new Exception('Conflicting existing slug '.$d[2]);
}
require_once ABSPATH.'wp-admin/includes/image.php';
$iconPath=ABSPATH.'wp-content/uploads/vectorforge/vectorforge-v18.0.1-icon.png';
$iconUrl=content_url('uploads/vectorforge/vectorforge-v18.0.1-icon.png');
$image=attachment_url_to_postid($iconUrl);
if(!$image){$image=wp_insert_attachment(['post_title'=>'VectorForge v18.0.1 品牌图标','post_mime_type'=>'image/png','post_status'=>'inherit','guid'=>$iconUrl],$iconPath,0,true);if(is_wp_error($image))throw new Exception($image->get_error_message());wp_update_attachment_metadata($image,wp_generate_attachment_metadata($image,$iconPath));}
update_post_meta($image,'_wp_attachment_image_alt','VectorForge 深蓝底橙色矢量笔尖与导出箭头品牌图标');
$ids=[];
foreach($definitions as $d){
 $found=get_posts(['post_type'=>$d[1],'name'=>$d[2],'post_parent'=>$d[3],'post_status'=>'any','numberposts'=>1]);
 $data=['post_type'=>$d[1],'post_name'=>$d[2],'post_parent'=>$d[3],'post_title'=>$d[4],'post_status'=>'publish','post_content'=>wp_slash(file_get_contents("$root/$d[0].html")), 'post_excerpt'=>'监控多个 Illustrator 文件夹，按源文件变化自动更新 PDF 和 JPG，支持像素宽度、PDF 预设与失败说明。'];
 if($found)$data['ID']=$found[0]->ID;
 if($d[1]==='post'){$term=get_term_by('slug','product-notes','category');if(!$term)throw new Exception('Missing product-notes category');$data['post_category']=[$term->term_id];}
 $id=$found?wp_update_post($data,true):wp_insert_post($data,true);if(is_wp_error($id))throw new Exception($id->get_error_message());
 update_post_meta($id,'_vectorforge_publication','18.0.1');
 update_post_meta($id,'_mootop_platform','Windows x64 + Adobe Illustrator');update_post_meta($id,'_mootop_current_version','18.0.1');
 update_post_meta($id,'_mootop_product_code','vectorforge');update_post_meta($id,'_mootop_license_enabled','0');update_post_meta($id,'_mootop_release_stage','stable');
 set_post_thumbnail($id,$image);$ids[$d[0]]=['id'=>$id,'url'=>get_permalink($id)];
}
$content=get_post_field('post_content',$software->ID);
if(strpos($content,'data-mootop-product="vectorforge"')===false){
 file_put_contents("$root/software-before.html",$content);
 $start=strpos($content,'id="illustrator-tools"');if($start===false)throw new Exception('Missing illustrator section');
 $grid=strpos($content,'<div class="mootop-software-grid">',$start);if($grid===false)throw new Exception('Missing grid');
 $pos=$grid+strlen('<div class="mootop-software-grid">');
 $card='<article class="mootop-software-card" data-mootop-product="vectorforge"><a class="mootop-software-card__media" href="/software/vectorforge/"><img src="'.$iconUrl.'" alt="VectorForge 文件夹监控导出工具图标" style="object-fit:contain;padding:10%;box-sizing:border-box" loading="lazy"><span class="mootop-software-card__badge">免费使用</span></a><div class="mootop-software-card__body"><p class="mootop-eyebrow">WINDOWS + ILLUSTRATOR</p><h2><a href="/software/vectorforge/">VectorForge <small>v18.0.1</small></a></h2><p class="mootop-software-card__description">监控多个 AI 目录，按文件变化自动更新 PDF 与 JPG，支持按宽度等比导图和自定义 PDF 预设。</p><ul class="mootop-software-card__meta"><li>Windows x64</li><li>免费使用</li><li>Illustrator 2026 实测</li></ul><div class="mootop-software-card__actions"><a class="wp-element-button" href="/software/vectorforge/">免费下载</a><a href="/docs/vectorforge/">使用指南 →</a></div></div></article>';
 $new=substr($content,0,$pos).$card.substr($content,$pos);
 if(count(parse_blocks($new))!==count(parse_blocks($content)))throw new Exception('Unexpected block structural change');
 $result=wp_update_post(['ID'=>$software->ID,'post_content'=>wp_slash($new)],true);if(is_wp_error($result))throw new Exception($result->get_error_message());
}
wp_cache_flush();echo wp_json_encode($ids,JSON_UNESCAPED_UNICODE|JSON_PRETTY_PRINT);
