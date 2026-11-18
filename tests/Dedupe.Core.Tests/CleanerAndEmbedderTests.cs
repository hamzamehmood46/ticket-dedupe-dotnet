using Dedupe.Core;

namespace Dedupe.Core.Tests;

public class CleanerTests
{
    [Theory]
    [InlineData("Re: Hello", "hello")]
    [InlineData("RE: Re: FW: Hello", "hello")]
    [InlineData("fwd: Fwd: payment failed", "payment failed")]
    [InlineData("Reply to my question", "reply to my question")]
    [InlineData("Refund: not received", "refund: not received")]
    public void Subject_prefixes_are_removed_only_at_the_start(string subject, string expected)
    {
        Assert.StartsWith(expected, TicketCleaner.Clean(subject, ""));
    }

    [Fact]
    public void The_subject_is_counted_twice_and_everything_is_lowercase_with_single_spaces()
    {
        var text = TicketCleaner.Clean("Login   Broken", "It  fails\n\n  every   time");
        Assert.Equal("login broken. login broken. it fails every time", text);
    }

    [Fact]
    public void Quoted_lines_are_dropped()
    {
        var text = TicketCleaner.Clean("x", "My export fails.\n> earlier message about billing\n> more quoted text\nStill fails.");
        Assert.Contains("my export fails", text);
        Assert.Contains("still fails", text);
        Assert.DoesNotContain("billing", text);
    }

    [Theory]
    [InlineData("On Mon, Oct 5, 2026 at 9:14 AM Support <help@example.com> wrote:")]
    [InlineData("on 5 Oct 2026, Jamie wrote:")]
    [InlineData("-----Original Message-----")]
    [InlineData("---- Forwarded message ----")]
    [InlineData("--")]
    public void Everything_after_a_reply_header_or_signature_delimiter_is_dropped(string delimiter)
    {
        var text = TicketCleaner.Clean("x", $"The app crashes on launch.\n{delimiter}\nThe quoted thread talks about invoices and refunds.");
        Assert.Contains("app crashes", text);
        Assert.DoesNotContain("invoices", text);
        Assert.DoesNotContain("refunds", text);
    }

    [Fact]
    public void A_forwarded_header_block_ends_the_new_message()
    {
        var body = "Please look at this.\nFrom: Jamie <j@example.com>\nSent: Monday\nTo: Support\nSubject: old thread\nThe old thread is about taxes.";
        var text = TicketCleaner.Clean("x", body);
        Assert.Contains("please look at this", text);
        Assert.DoesNotContain("taxes", text);
    }

    [Fact]
    public void A_line_that_only_starts_with_From_is_not_a_header()
    {
        var text = TicketCleaner.Clean("x", "From: my account settings page I can't change the language.");
        Assert.Contains("change the language", text);
    }

    [Theory]
    [InlineData("Sent from my iPhone")]
    [InlineData("Get Outlook for Android")]
    [InlineData("Sent from Outlook")]
    public void Device_lines_are_dropped(string line)
    {
        var text = TicketCleaner.Clean("x", $"Login is broken.\n{line}\nAnd the page is slow.");
        Assert.DoesNotContain("iphone", text);
        Assert.DoesNotContain("outlook", text);
        Assert.DoesNotContain("android", text);
        Assert.Contains("page is slow", text);
    }

    [Theory]
    [InlineData("Thanks,")]
    [InlineData("Thank you!")]
    [InlineData("Kind regards")]
    [InlineData("Best,")]
    [InlineData("Cheers")]
    [InlineData("thanks again.")]
    public void A_sign_off_ends_the_message(string signOff)
    {
        var text = TicketCleaner.Clean("x", $"Refund is missing.\n{signOff}\nJamie Rivera\nAcme Corp, Finance");
        Assert.Contains("refund is missing", text);
        Assert.DoesNotContain("jamie", text);
        Assert.DoesNotContain("finance", text);
    }

    [Fact]
    public void A_sentence_that_merely_starts_with_Thanks_is_kept()
    {
        var text = TicketCleaner.Clean("x", "Thanks for the quick reply, but the export still fails.\nIt times out.");
        Assert.Contains("export still fails", text);
        Assert.Contains("times out", text);
    }

    [Fact]
    public void Confidentiality_disclaimers_are_dropped()
    {
        var text = TicketCleaner.Clean("x", "Webhook is late.\nThis email is confidential and intended recipient only.\nIt is by 20 minutes.");
        Assert.DoesNotContain("confidential", text);
        Assert.Contains("by 20 minutes", text);
    }

