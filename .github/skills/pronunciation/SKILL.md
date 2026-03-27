---
name: pronunciation
description: 'Provide pronunciation guidance, IPA phonemes, and audio transcriptions for words, names, and phrases. Use when the user complains about mispronunciations, requests alternate pronunciations, or asks how to say something correctly.'
argument-hint: 'Provide the word or phrase and context (e.g., how it was mispronounced, desired pronunciation, language/locale if non-English).'
user-invocable: true
---

# Pronunciation & TTS Correction Guidance

Use this skill when the user reports TTS mispronunciations, requests pronunciation guidance for specific words or names, or wants to improve speech synthesis output.

## When to Invoke

- User complains: "The bot says X word wrong" or "That doesn't sound right"
- User requests: "How should this be pronounced?" or "Give me the IPA for..."
- User suggests: "Try pronouncing this as..." or "Add this correction"
- Context: Improving clarity in spoken output, fixing regional accent issues, handling proper nouns or technical terms

## Procedure

1. **Identify the problematic word/phrase.** Extract the exact text, confirm spelling, note context (is it a name, acronym, technical term, homophone, etc.).
2. **Provide IPA phoneme.** Use International Phonetic Alphabet (IPA) notation with stress marks. Example: `todo` → `/ˈtuː.duː/` (not `/ˈtoʊ.doʊ/`).
3. **Offer alternate transcriptions** if relevant (e.g., British vs. American English, formal vs. casual, regional variants).
4. **Explain the correction rationale.** Why is the proposed pronunciation better? Is it a common misspelling, a technical term, a proper noun, a homophone disambiguation?
5. **Suggest SSML encoding** if the correction should be stored permanently for TTS. Example: `<phoneme alphabet='ipa' ph='ˈtuː.duː'>todo</phoneme>`.
6. **Summarize for admin review.** Provide a concise entry (word, IPA, context) that can be added to the pronunciation corrections dictionary without further analysis.

## Reference: IPA Quick Guide

### Vowels
- `/iː/` — "ee" (fleece, easy)
- `/ɪ/` — "ih" (kit, city)
- `/ɛ/` — "eh" (dress, bed)
- `/æ/` — "a" (trap, cat)
- `/ɑː/` — "ah" (palm, father) [British]
- `/ɔː/` — "aw" (thought, law) [British]
- `/ʊ/` — "oo" (foot, book)
- `/uː/` — "oo" (goose, choose)
- `/ʌ/` — "uh" (strut, cut)
- `/ə/` — "schwa" (about, data) — unstressed central vowel

### Common Consonants
- `/θ/` — "th" (think, mouth) [voiceless]
- `/ð/` — "th" (this, bathe) [voiced]
- `/ʃ/` — "sh" (ship, wish)
- `/ʒ/` — "zh" (measure, vision)
- `/tʃ/` — "ch" (chip, watch)
- `/dʒ/` — "j" (judge, edge)
- `/n/` — "n" (no, pan)
- `/ŋ/` — "ng" (sing, think)
- `/l/` — "l" (love, feel)
- `/r/` — "r" (run, car)

### Stress Marks
- `ˈ` — primary stress (before the vowel)
- `ˌ` — secondary stress (before the vowel)

Example: `education` → `/ˌɛdʒʊˈkeɪʃən/` (stress on "kay", lighter stress on first syllable)

## Common Mispronunciation Patterns

| Word/Term | Common Error | Correct IPA | Notes |
|-----------|--------------|-------------|-------|
| `todo` | /ˈtoʊ.doʊ/ (one word) | /ˈtuː.duː/ (two syllables, British) | Homophone ambiguity; hyphenate as "to-do" for clarity |
| `Kubernetes` | /kuːˈbɜrnətɪs/ (wrong stress) | /ˌkuːbərˈnɛtiːz/ (stress on "net") | Greek origin; emphasis on second half |
| `SQL` | Spelled out "S-Q-L" | /ˌɛskjuːˈɛl/ ('sequel') | Context-dependent; some prefer spelling |
| `OAuth` | /oʊˈɔːθ/ | /ˈoʊ.ɔːθ/ or /oʊ-ɔːθ/ | Two syllables; compound of "Open" + "Auth" |
| `GIF` | /dʒɪf/ | /ɡɪf/ or /dʒɪf/ (both accepted) | Creator prefers /dʒɪf/; /ɡɪf/ also common |

## Submitting Corrections

When a word is confirmed as needing correction:

1. **Format for storage:**
   ```json
   {
     "originalWord": "todo",
     "replacement": "to-do",
     "ssmlPhoneme": "<phoneme alphabet='ipa' ph='ˈtuː.duː'>todo</phoneme>",
     "context": "Homophone disambiguation for task-list item",
     "enabled": true
   }
   ```

2. **Notify the admin** that a new correction is ready (via Telegram command, GitHub issue, or pull request).

3. **Track changes** by noting the date added, usage count, and whether the correction improved TTS output quality.

## Conflict Resolution

If multiple pronunciations exist for a single word:
- **Regional variants** (British vs. American) → Store both; TTS locale setting selects appropriate one
- **Technical vs. informal** → Store both; context in prompt may guide selection
- **Deprecated vs. modern** → Mark old version as `"enabled": false`; override only upon explicit request

## Notes for Intent Detection

This skill should auto-invoke when the user message contains:
- Complaint keywords: "wrong", "mispronounce", "sounds bad", "doesn't sound right", "fix the pronunciation"
- Request keywords: "how do you say", "pronunciation of", "IPA for", "phoneme for", "what's the correct"
- Correction keywords: "pronounce it as", "should sound like", "try saying", "add pronunciation"
