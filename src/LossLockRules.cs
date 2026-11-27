using System;
using System.Globalization;

namespace LossLock
{
    public enum RoundTripOutcome
    {
        Unknown,
        Scratch,
        Win,
        Loss
    }

    public readonly record struct SessionState(
        int ConsecutiveLosses,
        DateTime? LockUntilUtc,
        bool HoldsLock,
        string LastLocalDate);

    public readonly record struct LockSnapshot(
        int ConsecutiveLosses,
        DateTime? LockUntilUtc,
        bool HoldsLock,
        string Reason,
        bool Changed);

    public readonly record struct LockSettings(
        int LossLockMinutes,
        int LossesForLongLock,
        int LongLockMinutes,
        double MinimumLoss,
        bool ResetStreakEachLocalDay);

    /// <summary>
    /// Display strings for one moment. The strategy and the status window both
    /// render this, so the countdown cannot drift between the two.
    /// </summary>
    public readonly record struct StatusSnapshot(
        string CompactStatus,
        string State,
        string Streak,
        string NextLoss,
        string Remaining,
        string Until,
        string Hero,
        string Detail,
        string SeriesLine,
        bool Locked,
        bool Pending);

    /// <summary>
    /// Pure rules for the loss lock. No Quantower types, so the streak and
    /// the lock end can be tested without the terminal.
    /// </summary>
    public static class LossLockRules
    {
        public const double Dust = 0.00000001;
        public const int MaxLockMinutes = 7 * 24 * 60;

        public static RoundTripOutcome Classify(double? pnl, double minimumLoss)
        {
            if (!IsFinite(pnl))
                return RoundTripOutcome.Unknown;

            double floor = minimumLoss > 0 ? minimumLoss : 0;
            double value = pnl.Value;
            if (value > Dust)
                return RoundTripOutcome.Win;
            if (value <= -Math.Max(floor, Dust))
                return RoundTripOutcome.Loss;
            return RoundTripOutcome.Scratch;
        }

        /// <summary>
        /// Prefer the position PnL. If the connection reports 0 on the position
        /// but the fills of that position sum to a real result, use the fills.
        /// </summary>
        public static double? ChoosePnl(double? positionPnl, double? aggregatedTradePnl)
        {
            bool positionOk = IsFinite(positionPnl);
            bool tradesOk = IsFinite(aggregatedTradePnl);
            if (!positionOk && !tradesOk)
                return null;
            if (!positionOk)
                return aggregatedTradePnl;
            if (!tradesOk)
                return positionPnl;
            if (Math.Abs(positionPnl.Value) <= Dust && Math.Abs(aggregatedTradePnl.Value) > Dust)
                return aggregatedTradePnl;
            return positionPnl;
        }

        public static int StreakForNewDay(int consecutiveLosses, string lastLocalDate, string todayLocalDate, bool resetEachDay)
        {
            if (!resetEachDay)
                return consecutiveLosses < 0 ? 0 : consecutiveLosses;
            if (string.IsNullOrEmpty(lastLocalDate) || lastLocalDate == todayLocalDate)
                return consecutiveLosses < 0 ? 0 : consecutiveLosses;
            return 0;
        }

        public static (SessionState State, LockSnapshot Snapshot) OnRoundTrip(
            SessionState state,
            RoundTripOutcome outcome,
            DateTime nowUtc,
            string todayLocalDate,
            LockSettings settings)
        {
            int streak = outcome == RoundTripOutcome.Unknown
                ? state.ConsecutiveLosses
                : StreakForNewDay(state.ConsecutiveLosses, state.LastLocalDate, todayLocalDate, settings.ResetStreakEachLocalDay);
            LockSnapshot snapshot = ApplyTrade(streak, state.LockUntilUtc, state.HoldsLock, outcome, nowUtc, settings);
            string lastDate = outcome == RoundTripOutcome.Unknown ? state.LastLocalDate : todayLocalDate;
            SessionState next = new SessionState(snapshot.ConsecutiveLosses, snapshot.LockUntilUtc, snapshot.HoldsLock, lastDate);
            return (next, snapshot);
        }

        public static LockSnapshot ApplyTrade(
            int consecutiveLosses,
            DateTime? lockUntilUtc,
            bool holdsLock,
            RoundTripOutcome outcome,
            DateTime nowUtc,
            LockSettings settings)
        {
            if (consecutiveLosses < 0)
                consecutiveLosses = 0;

            if (outcome == RoundTripOutcome.Unknown || outcome == RoundTripOutcome.Scratch)
            {
                return new LockSnapshot(consecutiveLosses, lockUntilUtc, holdsLock, null, false);
            }

            if (outcome == RoundTripOutcome.Win)
            {
                bool changed = consecutiveLosses != 0;
                string reason = changed
                    ? "Gewinn. Die Verlustserie steht wieder auf 0. Eine laufende Sperre bleibt bis zu ihrem Ende."
                    : null;
                return new LockSnapshot(0, lockUntilUtc, holdsLock, reason, changed);
            }

            int streak = consecutiveLosses + 1;
            int trip = TripCount(settings);
            int minutes = ClampMinutes(streak >= trip ? settings.LongLockMinutes : settings.LossLockMinutes);

            if (minutes == 0)
            {
                return new LockSnapshot(
                    streak,
                    lockUntilUtc,
                    holdsLock,
                    "Verlust " + streak + ". Für diese Stufe ist keine Sperre konfiguriert.",
                    true);
            }

            DateTime candidate = nowUtc.AddMinutes(minutes);
            DateTime? until = lockUntilUtc;
            bool extended = until == null || candidate > until.Value;
            if (extended)
                until = candidate;

            string lockReason = streak >= trip
                ? "Verlust " + streak + " in Folge. Sperre " + minutes + " Minuten."
                : "Verlust. Serie " + streak + " von " + trip + ". Sperre " + minutes + " Minuten.";

            return new LockSnapshot(streak, until, true, lockReason, true);
        }

