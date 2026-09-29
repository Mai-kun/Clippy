# Prints the Visual Studio installation path that has the C++ toolchain, or nothing.
# Separate from build.cmd because the path lives under "Program Files (x86)", and cmd's parsing of
# that inside a for/f is what turns a working lookup into "vswhere.exe is not recognized".
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { exit 1 }
& $vswhere -latest -products * `
  -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
  -property installationPath