# Lensee v1 Sprint Plan - PRD v2.5 Alignment

This plan updates Sprint 3 through release after Sprint 1-2 foundation/auth work. It aligns with PRD v2.5, the enhanced ERD/UML, and the PostgreSQL script.

## Operation Modeling Update: Return vs Change

The PRD uses `Change / Return` as one domain area, but implementation must not treat every return as a full exchange.

### Change

A Change is a two-sided exchange:

- `ChangeOut` lines: goods the client returns to Lansee. Stock effect: increase `available_qty`.
- `ChangeIn` lines: goods Lansee gives to the client. Stock effect: decrease `available_qty`.
- Financial formula: `net_payment = change_in_total - change_out_total`.
- Positive net payment means the client owes Lansee.
- Negative net payment means Lansee owes the client as refund or credit.
- Merchant eligibility applies to every `ChangeOut` line.

### Return

A Return is a one-sided return without replacement goods:

- It has returned lines only.
- Stock effect: increase `available_qty`.
- Financial effect: refund, credit, or balance reduction depending on payment state.
- It must still consume merchant return eligibility.
- It should normally link to the original sale or at least validate against merchant/SKU purchase history.
- It is not the same as resolving an unresolved representative reserve as `Returned`; that reserve resolution simply releases reserved stock and has no sale refund semantics.

### Implementation Decision

For v1, implement Return as an explicit operation workflow. Prefer adding `Return` to `operation_logs.operation_type` and adding a distinct stock transaction type such as `ReturnIn`, while keeping `ChangeOut`/`ChangeIn` for exchange operations. If the database enum/check constraint is not changed in the first pass, support Return as a constrained `Change` with only return-side lines and a `return_mode`/metadata flag, then migrate to explicit `Return` before release hardening.

Acceptance tests must cover:

- Change with both returned and replacement lines.
- Pure Return with returned lines only.
- Merchant cannot return more than purchased minus previously returned.
- Return affects stock and merchant balance/refund logic without requiring replacement stock.
- Reserve resolution as `Returned` does not create refund/payment records unless explicitly converted into a Return operation.

## Sprint 3 - Catalog Management and Product Validation

### Backend

- Implement category tree, brands, products, and SKUs CRUD with soft delete/deactivation.
- Enforce PRD v2.5 catalog simplifications: no `categories.level`; no brand-level power bounds.
- Add product-type validation for colored lenses, medical lenses, and solutions.
- Implement `pieces_per_pack`, `sell_mode`, expiry type, clinical params, and extended attributes validation.
- Add search, filtering, active/inactive filtering, and pagination using shared primitives.
- Add audit entries for create/update/deactivate/reactivate actions.

### Frontend

- Catalog list/detail screens.
- Product and SKU forms with product-type-aware fields.
- Category tree picker.
- Active/inactive toggles.
- Pack/piece display hints based on sell mode.

### QA/DevOps

- Unit tests for product-type validation and category tree behavior.
- API contract tests for catalog response/error shape.

### Acceptance

- Admin manages catalog end to end.
- Clerks and C-Level can read allowed catalog data.
- Inactive SKUs are blocked for new operations but remain visible in historical logs.

## Sprint 4 - Inventory Core and Stock Ledger

### Backend

- Implement locations, stock balances read APIs, target quantities, inventory batches, and stock transaction history.
- Implement `StockLedgerService` as the only path that mutates `StockBalance`.
- Support transaction types: `Receipt`, `Sale`, `SupplyOut`, `SupplyIn`, `ReserveInWarehouse`, `ReserveWithRep`, `ReserveReleaseInWarehouse`, `ReserveReleaseWithRep`, `WriteOff`, `StocktakeAdjustment`, `ChangeOut`, `ChangeIn`, and the chosen Return transaction type or constrained return mapping.
- Add optimistic concurrency handling using `row_version`.
- Enforce no negative availability or reserved quantity.
- Add location-scoped reads for warehouse clerks.

### Frontend

- Inventory dashboard by location.
- SKU stock detail.
- Stock transaction history.
- Target stock editor for sub-warehouses.
- Pieces and pack-equivalent display.

### QA/DevOps

- Unit tests for ledger mapping and negative-state blocking.
- Integration tests for concurrent stock updates.

### Acceptance

