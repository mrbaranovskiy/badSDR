using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Dmr;
using Microsoft.Data.Sqlite;
using BadSdr.Core.Pipeline;

namespace BadSdr.Core.Storage;

public sealed record CapturedEvent(string RunId, ListenChannel Channel, DateTimeOffset CaptureTime, DmrEvent Event);

public sealed record SessionRow(string SessionId, string RunId, int ChannelId, double FrequencyHz,
    DateTimeOffset StartedUtc, DateTimeOffset UpdatedUtc, double DurationSeconds, int Track, int? Slot,
    string? ColourCode, string? SourceId, string? DestinationId, string Privacy, string State,
    string? EndReason, long Frames, string MetadataJson)
{
    public string Frequency => (FrequencyHz / 1e6).ToString("F6", CultureInfo.InvariantCulture);
    public string Started => StartedUtc.ToLocalTime().ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string SlotLabel => Slot?.ToString(CultureInfo.InvariantCulture) ?? $"? (track {Track})";
    public string Duration => $"{DurationSeconds:F2} s";
}

/// <summary>One bounded Channel feeds one SQLite writer. Decoder callbacks never perform I/O.</summary>
public sealed class SessionStore : IAsyncDisposable
{
    private abstract record Message;
    private sealed record EventMessage(CapturedEvent Value) : Message;
    private sealed record Barrier(TaskCompletionSource Completion) : Message;
    private sealed record RunEnd(string RunId, string Configuration, string Plan, string Metrics, string? Error) : Message;
    private sealed class ActiveSession(CapturedEvent item)
    {
        public readonly CapturedEvent First = item;
        public readonly Dictionary<string, object?> Metadata = new();
    }

    private readonly Channel<Message> queue;
    private readonly SqliteConnection connection;
    private readonly Task worker;
    private Exception? failure;
    private long written;
    private readonly string connectionString;
    private readonly FileStream writerLease;
    public string DatabasePath { get; }
    public long Written => Interlocked.Read(ref written);
    public int Pending => queue.Reader.Count;
    public Exception? Failure => Volatile.Read(ref failure);

