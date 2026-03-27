## Plan: Optional Pronunciation Skill + TTS Corrections

Yes, this is feasible. The recommended approach is a two-layer design: an optional Copilot skill for language/pronunciation guidance, plus a runtime pronunciation-corrections dictionary in the app for actual TTS output fixes. The skill is conditionally invoked when relevant, so it does not need to load every turn.

**Steps**
1. Confirm scope boundary: pronunciation-only for assistant responses vs actual runtime speech output correction, because these are separate systems. This decision gates all downstream work.
2. Phase 1 (Skill Authoring): add a new pronunciation-focused skill under the repository skills directory with explicit trigger criteria in description so it is auto-selected only when pronunciation intent is detected. This can run independently of runtime code changes.
3. Phase 1 (Skill Governance): define update protocol for complaints/corrections (for example: issue template or command phrase) and maintain a short approved word list with IPA guidance. This is parallel with Step 2.
4. Phase 2 (Runtime TTS Integration): add a pronunciation correction pass in the TTS pipeline before synthesis so known problematic words are normalized or mapped to phoneme-aware output. This depends on Step 1.
5. Phase 2 (Persistence): add a durable corrections store (JSON-first) and load it at startup, with safe fallback if malformed. This depends on Step 4.
6. Phase 2 (Invocation Path): expose a controlled way to add/update corrections from conversational feedback, with admin-only guardrails and audit logging. This depends on Step 5.
7. Verification phase: validate that optional skill invocation remains conditional and that runtime TTS output improves for corrected terms without regressions. This depends on Steps 2-6.

**Relevant files**
- [TextToSpeechService.cs](TextToSpeechService.cs) — insert pronunciation preprocessing before Azure synthesis; add logging around corrections.
- [Program.cs](Program.cs) — register/load pronunciation correction service and configuration.
- [SystemPromptBuilder.cs](SystemPromptBuilder.cs) — optional prompt guidance for when pronunciation-correction tools should be used.
- [.github/skills/upwork-proposal/SKILL.md](.github/skills/upwork-proposal/SKILL.md) — reference structure/frontmatter conventions for the new skill.
- [README.md](README.md) — document correction workflow and any new environment variables.

**Verification**
1. Skill behavior check: run prompt scenarios that should and should not trigger pronunciation skill usage to confirm conditional invocation.
2. Unit tests: boundary matching, case-insensitive lookup, malformed dictionary handling, and no-op behavior when no corrections exist.
3. Runtime smoke test: known mispronounced terms are corrected in synthesized output while unaffected text remains unchanged.
4. Regression test: ensure existing preview extraction, HTML stripping, and table-detection behavior still works.
5. Observability check: confirm logs record correction hits/misses without leaking sensitive data.

**Decisions**
- Included: optional skill that is not always loaded, periodic updates to pronunciation knowledge, and architecture for complaint-driven correction.
- Included: separation between LLM skill guidance and deterministic runtime TTS correction logic.
- Excluded for initial rollout: large database-backed dictionaries, automatic phoneme generation by model, and cross-locale pronunciation packs.

**Further Considerations**
1. Correction format recommendation: Option A plain replacement text (fast, simple), Option B IPA/SSML phoneme entries (more precise), Option C hybrid (best long-term).
2. Update channel recommendation: Option A admin command in Telegram, Option B manual JSON edit + restart, Option C both (recommended).
3. Conflict policy recommendation: Option A latest-wins, Option B priority tiers, Option C environment-specific overrides (recommended if multi-device).
