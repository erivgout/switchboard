using MimeKit;
using Switchboard.Modules.Messaging.Inbound;
using Switchboard.Modules.Messaging.Notifications;
using Switchboard.Modules.Messaging.Outbound;

namespace Switchboard.Modules.Messaging.Tests;

public sealed class TemplateRendererTests
{
    private static readonly Dictionary<string, TemplateValue> Values = new()
    {
        ["ticket.subject"] = TemplateValue.Text("<script>alert('x')</script> & \"quotes\""),
        ["message.body"] = TemplateValue.Html("<p><strong>Bold</strong> reply</p>"),
        ["requester.name"] = TemplateValue.Text("Dana\r\nBcc: evil@example.com"),
    };

    [Fact]
    public void Text_values_are_html_encoded()
    {
        var html = TemplateRenderer.RenderHtml("<p>{{ticket.subject}}</p>", Values);
        Assert.Equal("<p>&lt;script&gt;alert(&#39;x&#39;)&lt;/script&gt; &amp; &quot;quotes&quot;</p>", html);
    }

    [Fact]
    public void Sanitized_html_values_are_inserted_as_html()
    {
        Assert.Equal("<div><p><strong>Bold</strong> reply</p></div>", TemplateRenderer.RenderHtml("<div>{{ message.body }}</div>", Values));
    }

    [Fact]
    public void Unknown_placeholders_render_as_nothing_and_are_reported()
    {
        Assert.Equal("<p>Hi </p>", TemplateRenderer.RenderHtml("<p>Hi {{user.password}}</p>", Values));
        Assert.Equal(["user.password"], TemplateRenderer.UnknownPlaceholders("{{ticket.id}} {{user.password}}"));
    }

    [Fact]
    public void Subjects_are_one_line_so_headers_cannot_be_injected()
    {
        var subject = TemplateRenderer.RenderSubject("For {{requester.name}}: {{message.body}}", Values);
        Assert.Equal("For Dana Bcc: evil@example.com: Bold reply", subject);
        Assert.DoesNotContain('\n', subject);
    }

    [Fact]
    public void No_expressions_or_code_are_evaluated()
    {
        var template = "{{ticket.subject | upcase}} {{ 1+1 }} {% for x in y %}{{#each}}@(System.IO.File.Delete(\"x\"))";
        Assert.Equal(template, TemplateRenderer.RenderHtml(template, Values));
    }

    [Fact]
    public void Ticket_token_is_added_once()
    {
        Assert.Equal("Resolved: VPN [INC-7]", TicketMailer.WithToken("Resolved: VPN", "INC-7"));
        Assert.Equal("Re: VPN [INC-7]", TicketMailer.WithToken("Re: VPN [INC-7]", "INC-7"));
    }
}

public sealed class QuotedTextTests
{
    [Fact]
    public void Plain_text_cuts_at_on_wrote_and_quotes()
    {
        var text = "Works now, thanks!\n\nOn Mon, 26 Oct 2026 at 10:02, IT Support <help@example.com> wrote:\n> Please restart.\n> Sam";
        var result = QuotedText.StripText(text);
        Assert.True(result.Removed);
        Assert.Equal("Works now, thanks!", result.Content);
    }

    [Fact]
    public void Plain_text_cuts_at_wrapped_on_wrote_line()
    {
        var text = "Done.\n\nOn Mon, 26 Oct 2026 at 10:02, IT Support\n<help@example.com> wrote:\n> old";
        Assert.Equal("Done.", QuotedText.StripText(text).Content);
    }

    [Fact]
    public void Plain_text_cuts_at_our_reply_marker()
    {
        var text = $"Yes please go ahead.\n\n{EmailLayout.ReplyMarker}\n\nHi Dana, shall I reimage it?";
        Assert.Equal("Yes please go ahead.", QuotedText.StripText(text).Content);
    }

    [Fact]
    public void Plain_text_cuts_outlook_headers_and_signatures()
    {
        var outlook = "See attached.\n\n________________________________\nFrom: IT Support <help@example.com>\nSent: Monday, October 26, 2026 10:02 AM\nTo: Dana\nSubject: RE: VPN";
        Assert.Equal("See attached.", QuotedText.StripText(outlook).Content);

        var original = "Ok.\n\n-----Original Message-----\nFrom: IT";
        Assert.Equal("Ok.", QuotedText.StripText(original).Content);

        var signature = "Thanks\n-- \nDana Whitfield\nFinance\nSent from my iPhone";
        Assert.Equal("Thanks", QuotedText.StripText(signature).Content);
    }

    [Fact]
    public void Mobile_signature_lines_are_dropped()
    {
        Assert.Equal("On my way", QuotedText.StripText("On my way\n\nSent from my iPhone").Content);
    }

