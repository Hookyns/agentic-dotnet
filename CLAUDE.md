## 1. Think Before Coding

**Make assumptions explicit. Expose tradeoffs. Prefer doing over asking.**

Before you start implementing:
- Write down assumptions directly. If a reasonable assumption can be made, make it and proceed — mention it in your response.
- If multiple valid interpretations exist, pick the most likely one, state why, and note the alternative.
- If something is genuinely ambiguous and the wrong choice would waste significant work, ask — but keep it to one focused question.
- If there is a smaller or cleaner path, call it out. Challenge complexity when needed.
- If requirements are unclear, pause and name the exact ambiguity before proceeding.

## 2. Simplicity First

**Ship the smallest solution that fully satisfies the request. Nothing speculative.**

- Implement only what was asked.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If the implementation is longer than necessary, reduce it.

## 3. Clean OOP and SOLID

**Write object-oriented code that is easy to read, test, and extend.**

Apply SOLID principles where they add clarity without adding ceremony:

- **Single Responsibility** — each class/method does one thing.
- **Open/Closed** — prefer extension over modification of stable abstractions.
- **Liskov Substitution** — subtypes must be safely substitutable for their base types.
- **Interface Segregation** — small, focused interfaces over fat ones.
- **Dependency Inversion** — depend on abstractions; inject concrete dependencies.

Practical rules:
- Prefer composition over inheritance for behavior reuse.
- Name things for what they do, not how they do it.
- Keep constructors thin — no logic, just assignment.
- Abstractions are only justified when there is more than one implementation or a clear testability need.

## 4. Surgical Changes

**Modify only what is required. Only clean up what your change introduces.**

When working in existing code:
- Do not polish nearby formatting, comments, or code outside scope.
- Do not refactor stable code unless the task requires it.
- Follow the local style, even when it differs from your preference.
- If you see unrelated dead code, note it instead of removing it.

When your edits leave leftovers:
- Remove imports, variables, or functions made unused by YOUR changes.
- Leave pre-existing dead code in place unless asked to remove it.
