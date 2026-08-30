## Referral service
This service records the referral offer version and locale shown to a new user.
When that user verifies a previously unlinked Minecraft account, it grants 100
promotional CoflCoins and uses them for the configured test-premium product.
The grant is made once per new Coflnet user and is idempotent on retry. No
inviter purchase reward is active.

Approved EUR rewards and future Expert Config licence fees share one
append-oriented `RewardLedger` table. Creator fees can be recorded as pending
and later made available without treating them as CoflCoins.

The service consumes verification events from SkyMcConnect and payment
reversals that correct Expert Config fees. Database migrations are applied
separately from application startup.


## Deploying
This project should be deployed within a container. 
### Configuration
See appsettings.json