        public static bool ShouldBeLocked(DateTime? lockUntilUtc, bool holdsLock, DateTime nowUtc)
        {
            return holdsLock && lockUntilUtc.HasValue && nowUtc < lockUntilUtc.Value;
        }

        public static bool ShouldRelease(DateTime? lockUntilUtc, bool holdsLock, DateTime nowUtc)
        {
            return holdsLock && lockUntilUtc.HasValue && nowUtc >= lockUntilUtc.Value;
        }

        /// <summary>
        /// untilLocal is the already formatted clock time of the lock end.
        /// Empty when there is no end. The rules stay free of time zones.
        /// </summary>
        public static StatusSnapshot Describe(
            SessionState state,
            LockSettings settings,
            DateTime nowUtc,
            bool pendingConfirmation,
            string untilLocal)
        {
            int trip = TripCount(settings);
            int streak = state.ConsecutiveLosses < 0 ? 0 : state.ConsecutiveLosses;
            string streakText = streak.ToString(CultureInfo.InvariantCulture)
                + " von "
                + trip.ToString(CultureInfo.InvariantCulture);
            bool locked = ShouldBeLocked(state.LockUntilUtc, state.HoldsLock, nowUtc);
            string next = NextLossSentence(streak, settings);

            if (locked)
            {
                string remaining = FormatRemaining(state.LockUntilUtc.Value - nowUtc);
                string until = string.IsNullOrEmpty(untilLocal) ? "-" : untilLocal;
                return new StatusSnapshot(
                    "Gesperrt, noch " + remaining,
                    "Gesperrt",
                    streakText,
                    "-",
                    remaining,
                    until,
                    remaining,
                    "bis " + until,
                    "Serie " + streakText,
                    true,
                    pendingConfirmation);
            }

            if (pendingConfirmation)
            {
                return new StatusSnapshot(
                    "Sperre wird geprüft",
                    "Sperre wird geprüft",
                    streakText,
                    "-",
                    "-",
                    "-",
                    streakText,
                    "Der Abschluss wird bestätigt.",
                    "",
                    false,
                    true);
            }

            return new StatusSnapshot(
                "Aktiv",
                "Aktiv",
                streakText,
                next,
                "-",
                "-",
                streakText,
                next,
                "",
                false,
                false);
        }

        public static string FormatRemaining(TimeSpan remaining)
        {
            if (remaining.TotalSeconds <= 0)
                return "00:00";

            int total = (int)Math.Ceiling(remaining.TotalSeconds - 1e-9);
            if (total < 1)
                total = 1;

            int hours = total / 3600;
            int minutes = (total % 3600) / 60;
            int seconds = total % 60;
            if (hours > 0)
            {
                return hours.ToString(CultureInfo.InvariantCulture)
                    + ":"
                    + minutes.ToString("00", CultureInfo.InvariantCulture)
                    + ":"
                    + seconds.ToString("00", CultureInfo.InvariantCulture);
            }

            return minutes.ToString("00", CultureInfo.InvariantCulture)
                + ":"
                + seconds.ToString("00", CultureInfo.InvariantCulture);
        }

        public static string NextLossSentence(int consecutiveLosses, LockSettings settings)
        {
            int streak = consecutiveLosses < 0 ? 0 : consecutiveLosses;
            int minutes = streak + 1 >= TripCount(settings)
                ? settings.LongLockMinutes
                : settings.LossLockMinutes;
            minutes = ClampMinutes(minutes);
            if (minutes == 0)
                return "Der nächste Verlust sperrt nicht.";
            return "Der nächste Verlust sperrt " + MinutesLabel(minutes) + ".";
        }

        public static string MinutesLabel(int minutes)
        {
            if (minutes == 1)
                return "1 Minute";
            return minutes.ToString(CultureInfo.InvariantCulture) + " Minuten";
        }

        private static int TripCount(LockSettings settings)
        {
            return settings.LossesForLongLock < 1 ? 1 : settings.LossesForLongLock;
        }

        private static int ClampMinutes(int minutes)
        {
            if (minutes < 0)
                return 0;
            if (minutes > MaxLockMinutes)
                return MaxLockMinutes;
            return minutes;
        }

        private static bool IsFinite(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
        }
    }
}
