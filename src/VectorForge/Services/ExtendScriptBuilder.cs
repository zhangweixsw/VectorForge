using System.Text.Json;

namespace VectorForge.Services;

public static class ExtendScriptBuilder
{
    public static string Build(ExportRequest request)
    {
        var source = Quote(request.SourcePath);
        var prefix = Quote(request.WorkingPrefix);
        var progress = ProgressCode(request.ProgressPath);
        return request.Kind == ExportKind.Pdf
            ? BuildPdf(source, prefix, Quote(request.PdfPreset ?? ""), progress)
            : BuildJpg(source, prefix, request.JpgDpi, request.JpgQuality, progress, request.JpgSizing, request.JpgPixelWidth);
    }

    public static string BuildReadPdfPresets() => "(function(){var p=app.PDFPresetsList;var a=[];for(var i=0;i<p.length;i++)a.push(encodeURIComponent(String(p[i])));return 'PRESETS|'+a.join('|');}())";

    private static string ProgressCode(string path) => $$"""
        var vfStage = "Opening";
        var failedAt = "";
        function vfProgress(stage) {
            vfStage = stage;
            try {
                var progressPath = {{Quote(path)}};
                if (!progressPath) return;
                var progressFile = new File(progressPath);
                progressFile.encoding = "UTF-8";
                if (progressFile.open("w")) { progressFile.write(stage); progressFile.close(); }
            } catch (progressError) { }
        }
        """;

    private static string BuildPdf(string source, string prefix, string requestedPreset, string progress) => $$"""
        (function () {
            {{progress}}
            var doc = null;
            var previousDoc = app.documents.length > 0 ? app.activeDocument : null;
            var previousInteraction = app.userInteractionLevel;
            try {
                app.userInteractionLevel = UserInteractionLevel.DONTDISPLAYALERTS;
                var sourceFile = new File({{source}});
                for (var openIndex = 0; openIndex < app.documents.length; openIndex++) {
                    try {
                        if (String(app.documents[openIndex].fullName.fsName).toLowerCase() === String(sourceFile.fsName).toLowerCase())
                            return "ERR|" + encodeURIComponent("SOURCE_OPEN: 此 AI 文件正在 Illustrator 中编辑，已跳过以保护未保存内容");
                    } catch (openCheckError) { }
                }
                vfProgress("Opening");
                doc = app.open(sourceFile);
                vfProgress("Exporting");
                var target = {{prefix}} + ".pdf";
                var opts = new PDFSaveOptions();
                var presets = app.PDFPresetsList;
                var chosen = "";
                var requested = {{requestedPreset}};
                if (requested) {
                    for (var i = 0; i < presets.length; i++) {
                        if (String(presets[i]) === requested) { chosen = presets[i]; break; }
                    }
                    if (!chosen) return "ERR|" + encodeURIComponent("PDF_PRESET_NOT_FOUND: Illustrator 中找不到所选预设：" + requested);
                } else {
                    for (var i = 0; i < presets.length; i++) {
                        var lower = String(presets[i]).toLowerCase();
                        if (lower.indexOf("smallest") >= 0 || lower.indexOf("最小") >= 0) {
                            chosen = presets[i];
                            break;
                        }
                    }
                }
                if (chosen) {
                    opts.pDFPreset = chosen;
                } else {
                    opts.preserveEditability = false;
                    opts.generateThumbnails = false;
                    opts.optimization = true;
                    opts.compressArt = true;
                    opts.colorDownsampling = 100;
                    opts.colorDownsamplingImageThreshold = 150;
                    opts.grayscaleDownsampling = 100;
                    opts.grayscaleDownsamplingImageThreshold = 150;
                }
                opts.artboardRange = "";
                doc.saveAs(new File(target), opts);
                return "OK|" + doc.artboards.length + "|" + encodeURIComponent(target);
            } catch (e) {
                failedAt = vfStage;
                return "ERR|" + encodeURIComponent(e.name + ": " + e.message + " @" + e.line);
            } finally {
                if (doc !== null) {
                    vfProgress("Closing");
                    try { doc.close(SaveOptions.DONOTSAVECHANGES); }
                    catch (closeError) { return "ERR|" + encodeURIComponent("CLOSE_FAILED: " + closeError.message); }
                }
                vfProgress(failedAt || "Completed");
                try { if (previousDoc !== null) previousDoc.activate(); } catch (activateError) { }
                try { app.userInteractionLevel = previousInteraction; } catch (interactionError) { }
            }
        }())
        """;

