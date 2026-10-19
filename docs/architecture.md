# Architecture

## Components

```mermaid
flowchart LR
    subgraph Browser["Browser (support staff)"]
        UI["Blazor workspace<br/>(Interactive Server)"]
    end

    subgraph Web["TicketShine.Support.Web (ASP.NET Core, 127.0.0.1)"]
        WF["SupportWorkflow<br/>state machine + review gate"]
        GEN["IReplyDraftGenerator<br/>Template | Live AI"]
        VAL["DraftValidator"]
        REPO[("SQLite<br/>Cases, Evidence, ToolCalls,<br/>Drafts, Approvals, Outbox, Audit")]
        WORKER["OutboxWorker<br/>SimulatedSender"]
        GW["McpLookupGateway<br/>(MCP client, allowlist, timeout)"]
    end

    subgraph Child["Child process"]
        MCP["TicketShine.Support.McpServer<br/>get_order · get_delivery_status<br/>get_case_notes · get_demo_policy"]
        SEED[("data/demo-seed.json<br/>fake data")]
    end

    CLAUDE["Claude API<br/>(Live AI mode only)"]

    UI -- "SignalR (server-side)" --> WF
    WF --> GW
    GEN -. "allowlisted tool calls" .-> GW
    GW -- "MCP over stdio" --> MCP
    MCP --> SEED
    WF --> GEN
    GEN -. "Messages API" .-> CLAUDE
    WF --> VAL
    WF --> REPO
    WORKER --> REPO
```

Key points:

- The agent/workflow touches order data **only via MCP**. SQLite stores only the workflow's own data (cases, drafts, audit), not order data.
- The MCP server has no state-changing tools. Sending replies is the workflow's job, and happens only after Approve.
- The model (Live AI) sees only the 4 lookup tools, and its output is only a draft that goes through `DraftValidator`.

## State machine

```mermaid
stateDiagram-v2
    [*] --> Created
    Created --> LookingUp
    LookingUp --> AwaitingReview: valid draft
    LookingUp --> NeedsHuman: overdue / not found / invalid draft
    LookingUp --> LookupFailed: MCP error or timeout
    LookupFailed --> LookingUp: Retry lookup
    NeedsHuman --> AwaitingReview: staff requests a draft (still requires review)
    AwaitingReview --> AwaitingReview: edit draft (version + 1)
    AwaitingReview --> Approved: Approve matching version
    AwaitingReview --> Rejected: Reject
    Approved --> SimulatedSent: worker writes simulated outbox
```

## Case flow

1. **Create case** → `SupportWorkflow.CreateCaseAsync` validates the order ID/message and saves the case as `Created`.
2. **Lookups** → `get_order` first. If not found or on error, **stop** with no further lookups (to avoid disclosing other data). Otherwise, call `get_delivery_status`, `get_case_notes`, `get_demo_policy`.
3. Store a **snapshot** of each result (JSON envelope) and a **trace** of each tool call.
4. **Classify** with `CaseClassifier` using DemoClock (default `2026-10-17T18:00:00Z`).
5. **Draft**: the generator returns `{subject, body, evidence_refs}`. The validator checks the referenced order, evidence refs, and length, then adds an escalation warning if needed. The recipient is taken from `order.customerEmail`.
6. Save draft v1; the case moves to `AwaitingReview`.
7. Staff edit (creating a new version), **Approve**, or **Reject**.
8. Approve: within **a single** `BEGIN IMMEDIATE` **transaction**, run `UPDATE Cases ... WHERE State='AwaitingReview' AND CurrentDraftVersion=@v`, insert `ApprovalEvents`, insert `OutboxMessages` (unique per case + version), and insert the audit entry.
9. `OutboxWorker` writes to `SimulatedMailbox` by idempotency key (`INSERT OR IGNORE`), then marks the outbox entry `Sent` and moves the case to `SimulatedSent`.

## Concurrency and idempotency

- Double-click: the button is disabled while processing; even if the request is repeated, the second one gets `Conflict` because the state has already changed.
- Two tabs approving at once: SQLite serializes writers, so only one conditional `UPDATE` succeeds. The other tab gets a conflict and reloads the state. The test `Concurrent_approvals_from_two_tabs_produce_one_success_and_one_message` runs 6 approvals in parallel.
- Stale version: approving v1 when v2 exists is rejected, so an old version is never sent.
- Re-running the worker multiple times never creates extra messages.

## Live AI mode

- Anthropic C# SDK, default model `claude-opus-5-5`, effort `medium`, structured JSON output (`subject`, `body`, `evidence_refs`).
- The tool loop is controlled by code: at most 6 tool calls, 60 seconds total, and lookups restricted to the case's own order.
- Server-side refusal fallback (beta `server-side-fallback-2026-07-01`, `fallbacks: "default"`) is on by default; disable it with `LiveAi:UseServerSideFallback=false` if the endpoint does not support it.
- The prompt states explicitly that customer messages and case notes are untrusted data. But safety **does not rely on the prompt**: tool permissions and the review gate are enforced in the backend.
