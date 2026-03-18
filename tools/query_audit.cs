using System;
using System.Data.SQLite;

class Program
{
    static void Main()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = System.IO.Path.Combine(localAppData, "SirThaddeus", "audit.db");
        using var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;Read Only=True;");
        conn.Open();
        using var cmd = new SQLiteCommand("SELECT timestamp, action, result, details FROM audit_events ORDER BY id DESC LIMIT 50;", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            Console.WriteLine($"{reader["timestamp"]} | {reader["action"]} | {reader["result"]} | {reader["details"]}");
        }
    }
}
