## Feasibility Study: AI Conversational Form-Filling

### TL;DR
**High feasibility.** The foundation is already ~70% built in this repo. The core gap is a "form discovery" tool that uses Playwright's accessibility API to enumerate fields — without it the AI fills blindly. With one new tool and a system-prompt conversation loop, this feature would work well and can be framed entirely as an accessibility capability.

---

### What Already Exists (Discoveries)

| Component | Where | What It Does |
|---|---|---|
| `fill_web_form` AI tool | AssistantToolsFactory.cs | Registered tool — AI can call it already |
| `FillWebFormAsync` | WebBrowserAssistantService.cs | Navigates to URL, fills fields by `name`/`id`, optional submit |
| `navigate_and_read_page` | `WebBrowserAssistantService` | Reads page text — partial form awareness |
| Upwork JS injection | `ReplyInUpworkCurrentRoomAsync` | Proven pattern for DOM manipulation via Playwright |
| `submitForm` safety gate | WebBrowserAssistantService.cs | Defaults to `false` — submit is opt-in already |

**The critical missing piece**: The AI knows how to fill a form but has no way to *discover* what fields a form has. It fills blindly unless the user already knows the field `name`/`id` attributes.

---

### The Accessibility Approach (Playwright `accessibility.snapshot()`)

Playwright exposes `page.Accessibility.SnapshotAsync()` which returns the page's **ARIA/accessibility tree** — the same data a screen reader uses. This is:

- **Legitimately an accessibility tool** — it reads ARIA roles, labels, required markers, and field types
- More resilient than CSS selectors (uses semantic meaning, not DOM structure)
- Perfectly suited for the AI to understand "what does this form need from me?"

Example output from `page.Accessibility.SnapshotAsync()`:
```
role: form
  textbox "First Name" (required)
  textbox "Email" (required)  
  combobox "Country" → options: UK, USA, ...
  checkbox "I agree to terms"
  button "Submit"
```

The AI can then have a natural conversation: *"This form needs your First Name, Email, and Country. What would you like me to fill in?"*

---

### Feasibility Breakdown

**Technical**: **Very High**
- Playwright is already integrated and working
- `FillWebFormAsync` is already wired as an AI tool
- Adding `read_web_form_structure` is ~40 lines of C#
- The conversational loop already works naturally with the Copilot SDK

**Accessibility Framing**: **Solid**
- This is a legitimate accessibility use case (motor-impaired users who can dictate but struggle with mouse/tab navigation)
- `page.Accessibility.SnapshotAsync()` is the canonical accessibility API — not scraping
- Screen readers interact with pages this exact way

**Security**: **Manageable with guards** (detailed below)

**Edge Cases That Need Consideration**:
- Multi-step wizard forms (page 1 → page 2 based on answers)
- CAPTCHA — not solvable, must detect and inform user
- File upload fields — separate concern
- Forms behind authentication (login-gated) — requires the CDP shared Chrome session already in the codebase
- Dynamic dropdowns (value A changes available options for field B)

---

### Security Concerns & Mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| PII (name, address, DOB) passed through the AI model | Medium | User must be warned; consider field-value substitution *after* AI planning phase |
| AI auto-submitting without consent | High | `submitForm=false` default + explicit confirmation gate already exists |
| Malicious URL (form on phishing site) | High | URL allowlist config or per-URL confirmation prompt |
| Credential fields (passwords) | High | Detect `input[type=password]` in accessibility tree; refuse to take value from AI conversation; prompt user to type it directly |
| No URL validation beyond `http/https` | Medium | Already a gap in current `fill_web_form` — should add URL allowlist |

---

### Plan: What Would Need to Be Built

**Phase 1 — Discovery** (one new method + tool)
1. Add `ReadWebFormStructureAsync(url)` to `WebBrowserAssistantService` using `page.Accessibility.SnapshotAsync()` — flattens the tree to just form controls with labels, types, required status, and available options
2. Register `read_web_form_structure` as an AI tool in AssistantToolsFactory.cs

**Phase 2 — Safety** (config + guard)
3. Add `FORM_FILL_URL_ALLOWLIST` env var (comma-separated patterns) OR a per-request confirmation gate before any URL is navigated
4. Detect `password` field types in the snapshot and strip them from AI context — log a warning to the user

**Phase 3 — Conversation Flow** (system prompt)
5. Add guidance to `SystemPromptBuilder` for the form-fill conversational loop:
   - Read form structure first
   - Ask user for values field by field (or bulk if simple form)
   - Show summary before calling `fill_web_form` with `submitForm=false`
   - Ask explicit confirmation before calling again with `submitForm=true`

**Phase 4 — Optional Enhancement**
6. Add `take_page_screenshot` tool for the user to visually verify the filled form before submitting (returns screenshot path or base64 for Telegram image send)

---

### Relevant Files

- WebBrowserAssistantService.cs — add `ReadWebFormStructureAsync` here
- AssistantToolsFactory.cs — register the new tool alongside `fill_web_form` (~line 185)
- SystemPromptBuilder.cs — add form-fill conversation guidance
- EnvironmentSettings.cs — add `FORM_FILL_URL_ALLOWLIST` constant

---

### Verdict

| Dimension | Score |
|---|---|
| Technical feasibility | ★★★★★ |
| Accessibility framing legitimacy | ★★★★★ |
| Security (with guards) | ★★★★☆ |
| Edge case robustness | ★★★☆☆ |
| Overall | **Build it — low risk, high value** |

The biggest open questions before building are below. 

Asked 3 questions (Scope of forms, Submit safety, Sensitive fields)

