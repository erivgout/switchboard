using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Switchboard.Core.Data;
using Switchboard.Core.Domain;
using Switchboard.Core.Security;

namespace Switchboard.Core.Tickets;

/// <summary>
/// The ticket filter language shared by the queue, API and MCP (FR-TKT-20).
/// </summary>
/// <remarks>
/// <code>
/// priority>=high group:"Service Desk" is:lit -tag:vip created>-7d "vpn timeout"
/// (state:open OR state:pending) agent:me
/// </code>
/// Terms combine with implicit AND; <c>OR</c>, <c>NOT</c>/<c>-</c> and parentheses are supported.
/// Bare words and quoted phrases search subject and description. <c>INC-1042</c> matches a number.
/// </remarks>
public static partial class TicketQueryLanguage
{
    public static readonly IReadOnlyList<(string Field, string Help)> Fields =
    [
        ("status", "status:\"In progress\" (status name)"),
        ("state", "state:open | pending | resolved | closed | unresolved"),
        ("priority", "priority:urgent, priority>=high"),
        ("urgency", "urgency:high"),
        ("impact", "impact:high"),
        ("type", "type:incident | request"),
        ("source", "source:email"),
        ("group", "group:Network, group:mine, group:none"),
        ("agent", "agent:me, agent:none, agent:dana@example.com"),
        ("requester", "requester:me, requester:dana@example.com"),
        ("department", "department:Finance"),
        ("category", "category:Hardware"),
        ("tag", "tag:vip"),
        ("workspace", "workspace:it"),
        ("number", "number:1042"),
        ("created", "created>-7d, created:2026-10-01"),
        ("updated", "updated<-2d"),
        ("due", "due<now, due:today"),
        ("resolved", "resolved>-30d"),
        ("is", "is:lit | unassigned | assigned | mine | overdue | unresolved | major | spam | deleted | merged | child | parent"),
    ];

    public abstract record Node;
    public sealed record And(IReadOnlyList<Node> Items) : Node;
    public sealed record Or(IReadOnlyList<Node> Items) : Node;
    public sealed record Not(Node Inner) : Node;
    public sealed record FieldTerm(string Field, string Op, string Value) : Node;
    public sealed record TextTerm(string Value) : Node;