- Every stock movement is append-only in `stock_transactions`.
- `StockBalance` is updated atomically by the ledger service only.
- Clerk views are location-scoped.

## Sprint 5 - CRM, Representatives, and Return Eligibility

### Backend

- Implement merchants, merchant notes, representatives, status changes, soft delete, and search.
- Enforce `contact_person_name` as required.
- Implement `MerchantEligibilityService`.
- Compute per-merchant, per-SKU return eligibility from confirmed sales minus confirmed return-side quantities.
- Add merchant profile summary hooks: operation count, balance placeholder, change eligibility, return eligibility, and expiry placeholder.
- Attribute notes to authenticated users.

### Frontend

- Merchant and representative list/detail/forms.
- Merchant timeline shell for notes, operations, payments, returns, changes, and future expiry data.
- Merchant eligibility panel showing returnable quantities per SKU.

### QA/DevOps

- Unit tests for eligibility formula.
- Tests that cancelled or draft changes/returns do not consume eligibility.

### Acceptance

- Admin manages CRM records.
- Historical snapshots remain readable after deactivation.
- Merchant eligibility prevents returning more than purchased.

## Sprint 6 - Core Operations MVP

### Backend

- Implement operation creation for wholesale sale, retail sale, reserve, supply, inventory receipt, write-off, change, and return.
- Implement operation lines, pack-to-piece conversion, immutable unit price, snapshots, operation numbers, and version 1 snapshot.
- Implement confirm/cancel flows that call `StockLedgerService`.
- Implement return-specific validation and payment/balance output.
- Implement change-specific `ChangeOut` and `ChangeIn` sections, with net payment calculation.
- Implement reserve subtypes and reserve resolution into sale, change, return, or cancellation.
- Implement inventory receipt header and batch creation on confirmed receipt.

### Frontend

- Operation creation wizard by operation type.
- Clerk mobile-friendly operation entry.
- Separate Change and Return entry flows:
  - Change: returned items plus replacement items.
  - Return: returned items only, refund/credit/balance result.
- Operation history list/detail and confirm/cancel actions by role.

### QA/DevOps

- Integration tests for sale, reserve, supply, receipt, write-off, change, and return confirmation.
- Contract tests for operation validation failures.

### MVP Exit

- Admin and clerks can create and confirm core stock-affecting operations.
- Inventory balances update correctly.
- Return and Change flows are distinct and tested.
- Catalog, CRM, auth, RBAC, audit, and operation logs are usable end to end.

## Sprint 7 - Operation Versioning, Editing, Returns Hardening, and Stocktake

### Backend

- Implement post-creation edits with full `OperationVersion` snapshots.
- Enforce unit price immutability.
- Reverse and reapply stock effects on editable operation changes.
- Enforce return/change edit rules without allowing invalid merchant eligibility after edits.
- Implement stocktake sessions, adjustment lines, confirmation, and stock transaction posting.
- Lock clerk edits after Admin action.

### Frontend

- Operation version history viewer.
- Edit operation flow with mandatory reason.
- Return/change amendment UI that clearly separates return-side and replacement-side quantities.
- Stocktake session UI: open, count, review discrepancy, confirm.

### QA/DevOps

- Tests for version sequencing and immutable snapshots.
- Tests for stock reversal conflicts.
- Tests for edited return/change eligibility recalculation.

### Acceptance

- Every edit creates a sequential immutable version.
- Confirmed historical versions can generate stable reads.
- Stocktake confirmation applies only through ledger transactions.

## Sprint 8 - Payments, Refunds, Credits, and Accountant Workflow

### Backend

- Implement cash records and installment `MainPaymentLog`.
- Implement statuses: `PendingAdmin`, `PendingAccountant`, `PendingAdminReview`, `Completed`.
- Implement assignment to Accountant, draft sub-log, confirm sub-log, reject sub-log, amount-paid recalculation, and audit entries.
- Enforce Accountant can draft only assigned logs.
- Add financial handling for Change and Return:
  - Sale cash/installment creates payable records as defined.
  - Change positive net payment creates amount due from client.
  - Change negative net payment creates refund or merchant credit.
  - Return creates refund, merchant credit, or balance reduction.

### Frontend

