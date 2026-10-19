# Ticket Shine — Customer Support Agent (sample V1)

A local sample written in C#/.NET 10 that demonstrates a **customer-support agent** which:

1. looks up orders via **MCP** (Model Context Protocol, real stdio),
2. drafts **evidence-backed** replies (each draft cites the data sources it used),
3. **waits for staff approval**; only after approval does it simulate sending to a mock outbox.

> All data (names, `@example.com` emails, orders, notes, policies) is **fake data**.
> The "policies" in this sample are **not official Ticket Shine policies**.
> The sample does not connect to real systems, send email, issue refunds, cancel orders, or buy tickets.

## Run the demo in 1 minute

Requirement: **.NET 10 SDK** (`dotnet --list-sdks` must list a `10.x` entry).

```powershell
# Windows PowerShell
./scripts/run-demo.ps1            # Template demo, no API key needed
./scripts/run-demo.ps1 -Reset     # delete old demo data, then run
```

```bash
# bash (macOS/Linux/Git Bash)
./scripts/run-demo.sh
```

Open <http://127.0.0.1:5080>. The app binds to loopback only, because the demo has **no login**.

Run tests:

```powershell
./scripts/test.ps1      # or ./scripts/test.sh
# equivalent: dotnet restore; dotnet build; dotnet test --project tests/TicketShine.Support.Tests
```

