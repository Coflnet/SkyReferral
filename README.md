## Referral service
This service records the referral offer version and locale shown to a new user.
When that user verifies a previously unlinked Minecraft account, it grants 100
promotional CoflCoins and uses them for the configured test-premium product.
The grant is made once per new Coflnet user and is idempotent on retry. No
inviter purchase reward is active. If one is activated later, only the referred
user's first eligible paid subscription may qualify. Expert Config purchases
never qualify for a referral or creator-code reward in addition to their creator
fee. A subscription refund or finally lost chargeback cancels the pending
referral reward or adds a proportional, append-only correction after it becomes
available; an open chargeback does not reduce the reward.

Approved EUR bug-report and referral rewards and Expert Config licence fees share one
append-oriented `RewardLedger` table. Creator fees can be recorded as pending
and later made available without treating them as CoflCoins.
The balance response publishes a EUR 70 minimum cash-payout threshold, or the
lowest threshold recorded on any of the account's available Award entries if a
reward programme sets one lower. Coflnet charges no payout fee. A payout
request reserves the gross amount; its evidence must record the
selected method, gross remuneration, creator VAT, withholding tax, solidarity
surcharge, resulting net and one opaque, hashed payout-evidence bundle. A
completed payout also requires a namespaced payment reference. Only the
separate finance credential may request or complete a payout, and the payout
worker must show the exact calculation before confirmation.

The service consumes verification events from SkyMcConnect and purchase
reversions that correct Expert Config fees. A finally lost chargeback against
value used for an Expert Config is handled by reverting the affected
`config-purchase`; an open dispute is not reverted. The resulting transaction
event removes the buyer's managed access in SkyModCommands and cancels or
corrects the creator fee here. Database migrations are applied separately from
application startup.

Manual Expert onboarding is stored as immutable review records. The lightweight
review and, for a 16+ minor, the guardian's acceptance make `Eligible` permit
free publication from any country. `PaidPublicationReady` additionally requires
a supported paid territory and tax-document route (and verified entity details
for a business). Paid publication currently supports EU Member States, GB (England/Wales),
US, CH and, for individuals only, the non-treaty countries of Creator License Annex A.6 listed in `NonTreatyIndividualCountries` (currently NC, Statement route, full § 50a withholding). Individual identity and payout evidence is collected only when
a payout is prepared. Store any raw identity, age and representative documents
outside this service; only opaque references and evidence digests belong here.
The record keeps residence and tax countries, capacity jurisdiction
and declared adult/minor capacity, but no birth date or identity-document number.
The latest review controls seller
eligibility, while agreement acceptance remains
an explicit, separate action in `/cofl sellconfig`. Use separate 32-character
`CREATOR_ONBOARDING:READ_TOKEN` and `CREATOR_ONBOARDING:REVIEW_TOKEN` secrets.


## Deploying
This project should be deployed within a container. 
### Configuration
See appsettings.json

### Optional SQL TLS

`DB_CONNECTION` retains its existing hosts, port, database, account and password.
Set all three `DB:TLS:CA_CERT_PATH`, `DB:TLS:CERT_PATH` and `DB:TLS:KEY_PATH`
(or the corresponding `DB__TLS__...` environment variables) to enable verified
TLS. The CA file contains trusted roots; the client certificate PEM contains the
leaf and issuing intermediates. The database host names/IPs in `DB_CONNECTION`
must match the server certificate SANs. There is no hostname bypass.

The application uses MySqlConnector 2.6.2's `Required` mode with Core's strict
server-certificate callback because the driver's MariaDB password-bound TLS
fallback can soften native `VerifyFull` errors. Client credentials reload only
when a new physical TLS connection opens. The pool lifetime is capped at 300
seconds (a shorter configured lifetime is retained), and connection timeout at
10 seconds. This does not interrupt active queries or impose a maximum duration
on an already checked-out connection.

Run with a writable ephemeral `HOME`: the current user's CertificateAuthority
store holds only validated public client intermediates, needed for the driver's
leaf-only certificate API. No root/system trust is installed. The application owns certificates for its lifetime. Mount projected PEM files
without `subPath` so future physical connections can observe renewal.

Startup keeps the existing database migration before serving HTTP and then
checks `SELECT 1` with a five-second budget. `/health/ready` performs the same
read-only database check and returns 503 on failure. Kafka configuration,
consumption groups/topics, Payments HTTP and existing HTTP routes are unchanged.
