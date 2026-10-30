# Downloads the terminal files into app\Web\vendor. Needs internet. Changes nothing else.
# xterm.js versions are PINNED (written from memory of the npm registry, not verified by the assistant: if a download says 404, send logs\setup.log).
$ErrorActionPreference = 'Stop'
$vendor = Join-Path $PSScriptRoot 'Web\vendor'
New-Item -ItemType Directory -Force -Path $vendor | Out-Null
$tmp = Join-Path $env:TEMP ('cmdws-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

function Get-Package($name, $version, $tarballName) {
    $url = "https://registry.npmjs.org/$name/-/$tarballName-$version.tgz"
    Write-Host "Downloading $name $version ..."
    $file = Join-Path $tmp ([guid]::NewGuid().ToString('N') + '.tgz')
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $file
    $dir = Join-Path $tmp ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    tar -xzf $file -C $dir
    return (Join-Path $dir 'package')
}

$versions = @()
$pkg = Get-Package '@xterm/xterm' '5.5.0' 'xterm'
Copy-Item (Join-Path $pkg 'lib\xterm.js') $vendor -Force
Copy-Item (Join-Path $pkg 'css\xterm.css') $vendor -Force
$versions += '@xterm/xterm ' + (Get-Content (Join-Path $pkg 'package.json') -Raw | ConvertFrom-Json).version   # Phase 7: the version really read from the package

$pkg = Get-Package '@xterm/addon-fit' '0.10.0' 'addon-fit'
Copy-Item (Join-Path $pkg 'lib\addon-fit.js') $vendor -Force
$versions += '@xterm/addon-fit ' + (Get-Content (Join-Path $pkg 'package.json') -Raw | ConvertFrom-Json).version   # Phase 7: the version really read from the package

$pkg = Get-Package '@xterm/addon-web-links' '0.11.0' 'addon-web-links'
Copy-Item (Join-Path $pkg 'lib\addon-web-links.js') $vendor -Force
$versions += '@xterm/addon-web-links ' + (Get-Content (Join-Path $pkg 'package.json') -Raw | ConvertFrom-Json).version   # Phase 7: the version really read from the package

# Terminal fonts (optional, free fonts from fontsource, licence per package (mostly OFL, Ubuntu Mono is UFL), not pinned). A font that fails to download is skipped.
# The font names that need installing on the PC (SF Mono, Menlo, Monaco, Berkeley Mono, Nerd Font, Windows fonts) are not downloaded.
$fontDir = Join-Path $vendor 'fonts'
New-Item -ItemType Directory -Force -Path $fontDir | Out-Null
$fonts = @(
    @('jetbrains-mono', 'JetBrains Mono'), @('fira-code', 'Fira Code'), @('cascadia-code', 'Cascadia Code'),
    @('cascadia-mono', 'Cascadia Mono'), @('iosevka', 'Iosevka'), @('geist-mono', 'Geist Mono'),
    @('ibm-plex-mono', 'IBM Plex Mono'), @('source-code-pro', 'Source Code Pro'), @('hack', 'Hack'),
    @('maple-mono', 'Maple Mono'), @('ubuntu-mono', 'Ubuntu Mono')
)
$css = ''
foreach ($f in $fonts) {
    $id = $f[0]; $family = $f[1]
    try {
        $meta = Invoke-RestMethod -UseBasicParsing -Uri "https://registry.npmjs.org/@fontsource/$id/latest"
        Write-Host "Downloading font $family $($meta.version) ..."
        $file = Join-Path $tmp "$id.tgz"
        Invoke-WebRequest -UseBasicParsing -Uri $meta.dist.tarball -OutFile $file
        $dir = Join-Path $tmp "font-$id"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        tar -xzf $file -C $dir
        $added = 0
        foreach ($w in @('300', '400', '500', '600', '700')) {
            $name = "$id-latin-$w-normal.woff2"
            $src = Join-Path $dir "package\files\$name"
            if (Test-Path $src) {
                Copy-Item $src $fontDir -Force
                $css += "@font-face { font-family: '$family'; font-weight: $w; font-style: normal; font-display: block; src: url('$name') format('woff2'); }`n"
                $added++
            }
        }
        if ($added -gt 0) { $versions += "@fontsource/$id $($meta.version) (licence per package, see licenses-summary.txt, not pinned)" }
        else { Write-Host "Font skipped ($family): no matching files in the package" }
    } catch {
        Write-Host "Font skipped ($family): $($_.Exception.Message)"
    }
}
if ($css.Length -gt 0) { Set-Content -Path (Join-Path $fontDir 'fonts.css') -Value $css -Encoding UTF8 }

$versions | Set-Content (Join-Path $vendor 'versions.txt')
Remove-Item -Recurse -Force $tmp
Write-Host ''
Write-Host 'Done. Versions:'
$versions | ForEach-Object { Write-Host "  $_" }
