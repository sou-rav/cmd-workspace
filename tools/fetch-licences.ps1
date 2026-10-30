# Downloads the licence texts of the terminal files and fonts listed in app\Web\vendor\versions.txt (the SAME versions, from the npm registry).
# Writes app\Web\vendor\licenses\*.txt and licenses-summary.txt (the licence name each package declares). Needs internet. Changes nothing else.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$vendor = Join-Path $root 'app\Web\vendor'
$list = Join-Path $vendor 'versions.txt'
if (-not (Test-Path $list)) { Write-Host 'versions.txt not found. Run start.cmd or build-exe.cmd first.'; exit 1 }
$out = Join-Path $vendor 'licenses'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$tmp = Join-Path $env:TEMP ('cmdws-lic-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$summary = @('Licence names are read from each package (package.json, field "license"). Texts are in the files next to this one.', '')
$failed = 0
foreach ($line in Get-Content $list) {
    if ($line -notmatch '^(\S+)\s+(\d+\.\d+\.\d+\S*)') { continue }
    $name = $Matches[1]; $ver = $Matches[2]
    $seg = $name.Split('/')[-1]
    $safe = ($name -replace '[^A-Za-z0-9._-]', '_')
    try {
        $url = "https://registry.npmjs.org/$name/-/$seg-$ver.tgz"
        $file = Join-Path $tmp "$safe.tgz"
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $file
        $dir = Join-Path $tmp $safe
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        tar -xzf $file -C $dir
        $pkg = Join-Path $dir 'package'
        $lic = ''
        $pj = Join-Path $pkg 'package.json'
        if (Test-Path $pj) { $lic = (Get-Content $pj -Raw | ConvertFrom-Json).license }
        $texts = Get-ChildItem $pkg -File | Where-Object { $_.Name -match '^(LICEN[SC]E|COPYING|NOTICE)' }
        if ($texts) {
            $i = 0
            foreach ($t in $texts) { Copy-Item $t.FullName (Join-Path $out ("$safe-$ver-" + $t.Name + '.txt')) -Force; $i++ }
            $summary += "$name $ver : $lic ($i licence file(s))"
        } else {
            $summary += "$name $ver : $lic (NO licence file inside the package, check $url)"
            $failed++
        }
    } catch {
        $summary += "$name $ver : DOWNLOAD FAILED ($($_.Exception.Message))"
        $failed++
    }
}
$summary | Set-Content (Join-Path $out 'licenses-summary.txt') -Encoding UTF8
Remove-Item -Recurse -Force $tmp
$summary | ForEach-Object { Write-Host $_ }
if ($failed -gt 0) { Write-Host ''; Write-Host "WARNING: $failed package(s) without a licence text. See licenses-summary.txt." }
exit 0
