# V1 limitations

V1 is a **demo sample**, not production-ready.

## Security and access

- **No login.** "Reviewer label" is just a label the user types in, not an authenticated identity.
- No roles/authorization, no control over who can see which customer's data.
- The app only binds to `127.0.0.1` and stops itself if configured to bind to another address (unless `Demo:AllowNonLoopback=true` is set, **not recommended**).
- An entered Order ID is not proof of customer identity. The workflow is for staff reviewing a case.
- SQLite is not encrypted. Demo data is stored at `src/TicketShine.Support.Web/App_Data/support.db`.

Before real use, the minimum required is: SSO/staff login, role authorization for Approve, per-case data access control, audit tied to real identities, secret management (Key Vault...), and a security review of prompt injection handling.

## Data and integration

- Order data is a fake JSON file. The MCP server only reads this file.
- By default no email is sent or read. "Send" writes to the `SimulatedMailbox` table and inbound mail comes from the simulated inbox.
- The Gmail adapter (`TicketShine.Support.Gmail`) exists but has only been tested against a fake Gmail API. Inbound uses polling, not Pub/Sub push.
- Email attachments are ignored; there is no malware scanning. Email body parsing is basic (quoted text after "On … wrote:" and `>` lines is removed).
- If an email fails halfway through processing it can be retried, which in rare cases may create a duplicate case.
- No refund, cancel, ticket purchase, or any operation that modifies an order.
- Fault injection (`faultMode`) is for the demo only (TS-10029 fails on alternate calls, TS-10030 is delayed 20 seconds).

## Draft

- Template mode uses fixed templates, not AI reasoning.
- The validator uses simple rules (order number regex, a list of commitment phrases). It reduces risk but does not replace the human reviewer.
- Live AI mode **has not been run against a real model** while building the sample (no API key). It has only been tested through a fake transport going through the Anthropic SDK.
- Live AI uses the Claude API (provider `anthropic`). Other providers are not yet supported.

## Operations

- One instance, one SQLite file. No schema migrations yet (tables are created with `CREATE TABLE IF NOT EXISTS`).
- The outbox worker runs inside the web process.
- The UI and documentation are English only.

## Tested / not tested

| Item | Status |
|---|---|
| `dotnet restore`, `dotnet build`, `dotnet test` (63 tests) | Run, passing |
| Email ingest: sender authentication, threading, de-duplication, auto-replies | Tested |
| Simulated Gmail inbox flow in the UI | Click-tested automatically with headless Chrome |
| Gmail adapter against a real mailbox | **Not run** (fake Gmail API only) |
| Real MCP client ↔ server over stdio | Tested |
| Review gate, double/concurrent approve, stale version, restart | Tested |
| UI flows (create case, edit, approve, reject, retry, holding draft, 2 tabs) | Click-tested automatically with headless Chrome on Windows |
| Live AI with a real model | **Not run** |
| bash scripts on macOS/Linux | **Not run** (written only; the PowerShell version was run on Windows) |