    [Theory]
    [InlineData("write to me at jamie.r+test@example.co.uk now", "jamie")]
    [InlineData("see https://example.com/orders/123456 for details", "https")]
    [InlineData("see www.example.com/page for details", "www")]
    [InlineData("order #48213 is wrong", "48213")]
    [InlineData("invoice INV-9001 is wrong", "9001")]
    [InlineData("ticket T-0042 again", "0042")]
    [InlineData("call +1 (555) 123-4567 please", "555")]
    [InlineData("card 1234567890 was used", "1234567890")]
    [InlineData("order 48213 is wrong", "48213")]
    [InlineData("account 1234567 is locked", "1234567")]
    [InlineData("reference 12345678 failed", "12345678")]
    [InlineData("id 123e4567-e89b-12d3-a456-426614174000 failed", "e89b")]
    [InlineData("token 0123456789abcdef0123 rejected", "0123456789abcdef")]
    public void Values_that_identify_one_customer_are_removed(string body, string mustNotRemain)
    {
        Assert.DoesNotContain(mustNotRemain, TicketCleaner.Clean("x", body));
    }

    [Theory]
    [InlineData("we get 429 errors", "429")]
    [InlineData("returns 401 unauthorized", "401")]
    [InlineData("the 2FA code fails", "2fa")]
    [InlineData("on version 7.1.2 it crashes", "7.1.2")]
    public void Short_numbers_that_matter_are_kept(string body, string mustRemain)
    {
        Assert.Contains(mustRemain, TicketCleaner.Clean("x", body));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "\n\n  ")]
    public void Empty_input_gives_empty_text(string? subject, string? body)
    {
        Assert.Equal("", TicketCleaner.Clean(subject, body));
    }

    [Fact]
    public void Only_a_subject_still_works()
    {
        Assert.Equal("refund missing. refund missing.", TicketCleaner.Clean("Refund missing", null));
    }

    [Fact]
    public void Output_is_cut_to_the_limit()
    {
        // 'z' rather than 'a': a long run of hex letters would be removed as a token before the limit applies.
        var text = TicketCleaner.Clean("x", new string('z', 20_000));
        Assert.Equal(TicketCleaner.MaxChars, text.Length);
    }

    [Fact]
    public void Windows_and_old_Mac_line_endings_are_handled()
    {
        Assert.DoesNotContain("quoted", TicketCleaner.Clean("x", "Real text.\r\n> quoted\r\nMore text."));
        Assert.DoesNotContain("quoted", TicketCleaner.Clean("x", "Real text.\r> quoted\rMore text."));
    }

    [Fact]
    public void The_demo_email_reduces_to_the_actual_problem()
    {
        var demo = SampleTickets.Demos.Single(d => d.Ticket.Subject.StartsWith("Re: Re:"));
        var text = TicketCleaner.Clean(demo.Ticket.Subject, demo.Ticket.Body);
        Assert.Contains("billed twice", text);
        Assert.DoesNotContain("48213", text);
        Assert.DoesNotContain("jamie", text);
        Assert.DoesNotContain("iphone", text);
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain("how can we help", text);
    }

    [Fact]
    public void Cleaning_never_throws_on_odd_input()
    {
        foreach (var s in new[] { "\0\0\0", "😀 emoji", new string('>', 5000), string.Concat(Enumerable.Repeat("On x wrote:\n", 100)), "-- \n-- \n--" })
        {
            Assert.NotNull(TicketCleaner.Clean(s, s));
        }
    }
}

public class EmbedderTests
{
    private static readonly HashingEmbedder Plain = new();

    private static double Sim(string a, string b) => Vectors.Cosine(Plain.Embed(a), Plain.Embed(b));

    [Fact]
    public void Vectors_have_unit_length()
    {
        Assert.Equal(1.0, Vectors.Norm(Plain.Embed("the export times out on large ranges")), 5);
    }

