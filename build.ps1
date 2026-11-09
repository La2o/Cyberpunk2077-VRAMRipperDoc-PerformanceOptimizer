# Builds the self-contained exe and the Nexus/Vortex-ready zip (game-root layout).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = ([xml](Get-Content "$root/src/VramRipperdoc/VramRipperdoc.csproj")).Project.PropertyGroup.Version | Select-Object -First 1
dotnet publish "$root/src/VramRipperdoc" -c Release -o "$root/dist/bin" -nologo
$stage = "$root/dist/stage"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
$tool = New-Item -ItemType Directory -Force "$stage/tools/VRAM RipperDoc"
Copy-Item "$root/dist/bin/VramRipperdoc.exe", "$root/README.md", "$root/COMPARISON.md", "$root/LICENSE" $tool
Copy-Item -Recurse "$root/package/*" $tool  # double-click shortcuts + START HERE.txt, Linux/ shortcuts
$zip = "$root/dist/VRAM-RipperDoc-Performance-Optimizer-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::Open($zip, "Create")
foreach ($f in Get-ChildItem -Recurse -File $stage) {
    $entry = $f.FullName.Substring((Resolve-Path $stage).Path.Length + 1).Replace("\", "/")  # forward slashes for Vortex/MO2
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $f.FullName, $entry, "Optimal")
}
$z.Dispose()

# Linux: mark the .sh shortcuts executable. A zip made on Windows carries no Unix permissions, so the central directory
# entries of *.sh files are patched to "made by Unix" with mode rwxr-xr-x (unzip, Ark and file-roller honor it).
$bytes = [System.IO.File]::ReadAllBytes($zip)
$eocd = $bytes.Length - 22
while ([BitConverter]::ToUInt32($bytes, $eocd) -ne 0x06054b50) { $eocd-- }
$p = [BitConverter]::ToUInt32($bytes, $eocd + 16)
for ($i = 0; $i -lt [BitConverter]::ToUInt16($bytes, $eocd + 10); $i++) {
    $nameLen = [BitConverter]::ToUInt16($bytes, $p + 28)
    $name = [System.Text.Encoding]::UTF8.GetString($bytes, $p + 46, $nameLen)
    if ($name.EndsWith(".sh")) {
        $bytes[$p + 5] = 3                                                    # version made by: Unix
        $bytes[$p + 38] = 0; $bytes[$p + 39] = 0                              # external attributes: 0100755 << 16
        $bytes[$p + 40] = 0xED; $bytes[$p + 41] = 0x81
    }
    $p += 46 + $nameLen + [BitConverter]::ToUInt16($bytes, $p + 30) + [BitConverter]::ToUInt16($bytes, $p + 32)
}
[System.IO.File]::WriteAllBytes($zip, $bytes)
Write-Host "Built $zip"
