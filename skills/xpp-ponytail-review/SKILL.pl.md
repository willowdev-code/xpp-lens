---
name: xpp-ponytail-review
description: Przegląd zmiany w X++ (Dynamics 365 F&O) jak senior, którego budzą, gdy coś padnie na produkcji - poprawność, bezpieczeństwo, transakcje, wydajność SQL, rozszerzalność, testy i zbędny kod. Czyta powiązany kod przez xpp-lens, nie tylko diff; każde znalezisko wyjaśnia prostym językiem. Użyj, gdy ktoś prosi „zrób review”, „przejrzyj zmianę”, „sprawdź mój kod X++”, „czy to jest przekombinowane”, „review ostatniego changesetu”, /xpp-ponytail-review. Do przeglądu PR względem work itemu w Azure DevOps użyj dev-pr.
---

Review an X++ change like the senior developer who will be paged when it breaks in production.
Order of importance: correct, safe, transactions and data, holds under real volume, upgrade-safe, tested,
lean. Every extra object must be built, synced, secured and upgraded with each Microsoft update.
This is a report the user asked for, so give it in full. It lists findings and changes no code.

Based on `ponytail-review` of [Ponytail](https://github.com/DietrichGebert/ponytail) (MIT), rewritten for X++.

## 1. Understand first

- Review what the user names: files, objects, a model, a changeset, a PR. Nothing named: `xpp_changed
  since=build` (changed and not compiled yet), or the files changed on disk today (`xpp_changed since=<today>`).
- Read the changed methods with `xpp_method`, then the code they touch:
  - `xpp_callers` / `xpp_refs` for every changed method, field or signature - custom and Microsoft callers,
  - `xpp_callees` for what the changed code uses,
  - `xpp_extensions` for other CoC / handlers on the same standard methods (order and double effects),
  - `xpp_entity`, `xpp_security` when tables, fields or menu items changed.
- Trace the real flow: where data comes in (form, entity/integration, batch), what is written, what is posted.
- Find the expected volume: an interactive form for one user, or a batch over all lines of all companies.
  Judge scale against that, and say which volume you assumed.
- `xpp_build_errors` - errors of the last build in the touched model count as findings.

## 2. Look for

1. **Bug:** wrong result or crash - empty buffer not checked, `dateNull()`, zero or negative quantity,
   rounding of amounts, wrong company (`DataAreaId`, missing `changeCompany`), wrong language, `InventDim`
   compared by fields instead of `InventDimId`, an enum value added by someone else not handled in a `switch`,
   a caller broken by a signature change, a fix in one caller while the shared method stays broken.
2. **CoC and extensions:** CoC without `next`, or `next` called conditionally or twice; logic before `next` that
   belongs after it (or the reverse); an event handler changing args it does not own; a copied standard method
   body instead of an extension; a wrapped method that is `[Hookable(false)]` / `internal` / likely to change.
3. **Transactions and data:** `ttsbegin` without its `ttscommit` on every path; a transaction split so a document
   can be half-posted; `throw` caught and swallowed inside a transaction; record written without `forUpdate` /
   `selectForUpdate`; `skipDataMethods` / `skipDatabaseLog` / `skipEvents` / `skipBusinessEvents` that skips
   logic the data needs; `doInsert` / `doUpdate` bypassing validation; missing `validateWrite` / `validateField`
   on data from users or integrations.
4. **Security:** a menu item without a privilege, a privilege not in any duty/role (`xpp_security`); data shown
   or changed across companies the user cannot see; `crossCompany` without need; direct SQL
   (`Statement`, `executeQuery`) built from strings; secrets or URLs in code instead of parameters/Key Vault.
5. **Volume and SQL:** `select` inside `while select` (N+1) where a join, `exists join` or set-based statement
   works; record-by-record `insert`/`update`/`delete` where `insert_recordset` / `update_recordset` /
   `delete_from` / `RecordInsertList` fit (and would not be downgraded by overridden methods); missing
   `firstonly`; no field list on wide tables; a where clause without a supporting index (`xpp_object` shows
   indexes); work repeated per line in a batch that could run once; a batch that is not split into tasks for
   large volume; display methods doing queries on grids.
6. **Upgrade and structure:** hard-coded texts instead of labels; hard-coded company, account or ID values; a
   new model reference; a new parameter table or option nobody asked for; naming outside the team prefix.
7. **Missing test:** risky new logic (posting, money, a branch, a data write, a bug fix) with no SysTest or no
   named manual check. One good test, not coverage.
8. **Lean:** code that should not exist or should be smaller.
   - configure: a setting already does it (name it),
   - standard: Microsoft's framework already does it (name the class/method),
   - reuse: the model already has it (name the object),
   - delete: dead code, unused parameters, speculative options,
   - yagni: interface or factory with one implementation, parameter nobody sets,
   - narrow: CoC on a big method where a delegate or smaller method exists.

## 3. Check before you report

- Every finding needs a concrete case: "this data or this situation leads to this wrong result". No case, no
  finding.
- Re-check with xpp-lens: the caller exists, the buffer can really be empty, the index really is missing, no
  other extension already handles it.
- A shortcut marked `// shortcut:` that names its limit is a decision, not a finding, unless the expected volume
  already crosses it.
- Propose the smallest fix that works. Prefer fixes that delete code or move it to configuration or standard.
  Never add layers, frameworks or parameters the problem does not need.
- No style taste, no "consider", no vague worries. Best-practice warnings only when they hide a real problem.

## 4. Output

In the user's language (Polish by default), short sentences, everyday words. Explain a technical term the first
time you use it. The reader may never have seen this code.

Start with `Co robi ta zmiana:` in two or three sentences.

Then the findings in three groups, skip empty groups:
- **Do poprawy przed wdrożeniem:** bug, security, data loss or half-posted documents, breaks at the expected
  volume.
- **Warto poprawić:** risky code without a test, real slowness, a copied standard body, code that should not
  exist.
- **Drobne:** small speed-ups, shorter forms.

Number findings across all groups, so the user can say "popraw 2 i 5". Every finding has all four parts, each
one or two short sentences:

2. **Zamówienie częściowo zaksięgowane** (`MyModel/AxClass/SalesFormLetter_Extension.xml`, `postInvoice`)
   - **Co to jest:** Rozszerzenie księguje fakturę i od razu tworzy dokument w systemie zewnętrznym.
   - **Problem:** Wywołanie usługi jest wewnątrz `ttsbegin`. Gdy usługa nie odpowie, transakcja się wycofa, ale
     dokument po drugiej stronie już istnieje.
   - **Poprawka:** Przenieść wywołanie po `ttscommit` albo do business eventu. Kilka linii, logika bez zmian.
   - **Jeśli pominiemy:** Dokumenty zewnętrzne bez faktury w D365, uzgadniane ręcznie.

End with:
- `Werdykt: Można wdrażać.` or `Werdykt: najpierw popraw 1 i 3.`
- `Lean: -<N> linii możliwe.` when lean findings exist.
- `Synchronizacja DB: potrzebna / niepotrzebna.`
- `Nie sprawdzone:` one line, if something mattered and you could not check it.

Nothing found: `Co robi ta zmiana:`, then `Wygląda dobrze. Można wdrażać.` and one line on what you checked.