- Admin payment workspace: initialize, assign, review, confirm/reject.
- Accountant workspace: assigned logs, draft sub-log, payment history read-only.
- Merchant payment status on merchant detail.
- Refund/credit display for return and negative-net change operations.

### QA/DevOps

- Payment state-machine unit tests.
- Integration tests for accountant assignment and Admin confirmation.
- Tests for Return and Change financial outputs.

### Acceptance

- No installment payment reaches completed without Admin confirmation.
- Rejected sub-logs remain visible and non-deletable.
- Return and Change financial outcomes are visible and auditable.

## Sprint 9 - Notifications and Background Jobs

### Backend

- Implement notification log APIs, unread/read state, role/user targeting, and deep links.
- Add domain event handlers for operation confirmed, clerk confirmation, payment assigned, sub-log drafted/rejected, payment completed, low stock, expiry, outstanding balance, unresolved reserves, returns, and negative-net changes.
- Add Hangfire or equivalent job runner for daily expiry checks, balance age checks, reserve age checks, and supply suggestions.

### Frontend

- Notification badge/list.
- Mark read.
- Deep-link navigation.
- Polling adapter first; SignalR adapter can be added behind the same interface.

### QA/DevOps

- Idempotency tests for background jobs.
- Permission tests for notification visibility.

### Acceptance

- Events create correct notifications.
- Background jobs are idempotent.
- Notification permissions prevent cross-role data leakage.

## Sprint 10 - Dashboards and Reporting Reads

### Backend

- Implement role-scoped dashboard endpoints.
- Implement stock, operations, payments, merchant balance, receipt, write-off, return/change, installment, and eligibility report query APIs.
- Add optimized read models/queries where needed.
- Ensure Accountant reports exclude stock/inventory details.

### Frontend

- C-Level/Admin dashboards: inventory, financial, operational summaries.
- Clerk dashboard: own warehouse inventory and own operations.
- Accountant dashboard: payments and operations history only.
- Return/change dashboard breakdown, including refund/credit totals and returned quantities.

### QA/DevOps

- Authorization tests for report scopes.
- Query tests for date range, pagination, filters, empty states, and operation type breakdown.

### Acceptance

- Dashboards respect role and location scope.
- Reports distinguish sales, changes, returns, supplies, receipts, write-offs, and stocktakes.

## Sprint 11 - Exports, Documents, and Audit Evidence

### Backend

- Generate PDF operation bills from any confirmed operation version.
- Generate merchant payment statements.
- Generate CSV/Excel exports for PRD report types.
- Add exports for return/change reports if not covered by operations report filters.
- Log every export in `reporting.export_logs`.
- Ensure PDFs use immutable snapshots.

### Frontend

- Export actions, download states, report filters, export history.
- Bill preview/download from operation detail.
- Change bill rendering with OUT and IN sections.
- Return bill/credit note rendering with returned items and refund/credit result.

### QA/DevOps

- Snapshot-based PDF tests for sale, change, return, and receipt operations.
- Export permission tests.

### Acceptance

- PDFs use immutable snapshots.
- Accountant can export allowed operation/payment reports but cannot access stock exports.
- Return and Change documents are distinct and understandable.

## Sprint 12 - Hardening, UAT, and v1 Release

### Backend

- Security review: authorization tests, audit completeness, no direct stock writes, no hard deletes on protected tables.
- Performance pass: indexes, query plans, pagination, large operation history.
- Data seed finalization: roles, locations, alert configs, sample catalog, sample merchants, sample return/change scenarios.
- Production Docker/nginx/env review.
- Final database constraint review, including whether `Return` is explicit in operation/transaction constraints.

### Frontend

- Full responsive pass: desktop admin/accountant, mobile clerk.
- Accessibility pass: keyboard navigation, validation messages, loading/error states.
- UAT fixes and copy cleanup.

### QA/DevOps

- Full regression suite.
- E2E UAT scenarios:
  - Admin full flow.
  - Clerk sale/receipt flow.
  - Accountant draft flow.
  - C-Level read-only reporting flow.
  - Merchant Change flow.
  - Merchant pure Return/refund or credit flow.
  - With-rep reserve resolved as sale, change, return, and cancellation.

### Acceptance

- Full regression suite passes.
- UAT scenarios pass for each role.
- v1 release candidate can be deployed from a clean environment.
