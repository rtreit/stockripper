# Documentation standard

Stockripper uses ASD-STE100 Simplified Technical English, Issue 9, for
documentation and reports.

The user can request a different style. Use that style only for the requested
content.

## Scope

Use this standard for these text types:

- README files
- Architecture, design, API, integration, and operation documents
- Test, experiment, incident, and investigation reports
- Release notes
- Issue and pull request text
- Review findings, plans, and generated summaries

Apply this standard to new text and changed text.

## Vocabulary

- Use approved words with their approved meanings and parts of speech.
- Use established Stockripper terms as technical nouns or technical verbs.
- Define an uncommon project term at its first use.
- Use one term for one item or action.
- Do not use a different term only to add variety.
- Use American English spelling.
- Do not use Latin abbreviations.
- Keep a multi-word noun to three words or fewer when practical.

## Controlled technical terms

Use these Stockripper terms as technical nouns:

- account binding
- allocation
- audit record
- broker
- broker adapter
- capital allocation
- decision
- execution mode
- fill
- goal
- goal activation
- goal version
- idempotency key
- kill switch
- live account
- live trading
- market risk
- order
- paper account
- paper trading
- position
- reconciliation
- risk policy
- session

Use these Stockripper terms as technical verbs:

- activate
- allocate
- authorize
- reconcile
- replay
- submit

Keep these execution values exact:

- `REPLAY`
- `RESEARCH`
- `PAPER_REVIEW`
- `PAPER_AUTO`
- `LIVE_REVIEW`
- `LIVE_AUTO`

Add a term when it has a special project meaning. Use the same meaning in all
documents.

## Verbs and voice

- Use the infinitive, imperative, simple present, simple past, or simple
  future.
- Use a past participle as an adjective only when the context is clear.
- Use an `-ing` word only as a technical noun or technical modifier.
- Use active voice when you know the agent.
- Do not use contractions.

## Sentences

- Put one instruction in each sentence.
- You can combine instructions when the actions occur at the same time.
- Use no more than 20 words in a procedural sentence.
- Use no more than 25 words in a descriptive sentence.
- Use no more than six sentences in one paragraph.
- Use a vertical list for complex text.

## Punctuation

- Do not use an em dash.
- Do not use a semicolon.
- Do not use punctuation to join long sentences.
- Keep punctuation in exact technical content unchanged.

## Exact technical content

Keep these items exact:

- Code and identifiers
- Commands and file paths
- Protocol fields and wire values
- Configuration keys
- Log text and test output
- Direct quotations
- Legal notices

Explain an exact item with controlled text when an explanation is necessary.

## Review

Review new and changed prose against Issue 9 rules and its dictionary. Record
project terms as technical nouns or technical verbs.

Do not state that text conforms to STE unless you complete the full review.

Reference:
[ASD-STE100 Issue 9](https://www.asd-ste100.org/assets/files/ASD-STE100_ISSUE9.pdf).
