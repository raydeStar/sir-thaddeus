#requires -Version 5.1

param(
	[switch]$IncludeKnowledgeStoreHarness
)

# ═══════════════════════════════════════════════════════════════
#  test_all.ps1 — Full test suite in Release with a fresh
#  restore. Use for pre-commit / CI-equivalent validation.
#
#  Later you can chain additional stages here:
#    unit -> integration -> lint
# ═══════════════════════════════════════════════════════════════

& "$PSScriptRoot\test.ps1" -Configuration Release -Restore $true -IncludeKnowledgeStoreHarness:$IncludeKnowledgeStoreHarness
exit $LASTEXITCODE
