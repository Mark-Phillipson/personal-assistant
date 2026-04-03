# Plan: WhatsApp Group Summariser

**Date recorded:** April 3, 2026  
**Status:** Feasibility assessed — implementation not yet started

---

## Background

The goal is to allow the assistant to connect to the user's personal WhatsApp account, access group chats the user is a member of, and return a summary of activity within a given date range.

---

## Key Constraint: End-to-End Encryption

WhatsApp uses end-to-end encryption (E2EE) by design. This means:

- WhatsApp's servers **cannot** provide readable message content to a third party.
- Decryption only happens on authorised client devices (phone, WhatsApp Desktop, WhatsApp Web).
- Any integration must work **after decryption on the user's own device** — never via a server-side API that bypasses E2EE.

---

## Access Path Options

### ❌ Option 1: Official WhatsApp Cloud/Business API

- Designed for business messaging (customer service bots, notifications).
- Cannot access **personal account** group history.
- Does not satisfy the "my number, my groups, date range summary" use case.
- **Not viable for this requirement.**

---

### ✅ Option 2: WhatsApp Web / Desktop Browser Scraping (Playwright)

- The assistant already has Playwright infrastructure in [`WebBrowserAssistantService.cs`](../WebBrowserAssistantService.cs).
- The same pattern used for Upwork (attaching to a logged-in Chrome session via CDP) could attach to an open WhatsApp Web tab.
- Messages are decrypted by the browser; the assistant reads them from the DOM.

**Pros:**
- No extra client needed — uses the browser you already have open.
- Reuses the existing `UPWORK_CHROME_CDP_URL`-style CDP pattern.

**Cons:**
- WhatsApp Web DOM structure is not documented and may change.
- Could be brittle; rate-limiting or anti-automation measures may apply.
- Terms of service risk — automated scraping of WhatsApp Web is not officially supported.
- Date-range summaries require scrolling back through chat history, which is fragile.
- Only captures what is visible in the current browser session; no persistent history.

---

### ✅ Option 3: Chat Export Ingestion (Recommended First Version)

WhatsApp allows exporting individual group chats as `.txt` or `.zip` files from the mobile app.

The assistant could:
1. Ingest exported `.txt` chat files (standard WhatsApp export format).
2. Parse sender, timestamp, and message text.
3. Store normalised records locally (SQLite, matching the existing database pattern).
4. Expose AI tools for querying by group name and date range.
5. Summarise using the Copilot session.

**Pros:**
- No browser automation risk.
- No terms of service concerns — you are reading your own exported data.
- Works fully offline and locally.
- Clean, reliable, well-understood file format.
- Easy to extend later (re-import when fresh exports are generated).

**Cons:**
- Requires a manual export step on the phone for each refresh.
- Not real-time; summaries reflect the state at export time.

---

### ⚠️ Option 4: Unofficial Libraries (e.g., Baileys, WA-automate, go-whatsapp)

- Third-party Node.js/Go libraries that implement the WhatsApp multi-device protocol.
- Allow programmatic access to a phone number's account.
- Can read live group messages, list groups, fetch history.

**Pros:**
- Closest to a full "connect to my WhatsApp number" experience.
- Supports real-time message retrieval and date-range filtering.

**Cons:**
- Clear violation of WhatsApp Terms of Service — account bans are common.
- Reverse-engineered protocol; can break without notice when WhatsApp updates.
- Security risk: requires exchanging auth credentials with a third-party library.
- Not suitable for a production personal assistant without accepting account risk.
- **Not recommended unless all other options are exhausted.**

---

## Recommended Approach

### Phase 1 — Export-Based Ingestion (Safe, Build First)

1. Create a `WhatsAppChatImportService` that parses WhatsApp `.txt` export files.
2. Store messages in a local SQLite table: `GroupName`, `Sender`, `Timestamp`, `Message`.
3. Expose an `import_whatsapp_export` AI tool that accepts a file path or known-folder alias.
4. Expose a `summarise_whatsapp_group` AI tool with parameters: `groupName`, `fromDate`, `toDate`.
5. Hook into the existing `GenericDatabaseService` / `DatabaseRegistry` pattern if persistence is needed long-term.

### Phase 2 — Optional: WhatsApp Web Live Read (Browser CDP)

If real-time access becomes necessary:

1. Add a `WhatsAppWebService` that reuses `WebBrowserAssistantService`'s CDP attach pattern.
2. Navigate to `https://web.whatsapp.com` in the logged-in Chrome session.
3. Scrape the group list and scroll through messages within a date range.
4. Feed message text into the Copilot summarisation session.
5. Register as a new service in `Program.cs` and add tools in `AssistantToolsFactory.cs`.

---

## WhatsApp Export File Format (Reference)

Standard WhatsApp `.txt` export format:
```
[DD/MM/YYYY, HH:MM:SS] Sender Name: Message text
```
Example:
```
[01/04/2026, 09:15:32] Mark Phillipson: Morning everyone
[01/04/2026, 09:18:44] Jane Smith: Hi Mark!
```
System messages (no sender):
```
[01/04/2026, 09:00:00] Messages and calls are end-to-end encrypted.
```

---

## Integration Pattern (Consistent with This Repo)

Following the pattern of `GmailAssistantService` and `GoogleCalendarAssistantService`:

1. `WhatsAppChatImportService` — parses and stores exports, answers date-range queries.
2. Register in `Program.cs` with `FromEnvironment()`.
3. Add tools in `AssistantToolsFactory.Build(...)`.
4. Environment variable: `WHATSAPP_EXPORT_FOLDER` — path to a watch folder for `.txt`/`.zip` exports.

---

## Open Questions (To Resolve Before Implementation)

- [ ] Which groups are priority targets for summarisation?
- [ ] How often would exports be refreshed (manual vs scheduled)?
- [ ] Is Phase 2 (live WhatsApp Web) needed, or is Phase 1 sufficient?
- [ ] Should summaries be stored/cached, or always re-generated from raw messages?
- [ ] What summary format is most useful: bullet points per day, topic clusters, or a rolling digest?
