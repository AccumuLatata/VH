using System;
using System.Globalization;
using LossLock;

internal static class Program
{
    private static int failed;

    private static void Main()
    {
        Classify();
        ChoosePnl();
        OneLossLocksFiveMinutes();
        SecondLossKeepsTheLaterEnd();
        ThirdLossLocksThirtyMinutes();
        FourthLossDoesNotShortenALongerLock();
        WinResetsStreakAndKeepsLock();
        ScratchDoesNothing();
        UnknownDoesNothing();
        NewDayResetsStreakBeforeTheNextLoss();
        SameDayKeepsStreak();
        DisabledSingleLossStillCountsTowardThree();
        ZeroLongLockDoesNotStartALock();
        ReleaseAndHold();
        FormatRemainingTimes();
        StatusWhileActive();
        StatusWhileLocked();
        StatusWhilePending();
        if (failed > 0)
        {
            Console.Error.WriteLine(failed.ToString(CultureInfo.InvariantCulture) + " failed");
            Environment.Exit(1);
        }
        Console.WriteLine("all passed");
    }

    private static LockSettings Defaults()
    {
        return new LockSettings(5, 3, 30, 0, true);
    }

    private static void Classify()
    {
        Equal(RoundTripOutcome.Win, LossLockRules.Classify(12.5, 0));
        Equal(RoundTripOutcome.Loss, LossLockRules.Classify(-0.25, 0));
        Equal(RoundTripOutcome.Scratch, LossLockRules.Classify(0, 0));
        Equal(RoundTripOutcome.Scratch, LossLockRules.Classify(-4, 10), "below the minimum loss");
        Equal(RoundTripOutcome.Loss, LossLockRules.Classify(-10, 10));
        Equal(RoundTripOutcome.Win, LossLockRules.Classify(0.5, 10), "a win still resets even below the minimum loss");
        Equal(RoundTripOutcome.Unknown, LossLockRules.Classify(null, 0));
        Equal(RoundTripOutcome.Unknown, LossLockRules.Classify(double.NaN, 0));
    }

    private static void ChoosePnl()
    {
        Equal(12.0, LossLockRules.ChoosePnl(12, 99).Value);
        Equal(-8.0, LossLockRules.ChoosePnl(null, -8).Value);
        Equal(-8.0, LossLockRules.ChoosePnl(0, -8).Value, "a zero position P&L falls back to the fills");
        Check(!LossLockRules.ChoosePnl(null, null).HasValue, "both missing");
    }

    private static void OneLossLocksFiveMinutes()
    {
        DateTime now = Utc(10, 0);
        (SessionState state, LockSnapshot snap) = LossLockRules.OnRoundTrip(
            Empty(), RoundTripOutcome.Loss, now, "2026-11-27", Defaults());
        Equal(1, state.ConsecutiveLosses);
        Equal(true, state.HoldsLock);
        Equal(now.AddMinutes(5), state.LockUntilUtc.Value);
        Check(snap.Changed, "loss changes state");
    }

    private static void SecondLossKeepsTheLaterEnd()
    {
        DateTime first = Utc(10, 0);
        (SessionState state, _) = LossLockRules.OnRoundTrip(
            Empty(), RoundTripOutcome.Loss, first, "2026-11-27", Defaults());
        DateTime second = first.AddMinutes(4);
        (state, _) = LossLockRules.OnRoundTrip(state, RoundTripOutcome.Loss, second, "2026-11-27", Defaults());
        Equal(2, state.ConsecutiveLosses);
        Equal(second.AddMinutes(5), state.LockUntilUtc.Value);
    }

    private static void ThirdLossLocksThirtyMinutes()
    {
        SessionState state = Empty();
        DateTime now = Utc(10, 0);
        for (int i = 0; i < 3; i++)
        {
            (state, _) = LossLockRules.OnRoundTrip(state, RoundTripOutcome.Loss, now, "2026-11-27", Defaults());
            now = now.AddMinutes(1);
        }
        Equal(3, state.ConsecutiveLosses);
        Equal(Utc(10, 2).AddMinutes(30), state.LockUntilUtc.Value);
    }

