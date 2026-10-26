using System.Globalization;
using AdMng.Core.ActiveDirectory;
using AdMng.Core.Model;

namespace AdMng.Core.Tests.ActiveDirectory;

/// <summary>
/// <see cref="AccountStatus"/>: a fixed clock on Monday 2026-10-26 10:00 UTC (11:00 in Warsaw, the day after the end of
/// daylight saving time), Europe/Warsaw for dates and day counts.
/// </summary>
public class AccountStatusTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PasswordSet = new(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PasswordExpiry_ComesFromTheConstructedValue_SoAShorterFineGrainedPolicyWins()
    {
        // Domain maxPwdAge 42 days would give 12 November; the PSO of the account (30 days) gives 31 October 08:00 UTC.
        var account = User(
            (Attr.PwdLastSet, FileTime(PasswordSet)),
            (Attr.PasswordExpiryComputed, FileTime(PasswordSet.AddDays(30))));

        var status = AccountStatus.Evaluate(account, Now, Warsaw, domainMaxPasswordAge: TimeSpan.FromDays(42));

        Assert.Equal(PasswordState.Expires, status.Password);
        Assert.Equal(PasswordSet.AddDays(30), status.PasswordExpiresAt);
        Assert.False(status.IsPasswordExpiryEstimated);
        Assert.Equal(new DateOnly(2026, 10, 31), status.PasswordLastDay);
        Assert.Equal(5, status.PasswordDaysLeft);
        Assert.Equal(PasswordSet, status.PasswordLastSet);
        Assert.Equal(25, status.PasswordAgeDays);
        Assert.Equal(new DateOnly(2026, 10, 26), status.Today);
    }

    [Fact]
    public void PasswordExpired_WhenTheConstructedValueIsPast_OrTheDcReportsTheExpiredBit()
    {
        var past = AccountStatus.Evaluate(User(
            (Attr.PwdLastSet, FileTime(PasswordSet)),
            (Attr.PasswordExpiryComputed, FileTime(Now.AddDays(-3)))), Now, Warsaw);
        Assert.Equal(PasswordState.Expired, past.Password);
        Assert.Equal(-3, past.PasswordDaysLeft);

        var bit = AccountStatus.Evaluate(User(
            (Attr.PwdLastSet, FileTime(PasswordSet)),
            (Attr.UserAccountControlComputed, ((int)UserAccountControl.PasswordExpired).ToString(CultureInfo.InvariantCulture)),
            (Attr.PasswordExpiryComputed, FileTime(Now.AddDays(3)))), Now, Warsaw);
        Assert.Equal(PasswordState.Expired, bit.Password);
    }

    [Fact]
    public void NeverExpires_ByFlag_OrByAPolicyWithoutMaximumAge()
    {
        var flag = AccountStatus.Evaluate(User(
            (Attr.UserAccountControl, Uac(UserAccountControl.NormalAccount | UserAccountControl.DontExpirePassword)),
            (Attr.PwdLastSet, "0")), Now, Warsaw);
        Assert.Equal(PasswordState.NeverExpires, flag.Password);
        Assert.True(flag.HasPasswordNeverExpiresFlag);
        Assert.Null(flag.PasswordExpiresAt);

        var policy = AccountStatus.Evaluate(User(
            (Attr.PwdLastSet, FileTime(PasswordSet)),
            (Attr.PasswordExpiryComputed, AdConvert.FileTimeNever.ToString(CultureInfo.InvariantCulture))), Now, Warsaw);
        Assert.Equal(PasswordState.NeverExpires, policy.Password);
        Assert.False(policy.HasPasswordNeverExpiresFlag);
        Assert.Null(policy.PasswordDaysLeft);
    }

    [Fact]
    public void MustChange_WhenPwdLastSetIsZero_WhatEverTheConstructedValueSays()
    {
        var status = AccountStatus.Evaluate(User(
            (Attr.PwdLastSet, "0"),
            (Attr.PasswordExpiryComputed, "0")), Now, Warsaw);

        Assert.Equal(PasswordState.MustChange, status.Password);
        Assert.Null(status.PasswordLastSet);
        Assert.Null(status.PasswordAgeDays);
    }

    [Fact]
    public void WithoutTheConstructedValue_TheDomainMaximumAgeGivesAnEstimate_OrTheStateIsUnknown()
    {
        var account = User((Attr.PwdLastSet, FileTime(PasswordSet)));

        var estimate = AccountStatus.Evaluate(account, Now, Warsaw, domainMaxPasswordAge: TimeSpan.FromDays(42));
        Assert.Equal(PasswordState.Expires, estimate.Password);
        Assert.True(estimate.IsPasswordExpiryEstimated);
        Assert.Equal(PasswordSet.AddDays(42), estimate.PasswordExpiresAt);

        var expiredEstimate = AccountStatus.Evaluate(account, Now, Warsaw, domainMaxPasswordAge: TimeSpan.FromDays(10));
        Assert.Equal(PasswordState.Expired, expiredEstimate.Password);

        Assert.Equal(PasswordState.Unknown, AccountStatus.Evaluate(account, Now, Warsaw).Password);
    }

    [Fact]
    public void AccountExpiry_IsTheEndOfTheLastDay_InLocalTime_AcrossTheDaylightSavingChange()
    {
        // "End of 26 October" = local midnight that starts 27 October (CET, UTC+1) = 26 October 23:00 UTC.
        var endOfToday = AccountStatus.Evaluate(User((Attr.AccountExpires, FileTime(new DateTimeOffset(2026, 10, 26, 23, 0, 0, TimeSpan.Zero)))), Now, Warsaw);
        Assert.Equal(new DateOnly(2026, 10, 26), endOfToday.AccountLastDay);
        Assert.Equal(0, endOfToday.AccountDaysLeft);
        Assert.False(endOfToday.IsAccountExpired);

        // "End of 24 October" was written in summer time: local midnight of 25 October (CEST, UTC+2) = 24 October 22:00 UTC.
        var expired = AccountStatus.Evaluate(User((Attr.AccountExpires, FileTime(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero)))), Now, Warsaw);
        Assert.Equal(new DateOnly(2026, 10, 24), expired.AccountLastDay);
        Assert.Equal(-2, expired.AccountDaysLeft);
        Assert.True(expired.IsAccountExpired);

        foreach (var never in new[] { "0", AdConvert.FileTimeNever.ToString(CultureInfo.InvariantCulture) })
        {
            var status = AccountStatus.Evaluate(User((Attr.AccountExpires, never)), Now, Warsaw);
            Assert.Null(status.AccountExpiresAt);
            Assert.Null(status.AccountDaysLeft);
            Assert.False(status.IsAccountExpired);
        }
    }

    [Fact]
    public void LastLogon_AndCreation_InWholeDays_TheExactValueReplacesAnOlderTimestamp()
    {
        var status = AccountStatus.Evaluate(User(
            (Attr.LastLogonTimestamp, FileTime(Now.AddDays(-120).AddHours(-1))),
            (Attr.WhenCreated, AdConvert.ToGeneralizedTime(Now.AddDays(-400)))), Now, Warsaw);
        Assert.Equal(120, status.DaysSinceLogon);
        Assert.Equal(400, status.DaysSinceCreated);

        var exact = status.WithExactLastLogon(Now.AddDays(-2));
        Assert.Equal(Now.AddDays(-2), exact.LastLogon);
        Assert.Equal(2, exact.DaysSinceLogon);
        Assert.Equal(120, status.WithExactLastLogon(Now.AddDays(-300)).DaysSinceLogon);
        Assert.Equal(120, status.WithExactLastLogon(null).DaysSinceLogon);

        var never = AccountStatus.Evaluate(User((Attr.LastLogonTimestamp, "0")), Now, Warsaw);
        Assert.Null(never.LastLogon);
        Assert.Null(never.DaysSinceLogon);
        Assert.Equal(Now.AddDays(-1), never.WithExactLastLogon(Now.AddDays(-1)).LastLogon);
    }

    [Fact]
    public void Lock_UsesTheComputedBit_ElseTheLockoutDurationOfThePolicy()
    {
        var lockedAt = Now.AddMinutes(-10);
        var computedLocked = AccountStatus.Evaluate(User(
            (Attr.LockoutTime, FileTime(lockedAt)),
            (Attr.UserAccountControlComputed, "16")), Now, Warsaw);
        Assert.True(computedLocked.IsLocked);
        Assert.Equal(lockedAt, computedLocked.LockedSince);

        // lockoutTime stays set after the lockout expired: the computed attribute says it is over.
        var expiredLockout = AccountStatus.Evaluate(User(
            (Attr.LockoutTime, FileTime(lockedAt)),
            (Attr.UserAccountControlComputed, "0")), Now, Warsaw);
        Assert.False(expiredLockout.IsLocked);
        Assert.Null(expiredLockout.LockedSince);

        var withoutComputed = User((Attr.LockoutTime, FileTime(lockedAt)));
        Assert.True(AccountStatus.Evaluate(withoutComputed, Now, Warsaw).IsLocked);
        Assert.False(AccountStatus.Evaluate(withoutComputed, Now, Warsaw, isLockedByTime: _ => false).IsLocked);
        Assert.False(AccountStatus.Evaluate(User((Attr.LockoutTime, "0")), Now, Warsaw).IsLocked);
    }

    [Fact]
    public void HygieneFlags_AndDisabled_ComeFromUserAccountControl()
    {
        var status = AccountStatus.Evaluate(User((Attr.UserAccountControl, Uac(
            UserAccountControl.NormalAccount | UserAccountControl.PasswordNotRequired
            | UserAccountControl.EncryptedTextPasswordAllowed | UserAccountControl.AccountDisable))), Now, Warsaw);

        Assert.True(status.HasAccountControl);
        Assert.True(status.IsDisabled);
        Assert.True(status.IsPasswordNotRequired);
        Assert.True(status.HasReversibleEncryption);
        Assert.False(status.HasPasswordNeverExpiresFlag);
        Assert.False(AccountStatus.Evaluate(new DirectoryObject("CN=Brak,DC=contoso,DC=test", []), Now, Warsaw).HasAccountControl);
    }

    private static DirectoryObject User(params (string Attribute, string Value)[] values)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Attr.ObjectClass] = ObjectClasses.User,
            [Attr.UserAccountControl] = Uac(UserAccountControl.NormalAccount),
        };
        foreach (var (attribute, value) in values)
        {
            attributes[attribute] = value;
        }

        return new DirectoryObject(
            "CN=Grażyna Dąbrowska,OU=Księgowość,DC=contoso,DC=test",
            attributes.Select(a => new AttributeValues(a.Key, [a.Value])));
    }

    private static string Uac(UserAccountControl flags) => ((int)flags).ToString(CultureInfo.InvariantCulture);

    private static string FileTime(DateTimeOffset value) => AdConvert.ToFileTime(value).ToString(CultureInfo.InvariantCulture);
}
