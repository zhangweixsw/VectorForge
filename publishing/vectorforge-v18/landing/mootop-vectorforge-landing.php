<?php
/** Plugin Name: MooTop VectorForge Landing
 * Description: VectorForge 专用产品展示页；停用本文件即可恢复原 WordPress 页面。
 * Version: 1.0.0
 */
defined('ABSPATH') || exit;
add_filter('template_include',static function($template){
    if(is_page(338) && !is_preview())return __DIR__.'/vectorforge-landing/render.php';
    return $template;
},999);
