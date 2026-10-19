using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TicketShine.Support.Core;
using TicketShine.Support.Core.Domain;
using TicketShine.Support.Core.Email;

namespace TicketShine.Support.Infrastructure.Persistence;

/// <summary>
/// SQLite persistence. Every important state transition is a conditional UPDATE (state + version) in a
/// BEGIN IMMEDIATE transaction; a unique key on (CaseId, DraftVersion) ensures the outbox entry is created only once.
/// </summary>
public sealed class SqliteCaseRepository(string databasePath) : ICaseRepository, IMailStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        DefaultTimeout = 30,
        Pooling = true,
    }.ToString();

    public string DatabasePath { get; } = databasePath;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var conn = await OpenAsync(ct);
        await ExecAsync(conn, null, "PRAGMA journal_mode=WAL;", ct);
        await ExecAsync(conn, null, Schema, ct);

        // Additive migrations for databases created by an earlier version of the sample.
        await EnsureColumnAsync(conn, "Cases", "Channel", "TEXT NOT NULL DEFAULT 'Manual'", ct);
        await EnsureColumnAsync(conn, "Cases", "ChannelThreadId", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "Cases", "SenderEmail", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "Cases", "SenderAuthenticated", "INTEGER NOT NULL DEFAULT 0", ct);
        await EnsureColumnAsync(conn, "OutboxMessages", "ThreadId", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "OutboxMessages", "InReplyTo", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "OutboxMessages", "RefHeader", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "SimulatedMailbox", "ThreadId", "TEXT NULL", ct);
        await EnsureColumnAsync(conn, "SimulatedMailbox", "InReplyTo", "TEXT NULL", ct);
        await ExecAsync(conn, null, "CREATE INDEX IF NOT EXISTS IX_Cases_Thread ON Cases(ChannelThreadId);", ct);
    }

    private static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string definition, CancellationToken ct)
    {
        await using var cmd = Command(conn, null, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$col;", ("$col", column));
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 0)
        {
            await ExecAsync(conn, null, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", ct);
        }
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS Cases (
            Id TEXT PRIMARY KEY,
            OrderId TEXT NOT NULL,
            CustomerMessage TEXT NOT NULL,
            State TEXT NOT NULL,
            Classification TEXT NOT NULL,
            StateReason TEXT NULL,
            CurrentDraftVersion INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS EvidenceSnapshots (
            Id TEXT PRIMARY KEY,
            CaseId TEXT NOT NULL REFERENCES Cases(Id),
            ToolName TEXT NOT NULL,
            SourceId TEXT NOT NULL,
            Status TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            RetrievedAt TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS ToolCalls (
            Id TEXT PRIMARY KEY,
            CaseId TEXT NOT NULL REFERENCES Cases(Id),
            Seq INTEGER NOT NULL,
            ToolName TEXT NOT NULL,
            ArgumentsJson TEXT NOT NULL,
            Status TEXT NOT NULL,
            DurationMs INTEGER NOT NULL,
            StartedAt TEXT NOT NULL,
            Origin TEXT NOT NULL,
            SourceId TEXT NULL,
            Error TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS Drafts (
            Id TEXT PRIMARY KEY,
            CaseId TEXT NOT NULL REFERENCES Cases(Id),
            Version INTEGER NOT NULL,
            Recipient TEXT NOT NULL,
            Subject TEXT NOT NULL,
            Body TEXT NOT NULL,
            Source TEXT NOT NULL,
            Kind TEXT NOT NULL,
            EvidenceRefsJson TEXT NOT NULL,
            WarningsJson TEXT NOT NULL,
            CreatedBy TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UNIQUE (CaseId, Version)
        );
        CREATE TABLE IF NOT EXISTS ApprovalEvents (
            Id TEXT PRIMARY KEY,
            CaseId TEXT NOT NULL REFERENCES Cases(Id),
            DraftId TEXT NOT NULL REFERENCES Drafts(Id),
            DraftVersion INTEGER NOT NULL,
            Decision TEXT NOT NULL,
            ReviewerLabel TEXT NOT NULL,
            Reason TEXT NULL,
            At TEXT NOT NULL,
            UNIQUE (CaseId, DraftVersion)
        );
        CREATE TABLE IF NOT EXISTS OutboxMessages (
            Id TEXT PRIMARY KEY,
            CaseId TEXT NOT NULL REFERENCES Cases(Id),
            DraftId TEXT NOT NULL REFERENCES Drafts(Id),
            DraftVersion INTEGER NOT NULL,
            IdempotencyKey TEXT NOT NULL UNIQUE,
            Recipient TEXT NOT NULL,
            Subject TEXT NOT NULL,
            Body TEXT NOT NULL,
            Status TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            SentAt TEXT NULL,
            UNIQUE (CaseId, DraftVersion)
        );
        CREATE TABLE IF NOT EXISTS SimulatedMailbox (
            IdempotencyKey TEXT PRIMARY KEY,
            CaseId TEXT NOT NULL,
            Recipient TEXT NOT NULL,
            Subject TEXT NOT NULL,
            Body TEXT NOT NULL,
            WrittenAt TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS AuditEvents (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CaseId TEXT NOT NULL,
            EventType TEXT NOT NULL,
            Actor TEXT NOT NULL,
            Detail TEXT NOT NULL,
            At TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_Evidence_Case ON EvidenceSnapshots(CaseId);
        CREATE INDEX IF NOT EXISTS IX_ToolCalls_Case ON ToolCalls(CaseId, Seq);
        CREATE INDEX IF NOT EXISTS IX_Audit_Case ON AuditEvents(CaseId, Id);
        CREATE INDEX IF NOT EXISTS IX_Outbox_Status ON OutboxMessages(Status);
        CREATE TABLE IF NOT EXISTS CaseMessages (
            Id TEXT PRIMARY KEY,
            CaseId TEXT NOT NULL REFERENCES Cases(Id),
            Direction TEXT NOT NULL,
            Provider TEXT NOT NULL,
            ProviderMessageId TEXT NULL,
            RfcMessageId TEXT NULL,
            FromAddress TEXT NOT NULL,
            ToAddress TEXT NOT NULL,
            Subject TEXT NOT NULL,
            Body TEXT NOT NULL,
            AuthSummary TEXT NULL,
            At TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS IX_CaseMessages_Case ON CaseMessages(CaseId, At);
        CREATE TABLE IF NOT EXISTS InboundMessages (
            Provider TEXT NOT NULL,
            ProviderMessageId TEXT NOT NULL,
            ThreadId TEXT NULL,
            RfcMessageId TEXT NULL,
            FromAddress TEXT NOT NULL,
            Subject TEXT NOT NULL,
            ReceivedAt TEXT NOT NULL,
            Outcome TEXT NOT NULL,
            CaseId TEXT NULL,
            ProcessedAt TEXT NULL,
            PRIMARY KEY (Provider, ProviderMessageId)
        );
        CREATE TABLE IF NOT EXISTS MailSyncState (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL
        );
        """;

    public async Task CreateCaseAsync(SupportCase c, string actor, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await ExecAsync(conn, tx, """
            INSERT INTO Cases (Id, OrderId, CustomerMessage, State, Classification, StateReason, CurrentDraftVersion, CreatedAt, UpdatedAt,
                               Channel, ChannelThreadId, SenderEmail, SenderAuthenticated)
            VALUES ($id, $order, $msg, $state, $class, $reason, $ver, $created, $updated, $channel, $thread, $sender, $auth);
            """, ct,
            ("$id", c.Id.ToString()), ("$order", c.OrderId), ("$msg", c.CustomerMessage), ("$state", c.State.ToString()),
            ("$class", c.Classification.ToString()), ("$reason", c.StateReason), ("$ver", c.CurrentDraftVersion),
            ("$created", Ts(c.CreatedAtUtc)), ("$updated", Ts(c.UpdatedAtUtc)), ("$channel", c.Channel.ToString()),
            ("$thread", c.ChannelThreadId), ("$sender", c.SenderEmail), ("$auth", c.SenderAuthenticated ? 1 : 0));
        await AuditAsync(conn, tx, c.Id, "CaseCreated", actor, $"Case created for order {c.OrderId} ({c.Channel})", c.CreatedAtUtc, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<SupportCase?> GetCaseAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await ReadCaseAsync(conn, null, caseId, ct);
    }

    public async Task<IReadOnlyList<SupportCase>> ListCasesAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, null, "SELECT * FROM Cases ORDER BY CreatedAt DESC LIMIT $limit;", ("$limit", limit));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SupportCase>();
        while (await r.ReadAsync(ct)) list.Add(MapCase(r));
        return list;
    }

    public async Task<CaseDetails?> GetDetailsAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var c = await ReadCaseAsync(conn, null, caseId, ct);
        if (c is null) return null;
        var id = caseId.ToString();

        var evidence = await QueryAsync(conn, "SELECT * FROM EvidenceSnapshots WHERE CaseId=$id ORDER BY RetrievedAt;", r => new EvidenceSnapshot(
            Guid.Parse(r.GetString(r.GetOrdinal("Id"))), caseId, Str(r, "ToolName"), Str(r, "SourceId"),
            Enum.Parse<LookupStatus>(Str(r, "Status")), Str(r, "PayloadJson"), ParseTs(Str(r, "RetrievedAt"))), id, ct);

        var calls = await QueryAsync(conn, "SELECT * FROM ToolCalls WHERE CaseId=$id ORDER BY Seq;", r => new StoredToolCall(
            Guid.Parse(Str(r, "Id")), caseId, new ToolCallRecord(Str(r, "ToolName"), Str(r, "ArgumentsJson"),
                Enum.Parse<LookupStatus>(Str(r, "Status")), r.GetInt64(r.GetOrdinal("DurationMs")), ParseTs(Str(r, "StartedAt")),
                Str(r, "Origin"), NStr(r, "SourceId"), NStr(r, "Error"))), id, ct);

        var drafts = await QueryAsync(conn, "SELECT * FROM Drafts WHERE CaseId=$id ORDER BY Version;", MapDraft, id, ct);

        var approvals = await QueryAsync(conn, "SELECT * FROM ApprovalEvents WHERE CaseId=$id ORDER BY At;", r => new ApprovalEvent(
            Guid.Parse(Str(r, "Id")), caseId, Guid.Parse(Str(r, "DraftId")), r.GetInt32(r.GetOrdinal("DraftVersion")),
            Str(r, "Decision"), Str(r, "ReviewerLabel"), NStr(r, "Reason"), ParseTs(Str(r, "At"))), id, ct);

        var outbox = await QueryAsync(conn, "SELECT * FROM OutboxMessages WHERE CaseId=$id ORDER BY CreatedAt;", MapOutbox, id, ct);

        var audit = await QueryAsync(conn, "SELECT * FROM AuditEvents WHERE CaseId=$id ORDER BY Id;", r => new AuditEvent(
            r.GetInt64(r.GetOrdinal("Id")), caseId, Str(r, "EventType"), Str(r, "Actor"), Str(r, "Detail"), ParseTs(Str(r, "At"))), id, ct);

        var messages = await QueryAsync(conn, "SELECT * FROM CaseMessages WHERE CaseId=$id ORDER BY At;", MapMessage, id, ct);

        return new CaseDetails(c, evidence, calls, drafts, approvals, outbox, audit, messages);
    }

    public async Task<bool> TransitionAsync(
        Guid caseId,
        IReadOnlyCollection<CaseState> from,
        CaseState to,
        Classification? classification,
        string? reason,
        string actor,
        string auditDetail,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var c = await ReadCaseAsync(conn, tx, caseId, ct);
        if (c is null || !from.Contains(c.State)) return false;

        var now = DateTimeOffset.UtcNow;
        var rows = await ExecAsync(conn, tx, """
            UPDATE Cases SET State=$to, Classification=$class, StateReason=$reason, UpdatedAt=$now
            WHERE Id=$id AND State=$from;
            """, ct,
            ("$to", to.ToString()), ("$class", (classification ?? c.Classification).ToString()), ("$reason", reason ?? c.StateReason),
            ("$now", Ts(now)), ("$id", caseId.ToString()), ("$from", c.State.ToString()));
        if (rows != 1) return false;

        var eventType = to == c.State ? "CaseUpdated" : $"StateChanged:{c.State}->{to}";
        await AuditAsync(conn, tx, caseId, eventType, actor, auditDetail, now, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task SaveLookupDataAsync(Guid caseId, IReadOnlyList<EvidenceSnapshot> evidence, IReadOnlyList<ToolCallRecord> toolCalls, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        foreach (var e in evidence)
        {
            await ExecAsync(conn, tx, """
                INSERT INTO EvidenceSnapshots (Id, CaseId, ToolName, SourceId, Status, PayloadJson, RetrievedAt)
                VALUES ($id, $case, $tool, $source, $status, $payload, $at);
                """, ct,
                ("$id", e.Id.ToString()), ("$case", caseId.ToString()), ("$tool", e.ToolName), ("$source", e.SourceId),
                ("$status", e.Status.ToString()), ("$payload", e.PayloadJson), ("$at", Ts(e.RetrievedAtUtc)));
        }

        var seq = Convert.ToInt32(await ScalarAsync(conn, tx, "SELECT COALESCE(MAX(Seq), 0) FROM ToolCalls WHERE CaseId=$case;", ct, ("$case", caseId.ToString())));
        foreach (var t in toolCalls)
        {
            await ExecAsync(conn, tx, """
                INSERT INTO ToolCalls (Id, CaseId, Seq, ToolName, ArgumentsJson, Status, DurationMs, StartedAt, Origin, SourceId, Error)
                VALUES ($id, $case, $seq, $tool, $args, $status, $dur, $started, $origin, $source, $error);
                """, ct,
                ("$id", Guid.NewGuid().ToString()), ("$case", caseId.ToString()), ("$seq", ++seq), ("$tool", t.ToolName),
                ("$args", t.ArgumentsJson), ("$status", t.Status.ToString()), ("$dur", t.DurationMs), ("$started", Ts(t.StartedAtUtc)),
                ("$origin", t.Origin), ("$source", t.SourceId), ("$error", t.Error));
        }

        if (toolCalls.Count > 0)
        {
            var summary = string.Join(", ", toolCalls.Select(t => $"{t.ToolName}={t.Status}"));
            await AuditAsync(conn, tx, caseId, "ToolCalls", toolCalls[0].Origin, summary, DateTimeOffset.UtcNow, ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<bool> SaveDraftAsync(Draft draft, int expectedCurrentVersion, IReadOnlyCollection<CaseState> allowedStates, string auditDetail, CancellationToken ct = default)
    {
        if (draft.Version != expectedCurrentVersion + 1) throw new ArgumentException("Draft version must be expected + 1.", nameof(draft));

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var c = await ReadCaseAsync(conn, tx, draft.CaseId, ct);
        if (c is null || !allowedStates.Contains(c.State) || c.CurrentDraftVersion != expectedCurrentVersion) return false;

        await ExecAsync(conn, tx, """
            INSERT INTO Drafts (Id, CaseId, Version, Recipient, Subject, Body, Source, Kind, EvidenceRefsJson, WarningsJson, CreatedBy, CreatedAt)
            VALUES ($id, $case, $ver, $to, $subject, $body, $source, $kind, $refs, $warn, $by, $at);
            """, ct,
            ("$id", draft.Id.ToString()), ("$case", draft.CaseId.ToString()), ("$ver", draft.Version), ("$to", draft.Recipient),
            ("$subject", draft.Subject), ("$body", draft.Body), ("$source", draft.Source.ToString()), ("$kind", draft.Kind.ToString()),
            ("$refs", JsonSerializer.Serialize(draft.EvidenceRefs)), ("$warn", JsonSerializer.Serialize(draft.Warnings)),
            ("$by", draft.CreatedBy), ("$at", Ts(draft.CreatedAtUtc)));

        var rows = await ExecAsync(conn, tx, """
            UPDATE Cases SET State=$state, CurrentDraftVersion=$ver, StateReason=$reason, UpdatedAt=$now
            WHERE Id=$id AND State=$from AND CurrentDraftVersion=$expected;
            """, ct,
            ("$state", CaseState.AwaitingReview.ToString()), ("$ver", draft.Version),
            // Leaving NeedsHuman: the old reason no longer describes the case.
            ("$reason", c.State == CaseState.NeedsHuman ? $"Draft v{draft.Version} created on request by {draft.CreatedBy}. Previously: {c.StateReason}" : c.StateReason),
            ("$now", Ts(DateTimeOffset.UtcNow)),
            ("$id", draft.CaseId.ToString()), ("$from", c.State.ToString()), ("$expected", expectedCurrentVersion));
        if (rows != 1) return false;

        await AuditAsync(conn, tx, draft.CaseId, c.State == CaseState.AwaitingReview ? "DraftVersionCreated" : $"StateChanged:{c.State}->AwaitingReview",
            draft.CreatedBy, auditDetail, draft.CreatedAtUtc, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<ReviewResult> ApproveAsync(Guid caseId, int draftVersion, string reviewerLabel, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var c = await ReadCaseAsync(conn, tx, caseId, ct);
        if (c is null) return new(ApprovalOutcome.NotFound, "Case not found.");
        if (c.State != CaseState.AwaitingReview || c.CurrentDraftVersion != draftVersion)
        {
            return new(ApprovalOutcome.Conflict,
                $"Conflict: the case is now {c.State} with draft v{c.CurrentDraftVersion}; you reviewed v{draftVersion}. Reload to see the latest state.");
        }

        var draft = await ReadDraftAsync(conn, tx, caseId, draftVersion, ct);
        if (draft is null) return new(ApprovalOutcome.Conflict, "Draft not found.");

        var rows = await ExecAsync(conn, tx, """
            UPDATE Cases SET State=$approved, UpdatedAt=$now
            WHERE Id=$id AND State=$awaiting AND CurrentDraftVersion=$ver;
            """, ct,
            ("$approved", CaseState.Approved.ToString()), ("$now", Ts(at)), ("$id", caseId.ToString()),
            ("$awaiting", CaseState.AwaitingReview.ToString()), ("$ver", draftVersion));
        if (rows != 1) return new(ApprovalOutcome.Conflict, "Conflict: the case changed. Reload to see the latest state.");

        await ExecAsync(conn, tx, """
            INSERT INTO ApprovalEvents (Id, CaseId, DraftId, DraftVersion, Decision, ReviewerLabel, Reason, At)
            VALUES ($id, $case, $draft, $ver, 'Approved', $reviewer, NULL, $at);
            """, ct,
            ("$id", Guid.NewGuid().ToString()), ("$case", caseId.ToString()), ("$draft", draft.Id.ToString()), ("$ver", draftVersion),
            ("$reviewer", reviewerLabel), ("$at", Ts(at)));

        // Email cases reply in the customer's thread: In-Reply-To = latest customer message, References = whole conversation.
        var rfcIds = new List<(string Direction, string Id)>();
        await using (var cmd = Command(conn, tx, "SELECT Direction, RfcMessageId FROM CaseMessages WHERE CaseId=$id AND RfcMessageId IS NOT NULL ORDER BY At;",
                         ("$id", caseId.ToString())))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) rfcIds.Add((r.GetString(0), r.GetString(1)));
        }

        var inReplyTo = rfcIds.LastOrDefault(x => x.Direction == nameof(MessageDirection.Inbound)).Id;
        var references = rfcIds.Count == 0 ? null : string.Join(' ', rfcIds.Select(x => x.Id));

        // Unique (CaseId, DraftVersion) + IdempotencyKey: the outbox entry is created exactly once.
        await ExecAsync(conn, tx, """
            INSERT INTO OutboxMessages (Id, CaseId, DraftId, DraftVersion, IdempotencyKey, Recipient, Subject, Body, Status, CreatedAt, SentAt,
                                        ThreadId, InReplyTo, RefHeader)
            VALUES ($id, $case, $draft, $ver, $key, $to, $subject, $body, $status, $at, NULL, $thread, $inreplyto, $refs);
            """, ct,
            ("$id", Guid.NewGuid().ToString()), ("$case", caseId.ToString()), ("$draft", draft.Id.ToString()), ("$ver", draftVersion),
            ("$key", IdempotencyKey(caseId, draftVersion)), ("$to", draft.Recipient), ("$subject", draft.Subject), ("$body", draft.Body),
            ("$status", OutboxStatuses.Pending), ("$at", Ts(at)), ("$thread", c.ChannelThreadId), ("$inreplyto", inReplyTo), ("$refs", references));

        await AuditAsync(conn, tx, caseId, "StateChanged:AwaitingReview->Approved", reviewerLabel,
            $"Draft v{draftVersion} approved by '{reviewerLabel}' (demo label, not an authenticated identity). Send job queued.", at, ct);
        await tx.CommitAsync(ct);
        return new(ApprovalOutcome.Approved, $"Approved draft v{draftVersion}. Send queued.");
    }

    public async Task<ReviewResult> RejectAsync(Guid caseId, int draftVersion, string reviewerLabel, string? reason, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var c = await ReadCaseAsync(conn, tx, caseId, ct);
        if (c is null) return new(ApprovalOutcome.NotFound, "Case not found.");
        if (c.State != CaseState.AwaitingReview || c.CurrentDraftVersion != draftVersion)
        {
            return new(ApprovalOutcome.Conflict,
                $"Conflict: the case is now {c.State} with draft v{c.CurrentDraftVersion}; you reviewed v{draftVersion}. Reload to see the latest state.");
        }

        var draft = await ReadDraftAsync(conn, tx, caseId, draftVersion, ct);
        if (draft is null) return new(ApprovalOutcome.Conflict, "Draft not found.");

        var rows = await ExecAsync(conn, tx, """
            UPDATE Cases SET State=$rejected, StateReason=$reason, UpdatedAt=$now
            WHERE Id=$id AND State=$awaiting AND CurrentDraftVersion=$ver;
            """, ct,
            ("$rejected", CaseState.Rejected.ToString()), ("$reason", reason), ("$now", Ts(at)), ("$id", caseId.ToString()),
            ("$awaiting", CaseState.AwaitingReview.ToString()), ("$ver", draftVersion));
        if (rows != 1) return new(ApprovalOutcome.Conflict, "Conflict: the case changed. Reload to see the latest state.");

        await ExecAsync(conn, tx, """
            INSERT INTO ApprovalEvents (Id, CaseId, DraftId, DraftVersion, Decision, ReviewerLabel, Reason, At)
            VALUES ($id, $case, $draft, $ver, 'Rejected', $reviewer, $reason, $at);
            """, ct,
            ("$id", Guid.NewGuid().ToString()), ("$case", caseId.ToString()), ("$draft", draft.Id.ToString()), ("$ver", draftVersion),
            ("$reviewer", reviewerLabel), ("$reason", reason), ("$at", Ts(at)));

        await AuditAsync(conn, tx, caseId, "StateChanged:AwaitingReview->Rejected", reviewerLabel,
            $"Draft v{draftVersion} rejected by '{reviewerLabel}'. Reason: {reason ?? "(none)"}. No message queued.", at, ct);
        await tx.CommitAsync(ct);
        return new(ApprovalOutcome.Rejected, $"Rejected draft v{draftVersion}. Nothing will be sent.");
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingOutboxAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, null, "SELECT * FROM OutboxMessages WHERE Status=$s ORDER BY CreatedAt;", ("$s", OutboxStatuses.Pending));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<OutboxMessage>();
        while (await r.ReadAsync(ct)) list.Add(MapOutbox(r));
        return list;
    }

    public async Task<bool> WriteSimulatedMessageAsync(SimulatedMessage m, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var rows = await ExecAsync(conn, null, """
            INSERT OR IGNORE INTO SimulatedMailbox (IdempotencyKey, CaseId, Recipient, Subject, Body, WrittenAt, ThreadId, InReplyTo)
            VALUES ($key, $case, $to, $subject, $body, $at, $thread, $inreplyto);
            """, ct,
            ("$key", m.IdempotencyKey), ("$case", m.CaseId.ToString()), ("$to", m.Recipient), ("$subject", m.Subject),
            ("$body", m.Body), ("$at", Ts(m.WrittenAtUtc)), ("$thread", m.ThreadId), ("$inreplyto", m.InReplyTo));
        return rows == 1;
    }

    public async Task<bool> MarkOutboxSentAsync(Guid outboxId, DateTimeOffset at, string senderName, bool simulated, string? providerMessageId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        OutboxMessage? job;
        await using (var cmd = Command(conn, tx, "SELECT * FROM OutboxMessages WHERE Id=$id AND Status=$pending;",
                         ("$id", outboxId.ToString()), ("$pending", OutboxStatuses.Pending)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            job = await r.ReadAsync(ct) ? MapOutbox(r) : null;
        }

        if (job is null) return false;

        var target = simulated ? CaseState.SimulatedSent : CaseState.Sent;
        await ExecAsync(conn, tx, "UPDATE OutboxMessages SET Status=$sent, SentAt=$at WHERE Id=$id AND Status=$pending;", ct,
            ("$sent", OutboxStatuses.Sent), ("$at", Ts(at)), ("$id", outboxId.ToString()), ("$pending", OutboxStatuses.Pending));
        await ExecAsync(conn, tx, "UPDATE Cases SET State=$done, UpdatedAt=$at WHERE Id=$case AND State=$approved;", ct,
            ("$done", target.ToString()), ("$at", Ts(at)), ("$case", job.CaseId.ToString()), ("$approved", CaseState.Approved.ToString()));

        await InsertMessageAsync(conn, tx, new CaseMessage(Guid.NewGuid(), job.CaseId, MessageDirection.Outbound, senderName, providerMessageId,
            null, senderName, job.Recipient, job.Subject, job.Body, null, at), ct);

        await AuditAsync(conn, tx, job.CaseId, $"StateChanged:Approved->{target}", senderName,
            simulated ? "Message written to the simulated outbox (no real email sent)." : $"Reply sent via {senderName} (message id {providerMessageId}).", at, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task AddCaseMessageAsync(CaseMessage message, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await InsertMessageAsync(conn, null, message, ct);
    }

    public async Task<SupportCase?> FindLatestCaseByThreadAsync(string threadId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, null, "SELECT * FROM Cases WHERE ChannelThreadId=$t ORDER BY CreatedAt DESC LIMIT 1;", ("$t", threadId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapCase(r) : null;
    }

    // ---- IMailStore ----

    public async Task<bool> TryRecordInboundAsync(InboundEmail email, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var rows = await ExecAsync(conn, null, """
            INSERT INTO InboundMessages (Provider, ProviderMessageId, ThreadId, RfcMessageId, FromAddress, Subject, ReceivedAt, Outcome, CaseId, ProcessedAt)
            VALUES ($p, $id, $thread, $rfc, $from, $subject, $at, 'Processing', NULL, NULL)
            ON CONFLICT (Provider, ProviderMessageId) DO UPDATE SET Outcome='Processing' WHERE Outcome='Failed';
            """, ct,
            ("$p", email.Provider), ("$id", email.ProviderMessageId), ("$thread", email.ThreadId), ("$rfc", email.RfcMessageId),
            ("$from", EmailAddress.Normalize(email.FromAddress)), ("$subject", email.Subject), ("$at", Ts(email.ReceivedAtUtc)));
        return rows == 1;
    }

    public async Task CompleteInboundAsync(string provider, string providerMessageId, string outcome, Guid? caseId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await ExecAsync(conn, null, "UPDATE InboundMessages SET Outcome=$o, CaseId=$case, ProcessedAt=$at WHERE Provider=$p AND ProviderMessageId=$id;", ct,
            ("$o", outcome), ("$case", caseId?.ToString()), ("$at", Ts(DateTimeOffset.UtcNow)), ("$p", provider), ("$id", providerMessageId));
    }

    public async Task<IReadOnlyList<InboundRecord>> ListInboundAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, null, "SELECT * FROM InboundMessages ORDER BY ReceivedAt DESC LIMIT $limit;", ("$limit", limit));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<InboundRecord>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new InboundRecord(Str(r, "Provider"), Str(r, "ProviderMessageId"), NStr(r, "ThreadId"), Str(r, "FromAddress"), Str(r, "Subject"),
                ParseTs(Str(r, "ReceivedAt")), Str(r, "Outcome"), NStr(r, "CaseId") is { } c ? Guid.Parse(c) : null,
                NStr(r, "ProcessedAt") is { } p ? ParseTs(p) : null));
        }

        return list;
    }

    public async Task<string?> GetSyncStateAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await ScalarAsync(conn, null, "SELECT Value FROM MailSyncState WHERE Key=$k;", ct, ("$k", key)) as string;
    }

    public async Task SetSyncStateAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await ExecAsync(conn, null, "INSERT INTO MailSyncState (Key, Value) VALUES ($k, $v) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value;", ct,
            ("$k", key), ("$v", value));
    }

    private static Task InsertMessageAsync(SqliteConnection conn, SqliteTransaction? tx, CaseMessage m, CancellationToken ct) =>
        ExecAsync(conn, tx, """
            INSERT INTO CaseMessages (Id, CaseId, Direction, Provider, ProviderMessageId, RfcMessageId, FromAddress, ToAddress, Subject, Body, AuthSummary, At)
            VALUES ($id, $case, $dir, $p, $pid, $rfc, $from, $to, $subject, $body, $auth, $at);
            """, ct,
            ("$id", m.Id.ToString()), ("$case", m.CaseId.ToString()), ("$dir", m.Direction.ToString()), ("$p", m.Provider),
            ("$pid", m.ProviderMessageId), ("$rfc", m.RfcMessageId), ("$from", m.FromAddress), ("$to", m.ToAddress),
            ("$subject", m.Subject), ("$body", m.Body), ("$auth", m.AuthenticationSummary), ("$at", Ts(m.AtUtc)));

    public async Task<IReadOnlyList<SimulatedMessage>> ListSimulatedMessagesAsync(Guid? caseId = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = caseId is null
            ? Command(conn, null, "SELECT * FROM SimulatedMailbox ORDER BY WrittenAt DESC;")
            : Command(conn, null, "SELECT * FROM SimulatedMailbox WHERE CaseId=$case ORDER BY WrittenAt DESC;", ("$case", caseId.Value.ToString()));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<SimulatedMessage>();
        while (await r.ReadAsync(ct))
        {
            list.Add(new SimulatedMessage(Str(r, "IdempotencyKey"), Guid.Parse(Str(r, "CaseId")), Str(r, "Recipient"), Str(r, "Subject"),
                Str(r, "Body"), ParseTs(Str(r, "WrittenAt")), NStr(r, "ThreadId"), NStr(r, "InReplyTo")));
        }

        return list;
    }

    public async Task AppendAuditAsync(Guid caseId, string eventType, string actor, string detail, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await AuditAsync(conn, null, caseId, eventType, actor, detail, DateTimeOffset.UtcNow, ct);
    }

    public static string IdempotencyKey(Guid caseId, int draftVersion) => $"case-{caseId:N}-v{draftVersion}";

    // ---- helpers ----

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, null, "PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON;", ct);
        return conn;
    }

    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static async Task<int> ExecAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] ps)
    {
        await using var cmd = Command(conn, tx, sql, ps);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct, params (string, object?)[] ps)
    {
        await using var cmd = Command(conn, tx, sql, ps);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    private static async Task<List<T>> QueryAsync<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map, string caseId, CancellationToken ct)
    {
        await using var cmd = Command(conn, null, sql, ("$id", caseId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(map(r));
        return list;
    }

    private static async Task<SupportCase?> ReadCaseAsync(SqliteConnection conn, SqliteTransaction? tx, Guid caseId, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, "SELECT * FROM Cases WHERE Id=$id;", ("$id", caseId.ToString()));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapCase(r) : null;
    }

    private static async Task<Draft?> ReadDraftAsync(SqliteConnection conn, SqliteTransaction tx, Guid caseId, int version, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, "SELECT * FROM Drafts WHERE CaseId=$id AND Version=$v;", ("$id", caseId.ToString()), ("$v", version));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? MapDraft(r) : null;
    }

    private static Task AuditAsync(SqliteConnection conn, SqliteTransaction? tx, Guid caseId, string type, string actor, string detail, DateTimeOffset at, CancellationToken ct) =>
        ExecAsync(conn, tx, "INSERT INTO AuditEvents (CaseId, EventType, Actor, Detail, At) VALUES ($case, $type, $actor, $detail, $at);", ct,
            ("$case", caseId.ToString()), ("$type", type), ("$actor", actor), ("$detail", detail), ("$at", Ts(at)));

    private static SupportCase MapCase(SqliteDataReader r) => new(
        Guid.Parse(Str(r, "Id")), Str(r, "OrderId"), Str(r, "CustomerMessage"), Enum.Parse<CaseState>(Str(r, "State")),
        Enum.Parse<Classification>(Str(r, "Classification")), NStr(r, "StateReason"), r.GetInt32(r.GetOrdinal("CurrentDraftVersion")),
        ParseTs(Str(r, "CreatedAt")), ParseTs(Str(r, "UpdatedAt")),
        Enum.Parse<CaseChannel>(Str(r, "Channel")), NStr(r, "ChannelThreadId"), NStr(r, "SenderEmail"),
        r.GetInt64(r.GetOrdinal("SenderAuthenticated")) == 1);

    private static CaseMessage MapMessage(SqliteDataReader r) => new(
        Guid.Parse(Str(r, "Id")), Guid.Parse(Str(r, "CaseId")), Enum.Parse<MessageDirection>(Str(r, "Direction")), Str(r, "Provider"),
        NStr(r, "ProviderMessageId"), NStr(r, "RfcMessageId"), Str(r, "FromAddress"), Str(r, "ToAddress"), Str(r, "Subject"), Str(r, "Body"),
        NStr(r, "AuthSummary"), ParseTs(Str(r, "At")));

    private static Draft MapDraft(SqliteDataReader r) => new(
        Guid.Parse(Str(r, "Id")), Guid.Parse(Str(r, "CaseId")), r.GetInt32(r.GetOrdinal("Version")), Str(r, "Recipient"),
        Str(r, "Subject"), Str(r, "Body"), Enum.Parse<DraftSource>(Str(r, "Source")), Enum.Parse<DraftKind>(Str(r, "Kind")),
        JsonSerializer.Deserialize<List<string>>(Str(r, "EvidenceRefsJson")) ?? [],
        JsonSerializer.Deserialize<List<string>>(Str(r, "WarningsJson")) ?? [],
        Str(r, "CreatedBy"), ParseTs(Str(r, "CreatedAt")));

    private static OutboxMessage MapOutbox(SqliteDataReader r) => new(
        Guid.Parse(Str(r, "Id")), Guid.Parse(Str(r, "CaseId")), Guid.Parse(Str(r, "DraftId")), r.GetInt32(r.GetOrdinal("DraftVersion")),
        Str(r, "IdempotencyKey"), Str(r, "Recipient"), Str(r, "Subject"), Str(r, "Body"), Str(r, "Status"),
        ParseTs(Str(r, "CreatedAt")), NStr(r, "SentAt") is { } sent ? ParseTs(sent) : null,
        NStr(r, "ThreadId"), NStr(r, "InReplyTo"), NStr(r, "RefHeader"));

    private static string Str(SqliteDataReader r, string col) => r.GetString(r.GetOrdinal(col));

    private static string? NStr(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static string Ts(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTs(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
