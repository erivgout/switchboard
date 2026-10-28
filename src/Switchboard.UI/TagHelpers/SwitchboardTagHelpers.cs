using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Switchboard.Core.Domain;
using Switchboard.Core.Modules;

namespace Switchboard.UI.TagHelpers;

/// <summary>Renders every view component registered for a named slot: <c>&lt;sb-slot name="ticket.sidebar" model="Model.Ticket" /&gt;</c>.</summary>
[HtmlTargetElement("sb-slot", TagStructure = TagStructure.WithoutEndTag)]
public sealed class SlotTagHelper(IEnumerable<SlotRegistration> slots, IViewComponentHelper components) : TagHelper
{
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = default!;

    public string Name { get; set; } = "";

    public object? Model { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;
        ((IViewContextAware)components).Contextualize(ViewContext);
        foreach (var slot in slots.Where(s => s.Slot == Name).OrderBy(s => s.Order))
        {
            // Pass the model itself: MVC binds it straight to a single InvokeAsync parameter it is assignable to
            // (object, Ticket, Guid, string...). Wrapping it in new { model } would hand object-typed
            // parameters the wrapper instead of the model.
            output.Content.AppendHtml(await components.InvokeAsync(slot.ViewComponentType, Model));
        }
    }
}

/// <summary>The lamp: lit when a ticket waits on an agent; red when it also breached its target.</summary>
[HtmlTargetElement("sb-lamp", TagStructure = TagStructure.WithoutEndTag)]
public sealed class LampTagHelper : TagHelper
{
    public bool Lit { get; set; }
    public bool Alert { get; set; }
    /// <summary>Plays the arrival animation once (new ticket via live update).</summary>
    public bool Arriving { get; set; }
    /// <summary>Accessible label; defaults to a description of the state.</summary>
    public string? Label { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        var cls = "sb-lamp" + (Alert && Lit ? " sb-lamp--alert" : Lit ? " sb-lamp--lit" : "") + (Arriving ? " sb-lamp--arriving" : "");
        output.Attributes.SetAttribute("class", cls);
        output.Attributes.SetAttribute("role", "img");
        output.Attributes.SetAttribute("aria-label", Label ?? (Lit ? Alert ? "Waiting on an agent and overdue" : "Waiting on an agent" : "Not waiting"));
    }
}

[HtmlTargetElement("sb-status", TagStructure = TagStructure.WithoutEndTag)]
public sealed class StatusTagHelper : TagHelper
{
    public string Name { get; set; } = "";
    public StatusCategory Category { get; set; }
    /// <summary>Show the requester-facing label instead of the internal name.</summary>
    public string? RequesterLabel { get; set; }
    public bool ForRequester { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", $"sb-status sb-status--{Category.ToString().ToLowerInvariant()}");
        output.Content.SetContent(ForRequester && !string.IsNullOrWhiteSpace(RequesterLabel) ? RequesterLabel : Name);
    }
}

/// <summary>Priority as signal bars plus text (never color alone).</summary>
[HtmlTargetElement("sb-priority", TagStructure = TagStructure.WithoutEndTag)]
public sealed class PriorityTagHelper : TagHelper
{
    public Priority Value { get; set; }
    public bool ShowLabel { get; set; } = true;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", $"sb-priority sb-priority--{Value.ToString().ToLowerInvariant()}");
        if (!ShowLabel)
        {
            output.Attributes.SetAttribute("title", $"{Value} priority");
            output.Attributes.SetAttribute("aria-label", $"{Value} priority");
        }
        output.Content.SetHtmlContent("<span class=\"sb-bars\" aria-hidden=\"true\"><i></i><i></i><i></i><i></i></span>");
        if (ShowLabel)
        {
            output.Content.AppendHtml("<span>");
            output.Content.Append(Value.ToString());
            output.Content.AppendHtml("</span>");
        }
    }
}

/// <summary>Initials avatar with a stable hue per name.</summary>
[HtmlTargetElement("sb-avatar", TagStructure = TagStructure.WithoutEndTag)]
public sealed class AvatarTagHelper : TagHelper
{
    public string? Name { get; set; }
    /// <summary><c>sm</c>, <c>md</c> (default) or <c>lg</c>.</summary>
    public string Size { get; set; } = "md";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim();
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var initials = parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1],
            _ => string.Concat(parts[0][..1], parts[^1][..1]),
        };
        var hue = 0;
        foreach (var c in name)
        {
            hue = (hue * 31 + c) % 360;
        }
        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "sb-avatar" + (Size is "sm" or "lg" ? $" sb-avatar--{Size}" : ""));
        output.Attributes.SetAttribute("style", $"--sb-avatar-hue:{Math.Abs(hue)}");
        output.Attributes.SetAttribute("aria-hidden", "true");
        output.Content.SetContent(initials.ToUpperInvariant());
    }
}

