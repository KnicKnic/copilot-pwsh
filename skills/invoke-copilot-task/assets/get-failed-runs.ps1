param(
    [Parameter(Mandatory=$true)]
    [string]$Repo,

    [Parameter(Mandatory=$false)]
    [int]$Days = 7,

    [Parameter(Mandatory=$false)]
    [string]$Workflow = "",

    [Parameter(Mandatory=$false)]
    [int]$Limit = 100,

    [Parameter(Mandatory=$false)]
    [switch]$IdsOnly
)

$since = (Get-Date).AddDays(-$Days).ToString("yyyy-MM-ddTHH:mm:ssZ")

# Use REST API — older gh CLI versions lack --status/--created flags
$apiPath = "repos/$Repo/actions/runs?status=failure&created=%3E$since&per_page=$Limit"
if ($Workflow) {
    # Resolve workflow ID from name/filename if needed
    $apiPath += "&event=push"
}

$json = gh api $apiPath --paginate 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "gh api failed. Ensure 'gh' is installed and you are authenticated (gh auth login)."
}

$response = $json | ConvertFrom-Json
$runs = $response.workflow_runs
if ($Workflow) {
    $runs = $runs | Where-Object { $_.name -like $Workflow -or $_.path -like "*$Workflow*" }
}

# Map API fields to match expected shape
$runs = $runs | Select-Object -First $Limit | ForEach-Object {
    [PSCustomObject]@{
        databaseId    = $_.id
        workflowName  = $_.name
        headBranch    = $_.head_branch
        headSha       = $_.head_sha
        startedAt     = $_.created_at
        displayTitle  = $_.display_title
    }
}

if ($IdsOnly) {
    $runs | ForEach-Object { Write-Output $_.databaseId }
} else {
    $runs | ForEach-Object {
        [PSCustomObject]@{
            Id           = $_.databaseId
            Workflow     = $_.workflowName
            Branch       = $_.headBranch
            Commit       = $_.headSha.Substring(0, 7)
            StartedAt    = $_.startedAt
            Title        = $_.displayTitle
        }
    } | Format-Table -AutoSize
}
