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