    private static void FourthLossDoesNotShortenALongerLock()
    {
        DateTime now = Utc(10, 0);
        SessionState state = new SessionState(3, now.AddMinutes(30), true, "2026-11-27");
        // A configured single-loss lock of 5 minutes must not pull a 30 minute lock forward.
        LockSettings settings = new LockSettings(5, 99, 30, 0, true);
        (state, _) = LossLockRules.OnRoundTrip(state, RoundTripOutcome.Loss, now.AddMinutes(1), "2026-11-27", settings);
        Equal(4, state.ConsecutiveLosses);
        Equal(now.AddMinutes(30), state.LockUntilUtc.Value);
    }

    private static void WinResetsStreakAndKeepsLock()
    {
        DateTime now = Utc(10, 0);
        SessionState state = new SessionState(2, now.AddMinutes(20), true, "2026-11-27");
        (state, LockSnapshot snap) = LossLockRules.OnRoundTrip(
            state, RoundTripOutcome.Win, now.AddMinutes(1), "2026-11-27", Defaults());
        Equal(0, state.ConsecutiveLosses);
        Equal(true, state.HoldsLock);
        Equal(now.AddMinutes(20), state.LockUntilUtc.Value);
        Check(snap.Changed, "streak reset is a change");
    }

    private static void ScratchDoesNothing()
    {
        DateTime now = Utc(10, 0);
        SessionState state = new SessionState(2, now.AddMinutes(5), true, "2026-11-27");
        (SessionState next, LockSnapshot snap) = LossLockRules.OnRoundTrip(
            state, RoundTripOutcome.Scratch, now, "2026-11-27", Defaults());
        Equal(2, next.ConsecutiveLosses);
        Equal(state.LockUntilUtc, next.LockUntilUtc);
        Check(!snap.Changed, "scratch");
    }

    private static void UnknownDoesNothing()
    {
        SessionState state = new SessionState(2, null, false, "2026-11-26");
        (SessionState next, LockSnapshot snap) = LossLockRules.OnRoundTrip(
            state, RoundTripOutcome.Unknown, Utc(10, 0), "2026-11-27", Defaults());
        Equal(2, next.ConsecutiveLosses);
        Equal("2026-11-26", next.LastLocalDate);
        Check(!snap.Changed, "unknown");
    }

    private static void NewDayResetsStreakBeforeTheNextLoss()
    {
        SessionState state = new SessionState(2, null, false, "2026-11-26");
        (state, _) = LossLockRules.OnRoundTrip(
            state, RoundTripOutcome.Loss, Utc(9, 0), "2026-11-27", Defaults());
        Equal(1, state.ConsecutiveLosses);
        Equal(Utc(9, 0).AddMinutes(5), state.LockUntilUtc.Value);
    }

    private static void SameDayKeepsStreak()
    {
        SessionState state = new SessionState(2, null, false, "2026-11-27");
        (state, _) = LossLockRules.OnRoundTrip(
            state, RoundTripOutcome.Loss, Utc(9, 0), "2026-11-27", Defaults());
        Equal(3, state.ConsecutiveLosses);
        Equal(Utc(9, 0).AddMinutes(30), state.LockUntilUtc.Value);
    }

    private static void DisabledSingleLossStillCountsTowardThree()
    {
        LockSettings settings = new LockSettings(0, 3, 30, 0, false);
        SessionState state = Empty();
        DateTime now = Utc(8, 0);
        (state, _) = LossLockRules.OnRoundTrip(state, RoundTripOutcome.Loss, now, "2026-11-27", settings);
        Equal(1, state.ConsecutiveLosses);
        Check(!state.HoldsLock, "first loss does not lock when the short lock is 0");
        (state, _) = LossLockRules.OnRoundTrip(state, RoundTripOutcome.Loss, now, "2026-11-27", settings);
        Check(!state.HoldsLock, "second loss still does not lock");
        (state, _) = LossLockRules.OnRoundTrip(state, RoundTripOutcome.Loss, now, "2026-11-27", settings);
        Equal(3, state.ConsecutiveLosses);
        Equal(true, state.HoldsLock);
        Equal(now.AddMinutes(30), state.LockUntilUtc.Value);
    }

