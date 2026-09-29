[CmdletBinding()]
param([string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$venv = Join-Path $PSScriptRoot '.venv'
& $Python -c 'import sys; assert sys.version_info >= (3, 12), "Python 3.12 or newer is required"'
if ($LASTEXITCODE -ne 0) { throw 'Python version check failed.' }
& $Python -m venv $venv
if ($LASTEXITCODE -ne 0) { throw 'Virtual environment creation failed.' }
$capturePython = Join-Path $venv 'Scripts/python.exe'
& $capturePython -m pip install -r (Join-Path $PSScriptRoot 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Capture dependency installation failed.' }
Write-Host "Ready. Run $capturePython tools/capture/run.py --help from the repo root."
