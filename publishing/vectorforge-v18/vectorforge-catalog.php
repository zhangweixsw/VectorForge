<?php
/** Plugin Name: MooTop VectorForge Catalog */
defined('ABSPATH') || exit;
add_filter('mootop_public_catalog', static function(array $catalog): array {
    $page=get_page_by_path('software/vectorforge');
    $doc=get_page_by_path('docs/vectorforge');
    if(!$page || $page->post_status!=='publish')return $catalog;
    $catalog['vectorforge']=[
        'key'=>'vectorforge','name'=>'VectorForge','category'=>'illustrator',
        'lead'=>'监控多个 AI 目录，按文件变化自动更新 PDF 与 JPG，支持按宽度等比导图和 PDF 预设。',
        'platform'=>'Windows x64 · Illustrator','status'=>'免费使用','price_text'=>'免费使用',
        'version'=>'18.0.1','image'=>content_url('/uploads/vectorforge/vectorforge-v18.0.1-icon.png'),
        'free'=>true,'testing'=>false,'url'=>get_permalink($page),
        'doc'=>$doc?get_permalink($doc):home_url('/docs/'),'doc_title'=>'VectorForge 使用指南'
    ];
    return $catalog;
});
add_action('wp_head',static function(){
    echo '<style>.product-card[data-product="vectorforge"] .product-card-media img{object-fit:contain;padding:10%;box-sizing:border-box;background:#eff4fb}</style>';
});
