# Ticket deduper: find the earlier ticket that already answers a new one

[![Test and deploy](https://github.com/hamzamehmood46/ticket-dedupe-dotnet/actions/workflows/pages.yml/badge.svg)](https://github.com/hamzamehmood46/ticket-dedupe-dotnet/actions/workflows/pages.yml)

**Live demo: [hamzamehmood46.github.io/ticket-dedupe-dotnet](https://hamzamehmood46.github.io/ticket-dedupe-dotnet/)**

A support team gets the same problem reported ten different ways. This stores past tickets as vectors with their product, status and date, takes a new ticket, and returns the past tickets that look like it, labelled "likely duplicate" or "related". Filters are applied before ranking, and if the best match was resolved it drafts a reply from how it was fixed. The two similarity thresholds are not guessed: they are chosen on one part of the data and measured on a part that was held back. Written in C#, with a Blazor WebAssembly demo that runs entirely in your browser.

> **The embedder is a stand-in and the tickets are invented.** `HashingEmbedder` counts words and word pairs (weighted by how rare they are across the tickets), folds a short list of synonyms together and scales the result to length 1. It matches words, not meaning. The 100 sample tickets were written by hand for this repository, are tidier than real ones, and every figure below was measured on them. Treat the numbers as a demonstration of the method, not a promise about real tickets. The reply is built from a template and the matched tickets; no language model writes it.

## Try it

1. Open the demo. The first example is a new "Locked out" ticket. Four past tickets about the same problem, worded differently, come back as **likely duplicates**, and the draft reply says it matches a ticket that is already open.
2. Press **An email reply with a quoted thread**. The ticket contains an order number, a card fragment, a signature, "Sent from my iPhone" and a quoted support reply. Open **What the model saw** to see what is left once those are removed: just the complaint. It matches the double-charge tickets, and the draft quotes how that was fixed.
3. Press **Brand new problem**. Nothing is close enough, so it says so and offers no draft.
4. Press **Related, but a different problem**. This ticket mixes two known problems, and both come back. The closest one lands only just over the duplicate line, which shows how thin the margin can be.
5. Set **Product** to Billing and run the lockout example again. The account tickets can no longer match, so nothing comes back.
6. Open **How well it works** for the evidence behind the thresholds, every threshold side by side, and the mistakes it makes.

## How it works

```
raw ticket ─► clean ─► embed ─► vector ─┐
 (subject, body)  quotes, signatures,   │      filters (product, resolved,
                  ids removed           ▼      date, not itself) applied first
                                   vector index ─► top matches ─► verdict by threshold ─► draft reply
                                  (exact cosine)                  Duplicate / Related / none
```

| Piece | Behavior |
|---|---|
| **Cleaning** | Drops quoted lines, everything after "On ... wrote:", an "Original Message" rule or a signature delimiter, "Sent from my ..." lines, disclaimers and the sign-off and name after "Thanks,". Removes emails, links, UUIDs, long hex strings, `#48213` and `INV-9001` style references, phone numbers and any number of five or more digits. The values are dropped, not replaced with a token, because a shared token would make unrelated tickets look alike. Short numbers such as `401` and `429` stay because they are error codes. The subject is counted twice. |
| **Embedding** | Words and adjacent word pairs, hashed into 2,048 buckets, counted with a log scale, weighted by idf fitted on the stored tickets, then scaled to length 1. Phrases such as "sign in", "log me out", "timed out", "two-factor" and "money back" are merged first, a short synonym list folds words like "billed" into "charge", and endings are trimmed. Anything outside that list is invisible to it. |
| **Index** | `IVectorIndex`: upsert, search with filters, delete, count. `FlatVectorIndex` compares the query with every stored vector that passes the filter (exact cosine), ties broken by id so results repeat. Filters run before ranking, so asking for the best 5 still returns 5 when the best match is excluded. |
| **Finder** | A ticket is never its own duplicate. A ticket with a date can only match older tickets. `AddAsync` indexes a new ticket without refitting the weights; many additions mean refitting with `CreateAsync`. Empty or id-only tickets return nothing rather than failing. |
| **Draft reply** | From the best match only. An open duplicate gets "we are already working on it". A resolved one gets how it was fixed, worded as a possibility when the match is only related. It cites only tickets that were retrieved and never repeats the new ticket's text, so text in a ticket cannot steer it. |

## The vector database

`FlatVectorIndex` is what the demo and most tests use. `Dedupe.PgVector` has the same contract on Postgres with the **pgvector** extension, so the same finder can run against a real database:

```csharp
await using var runner = new NpgsqlRunner("Host=localhost;Username=postgres;Password=dev;Database=tickets");
var index = new PgVectorIndex(runner, HashingEmbedder.DefaultDimensions);
await index.EnsureSchemaAsync();                       // extension, table, product index (and HNSW when it fits)
var finder = await DuplicateFinder.CreateAsync(SampleTickets.All, index: index);
```

```bash
docker compose up -d
```

**What is and is not tested here.** The Postgres adapter was tested with a fake that records the SQL and parameters; it has **not been run against a live Postgres or the pgvector extension**, because this machine has no database. The tests show that:

- every value (ticket id, product, dates, the vector) reaches the database as a parameter, never inside the SQL text, including a deliberately hostile id and product;
- the table name and vector size are checked first (a name such as `tickets; DROP TABLE users` is refused);
- filters sit in the `WHERE` clause with type casts so null filters work, results are ordered by cosine distance then id, and `k` is capped;
- dates become UTC, vectors are written with a dot decimal separator on any machine, a zero vector is never sent (cosine distance to it is undefined), and wrong-size or non-finite vectors are refused before any query.

Only `NpgsqlRunner`, a thin wrapper, is untested. Treat the adapter as unproven against a real server until you have run it.

Two things worth knowing about pgvector that the adapter handles or does not:

- Its HNSW index holds at most 2,000 dimensions. The default embedder uses 2,048, so the schema skips the approximate index above 2,000 and search stays exact.
- With an approximate index, a very selective filter can return fewer than `k` rows because pgvector filters after the index scan. The adapter does not work around that.

## How the thresholds are chosen

Two cut-offs decide the label: at or above the **duplicate** threshold it is a likely duplicate, at or above the **related** threshold (and below the duplicate one) it is related. `Evaluator` picks both from data:

- Tickets are split by duplicate group: every third group, and every third one-off ticket, is held out as a **test** set (30 tickets). The other 70 are the **development** set.
- The embedder's word weights and both thresholds are fitted on the development set only. A test proves that changing every held-out ticket's text does not change a single development number.
- **Duplicate threshold:** the lowest score at which at least 95% of pairs called duplicates are real, because merging two different problems costs more than missing a duplicate.
- **Related threshold:** the highest score that still finds at least 90% of real duplicate pairs, so a helpful ticket is rarely hidden.

Measured on the held-out tickets, with the thresholds the development set chose:

| | Threshold | Result on held-out tickets |
|---|---|---|
| Duplicate | 0.28 | 100% of pairs called duplicates were real (0 wrong). 24 of 36 real duplicate pairs found (67%). |
| Related | 0.13 | All 36 real duplicate pairs found. 3 extra pairs flagged that are not duplicates (92% correct). |
| Ranking | | For each of 24 held-out tickets with a duplicate among the others, the nearest neighbour was a true duplicate every time (hit@1 100%, mean reciprocal rank 1.00). |

**Read these with care.** The held-out set is 30 tickets and 36 duplicate pairs, so one pair is almost 3 points. The tickets are invented and clean. The same development numbers show that at lower thresholds there are many wrong merges (at 0.15, precision falls to 68%). The 12 missed duplicate pairs at the duplicate threshold are mostly the same thing in different words, such as "Verification code wrong error on login" and "Authenticator codes always fail". A real embedding model would help there. `Evaluator.Run` would choose that model's thresholds and report its mistakes the same way, which matters because scores from different embedders are not comparable: the 0.28 here means nothing for another model.

## Tests

`dotnet test` runs 232 tests with no network and no database. They cover cleaning (every kind of email noise, with the cases that must not be removed), the embedder (synonyms, stemming, fitted weights), the index (every filter alone and combined, ties, dimension and NaN checks, concurrent writes, and a check against a brute-force version on random data), the finder (duplicates, related tickets, new problems, dates, product and resolved filters, the draft wording), the evaluator (the split, threshold picking on made-up sweeps, and the held-out numbers) and the Postgres adapter's SQL. They also run:

- **Random input.** 600 random tickets through cleaning, embedding and search, typo'd copies of real tickets (at least 36 of 40 still find their group), a ticket of over 250,000 characters, and 2,000 tickets indexed and searched in a few seconds.
- **Concurrency.** Writing the concurrent add-and-search test showed the finder's ticket lookup was a plain dictionary, which is not safe to write from several threads. It is now a concurrent dictionary.
- **Deliberate breakage.** I broke the code on purpose in 17 places (for example the index ignoring the excluded id, word weights fitted on all tickets instead of the development set, the duplicate boundary off by one, the vector literal using the machine's culture, `k` not capped) and checked that a test failed each time. One survived at first: removing the long-number redaction went unnoticed because the phone-number pattern happens to catch long digit runs. I added tests for five to eight digit numbers and it is now caught. This is a spot check, not full mutation testing.

## Honest limits

- A word-matching stand-in, not a language model or a real embedding model. The adapter and `IEmbedder` are the seams for one, but no real model is wired up or tested.
- 100 invented tickets, tidier than real ones; the synonym list is small and general but I read the sample tickets before choosing which words to include in it.
- The Postgres adapter is untested against a real database (see above).
- A new ticket is compared only with the stored ones; there is no thread or customer awareness, no merging and no feedback loop. It never changes a ticket.
- The reply is a template, not generated text.
- Exact search compares against every candidate, which is fine to tens of thousands of tickets and is not designed beyond that.
- Dates in the demo are counted from a fixed "today" (3 November 2026) so a look-back filter behaves the same every time.

## Run it

```bash
dotnet test Dedupe.sln
```

```bash
dotnet run --project src/Dedupe.Web
```

The demo is a static Blazor WebAssembly site. The workflow in `.github/workflows/pages.yml` runs the tests and then publishes the build to the `gh-pages` branch.

## Layout

```
src/Dedupe.Core       cleaning, embedder, vector index, finder, evaluator, sample tickets
src/Dedupe.PgVector   pgvector adapter (SQL tested with a fake, never run against a live database)
src/Dedupe.Web        Blazor WebAssembly demo
tests/Dedupe.Core.Tests
docker-compose.yml    a local Postgres with pgvector, for trying the adapter
```

MIT licensed. Contains no code from any employer, and every ticket is invented.
