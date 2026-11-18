namespace Dedupe.Core;

/// <summary>
/// Invented support tickets for the demo and the tests: 20 recurring problems with four differently worded reports
/// each, plus 20 one-off questions. Several groups share vocabulary on purpose (password reset vs reset email vs
/// two-factor, refund vs double charge vs renewal, export vs totals vs scheduled report), so a match on shared words
/// alone is wrong sometimes. The data was written by hand, not taken from any real company, and the wording is cleaner
/// than real tickets, so scores measured on it are optimistic.
/// </summary>
public static class SampleTickets
{
    /// <summary>"Today" for the demo, so a "last 30 days" filter means the same thing every time.</summary>
    public static DateTime DemoNow { get; } = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Start = new(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Group(string Id, string Product, string Resolution, (string Subject, string Body)[] Reports);

    private static readonly Group[] Groups =
    [
        new("ACC-LOOP", "Accounts", "Cleared the stale session cookie and re-issued the reset link; the old link was invalidating the new password. Fixed in release 4.2.",
        [
            ("Can't log in after resetting my password", "I reset my password this morning and now every time I try to sign in it sends me straight back to the login page. The password is definitely right."),
            ("Login keeps looping after password reset", "Changed my password using the reset link. Now when I log in the page just reloads and asks me to log in again."),
            ("Stuck on sign in screen since new password", "Since I set a new password I get bounced back to the sign in screen after entering it. Tried two browsers."),
            ("Password reset worked but I still can't get in", "The reset email worked and I picked a new password, but logging in just refreshes the page with no error."),
        ]),
        new("ACC-RESET", "Accounts", "Reset emails were being held by the spam filter for the customer's domain; allow-listed the sender and resent.",
        [
            ("Password reset email never arrives", "I click forgot password and wait but no email shows up. Checked spam too."),
            ("Not receiving the forgot password email", "Asked for a reset link three times and nothing arrives in my inbox or junk folder."),
            ("Reset link email missing", "No email comes after I request a password reset. My address is correct and I can receive other mail from you."),
            ("Forgot password does not send anything", "Pressing forgot password says an email was sent but I have waited an hour and received nothing."),
        ]),
        new("ACC-2FA", "Accounts", "Phone clock drift made the authenticator codes invalid; had the user sync the time and re-enrolled the device.",
        [
            ("Two-factor code says invalid", "My authenticator app gives a six digit code and your site rejects it every time."),
            ("2FA code not accepted", "I type the code from my authenticator and it says invalid code. Tried several fresh codes."),
            ("Authenticator codes always fail", "Every code from my authentication app is rejected at the verification step, so I can't log in."),
            ("Verification code wrong error on login", "Login asks for a verification code from my app but says the code is incorrect even though I copy it right away."),
        ]),
        new("ACC-LOCK", "Accounts", "Unlocked the account and raised the failed-attempt limit for the shared office address; asked the team to stop reusing an old saved password.",
        [
            ("Account locked after too many attempts", "My account got locked after a few wrong passwords and now I can't try again."),
            ("Locked out of my account", "It says my account is locked because of too many failed login attempts. How do I unlock it?"),
            ("Too many failed logins, account disabled", "After typing the wrong password a few times my account was disabled for failed attempts."),
            ("Account suspended for failed sign in attempts", "I'm locked out. The message says too many failed attempts and to contact support."),
        ]),

        new("BIL-TWICE", "Billing", "Duplicate charge caused by a retried payment during a gateway timeout; refunded the second charge and added idempotency to the checkout.",
        [
            ("Charged twice for the same invoice", "I see two identical charges on my card for the invoice this month."),
            ("Double charge on my card", "My card was billed two times for one subscription payment. Please refund one."),
            ("Duplicate payment taken for one order", "The same payment came out of my account twice on the same day."),
            ("Billed twice this month", "There are two charges for the monthly plan on my statement, same amount, same day."),
        ]),
        new("BIL-REFUND", "Billing", "The refund was issued but pending with the bank; gave the customer the refund reference and the expected 5-10 business days.",
        [
            ("Refund not received after cancelling", "I cancelled and was told I'd get a refund two weeks ago. Nothing has come back."),
            ("Still waiting for my refund", "Support approved a refund for my cancelled plan but the money never reached my account."),
            ("Where is my refund?", "Cancelled my subscription, the refund confirmation arrived, but it has been weeks and no money."),
            ("Refund missing", "I was promised a refund when I cancelled. It still has not appeared on my statement."),
        ]),
        new("BIL-TAX", "Billing", "The tax region was set from the billing address instead of the shipping address; corrected the tax profile and reissued the invoice.",
        [
            ("Invoice shows wrong tax rate", "The tax on my latest invoice is calculated at the wrong rate for my state."),
            ("Incorrect sales tax charged", "I'm being taxed at a rate that does not match where I am. The invoice total is too high."),
            ("Tax amount on invoice looks wrong", "The VAT line on the invoice is higher than it should be for my country."),
            ("Wrong tax applied to my bill", "My bill has the wrong tax percentage applied, please correct it and resend the invoice."),
        ]),
        new("BIL-RENEW", "Billing", "The renewal used an expired saved card; asked the customer to update the card and retried the payment.",
        [
            ("Card declined when plan renewed", "My plan failed to renew because the payment was declined but my card is fine."),
            ("Renewal payment failed", "The automatic renewal did not go through and my account is now past due."),
            ("Payment declined at renewal even though card works", "It says my card was declined at renewal but I used it elsewhere today."),
            ("Subscription renewal failed, card declined", "Got an email that my renewal was declined. The card on file is valid."),
        ]),

        new("MOB-CRASH", "Mobile", "The crash came from a corrupted local cache after the update; clearing the app storage fixed it. Patched in 7.1.2.",
        [
            ("App crashes on launch after update", "Since the last update the app closes immediately when I open it."),
            ("Crashes as soon as I open the app", "After updating, the app opens for a second and then shuts down every time."),
            ("App won't open since the new version", "The latest update broke it: tap the icon and it just crashes back to the home screen."),
            ("Immediate crash on startup", "The app crashes right at the splash screen since I updated."),
        ]),
        new("MOB-PUSH", "Mobile", "Notifications were disabled by the phone's battery saver; guided the user to exempt the app and re-registered the push token.",
        [
            ("Push notifications not arriving", "I don't get any alerts on my phone anymore."),
            ("No notifications on my phone", "Notifications stopped working. I used to get alerts for new messages."),
            ("Not getting push alerts", "The app never sends me notifications now, even though they are turned on in settings."),
            ("Alerts stopped coming to my phone", "Haven't had a single push notification in days, and notifications are enabled."),
        ]),
        new("MOB-LOGOUT", "Mobile", "Refresh tokens were expiring after 24 hours on mobile because of a config error; extended to 30 days in 7.2.",
        [
            ("App logs me out every day", "I have to sign in again each morning on the mobile app."),
            ("Keeps signing me out on mobile", "The phone app forgets my login overnight and asks me to log in every day."),
            ("Session expires daily in the app", "Each day my session ends and I need to enter my password again in the mobile app."),
            ("Mobile app does not stay logged in", "Logged in yesterday and today I am logged out again. It happens every day."),
        ]),
        new("MOB-DARK", "Mobile", "The dark theme used grey text on a grey background in the settings screen; contrast fixed in 7.3.",
        [
            ("Dark mode text is unreadable", "In dark mode the settings screen is grey on grey and I can't read anything."),
            ("Can't read text with dark theme", "The dark theme makes some labels almost invisible, very low contrast."),
            ("Low contrast in dark mode", "The text in dark mode is too faint to read on the settings page."),
            ("Dark theme grey text on dark background", "Everything is hard to see in dark theme, especially the menu labels."),
        ]),

        new("API-429", "API", "The rate limit was keyed by IP and the customer's gateway shared one IP; switched their key to per-token limits.",
        [
            ("Getting 429 errors at low volume", "Our integration sends only a few requests per minute but keeps getting 429 too many requests."),
            ("Rate limited even though traffic is tiny", "We hit 429 responses with maybe ten calls a minute, far below the documented limit."),
            ("429 too many requests, low usage", "The API returns 429 although we make very few calls."),
            ("Unexpected rate limit responses", "Receiving 429 errors from the API on light usage, nowhere near the limit."),
        ]),
        new("API-WEBHOOK", "API", "The webhook queue had backed up behind a slow endpoint belonging to another tenant; moved their deliveries to a dedicated queue.",
        [
            ("Webhook deliveries arriving late", "Our webhook events show up ten to twenty minutes after they happen."),
            ("Webhooks delayed by many minutes", "Events are delivered to our endpoint very late, sometimes almost half an hour."),
            ("Webhook events slow to arrive", "We get the webhook notifications but long after the action took place."),
            ("Delayed webhook notifications", "Webhooks used to be instant, now they come after a long delay."),
        ]),
        new("API-401", "API", "The rotated key had not propagated to the edge cache; flushed it and added a 5-minute overlap window for old keys.",
        [
            ("401 unauthorized after rotating API key", "We rotated our API key and now every request returns 401 even with the new key."),
            ("New API key returns 401", "Created a new key and updated our config but all calls come back unauthorized."),
            ("Unauthorized errors since key rotation", "After rotating the key we get 401 on every call. The new key is correct."),
            ("API key rotation breaks authentication", "Swapped to the rotated key and now authentication fails with 401."),
        ]),
        new("API-PAGING", "API", "The cursor was sorted by a non-unique timestamp; added the record id as a tie-breaker.",
        [
            ("Pagination returns duplicate records", "Paging through results I see the same items on two consecutive pages."),
            ("Same items repeated across pages", "When I loop through the next page cursor some records appear twice."),
            ("Duplicate rows when paging", "The list endpoint gives overlapping results between page one and page two."),
            ("Page results overlap", "Following the cursor returns items I already received on the previous page."),
        ]),

        new("REP-CSV", "Reports", "The export skipped the last column when a field contained a comma without quotes; fixed the CSV escaping.",
        [
            ("CSV export is missing the last column", "The exported CSV file has one column fewer than the report on screen."),
            ("Exported file drops the final column", "When I download the report as CSV the last column is gone."),
            ("Missing column in CSV download", "My CSV export doesn't include the last field that I can see in the app."),
            ("Export cuts off a column", "The downloaded spreadsheet is missing the right-most column compared to the dashboard."),
        ]),
        new("REP-TOTALS", "Reports", "The dashboard excluded refunded orders while the report included them; aligned both on the same definition.",
        [
            ("Report totals don't match the dashboard", "The total in my report is different from the number on the dashboard for the same dates."),
            ("Numbers differ between report and dashboard", "Revenue in the report is higher than what the dashboard shows for last month."),
            ("Dashboard and report disagree", "Two different totals for the same period, one in the report and one on the dashboard."),
            ("Mismatch between report total and dashboard figure", "The report sum does not equal the dashboard total for the same date range."),
        ]),
        new("REP-SCHEDULE", "Reports", "The schedule was paused when the owner's seat was removed; reassigned the schedule to an active user.",
        [
            ("Scheduled report never arrives", "My weekly report email stopped coming."),
            ("Weekly report email not sent", "The scheduled report that goes out every Monday did not arrive for three weeks."),
            ("Scheduled email report stopped", "I set up a recurring report and it no longer lands in my inbox."),
            ("Recurring report not delivered", "Our team's automatic report emails stopped being delivered."),
        ]),
        new("REP-TIMEOUT", "Reports", "Large exports ran inside the request; moved them to a background job with an emailed download link.",
        [
            ("Export times out on large date range", "Exporting a full year of data fails with a timeout."),
            ("Report export fails for big ranges", "Selecting a long date range and exporting gives a timeout error."),
            ("Timeout when exporting a year of data", "The export spins and then fails with a time out when the range is large."),
            ("Large export never finishes", "Exports for long periods time out before the file downloads."),
        ]),
    ];

    private static readonly (string Product, string Subject, string Body)[] OneOffs =
    [
        ("Accounts", "Change the email address on my account", "How do I update the email address linked to my account?"),
        ("Accounts", "Add a teammate to my workspace", "I want to invite a colleague, where is the invite button?"),
        ("Accounts", "Delete my account and data", "Please remove my account and all stored data under privacy law."),
        ("Accounts", "Rename my workspace", "Can I change the name of our workspace?"),
        ("Billing", "Need a receipt for expense report", "Can you send a PDF receipt for last quarter's payments for my finance team?"),
        ("Billing", "Switch from monthly to yearly plan", "I'd like to move to yearly billing, will I be prorated?"),
        ("Billing", "Update company name on invoices", "Our company changed its name and the invoices need the new name."),
        ("Billing", "Do you offer a nonprofit discount", "We are a registered charity, is there a discounted plan?"),
        ("Mobile", "Feature request: widget for home screen", "It would be great to have a home screen widget showing today's summary."),
        ("Mobile", "Is there an iPad version", "Will the app get a tablet layout?"),
        ("Mobile", "How to change app language", "Where can I switch the language in the mobile app?"),
        ("Mobile", "Biometric unlock option", "Can I use fingerprint or face unlock to open the app?"),
        ("API", "Request: bulk delete endpoint", "We need an API call to delete many records at once."),
        ("API", "Where are the API changelogs", "Is there a page that lists breaking changes to the API?"),
        ("API", "Sandbox environment access", "Do you offer a sandbox where we can test without touching production?"),
        ("API", "Increase request body size limit", "Our payload is a bit over the maximum size, can the limit be raised?"),
        ("Reports", "Add a custom date filter to reports", "Can we filter reports by fiscal quarter?"),
        ("Reports", "Share a report with a client", "I'd like to share a read-only report link with an outside client."),
        ("Reports", "Chart colors for color blind users", "Are there colour blind friendly palettes for charts?"),
        ("Reports", "Schedule report in a different time zone", "Can the scheduled report be sent at 8am in my time zone?"),
    ];

    public static IReadOnlyList<string> Products { get; } = ["Accounts", "Billing", "Mobile", "API", "Reports"];

    /// <summary>The 100 sample tickets, oldest first, with ids T-0001 and up. Each group's first two reports are resolved and the last two are still open.</summary>
    public static IReadOnlyList<Ticket> All { get; } = Build();

    private static List<Ticket> Build()
    {
        var raw = new List<(DateTime At, string Product, string Subject, string Body, TicketStatus Status, string? Resolution, string? Group)>();

        for (var g = 0; g < Groups.Length; g++)
        {
            var group = Groups[g];
            for (var v = 0; v < group.Reports.Length; v++)
            {
                var at = Start.AddDays(g * 2 + v * 19).AddHours(v * 3);
                var resolved = v < 2;
                raw.Add((at, group.Product, group.Reports[v].Subject, group.Reports[v].Body,
                    resolved ? TicketStatus.Resolved : TicketStatus.Open, resolved ? group.Resolution : null, group.Id));
            }
        }

        for (var s = 0; s < OneOffs.Length; s++)
        {
            var (product, subject, body) = OneOffs[s];
            raw.Add((Start.AddDays(s * 5 + 3), product, subject, body, TicketStatus.Resolved, "Answered with the relevant help-centre article.", null));
        }

        return raw
            .OrderBy(r => r.At)
            .ThenBy(r => r.Subject, StringComparer.Ordinal)
            .Select((r, i) => new Ticket($"T-{i + 1:0000}", r.Product, r.Subject, r.Body, r.At, r.Status, r.Resolution, r.Group))
            .ToList();
    }

    /// <summary>A new ticket for the demo, and what it should find, so the demo and the tests agree.</summary>
    public sealed record Demo(string Label, NewTicket Ticket, string? ExpectedGroup, string Note);

    public static IReadOnlyList<Demo> Demos { get; } =
    [
        new("A duplicate, new wording", new NewTicket("Locked out - says too many failed attempts",
            "My account is locked since I mistyped my password a few times. Please unlock me.", DemoNow),
            "ACC-LOCK", "Same problem as four past tickets, worded differently."),
        new("Related, but a different problem", new NewTicket("Reset link works but my 2FA code fails afterwards",
            "I reset my password and logged in, then the authenticator code I type is rejected.", DemoNow),
            null, "Mixes two known problems (password reset and two-factor). Both come back, and the closest one scores only just over the duplicate line, which shows how thin the margin can be."),
        new("Brand new problem", new NewTicket("Can you add a Slack integration?",
            "We would like ticket updates posted to a Slack channel.", DemoNow),
            null, "Nothing like it in the history, so nothing should be suggested."),
        new("An email reply with a quoted thread", new NewTicket("Re: Re: Charged two times!!",
            "Hi team,\n\nI got billed twice for order #48213 on my card ending 4242, please fix this.\n\nThanks,\nJamie Rivera\nSent from my iPhone\n\n" +
            "On Mon, Oct 5, 2026 at 9:14 AM Support <help@example.com> wrote:\n> Hello Jamie, how can we help today?\n> Our team is online until 6pm.\n", DemoNow),
            "BIL-TWICE", "Signature, device line and the quoted thread are removed before matching."),
    ];
}