Those answers sharpen the plan significantly. Let me update and present the refined version. 

Updated memory file [](file:///c%3A/Users/MPhil/AppData/Roaming/Code/User/workspaceStorage/3a762d029b5ebc41679a8d6fc85c6450/GitHub.copilot-chat/memory-tool/memories/OWRmZTljNmItYmVjMi00NjczLTlkOGMtZTU3ZmM0NDc1YTMw/plan.md)

---

## Plan: AI Conversational Form-Filling (Refined)

**Scope: Blazor app at `localhost:5008` first, then expandable. Submit always requires explicit confirmation. Password fields refused.**

This is good news: starting with your own Blazor app (visible in the screenshot — the CustomIntelliSense snippet form) eliminates the URL-safety and phishing risks entirely for Phase 1. The accessibility-tree approach means no brittle CSS selectors — it reads what a screen reader would see.

---

### Steps

**Phase 1 — Form Discovery Tool**

1. Add `ReadWebFormStructureAsync(url)` to WebBrowserAssistantService.cs: navigate to URL, call `page.Accessibility.SnapshotAsync()`, flatten the tree to extract only form-control nodes (textbox, combobox, checkbox, radio, button with submit role), return a structured JSON list of: `{ label, role, required, options[] }`
2. Detect `password`-role inputs in the snapshot — exclude them from the returned structure and append a warning message: *"This form has a password field — please type that manually"*
3. Register `read_web_form_structure` as an AI tool in AssistantToolsFactory.cs alongside `fill_web_form` (~line 185)

**Phase 2 — Conversation Loop (System Prompt)**

4. Add guidance to SystemPromptBuilder.cs (or the documents runtime guidance markdown) for the form-fill loop:
   - Step 1: call `read_web_form_structure` to see what fields exist
   - Step 2: ask the user for values (field by field, or as a list for short forms)
   - Step 3: call `fill_web_form` with `submitForm=false` — confirm fields have been filled
   - Step 4: present a summary and ask *"Shall I submit? Reply 'yes, submit' to confirm"*
   - Step 5: only then call `fill_web_form` again with `submitForm=true` (*depends on step 4*)

**Phase 3 — Optional Screenshot Verification**

5. Add `TakePageScreenshotAsync(url)` to `WebBrowserAssistantService` — returns a local file path; wire it to send as a Telegram image via existing `TelegramAttachmentService` (*parallel with step 4, independent of phase 2*)
6. Register `take_page_screenshot` AI tool (*depends on step 5*)

**Phase 4 — URL Expansion (future)**

7. Add `FORM_FILL_ALLOWED_HOSTS` env var in EnvironmentSettings.cs with a default of `localhost` for Phase 1
8. Validate URL against allowlist in `FillWebFormAsync` and `ReadWebFormStructureAsync` before navigating (*depends on step 7*)

---

### Relevant Files

- WebBrowserAssistantService.cs — add `ReadWebFormStructureAsync` + optional `TakePageScreenshotAsync`
- AssistantToolsFactory.cs — register new tools alongside `fill_web_form` (~line 185)
- SystemPromptBuilder.cs — add form-fill conversation guidance
- EnvironmentSettings.cs — add `FORM_FILL_ALLOWED_HOSTS` (Phase 4 only)

---

### Verification

1. Ask the assistant: *"Fill in the snippet form at localhost:5008/customintellisense"* — verify it calls `read_web_form_structure` and returns the correct field list (Language, Category, Display Value, Remarks, Deliver Type, etc.)
2. Provide values via chat — verify `fill_web_form` is called with `submitForm=false` and fields appear filled on screen
3. Reply with anything except "yes, submit" — verify it does NOT submit
4. Reply "yes, submit" — verify the form is submitted and Blazor saves the record
5. Test with a login form — verify password field is refused with a clear message

---

### Decisions

- **Playwright accessibility API** (`page.Accessibility.SnapshotAsync()`) used for discovery, not DOM scraping — legitimately an accessibility tool
- **Phase 1 is localhost only** — no URL allowlist needed; add it in Phase 4 when expanding
- **Fresh Playwright browser context per call** — already how the service works, keeps sessions isolated
- **Screenshot verification** is optional — adds significant value but is independent of the core flow

---

Would you like me to hand this off for implementation, or are there any adjustments to the plan first? 



User: Start implementation

GitHub Copilot: Read memory [](file:///c%3A/Users/MPhil/AppData/Roaming/Code/User/workspaceStorage/3a762d029b5ebc41679a8d6fc85c6450/GitHub.copilot-chat/memory-tool/memories/OWRmZTljNmItYmVjMi00NjczLTlkOGMtZTU3ZmM0NDc1YTMw/plan.md)

Created 6 todos

Read [](file:///c%3A/Users/MPhil/source/repos/personal-assistant/WebBrowserAssistantService.cs#1-1), lines 1 to 60

Read [](file:///c%3A/Users/MPhil/source/repos/personal-assistant/AssistantToolsFactory.cs#1-1), lines 1 to 50

Read [](file:///c%3A/Users/MPhil/source/repos/personal-assistant/WebBrowserAssistantService.cs#820-820), lines 820 to 850

Searched for text `StatusAsync|GetBrowserAsync|GetPlaywrightAsync` (`**/WebBrowserAssistantService.cs`), 6 results

## Launching Browser

```powershell
Start-Process -FilePath "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" -ArgumentList '--remote-debugging-port=9223','--user-data-dir=msedge-remote-profile'
```

Start-Process 'msedge' '--remote-debugging-port=9223 --user-data-dir="C:\Users\MPhil\AppData\Local\Microsoft\Edge\User Data"' 