    private static void ZeroLongLockDoesNotStartALock()
    {
        LockSettings settings = new LockSettings(0, 3, 0, 0, false);
        SessionState state = new SessionState(2, null, false, "2026-11-27");
        (state, _) = LossLockRules.OnRoundTrip(state, RoundTripOutcome.Loss, Utc(8, 0), "2026-11-27", settings);
        Equal(3, state.ConsecutiveLosses);
        Check(!state.HoldsLock, "no duration means no lock");
    }

    private static void FormatRemainingTimes()
    {
        Equal("30:00", LossLockRules.FormatRemaining(TimeSpan.FromMinutes(30)));
        Equal("00:01", LossLockRules.FormatRemaining(TimeSpan.FromSeconds(1)));
        Equal("00:01", LossLockRules.FormatRemaining(TimeSpan.FromSeconds(0.2)));
        Equal("00:00", LossLockRules.FormatRemaining(TimeSpan.Zero));
        Equal("00:00", LossLockRules.FormatRemaining(TimeSpan.FromSeconds(-3)));
        Equal("01:00", LossLockRules.FormatRemaining(TimeSpan.FromSeconds(59.1)));
        Equal("1:30:01", LossLockRules.FormatRemaining(TimeSpan.FromMinutes(90) + TimeSpan.FromSeconds(1)));
    }

    private static void StatusWhileActive()
    {
        DateTime now = Utc(10, 0);
        StatusSnapshot fresh = LossLockRules.Describe(Empty(), Defaults(), now, false, "");
        Equal("Aktiv", fresh.State);
        Equal("Aktiv", fresh.CompactStatus);
        Equal("0 von 3", fresh.Streak);
        Equal("0 von 3", fresh.Hero);
        Equal("Der nächste Verlust sperrt 5 Minuten.", fresh.NextLoss);
        Equal("Der nächste Verlust sperrt 5 Minuten.", fresh.Detail);
        Equal("-", fresh.Remaining);
        Equal("-", fresh.Until);
        Equal("", fresh.SeriesLine);
        Check(!fresh.Locked, "fresh is not locked");

        StatusSnapshot two = LossLockRules.Describe(
            new SessionState(2, null, false, "2026-11-27"), Defaults(), now, false, "");
        Equal("2 von 3", two.Streak);
        Equal("Der nächste Verlust sperrt 30 Minuten.", two.NextLoss);

        StatusSnapshot noShort = LossLockRules.Describe(
            Empty(), new LockSettings(0, 3, 30, 0, true), now, false, "");
        Equal("Der nächste Verlust sperrt nicht.", noShort.NextLoss);

        StatusSnapshot noLong = LossLockRules.Describe(
            new SessionState(2, null, false, "2026-11-27"), new LockSettings(5, 3, 0, 0, true), now, false, "");
        Equal("Der nächste Verlust sperrt nicht.", noLong.NextLoss);

        StatusSnapshot oneMinute = LossLockRules.Describe(
            Empty(), new LockSettings(1, 3, 30, 0, true), now, false, "");
        Equal("Der nächste Verlust sperrt 1 Minute.", oneMinute.NextLoss);

        StatusSnapshot everyLossIsLong = LossLockRules.Describe(
            Empty(), new LockSettings(5, 1, 30, 0, true), now, false, "");
        Equal("0 von 1", everyLossIsLong.Streak);
        Equal("Der nächste Verlust sperrt 30 Minuten.", everyLossIsLong.NextLoss);

        StatusSnapshot negative = LossLockRules.Describe(
            new SessionState(-4, null, false, "2026-11-27"), Defaults(), now, false, "");
        Equal("0 von 3", negative.Streak);

        StatusSnapshot clamped = LossLockRules.Describe(
            new SessionState(2, null, false, "2026-11-27"),
            new LockSettings(5, 3, LossLockRules.MaxLockMinutes + 50, 0, true),
            now,
            false,
            "");
        Equal("Der nächste Verlust sperrt " + LossLockRules.MaxLockMinutes.ToString(CultureInfo.InvariantCulture) + " Minuten.", clamped.NextLoss);

        StatusSnapshot expired = LossLockRules.Describe(
            new SessionState(2, now, true, "2026-11-27"), Defaults(), now, false, "11:00:00");
        Equal("Aktiv", expired.State);
        Check(!expired.Locked, "a lock that has reached its end is not shown as locked");
        Equal("Der nächste Verlust sperrt 30 Minuten.", expired.Detail);
    }

