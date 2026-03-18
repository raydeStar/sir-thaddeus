param(
    [string]$DbPath = "$env:LOCALAPPDATA\SirThaddeus\audit.db"
)

$sqliteDll = Join-Path $PSScriptRoot "..\packages\memory-sqlite\SirThaddeus.Memory.Sqlite\bin\Debug\net10.0\System.Data.SQLite.dll"
Add-Type -Path $sqliteDll

$conn = New-Object System.Data.SQLite.SQLiteConnection "Data Source=$DbPath;Version=3;Read Only=True;"
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT timestamp, action, result, details FROM audit_events ORDER BY id DESC LIMIT 50;"
$reader = $cmd.ExecuteReader()

while ($reader.Read()) {
    $timestamp = $reader["timestamp"]
    $action = $reader["action"]
    $result = $reader["result"]
    $details = $reader["details"]
    Write-Host "$timestamp | $action | $result | $details"
}

$conn.Close()
