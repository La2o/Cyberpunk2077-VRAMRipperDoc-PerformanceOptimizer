# Builds the self-contained exe and the Nexus/Vortex-ready zip (game-root layout).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = ([xml](Get-Content "$root/src/VramRipperdoc/VramRipperdoc.csproj")).Project.PropertyGroup.Version | Select-Object -First 1
dotnet publish "$root/src/VramRipperdoc" -c Release -o "$root/dist/bin" -nologo
$stage = "$root/dist/stage"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
$tool = New-Item -ItemType Directory -Force "$stage/tools/VRAM RipperDoc"
Copy-Item "$root/dist/bin/VramRipperdoc.exe", "$root/README.md", "$root/COMPARISON.md", "$root/LICENSE" $tool
$zip = "$root/dist/VRAM-RipperDoc-Performance-Optimizer-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::Open($zip, "Create")
foreach ($f in Get-ChildItem -Recurse -File $stage) {
    $entry = $f.FullName.Substring((Resolve-Path $stage).Path.Length + 1).Replace("\", "/")  # forward slashes for Vortex/MO2
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $f.FullName, $entry, "Optimal")
}
$z.Dispose()
Write-Host "Built $zip"