To open in Visual Studio 2026 / VS Code (C# Dev Kit): open `TicketShine.SupportAgent.sln` and set `TicketShine.Support.Web` as the startup project.

5-minute demo script: [docs/demo-script.md](docs/demo-script.md).

## Two drafting modes

| | Template demo (default) | Live AI (optional) |
|---|---|---|
| API key | Not required | Required (`ANTHROPIC_API_KEY` or `ant auth login`) |
| MCP + workflow + review gate | Real | Real |
| How drafts are written | Deterministic template | Claude (`claude-opus-5-5`) calls allowlisted lookup tools |
| UI label | `Template demo — no LLM` | `Live AI (claude-opus-5-5)` |

Enable Live AI:

```powershell
$env:ANTHROPIC_API_KEY = "<your key>"   # stays on the server only; never passed to the browser or the MCP server
./scripts/run-demo.ps1 -LiveAi
```

Configuration (`appsettings.json`, `LiveAi` section): `Model`, `BaseUrl`, `Effort`, `MaxToolCalls` (6), `TimeoutSeconds` (60), `UseServerSideFallback`.
Missing configuration or a provider error → the case moves to `NeedsHuman` with a clear reason; **it never silently falls back to the template**.

Live AI is tested through a fake HTTP transport that goes through the real Anthropic SDK. **It has not been run against a real model while building this sample** (no API key was available). Run it once yourself after configuring a key.

## Solution structure

```
TicketShine.SupportAgent.sln
├─ src/TicketShine.Support.Core            domain, workflow, validation, interfaces (no infrastructure dependencies)
├─ src/TicketShine.Support.Infrastructure  SQLite, MCP client, draft generators, simulated sender, simulated inbox
├─ src/TicketShine.Support.McpServer       MCP stdio server — lookup tools only
├─ src/TicketShine.Support.Gmail           Gmail adapter: inbound (history API) and reply sender (not yet run against a real mailbox)
├─ src/TicketShine.Support.Web             ASP.NET Core + Blazor Interactive Server (workspace review)
├─ tests/TicketShine.Support.Tests         63 tests: real MCP stdio, classification, review gate, concurrency, restart,
│                                          Live AI (fake transport), email ingest, sender authentication, Gmail adapter (fake HTTP)
├─ data/demo-seed.json                     all fake data
├─ docs/                                   architecture, demo script, limitations
└─ scripts/                                run-demo / test for PowerShell and bash
```

Diagrams and detailed flows: [docs/architecture.md](docs/architecture.md).

## How is an agent different from a chatbot?

A chatbot takes a question and generates an answer from what it "knows". The agent in this sample:

- **Takes actions to gather facts**: calls the `get_order`, `get_delivery_status`, `get_case_notes`, and `get_demo_policy` tools before drafting.
- **Makes structured decisions**: classifies the case (in progress / delivered / overdue / not found) based on the data and DemoClock, then picks the next step (draft a reply or hand off to a human). See [CaseClassifier.cs](src/TicketShine.Support.Core/Workflow/CaseClassifier.cs).
- **Has state and a trail**: each case has a state machine, an evidence snapshot, a trace of every tool call, and an audit log, all stored in SQLite.
- **Is constrained by code**, not just by the prompt: tool allowlist, tool-call limit, review gate.

## How is MCP different from calling the database directly?

- The agent and workflow do **not** read the order database or repository. They only see the **4 tools** published by the MCP server (name, description, JSON schema), called over the standard protocol on stdio. See [McpLookupGateway.cs](src/TicketShine.Support.Infrastructure/Mcp/McpLookupGateway.cs).
- The permission boundary lives on the server: the server has **no** tools for send, approve, refund, cancel, or arbitrary SQL. For the agent to do something, the server must expose a tool for it. See [LookupToolset.cs](src/TicketShine.Support.McpServer/LookupToolset.cs).
- The MCP server is a separate **child process**. Logs go to stderr so they don't corrupt the protocol's stdout. The child process receives only an allowlist of environment variables, with no API key. See [McpServerLaunch.cs](src/TicketShine.Support.Infrastructure/Mcp/McpServerLaunch.cs).
- Each result is an envelope with `status` (`Success`/`NotFound`/`Error`), `sourceId`, `retrievedAtUtc`. Timeouts and not-found results are **never** turned into successful data.
- Another MCP client (e.g. Claude Desktop) can reuse this server without knowing anything about SQLite or the real systems behind it.

## Where is human approval enforced in code?

| Guarantee | Location |
|---|---|
| No send/approve tool on the MCP server | [LookupToolset.cs](src/TicketShine.Support.McpServer/LookupToolset.cs), allowlist in [LookupContract.cs](src/TicketShine.Support.Core/Lookups/LookupContract.cs) |
| Gateway rejects tools outside the allowlist before calling the server | `McpLookupGateway.CallAsync` |
| The model only returns a draft. Unknown tools, lookups of other orders, or exceeding 6 calls are all rejected | `AnthropicReplyDraftGenerator.ExecuteToolAsync` in [AnthropicReplyDraftGenerator.cs](src/TicketShine.Support.Infrastructure/Drafting/AnthropicReplyDraftGenerator.cs) |
| Drafts must pass validation: refer only to the correct order, real evidence refs, valid length | [DraftValidator.cs](src/TicketShine.Support.Core/Workflow/DraftValidator.cs) |
| The recipient always comes from the order record | `SupportWorkflow.GenerateAndSaveDraftAsync` in [SupportWorkflow.cs](src/TicketShine.Support.Core/Workflow/SupportWorkflow.cs) |
| Approve succeeds only when the case is `AwaitingReview` **and** the version matches the one being viewed; the outbox entry is created in the same transaction | `SqliteCaseRepository.ApproveAsync` in [SqliteCaseRepository.cs](src/TicketShine.Support.Infrastructure/Persistence/SqliteCaseRepository.cs) |
| Outbox entries are created only once: `UNIQUE (CaseId, DraftVersion)` + `IdempotencyKey`; the sender uses `INSERT OR IGNORE` | same file, schema + `WriteSimulatedMessageAsync` |
| No auto-approve: only the UI button (server-side Blazor) calls `ApproveAsync` | [Home.razor](src/TicketShine.Support.Web/Components/Pages/Home.razor) |

Tests proving the above are in [ReviewGateTests.cs](tests/TicketShine.Support.Tests/ReviewGateTests.cs) and [LiveProviderTests.cs](tests/TicketShine.Support.Tests/LiveProviderTests.cs).

## Email channel (Gmail)

In production, customers email the support mailbox and cases are created automatically; the "Create case" button stays as a manual fallback. Both paths end in the same workflow, so lookups, drafting and the review gate are unchanged.

- **Demo (default):** the sidebar has a **Simulated Gmail inbox**. Pick a sample email and click *Deliver*. The ingest worker turns it into a case within a couple of seconds. Samples cover a verified customer, a reply on the same thread, a spoofed sender, a forged authentication header, a sender who is not the order's customer, an email without an order number, and an auto-reply.
- **Real Gmail:** set `Mail:Inbound` / `Mail:Outbound` to `Gmail` and configure a service account. See [docs/email-channel.md](docs/email-channel.md). This adapter has been tested against a fake Gmail API only.

Safety rules enforced in code ([EmailIngestService.cs](src/TicketShine.Support.Core/Email/EmailIngestService.cs), [SupportWorkflow.cs](src/TicketShine.Support.Core/Workflow/SupportWorkflow.cs)):

- Order details and drafts only for a sender whose DMARC (or aligned DKIM) passed **and** whose address matches the order. Otherwise the case goes to `NeedsHuman` after the order lookup.
- Only `Authentication-Results` headers stamped by `mx.google.com` are trusted.
- Searching orders by email address (`find_orders_by_email`) happens only for authenticated senders and is not available to the model.
- Each Gmail message ID is processed once; auto-replies and mail from the support mailbox are ignored.
- A new customer message on the thread sends a pending draft back to a human; replies are sent in the customer's Gmail thread.

## Swapping the data adapter for a real Ticket Shine integration

Only the part **behind the MCP server** needs to change. The workflow, UI, and agent stay the same.

1. In `TicketShine.Support.McpServer`, replace [DemoDataStore.cs](src/TicketShine.Support.McpServer/DemoDataStore.cs) with an adapter that reads the real system (internal API or read-only DB), keeping the tool names and envelope unchanged.
2. Remove the fault injection (`faultMode`) in `LookupToolset`.
3. If the server runs elsewhere, configure `Mcp:Command` / `Mcp:Arguments`, or switch to the MCP SDK's HTTP transport.
4. Switch the sender from `SimulatedSender` ([SimulatedSending.cs](src/TicketShine.Support.Infrastructure/Sending/SimulatedSending.cs)) to `GmailReplySender` with `Mail:Outbound=Gmail`. Any other `IReplySender` must keep the idempotency key.
5. Before real use, you must add everything listed in [docs/limitations.md](docs/limitations.md): staff login, role authorization, data access control, etc.

## Versions used

.NET SDK 10.0.402 · ModelContextProtocol 2.2.0 · Anthropic 12.58.0 · Microsoft.Data.Sqlite 10.0.13 · Google.Apis.Gmail.v1 1.77.0.4295 · MimeKit 4.18.1 · xunit.v3 4.0.2