    private static string BuildJpg(string source, string prefix, int dpi, int quality, string progress, JpgSizingMode sizing, int pixelWidth)
    {
        var scale = dpi / 72.0 * 100.0;
        return $$"""
            (function () {
                {{progress}}
                var doc = null;
                var previousDoc = app.documents.length > 0 ? app.activeDocument : null;
                var previousInteraction = app.userInteractionLevel;
                try {
                    app.userInteractionLevel = UserInteractionLevel.DONTDISPLAYALERTS;
                    var sourceFile = new File({{source}});
                    for (var openIndex = 0; openIndex < app.documents.length; openIndex++) {
                        try {
                            if (String(app.documents[openIndex].fullName.fsName).toLowerCase() === String(sourceFile.fsName).toLowerCase())
                                return "ERR|" + encodeURIComponent("SOURCE_OPEN: 此 AI 文件正在 Illustrator 中编辑，已跳过以保护未保存内容");
                        } catch (openCheckError) { }
                    }
                    vfProgress("Opening");
                    doc = app.open(sourceFile);
                    vfProgress("Exporting");
                    doc.artboards.setActiveArtboardIndex(0);
                    var exportScale = {{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
                    if ({{(sizing == JpgSizingMode.PixelWidth ? "true" : "false")}}) {
                        var bounds = doc.artboards[0].artboardRect;
                        var boardWidth = bounds[2] - bounds[0];
                        if (!(boardWidth > 0)) throw new Error("画板宽度无效");
                        exportScale = {{pixelWidth}} / boardWidth * 100;
                        var folder = new Folder({{prefix}} + "_pixels");
                        if (folder.exists || !folder.create()) throw new Error("无法创建独立像素导出临时目录");
                        doc.artboards[0].name = "VectorForge";
                        var pixelOptions = new ExportForScreensOptionsJPEG();
                        pixelOptions.scaleType = ExportForScreensScaleType.SCALEBYWIDTH;
                        pixelOptions.scaleTypeValue = {{pixelWidth}};
                        var items = new ExportForScreensItemToExport();
                        items.artboards = "1";
                        items.document = false;
                        var type = {{quality}} <= 35 ? ExportForScreensType.SE_JPEG20 :
                            {{quality}} <= 65 ? ExportForScreensType.SE_JPEG50 :
                            {{quality}} <= 90 ? ExportForScreensType.SE_JPEG80 : ExportForScreensType.SE_JPEG100;
                        doc.exportForScreens(folder, type, pixelOptions, items);
                        return "SCREEN|" + encodeURIComponent(folder.fsName);
                    }
                    var opts = new ExportOptionsJPEG();
                    opts.antiAliasing = true;
                    opts.artBoardClipping = true;
                    opts.matte = true;
                    opts.optimization = true;
                    opts.qualitySetting = {{quality}};
                    opts.horizontalScale = exportScale;
                    opts.verticalScale = exportScale;
                    doc.artboards.setActiveArtboardIndex(0);
                    var basePath = {{prefix}};
                    doc.exportFile(new File(basePath), ExportType.JPEG, opts);
                    return "OK|1|" + encodeURIComponent(basePath + ".jpg");
                } catch (e) {
                    failedAt = vfStage;
                    return "ERR|" + encodeURIComponent(e.name + ": " + e.message + " @" + e.line);
                } finally {
                    if (doc !== null) {
                        vfProgress("Closing");
                        try { doc.close(SaveOptions.DONOTSAVECHANGES); }
                        catch (closeError) { return "ERR|" + encodeURIComponent("CLOSE_FAILED: " + closeError.message); }
                    }
                    vfProgress(failedAt || "Completed");
                    try { if (previousDoc !== null) previousDoc.activate(); } catch (activateError) { }
                    try { app.userInteractionLevel = previousInteraction; } catch (interactionError) { }
                }
            }())
            """;
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value.Replace('\\', '/'));
}