    private static void StatusWhileLocked()
    {
        DateTime now = Utc(10, 0);
        DateTime until = now.AddMinutes(4).AddSeconds(12).AddMilliseconds(400);
        StatusSnapshot snap = LossLockRules.Describe(
            new SessionState(2, until, true, "2026-11-27"), Defaults(), now, false, "14:04:12");
        Equal("Gesperrt", snap.State);
        Equal("04:13", snap.Remaining);
        Equal("Gesperrt, noch 04:13", snap.CompactStatus);
        Equal("04:13", snap.Hero);
        Equal("bis 14:04:12", snap.Detail);
        Equal("14:04:12", snap.Until);
        Equal("Serie 2 von 3", snap.SeriesLine);
        Equal("-", snap.NextLoss);
        Equal("2 von 3", snap.Streak);
        Check(snap.Locked, "locked");

        StatusSnapshot exact = LossLockRules.Describe(
            new SessionState(1, now.AddMinutes(5), true, "2026-11-27"), Defaults(), now, false, "11:05:00");
        Equal("05:00", exact.Remaining);
        Equal("bis 11:05:00", exact.Detail);
    }

    private static void StatusWhilePending()
    {
        DateTime now = Utc(10, 0);
        StatusSnapshot pending = LossLockRules.Describe(
            new SessionState(1, null, false, "2026-11-27"), Defaults(), now, true, "");
        Equal("Sperre wird geprüft", pending.State);
        Equal("Sperre wird geprüft", pending.CompactStatus);
        Equal("1 von 3", pending.Hero);
        Equal("Der Abschluss wird bestätigt.", pending.Detail);
        Equal("", pending.SeriesLine);
        Equal("-", pending.Remaining);
        Equal("-", pending.NextLoss);
        Check(pending.Pending, "pending");
        Check(!pending.Locked, "pending is not a finished lock");

        StatusSnapshot both = LossLockRules.Describe(
            new SessionState(1, now.AddMinutes(5), true, "2026-11-27"), Defaults(), now, true, "11:05:00");
        Check(both.Locked, "an existing lock stays on screen while a new close is confirmed");
        Equal("Gesperrt", both.State);
        Equal("05:00", both.Remaining);
        Equal("bis 11:05:00", both.Detail);
        Check(both.Pending, "pending flag is kept for the caller");
    }

    private static void ReleaseAndHold()
    {
        DateTime until = Utc(10, 5);
        Check(LossLockRules.ShouldBeLocked(until, true, Utc(10, 4)), "before the end");
        Check(!LossLockRules.ShouldBeLocked(until, true, until), "at the end");
        Check(LossLockRules.ShouldRelease(until, true, until), "release at the end");
        Check(!LossLockRules.ShouldRelease(until, false, until.AddMinutes(1)), "not ours");
        Check(!LossLockRules.ShouldBeLocked(null, true, Utc(10, 0)), "no end");
    }

    private static SessionState Empty()
    {
        return new SessionState(0, null, false, null);
    }

    private static DateTime Utc(int hour, int minute)
    {
        return new DateTime(2026, 11, 27, hour, minute, 0, DateTimeKind.Utc);
    }

    private static void Equal(object expected, object actual, string message = null)
    {
        if (!Equals(expected, actual))
            Fail((message ?? "equal") + ": expected <" + expected + "> actual <" + actual + ">");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            Fail(message);
    }

    private static void Fail(string message)
    {
        failed++;
        Console.Error.WriteLine("FAIL " + message);
    }
}
