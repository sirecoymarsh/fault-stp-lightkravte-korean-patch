param(
    [string]$GameRoot = 'Y:\GAME\Fault StP LIGHTKRAVTE',
    [string]$BackupRoot = '',
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$arguments = @{
    Action = 'Install'
    GameRoot = $GameRoot
    Json = $Json
}
if (-not [string]::IsNullOrWhiteSpace($BackupRoot)) {
    $arguments.BackupRoot = $BackupRoot
}
& (Join-Path $PSScriptRoot 'Patch-Korean.ps1') @arguments
