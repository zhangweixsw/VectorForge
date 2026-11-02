param([string]$Exe = (Join-Path $PSScriptRoot '../dist/win-x64-v7/VectorForge.exe'))
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path $PSScriptRoot ('../artifacts/illustrator-v7-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$testRoot = [System.IO.Path]::GetFullPath($testRoot)
$source = Join-Path $testRoot 'v7-source.ai'
$sourceJs = ConvertTo-Json ($source.Replace('\','/')) -Compress
$app = New-Object -ComObject Illustrator.Application
$baseline = $app.Documents.Count
$create = @"
(function(){
var old = app.documents.length ? app.activeDocument : null;
var d = app.documents.add(DocumentColorSpace.RGB, 240, 160);
d.artboards.add([260,160,500,0]);
var shape = d.pathItems.rectangle(140,20,190,100);
var c = new RGBColor(); c.red=255; c.green=154; c.blue=50;
shape.fillColor=c; shape.stroked=false;
var opts=new IllustratorSaveOptions(); opts.pdfCompatible=true;
d.saveAs(new File($sourceJs),opts);
if(old)old.activate();
return String(d.artboards.length);
}())
"@
$boards = $app.DoJavaScript($create)
$before = Get-FileHash -LiteralPath $source -Algorithm SHA256
$stamp = Get-Item -LiteralPath $source
$beforeLength = $stamp.Length
$beforeTime = $stamp.LastWriteTimeUtc
$start = [System.Diagnostics.ProcessStartInfo]::new([System.IO.Path]::GetFullPath($Exe), '--worker')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
$start.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
$worker = [System.Diagnostics.Process]::Start($start)
$stderr = $worker.StandardError.ReadToEndAsync()
$results = @()
try {
    foreach ($case in @(@{Kind=1;Dpi=72;Quality=50;Name='jpg72'}, @{Kind=1;Dpi=300;Quality=95;Name='jpg300'}, @{Kind=0;Dpi=150;Quality=80;Name='pdf'})) {
        $prefix = Join-Path $testRoot $case.Name
        $working = $prefix + '.ai'
        Copy-Item -LiteralPath $source -Destination $working
        $id = [guid]::NewGuid().ToString()
        $request = @{JobId=$id;Kind=$case.Kind;SourcePath=$working;WorkingPrefix=$prefix;OwnershipFile=(Join-Path $testRoot 'ownership.json');JpgDpi=$case.Dpi;JpgQuality=$case.Quality;Attempt=1}
        $worker.StandardInput.WriteLine(($request | ConvertTo-Json -Compress))
        $worker.StandardInput.Flush()
        $responseTask = $worker.StandardOutput.ReadLineAsync()
        if (!$responseTask.Wait(120000)) { throw 'Worker response timed out' }
        $response = $responseTask.Result | ConvertFrom-Json
        if (!$response.Success) { throw ($response | ConvertTo-Json -Compress) }
        if ($response.JobId -ne $id) { throw 'Job ID mismatch' }
        $output = $response.TemporaryOutputs[0]
        if (!(Test-Path -LiteralPath $output) -or (Get-Item -LiteralPath $output).Length -eq 0) { throw 'Output missing' }
        $results += @{Case=$case.Name;WorkerPid=$worker.Id;Result=$response;Bytes=(Get-Item -LiteralPath $output).Length}
    }
    $worker.StandardInput.Close()
    if (!$worker.WaitForExit(10000)) { throw 'Worker shutdown timed out' }
    $after = Get-FileHash -LiteralPath $source -Algorithm SHA256
    $afterInfo = Get-Item -LiteralPath $source
    if ($before.Hash -ne $after.Hash -or $beforeLength -ne $afterInfo.Length -or $beforeTime -ne $afterInfo.LastWriteTimeUtc) { throw 'Source changed' }
    if ($app.Documents.Count -ne ($baseline + 1)) { throw 'Unexpected document count after shared worker' }
    $report = @{Source=$source;Artboards=$boards;SourceUnchanged=$true;UserDocumentCountPreserved=$true;BaselineDocuments=$baseline;Results=$results}
    [System.IO.File]::WriteAllText((Join-Path $testRoot 'result.json'), ($report | ConvertTo-Json -Depth 10))
    $report | ConvertTo-Json -Depth 10
} finally {
    if (!$worker.HasExited) { $worker.Kill() }
    $worker.Dispose()
    # Only close the exact fixture document created above. Do not save it again.
    $close = "(function(){for(var i=app.documents.length-1;i>=0;i--){try{if(app.documents[i].fullName.fsName.toLowerCase()===new File($sourceJs).fsName.toLowerCase())app.documents[i].close(SaveOptions.DONOTSAVECHANGES);}catch(e){}}}())"
    $app.DoJavaScript($close) | Out-Null
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($app) | Out-Null
}

