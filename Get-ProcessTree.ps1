<#
.SYNOPSIS
    Show the full process tree (ancestors + descendants) for a given PID.
.EXAMPLE
    .\Get-ProcessTree.ps1 1234
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [int]$Id
)

# Get all processes with parent PID via CIM (fast, single query)
$all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, CommandLine

# Build lookup tables
$byId = @{}
$childrenOf = @{}
foreach ($p in $all) {
    $byId[[int]$p.ProcessId] = $p
    $parentId = [int]$p.ParentProcessId
    if (-not $childrenOf.ContainsKey($parentId)) {
        $childrenOf[$parentId] = [System.Collections.Generic.List[object]]::new()
    }
    $childrenOf[$parentId].Add($p)
}

# Walk ancestors (bottom-up)
$ancestors = [System.Collections.Generic.List[object]]::new()
$current = $Id
while ($byId.ContainsKey($current)) {
    $proc = $byId[$current]
    $parentId = [int]$proc.ParentProcessId
    if ($parentId -eq $current -or $parentId -eq 0) {
        $ancestors.Add($proc)
        break
    }
    $ancestors.Add($proc)
    $current = $parentId
}
$ancestors.Reverse()

# Print ancestors
Write-Host "`n=== Ancestor chain ===" -ForegroundColor Cyan
$depth = 0
foreach ($a in $ancestors) {
    $prefix = '  ' * $depth
    $marker = if ([int]$a.ProcessId -eq $Id) { ' <-- TARGET' } else { '' }
    Write-Host ("{0}[{1}] {2}{3}" -f $prefix, $a.ProcessId, $a.Name, $marker) -ForegroundColor $(if ([int]$a.ProcessId -eq $Id) { 'Yellow' } else { 'Gray' })
    $depth++
}

# Print descendants (recursive)
Write-Host "`n=== Descendants ===" -ForegroundColor Cyan
function Show-Children {
    param([int]$ParentId, [int]$Depth)
    if ($childrenOf.ContainsKey($ParentId)) {
        foreach ($child in $childrenOf[$ParentId]) {
            $cid = [int]$child.ProcessId
            if ($cid -eq $ParentId) { continue }
            $prefix = '  ' * $Depth
            $cmdShort = if ($child.CommandLine.Length -gt 80) { $child.CommandLine.Substring(0, 80) + '...' } else { $child.CommandLine }
            Write-Host ("{0}[{1}] {2}  {3}" -f $prefix, $cid, $child.Name, $cmdShort) -ForegroundColor White
            Show-Children -ParentId $cid -Depth ($Depth + 1)
        }
    }
}

if ($childrenOf.ContainsKey($Id)) {
    Show-Children -ParentId $Id -Depth 1
} else {
    Write-Host "  (no children)" -ForegroundColor DarkGray
}
Write-Host ""
