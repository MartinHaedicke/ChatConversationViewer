using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ChatConversationViewer.Models;

/// <summary>
/// Reads Copilot CLI conversations from ~/.copilot/session-store.db (SQLite).
/// The CLI stores sessions and simple user/assistant turns — no thinking blocks
/// or tool details are persisted (as of the current version).
/// </summary>
public static class CopilotCliReader
{
    public static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "session-store.db");

    public static bool IsAvailable => File.Exists(DbPath);

    public sealed record SessionInfo(string Id, string Title, string Directory, DateTime Updated);

    public static List<SessionInfo> ReadSessions()
    {
        var sessions = new List<SessionInfo>();

        using var connection = Open();
        using var command = connection.CreateCommand();
        // skip sessions without any persisted turns — they would render as empty conversations
        command.CommandText =
            """
            SELECT s.id, COALESCE(s.summary,
                       (SELECT substr(t.user_message, 1, 80) FROM turns t
                        WHERE t.session_id = s.id ORDER BY t.turn_index LIMIT 1),
                       s.id),
                   COALESCE(s.cwd, ''), s.updated_at
            FROM sessions s
            WHERE EXISTS (SELECT 1 FROM turns t WHERE t.session_id = s.id)
            ORDER BY s.updated_at DESC
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(new SessionInfo(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ParseTimestamp(reader.GetString(3))));
        }

        return sessions;
    }

    public static List<ConversationEntry> ParseSession(string sessionId)
    {
        var entries = new List<ConversationEntry>();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT user_message, assistant_response, timestamp FROM turns WHERE session_id = $sid ORDER BY turn_index";
        command.Parameters.AddWithValue("$sid", sessionId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var timestamp = reader.IsDBNull(2) ? (DateTime?)null : ParseTimestamp(reader.GetString(2));

            if (!reader.IsDBNull(0) && reader.GetString(0) is { Length: > 0 } userMessage)
                entries.Add(new UserTextEntry(userMessage, timestamp, IsSidechain: false));

            if (!reader.IsDBNull(1) && reader.GetString(1) is { Length: > 0 } assistantResponse)
                entries.Add(new AssistantTextEntry(assistantResponse, timestamp, IsSidechain: false));
        }

        return entries;
    }

    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    private static DateTime ParseTimestamp(string iso)
        => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind).LocalDateTime;
}
