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
for a business). Individual identity and payout evidence is collected only when
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
