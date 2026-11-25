# SPDX-License-Identifier: MPL-2.0
# Read-only Git index observation. No checkout or index flag mutation.
function Assert-VeilOrdinaryBuildIndex {
    param([Parameter(Mandatory=$true)][string]$Checkout)
    # Git quotes control characters in filenames, so each output line has one
    # status tag. -v lowercases assume-unchanged tags; S denotes skip-worktree.
    $entries = @(& git -C $Checkout -c core.quotePath=true ls-files -v)
    if ($LASTEXITCODE -ne 0 -or $entries.Count -eq 0) {
        throw 'Build index observation failed or empty.'
    }
    foreach ($entry in $entries) {
        if (-not ($entry -cmatch '^H .+')) {
            throw 'Non-ordinary build index entry refused (including assume-unchanged or skip-worktree).'
        }
    }
}
