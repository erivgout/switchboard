using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using QRCoder;
using Switchboard.Core.Domain;
using Switchboard.Core.Services;
using Switchboard.UI;

namespace Switchboard.Web.Pages;

public sealed class ProfileModel(UserManager<User> users, SignInManager<User> signIn, IHtmlContent html, IAuditLog audit) : PageModel
{
    public sealed class DetailsInput
    {
        [Required(ErrorMessage = "Enter your name."), StringLength(200)]
        public string DisplayName { get; set; } = "";

        [StringLength(200)]
        public string? JobTitle { get; set; }

        [StringLength(64)]
        public string? Phone { get; set; }

        [StringLength(64)]
        public string? Mobile { get; set; }

        public string? TimeZone { get; set; }

        [StringLength(2000)]
        public string? Signature { get; set; }
    }

    public sealed class PasswordInput
    {
        public string Current { get; set; } = "";
        public string New { get; set; } = "";
        public string Confirm { get; set; } = "";
    }

    [BindProperty]
    public DetailsInput Details { get; set; } = new();

    [BindProperty]
    public PasswordInput Passwords { get; set; } = new();

    [BindProperty]
    public string? MfaCode { get; set; }

    public Guid UserId { get; private set; }
    public bool IsAgent { get; private set; }
    public bool MfaEnabled { get; private set; }
    public string? SharedKey { get; private set; }
    public string? QrSvg { get; private set; }
    public IReadOnlyList<string>? RecoveryCodes { get; private set; }
    public IEnumerable<SelectListItem> TimeZones { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await CurrentAsync();
        Details = new DetailsInput
        {
            DisplayName = user.DisplayName,
            JobTitle = user.JobTitle,
            Phone = user.PhoneNumber,
            Mobile = user.Mobile,
            TimeZone = user.TimeZone ?? "UTC",
            Signature = html.ToPlainText(user.Signature),
        };
        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostDetailsAsync()
    {
        var user = await CurrentAsync();
        ModelState.Remove(nameof(MfaCode));
        foreach (var key in ModelState.Keys.Where(k => k.StartsWith("Passwords", StringComparison.Ordinal)).ToList())
        {
            ModelState.Remove(key);
        }
        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }
        user.DisplayName = Details.DisplayName.Trim();
        user.JobTitle = Details.JobTitle?.Trim();
        user.PhoneNumber = Details.Phone?.Trim();
        user.Mobile = Details.Mobile?.Trim();
        user.TimeZone = TimeZoneInfo.TryFindSystemTimeZoneById(Details.TimeZone ?? "UTC", out _) ? Details.TimeZone : "UTC";
        if (user.IsAgent)
        {
            user.Signature = html.FromPlainText(Details.Signature);
        }
        await users.UpdateAsync(user);
        await signIn.RefreshSignInAsync(user);
        TempData.Toast("Details saved.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPasswordAsync()
    {
        var user = await CurrentAsync();
        if (Passwords.New != Passwords.Confirm)
        {
            ModelState.AddModelError(nameof(Passwords), "The two new passwords don't match.");
        }
        else
        {
            var result = await users.ChangePasswordAsync(user, Passwords.Current ?? "", Passwords.New ?? "");
            if (result.Succeeded)
            {
                await audit.WriteAsync("auth.password_changed", "user", user.Id.ToString());
                await signIn.RefreshSignInAsync(user);
                TempData.Toast("Password changed. Other devices were signed out.");
                return RedirectToPage();
            }
            foreach (var e in result.Errors)
            {
                ModelState.AddModelError(nameof(Passwords), e.Code == "PasswordMismatch" ? "Your current password isn't right." : e.Description);
            }
        }
        await LoadAsync(user, keepDetails: false);
        return Page();
    }

    public async Task<IActionResult> OnPostEnableMfaAsync()
    {
        var user = await CurrentAsync();
        var code = (MfaCode ?? "").Replace(" ", "").Replace("-", "");
        var valid = await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code);
        if (!valid)
        {
            ModelState.AddModelError(nameof(MfaCode), "That code didn't match. Check the time on your phone and try the next code.");
            await LoadAsync(user, keepDetails: false);
            return Page();
        }
        await users.SetTwoFactorEnabledAsync(user, true);
        await audit.WriteAsync("auth.mfa.enabled", "user", user.Id.ToString());
        RecoveryCodes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToList();
        await LoadAsync(user, keepDetails: false);
        TempData.Toast("Two-step verification is on.");
        return Page();
    }

    public async Task<IActionResult> OnPostDisableMfaAsync()
    {
        var user = await CurrentAsync();
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await audit.WriteAsync("auth.mfa.disabled", "user", user.Id.ToString());
        TempData.Toast("Two-step verification is off.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRecoveryCodesAsync()
    {
        var user = await CurrentAsync();
        RecoveryCodes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))?.ToList();
        await audit.WriteAsync("auth.mfa.recovery_codes_regenerated", "user", user.Id.ToString());
        await LoadAsync(user, keepDetails: false);
        return Page();
    }

    private async Task<User> CurrentAsync() =>
        await users.GetUserAsync(User) ?? throw new Core.Security.NotFoundException();

    private async Task LoadAsync(User user, bool keepDetails = true)
    {
        UserId = user.Id;
        IsAgent = user.IsAgent || user.IsOrgAdmin;
        MfaEnabled = await users.GetTwoFactorEnabledAsync(user);
        if (!keepDetails)
        {
            Details = new DetailsInput
            {
                DisplayName = user.DisplayName,
                JobTitle = user.JobTitle,
                Phone = user.PhoneNumber,
                Mobile = user.Mobile,
                TimeZone = user.TimeZone ?? "UTC",
                Signature = html.ToPlainText(user.Signature),
            };
        }
        TimeZones = TimeZoneInfo.GetSystemTimeZones()
            .Select(z => new SelectListItem(z.DisplayName, z.Id, z.Id == (Details.TimeZone ?? "UTC")))
            .Prepend(new SelectListItem("UTC", "UTC", (Details.TimeZone ?? "UTC") == "UTC"))
            .DistinctBy(i => i.Value)
            .ToList();

        if (!MfaEnabled)
        {
            var key = await users.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                await users.ResetAuthenticatorKeyAsync(user);
                key = await users.GetAuthenticatorKeyAsync(user);
            }
            SharedKey = string.Join(' ', Enumerable.Range(0, (key!.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4)))).ToLowerInvariant();
            var uri = $"otpauth://totp/Switchboard:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer=Switchboard&digits=6";
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
            // viewBox sizing so the code scales to its box instead of being cropped at a fixed pixel size.
            QrSvg = new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
        }
    }
}
