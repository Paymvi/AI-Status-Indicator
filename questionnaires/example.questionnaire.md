<!-- questionnaire v1 -->
# Guest portal: notification settings

<!-- project: Toah Nipi Portal -->

I'm about to add **notification settings** to the guest portal. These questions decide what I build first.
The current code is in `src/portal/notifications/` and sends only booking-confirmation emails.

<!-- question id="channels" type="multiple" required="true" -->
## Which channels should guests be able to turn on?

Pick every channel the first version should support.

<!-- context -->
What exists today:

- Email goes through the existing `Mailer` (SMTP, already configured).
- There is **no** SMS provider yet; adding one needs an account and a monthly fee.
- Push needs the portal's service worker, which only exists on the staff app.

See the [mailer README](https://example.com/mailer) for limits.

<!-- options -->
- `email` Email
- `sms` SMS text messages
- `push` Browser push notifications

<!-- recommended: email, push -->
Email works today and push reuses code from the staff app. SMS adds a paid provider, so I'd leave it for later.

<!-- question id="default-state" type="single" required="true" -->
## Should notifications be on or off for existing guests?

<!-- context -->
The settings table will get a new column. Its default decides what about 4,200 existing guests receive:

```sql
ALTER TABLE guest_settings ADD COLUMN notify_email BOOLEAN NOT NULL DEFAULT TRUE;
-- a very long line to check that code blocks scroll inside themselves on the phone instead of widening the whole page ........................
```

~~~text
<!-- question id="not-a-question" type="single" -->
## This heading and directive are inside a code block, so they are just text.
~~~

<!-- options -->
- `on` On (guests can turn them off)
- `off` Off (guests must opt in)
- `ask` Ask each guest on their next login

<!-- recommended: off -->
Opt-in is the safer choice for privacy rules and avoids surprising existing guests.

<!-- question id="sender-name" type="short" required="false" -->
## What sender name should the emails use?

For example `Toah Nipi Reservations`. Leave it to me if you don't mind.

<!-- question id="wording" type="long" required="false" -->
## Anything specific the notification texts must say or avoid?

Paste existing wording if you have it. Code blocks and Markdown are fine.

![Current confirmation email](images/confirmation-email.png)

<!-- question id="launch" type="single" required="true" -->
## When should this go live?

<!-- options -->
- `now` As soon as it's tested
- `next-release` With the next planned release
- `behind-flag` Behind a feature flag, turned on later