/// <summary>
/// A timestamp rendered in the viewer's timezone by switchboard.js; the server text is a UTC fallback.
/// <c>mode</c>: <c>relative</c> (default) or <c>datetime</c>.
/// </summary>
[HtmlTargetElement("sb-time", TagStructure = TagStructure.WithoutEndTag)]
public sealed class TimeTagHelper(TimeProvider time) : TagHelper
{
    public DateTimeOffset? Value { get; set; }
    public string Mode { get; set; } = "relative";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (Value is not { } value)
        {
            output.SuppressOutput();
            return;
        }
        output.TagName = "time";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("datetime", value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        output.Attributes.SetAttribute("data-sb-time", Mode);
        output.Content.SetContent(Mode == "relative" ? Humanize.Relative(value, time.GetUtcNow()) : value.ToUniversalTime().ToString("MMM d, yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture));
    }
}

/// <summary>SLA due chip. States: running (due in / overdue), paused (on hold), met, missed.</summary>
[HtmlTargetElement("sb-due", TagStructure = TagStructure.WithoutEndTag)]
public sealed class DueTagHelper(TimeProvider time) : TagHelper
{
    public DateTimeOffset? Due { get; set; }
    public StatusCategory Category { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    /// <summary>Defaults to true when the status category is Pending.</summary>
    public bool? Paused { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (Due is not { } due)
        {
            output.SuppressOutput();
            return;
        }

        string state, icon, text, cls;
        if (Category is StatusCategory.Resolved or StatusCategory.Closed)
        {
            var met = ResolvedAt is { } r && r <= due;
            state = met ? "met" : "missed";
            icon = met ? "bi-check2" : "bi-x-circle";
            text = met ? "Met target" : "Missed target";
            cls = met ? "is-met" : "is-missed";
        }
        else if (Paused ?? Category == StatusCategory.Pending)
        {
            state = "paused";
            icon = "bi-pause-circle";
            text = "On hold";
            cls = "";
        }
        else
        {
            var left = due - time.GetUtcNow();
            state = "running";
            icon = left < TimeSpan.Zero ? "bi-exclamation-circle" : "bi-clock";
            text = left < TimeSpan.Zero ? $"Overdue by {Humanize.Duration(left)}" : $"Due in {Humanize.Duration(left)}";
            cls = left < TimeSpan.Zero ? "is-overdue" : left < TimeSpan.FromHours(2) ? "is-soon" : "";
        }

        output.TagName = "span";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", ("sb-due " + cls).Trim());
        output.Attributes.SetAttribute("data-sb-due", due.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        output.Attributes.SetAttribute("data-sb-due-state", state);
        output.Content.SetHtmlContent($"<i class=\"bi {icon}\" aria-hidden=\"true\"></i><span data-sb-due-text>{HtmlEncoder.Default.Encode(text)}</span>");
    }
}

/// <summary>Page title row: <c>&lt;sb-page-header title="Tickets" subtitle="..."&gt;actions&lt;/sb-page-header&gt;</c>.</summary>
[HtmlTargetElement("sb-page-header")]
public sealed class PageHeaderTagHelper : TagHelper
{
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var actions = await output.GetChildContentAsync();
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "sb-page-header");
        var enc = HtmlEncoder.Default;
        var html = $"<div><h1 class=\"sb-title\">{enc.Encode(Title)}</h1>"
            + (string.IsNullOrWhiteSpace(Subtitle) ? "" : $"<p class=\"sb-subtitle\">{enc.Encode(Subtitle)}</p>")
            + "</div>";
        output.Content.SetHtmlContent(html);
        if (!actions.IsEmptyOrWhiteSpace)
        {
            output.Content.AppendHtml("<div class=\"sb-page-actions\">");
            output.Content.AppendHtml(actions);
            output.Content.AppendHtml("</div>");
        }
    }
}

/// <summary>Empty state that invites the next action. Child content holds text and buttons.</summary>
[HtmlTargetElement("sb-empty")]
public sealed class EmptyTagHelper : TagHelper
{
    public string Icon { get; set; } = "bi-inbox";
    public string Title { get; set; } = "";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var body = await output.GetChildContentAsync();
        output.TagName = "div";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("class", "sb-empty");
        var enc = HtmlEncoder.Default;
        output.Content.SetHtmlContent($"<i class=\"bi {enc.Encode(Icon)}\" aria-hidden=\"true\"></i><h2 class=\"sb-empty-title\">{enc.Encode(Title)}</h2>");
        output.Content.AppendHtml(body);
    }
}

public static class Humanize
{
    public static string Duration(TimeSpan span)
    {
        var s = span.Duration();
        if (s.TotalDays >= 1)
        {
            return $"{(int)s.TotalDays}d {s.Hours}h";
        }
        if (s.TotalHours >= 1)
        {
            return $"{(int)s.TotalHours}h {s.Minutes}m";
        }
        return $"{Math.Max(1, s.Minutes)}m";
    }

    public static string Relative(DateTimeOffset value, DateTimeOffset now)
    {
        var diff = value - now;
        var abs = diff.Duration();
        string Ago(string unit, double n) => diff < TimeSpan.Zero ? $"{(int)Math.Round(n)}{unit} ago" : $"in {(int)Math.Round(n)}{unit}";
        if (abs < TimeSpan.FromSeconds(45))
        {
            return "just now";
        }
        if (abs < TimeSpan.FromHours(1))
        {
            return Ago("m", abs.TotalMinutes);
        }
        if (abs < TimeSpan.FromDays(1))
        {
            return Ago("h", abs.TotalHours);
        }
        if (abs < TimeSpan.FromDays(7))
        {
            return Ago("d", abs.TotalDays);
        }
        return value.ToUniversalTime().ToString("MMM d", CultureInfo.InvariantCulture);
    }
}