    [Fact]
    public void Html_gmail_quote_is_removed()
    {
        var html = "<div dir=\"ltr\">That worked.</div><br><div class=\"gmail_quote\"><div class=\"gmail_attr\">On Mon, Oct 26, 2026 at 10:02 AM IT &lt;help@example.com&gt; wrote:<br></div><blockquote class=\"gmail_quote\">Restart it.</blockquote></div>";
        var result = QuotedText.StripHtml(html);
        Assert.True(result.Removed);
        Assert.Contains("That worked.", result.Content);
        Assert.DoesNotContain("Restart it", result.Content);
        Assert.DoesNotContain("wrote:", result.Content);
    }

    [Fact]
    public void Html_outlook_header_block_is_removed()
    {
        var html = "<div><p>Please close it.</p><div id=\"appendonsend\"></div><hr><div id=\"divRplyFwdMsg\"><b>From:</b> IT<br><b>Sent:</b> Monday</div><div>Old message text</div></div>";
        var result = QuotedText.StripHtml(html);
        Assert.Contains("Please close it.", result.Content);
        Assert.DoesNotContain("Old message text", result.Content);
    }

    [Fact]
    public void Html_outlook_desktop_border_block_is_removed()
    {
        var html = "<p>Fine by me.</p><div style=\"border:none;border-top:solid #E1E1E1 1.0pt;padding:3.0pt 0in 0in 0in\"><p><b>From:</b> IT Support<br><b>Sent:</b> Monday<br><b>To:</b> Dana</p></div><p>Earlier text</p>";
        var result = QuotedText.StripHtml(html);
        Assert.Contains("Fine by me.", result.Content);
        Assert.DoesNotContain("Earlier text", result.Content);
    }

    [Fact]
    public void Html_apple_mail_cite_and_marker_are_removed()
    {
        var html = "<div>Sure.</div><div><br><blockquote type=\"cite\">old</blockquote></div>";
        Assert.DoesNotContain("old", QuotedText.StripHtml(html).Content);

        var marker = $"<p>Approved.</p><table><tr><td>{EmailLayout.ReplyMarker}</td></tr><tr><td>Hi Dana</td></tr></table>";
        var result = QuotedText.StripHtml(marker);
        Assert.Contains("Approved.", result.Content);
        Assert.DoesNotContain("Hi Dana", result.Content);
    }

    [Fact]
    public void Nothing_left_keeps_the_original()
    {
        var html = "<blockquote type=\"cite\">Only a quote</blockquote>";
        var result = QuotedText.StripHtml(html);
        Assert.False(result.Removed);
        Assert.Contains("Only a quote", result.Content);
    }

    [Fact]
    public void Messages_without_quotes_are_untouched()
    {
        var text = "Hello,\n\nMy printer is jammed.\n\nThanks";
        var result = QuotedText.StripText(text);
        Assert.False(result.Removed);
        Assert.Equal(text, result.Content);
    }
}

public sealed class MailAnalysisTests
{
    private static MimeMessage Message(string subject = "Hello", params (string Name, string Value)[] headers)
    {
        var m = new MimeMessage { Subject = subject };
        m.From.Add(MailboxAddress.Parse("pat@example.com"));
        foreach (var (name, value) in headers)
        {
            m.Headers.Add(name, value);
        }
        m.Body = new TextPart("plain") { Text = "x" };
        return m;
    }

    [Theory]
    [InlineData("Auto-Submitted", "auto-replied", AutomaticKind.AutoReply)]
    [InlineData("Auto-Submitted", "auto-generated", AutomaticKind.AutoGenerated)]
    [InlineData("X-Autoreply", "yes", AutomaticKind.AutoReply)]
    [InlineData("X-Autorespond", "yes", AutomaticKind.AutoReply)]
    [InlineData("Precedence", "bulk", AutomaticKind.AutoGenerated)]
    [InlineData("Precedence", "junk", AutomaticKind.AutoGenerated)]
    [InlineData("Precedence", "list", AutomaticKind.AutoGenerated)]
    [InlineData("Precedence", "auto_reply", AutomaticKind.AutoReply)]
    [InlineData("Return-Path", "<>", AutomaticKind.AutoGenerated)]
    [InlineData("Auto-Submitted", "no", AutomaticKind.None)]
    public void Automatic_mail_is_recognized_by_headers(string header, string value, AutomaticKind expected)
    {
        Assert.Equal(expected, MailAnalysis.DetectAutomatic(Message("Hello", (header, value))).Kind);
    }