    [Fact]
    public void Embedding_is_deterministic_across_instances()
    {
        Assert.Equal(new HashingEmbedder().Embed("webhook delivered late"), new HashingEmbedder().Embed("webhook delivered late"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("the and of to it is")]
    [InlineData("a b c")]
    public void Text_with_no_usable_words_is_a_zero_vector(string text)
    {
        Assert.True(Vectors.IsZero(Plain.Embed(text)));
    }

    [Theory]
    [InlineData("can't sign in", "cannot login")]
    [InlineData("signed in", "login")]
    [InlineData("log-in fails", "login fails")]
    [InlineData("it logs me out", "logout")]
    [InlineData("keeps signing me out", "keeps logout")]
    [InlineData("export timed out", "export timeout")]
    [InlineData("export time out", "export timeout")]
    [InlineData("two-factor code", "2fa code")]
    [InlineData("mfa code", "2fa code")]
    [InlineData("rate limited", "ratelimit")]
    [InlineData("charged twice", "billed twice")]
    [InlineData("double charge", "twice charge")]
    [InlineData("alerts stopped", "notifications stopped")]
    [InlineData("money back", "refund")]
    [InlineData("crashes crashed crashing", "crash crash crash")]
    [InlineData("can't read the text", "unreadable text")]
    public void Synonyms_and_word_endings_are_folded_together(string a, string b)
    {
        Assert.True(Sim(a, b) > 0.99, $"'{a}' vs '{b}' = {Sim(a, b):0.000}");
    }

    [Fact]
    public void Different_problems_score_low()
    {
        Assert.True(Sim("refund missing after cancel", "dark mode text unreadable") < 0.1);
        Assert.True(Sim("webhook delay", "csv column missing") < 0.1);
    }

    [Fact]
    public void Word_order_matters_a_little_through_word_pairs()
    {
        var s = Sim("login failed badly", "badly failed login");
        Assert.InRange(s, 0.4, 0.99);
    }

    [Fact]
    public void The_same_text_scores_one_with_itself()
    {
        Assert.Equal(1.0, Sim("the app crashes at startup", "the app crashes at startup"), 5);
    }

    [Fact]
    public void Fitted_weights_make_rare_words_count_for_more()
    {
        var docs = new[] { "error export", "error login", "error billing", "error webhook", "error refund missing" };
        var fitted = HashingEmbedder.Fit(docs);

        var q = fitted.Embed("error refund");
        var rare = Vectors.Cosine(q, fitted.Embed("refund"));
        var common = Vectors.Cosine(q, fitted.Embed("error"));
        Assert.True(rare > common, $"rare {rare:0.000} should beat common {common:0.000}");

        // Unfitted, the two words weigh the same.
        var u = Plain.Embed("error refund");
        Assert.Equal(Vectors.Cosine(u, Plain.Embed("refund")), Vectors.Cosine(u, Plain.Embed("error")), 3);
    }

    [Fact]
    public void Fit_with_no_documents_still_works()
    {
        var e = HashingEmbedder.Fit([]);
        Assert.False(Vectors.IsZero(e.Embed("refund")));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(100_000)]
    public void Dimensions_are_range_checked(int dims)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashingEmbedder(dims));
    }

    [Fact]
    public void The_idf_table_must_match_the_dimensions()
    {
        Assert.Throws<ArgumentException>(() => new HashingEmbedder(64, new float[10]));
    }

    [Fact]
    public void Smaller_embedders_have_that_many_dimensions()
    {
        Assert.Equal(64, new HashingEmbedder(64).Embed("refund").Length);
        Assert.Equal(HashingEmbedder.DefaultDimensions, Plain.Dimensions);
    }

    [Fact]
    public void Tokens_drop_stopwords_and_short_words_but_keep_error_codes()
    {
        var tokens = HashingEmbedder.Tokens("The API returns 429 and a b c errors");
        Assert.Contains("429", tokens);
        Assert.DoesNotContain("the", tokens);
        Assert.DoesNotContain("a", tokens);
        Assert.Contains("api", tokens);
    }

    [Fact]
    public void Tokens_are_stemmed_consistently()
    {
        Assert.Equal(HashingEmbedder.Tokens("invoice"), HashingEmbedder.Tokens("invoices"));
        Assert.Equal(HashingEmbedder.Tokens("updated"), HashingEmbedder.Tokens("updates"));
        Assert.Equal(HashingEmbedder.Tokens("arrive"), HashingEmbedder.Tokens("arriving"));
        Assert.Equal(["access"], HashingEmbedder.Tokens("access"));
    }

    [Fact]
    public void Embedding_never_throws_on_odd_text()
    {
        foreach (var s in new[] { "\0", "😀", new string('x', 100_000), string.Join(" ", Enumerable.Range(0, 5000)) })
        {
            // Either nothing usable was found (a zero vector) or the vector has unit length.
            var norm = Vectors.Norm(Plain.Embed(s));
            Assert.True(norm == 0 || Math.Abs(norm - 1) < 1e-5, $"norm was {norm}");
        }
    }
}

public class VectorMathTests
{
    [Fact]
    public void Cosine_of_identical_orthogonal_and_opposite_vectors()
    {
        Assert.Equal(1.0, Vectors.Cosine([1, 2, 3], [2, 4, 6]), 6);
        Assert.Equal(0.0, Vectors.Cosine([1, 0], [0, 1]), 6);
        Assert.Equal(-1.0, Vectors.Cosine([1, 0], [-1, 0]), 6);
    }

    [Fact]
    public void A_zero_vector_matches_nothing()
    {
        Assert.Equal(0, Vectors.Cosine([0, 0], [1, 1]));
        Assert.Equal(0, Vectors.Cosine([0, 0], [0, 0]));
    }

    [Fact]
    public void Different_lengths_are_an_error()
    {
        Assert.Throws<ArgumentException>(() => Vectors.Cosine([1, 2], [1, 2, 3]));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Non_finite_values_are_detected(float bad)
    {
        Assert.False(Vectors.IsFinite([1, bad]));
        Assert.False(Vectors.IsFinite([]));
        Assert.True(Vectors.IsFinite([0, -1, 2.5f]));
    }
}