    public static Node? Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }
        if (query.Length > 2000)
        {
            throw new ValidationException("q", "The search is too long.");
        }
        var tokens = Tokenize(query);
        var pos = 0;
        var node = ParseOr(tokens, ref pos, 0);
        if (pos < tokens.Count)
        {
            throw new ValidationException("q", $"Unexpected '{tokens[pos].Text}' in the search.");
        }
        return node;
    }

    private enum TokenKind { Word, Quoted, LParen, RParen }

    private sealed record Token(TokenKind Kind, string Text);

    private static List<Token> Tokenize(string query)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < query.Length)
        {
            var c = query[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }
            if (c == '(')
            {
                tokens.Add(new Token(TokenKind.LParen, "("));
                i++;
                continue;
            }
            if (c == ')')
            {
                tokens.Add(new Token(TokenKind.RParen, ")"));
                i++;
                continue;
            }
            if (c == '"')
            {
                tokens.Add(new Token(TokenKind.Quoted, ReadQuoted(query, ref i)));
                continue;
            }

            var sb = new StringBuilder();
            while (i < query.Length && !char.IsWhiteSpace(query[i]) && query[i] is not '(' and not ')')
            {
                if (query[i] == '"')
                {
                    sb.Append('\u0001').Append(ReadQuoted(query, ref i)).Append('\u0001');
                    continue;
                }
                sb.Append(query[i]);
                i++;
            }
            tokens.Add(new Token(TokenKind.Word, sb.ToString()));
        }
        return tokens;
    }

    private static string ReadQuoted(string query, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < query.Length && query[i] != '"')
        {
            if (query[i] == '\\' && i + 1 < query.Length)
            {
                i++;
            }
            sb.Append(query[i]);
            i++;
        }
        if (i >= query.Length)
        {
            throw new ValidationException("q", "A quote in the search isn't closed.");
        }
        i++; // closing quote
        return sb.ToString();
    }

    private static Node ParseOr(List<Token> tokens, ref int pos, int depth)
    {
        if (depth > 20)
        {
            throw new ValidationException("q", "The search has too many nested parentheses.");
        }
        var items = new List<Node> { ParseAnd(tokens, ref pos, depth) };
        while (pos < tokens.Count && tokens[pos] is { Kind: TokenKind.Word, Text: "OR" })
        {
            pos++;
            items.Add(ParseAnd(tokens, ref pos, depth));
        }
        return items.Count == 1 ? items[0] : new Or(items);
    }

    private static Node ParseAnd(List<Token> tokens, ref int pos, int depth)
    {
        var items = new List<Node>();
        while (pos < tokens.Count && tokens[pos].Kind != TokenKind.RParen && tokens[pos] is not { Kind: TokenKind.Word, Text: "OR" })
        {
            if (tokens[pos] is { Kind: TokenKind.Word, Text: "AND" })
            {
                pos++;
                continue;
            }
            items.Add(ParseUnary(tokens, ref pos, depth));
        }
        return items.Count switch
        {
            0 => throw new ValidationException("q", "The search has an empty group."),
            1 => items[0],
            _ => new And(items),
        };
    }

    private static Node ParseUnary(List<Token> tokens, ref int pos, int depth)
    {
        var token = tokens[pos];
        if (token is { Kind: TokenKind.Word, Text: "NOT" })
        {
            pos++;
            if (pos >= tokens.Count)
            {
                throw new ValidationException("q", "NOT needs something after it.");
            }
            return new Not(ParseUnary(tokens, ref pos, depth));
        }
        if (token.Kind == TokenKind.LParen)
        {
            pos++;
            var inner = ParseOr(tokens, ref pos, depth + 1);
            if (pos >= tokens.Count || tokens[pos].Kind != TokenKind.RParen)
            {
                throw new ValidationException("q", "A parenthesis in the search isn't closed.");
            }
            pos++;
            return inner;
        }
        if (token.Kind == TokenKind.RParen)
        {
            throw new ValidationException("q", "Unexpected ')' in the search.");
        }

        pos++;
        if (token.Kind == TokenKind.Quoted)
        {
            return new TextTerm(token.Text);
        }

        var word = token.Text;
        var negate = false;
        if (word.Length > 1 && word[0] == '-')
        {
            negate = true;
            word = word[1..];
        }
        var term = ClassifyWord(word);
        return negate ? new Not(term) : term;
    }

    private static Node ClassifyWord(string word)
    {
        var match = FieldPattern().Match(word);
        if (match.Success)
        {
            var value = match.Groups["value"].Value.Replace("\u0001", "");
            return new FieldTerm(match.Groups["field"].Value.ToLowerInvariant(), match.Groups["op"].Value, value);
        }
        var number = NumberPattern().Match(word);
        if (number.Success)
        {
            return new FieldTerm("number", ":", number.Groups["n"].Value);
        }
        return new TextTerm(word.Replace("\u0001", ""));
    }

    [GeneratedRegex(@"^(?<field>[A-Za-z_]+)(?<op>>=|<=|!=|:|>|<|=)(?<value>.+)$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"^(?:#|(?:INC|SR)-)(?<n>\d{1,9})$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberPattern();

    public static bool Mentions(Node? node, string field, string? value = null) => node switch
    {
        null => false,
        And a => a.Items.Any(i => Mentions(i, field, value)),
        Or o => o.Items.Any(i => Mentions(i, field, value)),
        Not n => Mentions(n.Inner, field, value),
        FieldTerm f => f.Field == field && (value is null || string.Equals(f.Value, value, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };
}

/// <summary>Compiles a parsed query to an EF-translatable predicate. Name lookups hit the database once per term.</summary>
public sealed class TicketQueryCompiler(SwitchboardDbContext db, IActorAccessor actors, TimeProvider time)
{
    private static readonly StatusCategory[] Unresolved = [StatusCategory.Open, StatusCategory.Pending];

    /// <summary>
    /// When true, free text only matches content requesters can see (subject, description, public messages),
    /// so a search can't reveal what is written in a private note or an internal attachment.
    /// </summary>
    private bool _requesterText;

    public async Task<Expression<Func<Ticket, bool>>> CompileAsync(TicketQueryLanguage.Node? node, CancellationToken ct) =>
        await CompileAsync(node, asRequester: false, ct);

    public async Task<Expression<Func<Ticket, bool>>> CompileAsync(TicketQueryLanguage.Node? node, bool asRequester, CancellationToken ct)
    {
        _requesterText = asRequester;
        var predicate = node is null ? True : await CompileNodeAsync(node, ct);
        // Unless asked for, hide trash, spam and merged tickets.
        if (!TicketQueryLanguage.Mentions(node, "is", "deleted"))
        {
            predicate = predicate.AndAlso(t => t.DeletedAt == null);
        }
        if (!TicketQueryLanguage.Mentions(node, "is", "spam"))
        {
            predicate = predicate.AndAlso(t => !t.IsSpam);
        }
        if (!TicketQueryLanguage.Mentions(node, "is", "merged"))
        {
            predicate = predicate.AndAlso(t => t.MergedIntoId == null);
        }
        return predicate.AndAlso(t => t.PurgedAt == null);
    }

    private static Expression<Func<Ticket, bool>> True => _ => true;

    private async Task<Expression<Func<Ticket, bool>>> CompileNodeAsync(TicketQueryLanguage.Node node, CancellationToken ct)
    {
        switch (node)
        {
            case TicketQueryLanguage.And and:
            {
                var result = True;
                foreach (var item in and.Items)
                {
                    result = result.AndAlso(await CompileNodeAsync(item, ct));
                }
                return result;
            }
            case TicketQueryLanguage.Or or:
            {
                Expression<Func<Ticket, bool>>? result = null;
                foreach (var item in or.Items)
                {
                    var compiled = await CompileNodeAsync(item, ct);
                    result = result is null ? compiled : result.OrElse(compiled);
                }
                return result!;
            }
            case TicketQueryLanguage.Not not:
                return (await CompileNodeAsync(not.Inner, ct)).Negate();
            case TicketQueryLanguage.TextTerm text:
            {
                var v = text.Value.Trim();
                if (v.Length == 0)
                {
                    return True;
                }
                if (_requesterText)
                {
                    return t => t.Subject.Contains(v) || t.DescriptionText.Contains(v)
                        || t.Conversations.Any(c => !c.IsPrivate && c.Kind != ConversationKind.Forward && c.DeletedAt == null && c.BodyText.Contains(v));
                }
                // Agents also match message bodies and attachment names (FR-TKT-20).
                var attachments = db.Attachments;
                var conversations = db.TicketConversations;
                return t => t.Subject.Contains(v) || t.DescriptionText.Contains(v)
                    || conversations.Any(c => c.TicketId == t.Id && c.DeletedAt == null && c.BodyText.Contains(v))
                    || attachments.Any(a => a.FileName.Contains(v)
                        && ((a.OwnerType == AttachmentOwners.Ticket && a.OwnerId == t.Id)
                            || (a.OwnerType == AttachmentOwners.Conversation && conversations.Any(c => c.Id == a.OwnerId && c.TicketId == t.Id))));
            }
            case TicketQueryLanguage.FieldTerm field:
                return await CompileFieldAsync(field, ct);
            default:
                throw new InvalidOperationException();
        }
    }

    private async Task<Expression<Func<Ticket, bool>>> CompileFieldAsync(TicketQueryLanguage.FieldTerm f, CancellationToken ct)
    {
        var value = f.Value.Trim();
        var me = actors.Current.UserId;
        Expression<Func<Ticket, bool>> eq;

        switch (f.Field)
        {
            case "status":
            {
                var ids = await db.TicketStatuses.Where(s => s.Name == value).Select(s => s.Id).ToListAsync(ct);
                eq = t => ids.Contains(t.StatusId);
                break;
            }
            case "state":
            {
                var cats = value.ToLowerInvariant() switch
                {
                    "open" => [StatusCategory.Open],
                    "pending" => [StatusCategory.Pending],
                    "resolved" => [StatusCategory.Resolved],
                    "closed" => [StatusCategory.Closed],
                    "unresolved" => Unresolved,
                    _ => throw Unknown(f, "open, pending, resolved, closed or unresolved"),
                };
                eq = t => cats.Contains(t.Status!.Category);
                break;
            }
            case "priority":
            {
                var p = ParseEnum<Priority>(f, value);
                return Compare(f, p, t => t.Priority == p, t => t.Priority > p, t => t.Priority >= p, t => t.Priority < p, t => t.Priority <= p);
            }
            case "urgency":
            {
                var l = ParseEnum<Level>(f, value);
                return Compare(f, l, t => t.Urgency == l, t => t.Urgency > l, t => t.Urgency >= l, t => t.Urgency < l, t => t.Urgency <= l);
            }
            case "impact":
            {
                var l = ParseEnum<Level>(f, value);
                return Compare(f, l, t => t.Impact == l, t => t.Impact > l, t => t.Impact >= l, t => t.Impact < l, t => t.Impact <= l);
            }
            case "type":
            {
                var type = value.ToLowerInvariant() switch
                {
                    "incident" or "inc" => TicketType.Incident,
                    "request" or "service_request" or "sr" or "servicerequest" => TicketType.ServiceRequest,
                    _ => throw Unknown(f, "incident or request"),
                };
                eq = t => t.Type == type;
                break;
            }
            case "source":
            {
                var ids = await db.TicketSources.Where(s => s.Key == value || s.Name == value).Select(s => s.Id).ToListAsync(ct);
                eq = t => ids.Contains(t.SourceId);
                break;
            }
            case "group":
            {
                if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    eq = t => t.GroupId == null;
                    break;
                }
                var ids = value.Equals("mine", StringComparison.OrdinalIgnoreCase)
                    ? await db.AgentGroupMembers.Where(m => m.UserId == me && !m.IsObserver).Select(m => (Guid?)m.GroupId).ToListAsync(ct)
                    : await db.AgentGroups.Where(g => g.Name == value).Select(g => (Guid?)g.Id).ToListAsync(ct);
                eq = t => ids.Contains(t.GroupId);
                break;
            }
            case "agent":
            {
                if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    eq = t => t.AgentId == null;
                    break;
                }
                var ids = await ResolveUsersAsync(value, me, ct);
                eq = t => ids.Contains(t.AgentId);
                break;
            }
            case "requester":
            {
                var ids = await ResolveUsersAsync(value, me, ct);
                eq = t => ids.Contains(t.RequesterId) || ids.Contains(t.RequestedForId);
                break;
            }
            case "department":
            {
                var ids = await db.Departments.Where(d => d.Name == value).Select(d => (Guid?)d.Id).ToListAsync(ct);
                eq = t => ids.Contains(t.DepartmentId);
                break;
            }
            case "category":
            {
                var ids = await db.TicketCategories.Where(c => c.Name == value).Select(c => (Guid?)c.Id).ToListAsync(ct);
                eq = t => ids.Contains(t.CategoryId) || ids.Contains(t.SubCategoryId) || ids.Contains(t.ItemCategoryId);
                break;
            }
            case "tag":
            {
                var tag = value.ToLowerInvariant();
                eq = t => t.Tags.Any(x => x.Tag == tag);
                break;
            }
            case "workspace":
            {
                var ids = await db.Workspaces.Where(w => w.Key == value || w.Name == value).Select(w => w.Id).ToListAsync(ct);
                eq = t => ids.Contains(t.WorkspaceId);
                break;
            }
            case "number":
            {
                if (!int.TryParse(value.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                {
                    throw Unknown(f, "a ticket number");
                }
                return Compare(f, n, t => t.Number == n, t => t.Number > n, t => t.Number >= n, t => t.Number < n, t => t.Number <= n);
            }
            case "created":
                return CompileDate(f, value, t => t.CreatedAt);
            case "updated":
                return CompileDate(f, value, t => t.UpdatedAt);
            case "due":
                return CompileNullableDate(f, value, t => t.DueBy);
            case "resolved":
                return CompileNullableDate(f, value, t => t.ResolvedAt);
            case "is":
                eq = CompileIs(f, value, me);
                break;
            default:
                throw new ValidationException("q", $"Unknown filter '{f.Field}'. Try status, state, priority, group, agent, requester, tag, created, due or is.");
        }

        return f.Op switch
        {
            ":" or "=" => eq,
            "!=" => eq.Negate(),
            _ => throw new ValidationException("q", $"'{f.Field}' only supports ':' and '!='."),
        };
    }

    private Expression<Func<Ticket, bool>> CompileIs(TicketQueryLanguage.FieldTerm f, string value, Guid? me)
    {
        var now = time.GetUtcNow();
        var tickets = db.Tickets;
        return value.ToLowerInvariant() switch
        {
            "lit" or "waiting" => t => t.AwaitingAgentSince != null && Unresolved.Contains(t.Status!.Category),
            "unassigned" => t => t.AgentId == null,
            "assigned" => t => t.AgentId != null,
            "mine" => t => t.AgentId == me,
            // A paused clock (status with the SLA timer off) is not overdue.
            "overdue" => t => t.DueBy != null && t.DueBy < now && Unresolved.Contains(t.Status!.Category) && t.Status.SlaTimerOn,
            "unresolved" => t => Unresolved.Contains(t.Status!.Category),
            "major" => t => t.IsMajorIncident,
            "spam" => t => t.IsSpam,
            "deleted" => t => t.DeletedAt != null,
            "merged" => t => t.MergedIntoId != null,
            "child" => t => t.ParentId != null,
            "parent" => t => tickets.Any(c => c.ParentId == t.Id),
            _ => throw Unknown(f, "lit, unassigned, assigned, mine, overdue, unresolved, major, spam, deleted, merged, child or parent"),
        };
    }

    private async Task<List<Guid?>> ResolveUsersAsync(string value, Guid? me, CancellationToken ct)
    {
        if (value.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            return [me];
        }
        if (value.Contains('@'))
        {
            var normalized = value.Trim().ToUpperInvariant();
            return await db.Users.Where(u => u.NormalizedEmail == normalized).Select(u => (Guid?)u.Id).ToListAsync(ct);
        }
        return await db.Users.Where(u => u.DisplayName.Contains(value)).Select(u => (Guid?)u.Id).Take(200).ToListAsync(ct);
    }

    private Expression<Func<Ticket, bool>> CompileDate(TicketQueryLanguage.FieldTerm f, string value, Expression<Func<Ticket, DateTimeOffset>> selector)
    {
        var (start, end) = ParseDate(f, value);
        return f.Op switch
        {
            ":" or "=" => end is { } e ? Between(selector, start, e) : Cmp(selector, ExpressionType.GreaterThanOrEqual, start),
            ">" => Cmp(selector, ExpressionType.GreaterThan, end ?? start),
            ">=" => Cmp(selector, ExpressionType.GreaterThanOrEqual, start),
            "<" => Cmp(selector, ExpressionType.LessThan, start),
            "<=" => Cmp(selector, ExpressionType.LessThan, end ?? start),
            _ => throw new ValidationException("q", $"'{f.Field}' supports :, >, >=, < and <=."),
        };
    }

    private Expression<Func<Ticket, bool>> CompileNullableDate(TicketQueryLanguage.FieldTerm f, string value, Expression<Func<Ticket, DateTimeOffset?>> selector)
    {
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            var isNull = Expression.Equal(selector.Body, Expression.Constant(null, typeof(DateTimeOffset?)));
            var nullCheck = Expression.Lambda<Func<Ticket, bool>>(isNull, selector.Parameters);
            return f.Op == "!=" ? nullCheck.Negate() : nullCheck;
        }
        var converted = Expression.Lambda<Func<Ticket, DateTimeOffset>>(
            Expression.Convert(selector.Body, typeof(DateTimeOffset)), selector.Parameters);
        var notNull = Expression.Lambda<Func<Ticket, bool>>(
            Expression.NotEqual(selector.Body, Expression.Constant(null, typeof(DateTimeOffset?))), selector.Parameters);
        return notNull.AndAlso(CompileDate(f, value, converted));
    }

    private (DateTimeOffset Start, DateTimeOffset? End) ParseDate(TicketQueryLanguage.FieldTerm f, string value)
    {
        var now = time.GetUtcNow();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        switch (value.ToLowerInvariant())
        {
            case "now":
                return (now, null);
            case "today":
                return (today, today.AddDays(1));
            case "yesterday":
                return (today.AddDays(-1), today);
            case "tomorrow":
                return (today.AddDays(1), today.AddDays(2));
        }

        var relative = Regex.Match(value, @"^(?<sign>[+-])(?<n>\d{1,5})(?<unit>[mhdw])$");
        if (relative.Success)
        {
            var n = int.Parse(relative.Groups["n"].Value, CultureInfo.InvariantCulture) * (relative.Groups["sign"].Value == "-" ? -1 : 1);
            var at = relative.Groups["unit"].Value switch
            {
                "m" => now.AddMinutes(n),
                "h" => now.AddHours(n),
                "d" => now.AddDays(n),
                _ => now.AddDays(n * 7),
            };
            return (at, null);
        }

        if (DateTimeOffset.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var day))
        {
            return (day, day.AddDays(1));
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var exact))
        {
            return (exact, null);
        }
        throw Unknown(f, "a date like 2026-10-01, -7d, today or now");
    }

    private static Expression<Func<Ticket, bool>> Between(Expression<Func<Ticket, DateTimeOffset>> selector, DateTimeOffset start, DateTimeOffset end) =>
        Cmp(selector, ExpressionType.GreaterThanOrEqual, start).AndAlso(Cmp(selector, ExpressionType.LessThan, end));

    private static Expression<Func<Ticket, bool>> Cmp(Expression<Func<Ticket, DateTimeOffset>> selector, ExpressionType op, DateTimeOffset value)
    {
        // Wrap the constant in a closure so EF parameterizes it.
        var holder = new ValueHolder<DateTimeOffset>(value);
        var constant = Expression.Property(Expression.Constant(holder), nameof(ValueHolder<DateTimeOffset>.Value));
        return Expression.Lambda<Func<Ticket, bool>>(Expression.MakeBinary(op, selector.Body, constant), selector.Parameters);
    }

    private sealed record ValueHolder<T>(T Value);

    private static Expression<Func<Ticket, bool>> Compare<T>(TicketQueryLanguage.FieldTerm f, T _,
        Expression<Func<Ticket, bool>> eq, Expression<Func<Ticket, bool>> gt, Expression<Func<Ticket, bool>> ge,
        Expression<Func<Ticket, bool>> lt, Expression<Func<Ticket, bool>> le) => f.Op switch
        {
            ":" or "=" => eq,
            "!=" => eq.Negate(),
            ">" => gt,
            ">=" => ge,
            "<" => lt,
            "<=" => le,
            _ => throw new ValidationException("q", $"Unsupported operator '{f.Op}'."),
        };

    private static T ParseEnum<T>(TicketQueryLanguage.FieldTerm f, string value) where T : struct, Enum
    {
        if (Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }
        throw Unknown(f, string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant())));
    }

    private static ValidationException Unknown(TicketQueryLanguage.FieldTerm f, string expected) =>
        new("q", $"'{f.Value}' isn't valid for {f.Field}. Use {expected}.");
}

public static class PredicateExtensions
{
    public static Expression<Func<T, bool>> AndAlso<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right) =>
        Combine(left, right, Expression.AndAlso);

    public static Expression<Func<T, bool>> OrElse<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right) =>
        Combine(left, right, Expression.OrElse);

    public static Expression<Func<T, bool>> Negate<T>(this Expression<Func<T, bool>> expr) =>
        Expression.Lambda<Func<T, bool>>(Expression.Not(expr.Body), expr.Parameters);

    private static Expression<Func<T, bool>> Combine<T>(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right,
        Func<Expression, Expression, BinaryExpression> merge)
    {
        var parameter = left.Parameters[0];
        var rightBody = new ReplaceParameter(right.Parameters[0], parameter).Visit(right.Body);
        return Expression.Lambda<Func<T, bool>>(merge(left.Body, rightBody), parameter);
    }

    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