    [Theory]
    [InlineData("Automatic reply: Your request", AutomaticKind.AutoReply)]
    [InlineData("Out of Office: back Monday", AutomaticKind.AutoReply)]
    [InlineData("Re: printer", AutomaticKind.None)]
    public void Automatic_replies_are_recognized_by_subject(string subject, AutomaticKind expected)
    {
        Assert.Equal(expected, MailAnalysis.DetectAutomatic(Message(subject)).Kind);
    }

    [Fact]
    public void Daemon_failure_notices_are_bounces()
    {
        var m = new MimeMessage { Subject = "Undeliverable: Your request" };
        m.From.Add(MailboxAddress.Parse("MAILER-DAEMON@mx.example.com"));
        m.Body = new TextPart("plain") { Text = "failed" };
        Assert.Equal(AutomaticKind.Bounce, MailAnalysis.DetectAutomatic(m).Kind);
    }

    [Fact]
    public void Authentication_results_are_read_from_the_top_header_only()
    {
        var m = Message("Hi",
            ("Authentication-Results", "mx.ours.test; spf=fail smtp.mailfrom=pat@example.com; dkim=none; dmarc=fail header.from=example.com"),
            ("Authentication-Results", "forged.example; spf=pass; dkim=pass; dmarc=pass"));
        var check = MailAnalysis.ReadAuthentication(m, null);
        Assert.Equal(AuthVerdict.Fail, check.Verdict);
        Assert.Equal("mx.ours.test", check.AuthServId);

        // With a trusted server named, other servers' headers are ignored.
        var trusted = MailAnalysis.ReadAuthentication(m, "forged.example");
        Assert.Equal(AuthVerdict.Pass, trusted.Verdict);
        Assert.Equal(AuthVerdict.None, MailAnalysis.ReadAuthentication(m, "other.test").Verdict);
    }

    // The test message is from pat@example.com.
    [Theory]
    [InlineData("mx; spf=pass smtp.mailfrom=pat@example.com; dkim=pass header.d=example.com; dmarc=pass header.from=example.com", AuthVerdict.Pass)]
    [InlineData("mx; spf=pass smtp.mailfrom=bounces@mail.example.com", AuthVerdict.Pass)]
    [InlineData("mx; dkim=pass header.d=example.com header.s=sel", AuthVerdict.Pass)]
    [InlineData("mx; spf=softfail (domain does not designate) smtp.mailfrom=pat@example.com", AuthVerdict.None)]
    [InlineData("mx; dkim=fail header.d=example.com", AuthVerdict.Fail)]
    [InlineData("mx; dmarc=fail header.from=example.com; dkim=pass header.d=example.com", AuthVerdict.Fail)]
    [InlineData("mx; none", AuthVerdict.None)]
    // A pass for someone else's domain doesn't vouch for the From address.
    [InlineData("mx; spf=pass smtp.mailfrom=attacker@evil.test; dkim=pass header.d=evil.test", AuthVerdict.None)]
    [InlineData("mx; dkim=pass header.d=com", AuthVerdict.None)]
    public void Authentication_verdicts(string header, AuthVerdict expected)
    {
        Assert.Equal(expected, MailAnalysis.ReadAuthentication(Message("Hi", ("Authentication-Results", header)), null).Verdict);
    }

    [Fact]
    public void Email_commands_are_read_from_the_top_only()
    {
        var parsed = EmailCommands.Parse("\n#status Resolved\n#priority: high\n\nFixed it.\n#tag later");
        Assert.Equal(2, parsed.Commands.Count);
        Assert.Equal(("status", "Resolved"), (parsed.Commands[0].Name, parsed.Commands[0].Argument));
        Assert.Equal(("priority", "high"), (parsed.Commands[1].Name, parsed.Commands[1].Argument));
        Assert.Equal("Fixed it.\n#tag later", parsed.RemainingText);
        Assert.Empty(EmailCommands.Parse("Hello\n#status Closed").Commands);
    }

    [Fact]
    public void Non_production_allowlist_matches_addresses_and_domains()
    {
        Assert.True(MailDispatcher.IsAllowlisted("qa@test.example.com", ["test.example.com"]));
        Assert.True(MailDispatcher.IsAllowlisted("QA@example.com", ["qa@example.com"]));
        Assert.False(MailDispatcher.IsAllowlisted("dana@example.com", ["qa@example.com", "test.example.com"]));
    }

    [Fact]
    public void Sender_rule_patterns_are_normalized()
    {
        Assert.Equal("example.com", AddressRules.NormalizePattern("@Example.com"));
        Assert.Equal("pat@example.com", AddressRules.NormalizePattern(" Pat@Example.com "));
        Assert.Null(AddressRules.NormalizePattern("not a domain"));
        Assert.Null(AddressRules.NormalizePattern(""));
    }
}