    public SessionStore(string path, int capacity = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        DatabasePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        // The file handle, not a thread-affine mutex, owns the single-writer application lease.
        try { writerLease = new FileStream(DatabasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("This session database is already open in another badSDR instance, or its lock file cannot be opened.", error); }
        connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, DefaultTimeout = 5 }.ToString();
        connection = new(connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                CREATE TABLE IF NOT EXISTS events (
                    id INTEGER PRIMARY KEY, run_id TEXT NOT NULL, channel_id INTEGER NOT NULL,
                    frequency_hz REAL NOT NULL, received_utc TEXT NOT NULL, type TEXT NOT NULL,
                    session_id TEXT, rf_seconds REAL NOT NULL, json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_events_session ON events(session_id, id);
                CREATE INDEX IF NOT EXISTS ix_events_run ON events(run_id, id);
                CREATE TABLE IF NOT EXISTS sessions (
                    session_id TEXT PRIMARY KEY, run_id TEXT NOT NULL, channel_id INTEGER NOT NULL,
                    frequency_hz REAL NOT NULL, started_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                    duration_seconds REAL NOT NULL, track INTEGER NOT NULL, slot INTEGER,
                    colour_code TEXT, source_id TEXT, destination_id TEXT, privacy TEXT NOT NULL,
                    state TEXT NOT NULL, end_reason TEXT, frames INTEGER NOT NULL, metadata_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_sessions_time ON sessions(updated_utc DESC);
                CREATE TABLE IF NOT EXISTS runs (run_id TEXT PRIMARY KEY, ended_utc TEXT NOT NULL,
                    configuration_json TEXT NOT NULL, plan TEXT NOT NULL, metrics_json TEXT NOT NULL, error TEXT);
                UPDATE sessions SET state='Interrupted',end_reason='application_restart' WHERE state='Active';
                """;
            command.ExecuteNonQuery();
        }
        catch { connection.Dispose(); writerLease.Dispose(); throw; }
        queue = Channel.CreateBounded<Message>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        worker = Task.Run(WriteLoopAsync);
    }

    public void Record(CapturedEvent item)
    {
        ThrowIfFaulted();
        // These high-volume DSP/voice payloads are deliberately excluded from this metadata logger.
        if (item.Event.Type is "vocoder_frame" or "burst" or "symbols") return;
        if (!queue.Writer.TryWrite(new EventMessage(item)))
            throw new IOException("Session database queue is full or closed. Capture stopped to avoid silently losing session metadata.");
    }

    public void ThrowIfFaulted()
    {
        if (Failure is { } error) throw new IOException("Session database writer failed.", error);
    }

    public async Task FlushAsync()
    {
        ThrowIfFaulted();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new Barrier(completion));
        await completion.Task;
        ThrowIfFaulted();
    }

    public async Task EndRunAsync(string runId, RadioSettings settings, IReadOnlyList<ListenChannel> channels,
        string plan, MetricsSnapshot metrics, string? error)
    {
        ThrowIfFaulted();
        await queue.Writer.WriteAsync(new RunEnd(runId, JsonSerializer.Serialize(new { settings, channels }),
            plan, JsonSerializer.Serialize(metrics), error));
    }

    private async Task WriteLoopAsync()
    {
        var active = new Dictionary<string, ActiveSession>();
        var barriers = new List<TaskCompletionSource>();
        try
        {
            while (await queue.Reader.WaitToReadAsync())
            {
                using var transaction = connection.BeginTransaction();
                int count = 0;
                int eventCount = 0;
                while (count++ < 256 && queue.Reader.TryRead(out var message))
                {
                    if (message is Barrier barrier) { barriers.Add(barrier.Completion); continue; }
                    if (message is RunEnd run)
                    {
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = """
                            INSERT OR REPLACE INTO runs VALUES ($run,$time,$config,$plan,$metrics,$error);
                            UPDATE sessions SET state='Interrupted',end_reason='capture_stopped'
                            WHERE run_id=$run AND state='Active';
                            """;
                        Add(command, "$run", run.RunId); Add(command, "$time", DateTimeOffset.UtcNow.ToString("O"));
                        Add(command, "$config", run.Configuration); Add(command, "$plan", run.Plan);
                        Add(command, "$metrics", run.Metrics); Add(command, "$error", run.Error);
                        command.ExecuteNonQuery();
                        foreach (var key in active.Where(p => p.Value.First.RunId == run.RunId).Select(p => p.Key).ToArray()) active.Remove(key);
                        continue;
                    }
                    var item = ((EventMessage)message).Value;
                    WriteEvent(item, transaction);
                    eventCount++;
                    var ev = item.Event;
                    if (ev.SessionId is not null && ev.Type is "call_start" or "call_update" or "call_end")
                    {
                        if (!active.TryGetValue(ev.SessionId, out var session))
                            active[ev.SessionId] = session = new(item);
                        foreach (var pair in ev.Data) session.Metadata[pair.Key] = pair.Value;
                        WriteSession(item, session, transaction);
                        if (ev.Type == "call_end") active.Remove(ev.SessionId);
                    }
                }
                transaction.Commit();
                Interlocked.Add(ref written, eventCount);
                foreach (var barrier in barriers) barrier.TrySetResult();
                barriers.Clear();
            }
        }
        catch (Exception error)
        {
            Volatile.Write(ref failure, error);
            queue.Writer.TryComplete(error);
            foreach (var barrier in barriers) barrier.TrySetException(error);
            while (queue.Reader.TryRead(out var pending))
                if (pending is Barrier barrier) barrier.Completion.TrySetException(error);
        }
        finally { connection.Dispose(); }
    }

    private void WriteEvent(CapturedEvent item, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO events (run_id,channel_id,frequency_hz,received_utc,type,session_id,rf_seconds,json)
            VALUES ($run,$channel,$frequency,$utc,$type,$session,$rf,$json);
            """;
        Add(command, "$run", item.RunId); Add(command, "$channel", item.Channel.Id);
        Add(command, "$frequency", item.Channel.FrequencyHz); Add(command, "$utc", DateTimeOffset.UtcNow.ToString("O"));
        Add(command, "$type", item.Event.Type); Add(command, "$session", item.Event.SessionId);
        Add(command, "$rf", item.Event.RfTimeSeconds); Add(command, "$json", item.Event.ToJson());
        command.ExecuteNonQuery();
    }

    private void WriteSession(CapturedEvent item, ActiveSession session, SqliteTransaction transaction)
    {
        var ev = item.Event;
        var first = session.First;
        object? Field(string key) => session.Metadata.GetValueOrDefault(key);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions VALUES ($id,$run,$channel,$frequency,$start,$updated,$duration,$track,$slot,
                $colour,$source,$destination,$privacy,$state,$reason,$frames,$metadata)
            ON CONFLICT(session_id) DO UPDATE SET updated_utc=excluded.updated_utc,
                duration_seconds=excluded.duration_seconds, slot=excluded.slot, colour_code=excluded.colour_code,
                source_id=excluded.source_id, destination_id=excluded.destination_id, privacy=excluded.privacy,
                state=excluded.state, end_reason=excluded.end_reason, frames=excluded.frames, metadata_json=excluded.metadata_json;
            """;
        Add(command, "$id", ev.SessionId); Add(command, "$run", item.RunId); Add(command, "$channel", item.Channel.Id);
        Add(command, "$frequency", item.Channel.FrequencyHz);
        Add(command, "$start", (first.CaptureTime + TimeSpan.FromSeconds(first.Event.RfTimeSeconds)).ToString("O"));
        Add(command, "$updated", (item.CaptureTime + TimeSpan.FromSeconds(ev.RfTimeSeconds)).ToString("O"));
        Add(command, "$duration", Math.Max(0, ev.RfTimeSeconds - first.Event.RfTimeSeconds));
        Add(command, "$track", ev.Track); Add(command, "$slot", ev.Slot);
        Add(command, "$colour", Field("colour_code")?.ToString()); Add(command, "$source", Field("source_id")?.ToString());
        Add(command, "$destination", Field("destination_id")?.ToString());
        Add(command, "$privacy", Field("privacy_status")?.ToString() ?? "unknown");
        Add(command, "$state", ev.Type == "call_end" ? "Ended" : "Active");
        Add(command, "$reason", ev.Type == "call_end" ? Field("reason")?.ToString() : null);
        Add(command, "$frames", Field("frames") ?? 0);
        Add(command, "$metadata", JsonSerializer.Serialize(session.Metadata));
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string key, object? value) => command.Parameters.AddWithValue(key, value ?? DBNull.Value);

    public async Task<IReadOnlyList<SessionRow>> ReadRecentAsync(int limit = 500)
    {
        return await Task.Run<IReadOnlyList<SessionRow>>(() =>
        {
            using var readerConnection = new SqliteConnection(connectionString);
            readerConnection.Open();
            using var command = readerConnection.CreateCommand();
            command.CommandText = "SELECT * FROM sessions ORDER BY updated_utc DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
            using var reader = command.ExecuteReader();
            var rows = new List<SessionRow>();
            string? Optional(int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetDouble(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture), reader.GetDouble(6), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8), Optional(9), Optional(10), Optional(11), reader.GetString(12),
                reader.GetString(13), Optional(14), reader.GetInt64(15), reader.GetString(16)));
            return rows;
        });
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try { await worker; }
        finally { writerLease.Dispose(); }
        ThrowIfFaulted();
    }
}
