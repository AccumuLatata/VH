using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using TradingPlatform.BusinessLayer;

namespace LossLock
{
    /// <summary>
    /// Locks one Quantower account after a losing round trip.
    /// One loss locks for LossLockMinutes (default 5).
    /// The third consecutive loss locks for LongLockMinutes (default 30).
    /// The lock is Quantower's own account lock (Core.LockAccount), the same
    /// mechanism as the account lock in the Account Info panel. It is not the
    /// toolbar button, which also disables closing a position.
    /// </summary>
    public sealed class LossLockStrategy : Strategy, ICurrentAccount
    {
        private const int ConfirmMilliseconds = 600;
        private const int TickMilliseconds = 250;

        [InputParameter("Konto", 0)]
        public Account CurrentAccount { get; set; }

        [InputParameter("Nur ein Symbol zählen", 1)]
        public bool OnlySelectedSymbol { get; set; }

        [InputParameter("Symbol", 2)]
        public Symbol CurrentSymbol { get; set; }

        [InputParameter("Sperre nach einem Verlust, Minuten", 3, minimum: 0, maximum: 1440, increment: 1, decimalPlaces: 0)]
        public int LossLockMinutes { get; set; }

        [InputParameter("Verluste in Folge für die lange Sperre", 4, minimum: 1, maximum: 20, increment: 1, decimalPlaces: 0)]
        public int LossesForLongLock { get; set; }

        [InputParameter("Lange Sperre, Minuten", 5, minimum: 0, maximum: 1440, increment: 1, decimalPlaces: 0)]
        public int LongLockMinutes { get; set; }

        [InputParameter("Mindestverlust in Kontowährung", 6, minimum: 0, maximum: 1000000, increment: 1, decimalPlaces: 2)]
        public double MinimumLoss { get; set; }

        [InputParameter("Gebühren mitzählen (Netto statt Brutto)", 7)]
        public bool CountFees { get; set; }

        [InputParameter("Serie an einem neuen lokalen Tag zurücksetzen", 8)]
        public bool ResetStreakEachLocalDay { get; set; }

        [InputParameter("Kontosperre von Quantower setzen", 9)]
        public bool UseAccountLock { get; set; }

        [InputParameter("Neue Einstiege während der Sperre abbrechen", 10)]
        public bool CancelOpeningOrders { get; set; }

        public override string[] MonitoringConnectionsIds => new[] { this.CurrentAccount?.ConnectionId };

        private readonly object gate = new object();
        private readonly Dictionary<string, double> grossByPosition = new Dictionary<string, double>();
        private readonly Dictionary<string, double> netByPosition = new Dictionary<string, double>();
        private readonly HashSet<string> trackedPositions = new HashSet<string>();
        private readonly Dictionary<string, PendingClose> pending = new Dictionary<string, PendingClose>();

        private Timer timer;
        private bool armed;
        private bool provisionalHeld;
        private SessionState session = new SessionState(0, null, false, null);
        private string status = "Startet";
        private string lastRoundTrip = "-";
        private string statePath;
        private StatusWindow statusWindow;
        private bool statusWindowClosedLogged;

        public LossLockStrategy()
        {
            this.Name = "Loss Lock";
            this.Description = "Sperrt das Konto nach einem Verlust und länger nach einer Verlustserie.";
            this.LossLockMinutes = 5;
            this.LossesForLongLock = 3;
            this.LongLockMinutes = 30;
            this.MinimumLoss = 0;
            this.CountFees = false;
            this.ResetStreakEachLocalDay = true;
            this.UseAccountLock = true;
            this.CancelOpeningOrders = true;
            this.OnlySelectedSymbol = false;
        }

        protected override void OnRun()
        {
            this.RestoreAccount();
            if (this.CurrentAccount == null || string.IsNullOrEmpty(this.CurrentAccount.Id))
            {
                this.status = "Konto fehlt";
                this.Log("Kein Konto gewählt. Strategie stoppen, das Konto setzen und neu starten.", StrategyLoggingLevel.Error);
                return;
            }

            if (this.OnlySelectedSymbol)
            {
                this.RestoreSymbol();
                if (this.CurrentSymbol == null)
                {
                    this.status = "Symbol fehlt";
                    this.Log("Nur ein Symbol ist aktiv, aber es ist keins gewählt.", StrategyLoggingLevel.Error);
                    return;
                }

                if (this.CurrentSymbol.ConnectionId != this.CurrentAccount.ConnectionId)
                {
                    this.status = "Symbol und Konto passen nicht zusammen";
                    this.Log("Symbol und Konto kommen von verschiedenen Verbindungen.", StrategyLoggingLevel.Error);
                    return;
                }
            }

            this.statePath = StateFilePath(this.CurrentAccount);
            this.session = LoadState(this.statePath);
            this.ApplyDayRoll();
            this.grossByPosition.Clear();
            this.netByPosition.Clear();
            this.trackedPositions.Clear();
            this.pending.Clear();

            foreach (Position position in Core.Instance.Positions)
            {
                if (this.IsWatched(position?.Account, position?.Symbol) && !string.IsNullOrEmpty(position.Id))
                    this.trackedPositions.Add(position.Id);
            }

            Core.Instance.PositionAdded += this.OnPositionAdded;
            Core.Instance.PositionRemoved += this.OnPositionRemoved;
            Core.Instance.TradeAdded += this.OnTradeAdded;
            Core.Instance.OrderAdded += this.OnOrderAdded;

            this.timer = new Timer(_ => this.Tick(), null, TickMilliseconds, TickMilliseconds);
            this.armed = true;

            bool lockedNow;
            lock (this.gate)
            {
                lockedNow = LossLockRules.ShouldBeLocked(this.session.LockUntilUtc, this.session.HoldsLock, this.NowUtc());
                this.status = this.CaptureSnapshot(this.NowUtc()).CompactStatus;
            }

            if (lockedNow && this.UseAccountLock)
                this.Lock(this.CurrentAccount);

            this.Log(
                "Loss Lock aktiv für " + this.CurrentAccount.Name
                + ". Ein Verlust sperrt " + this.LossLockMinutes + " min, "
                + this.LossesForLongLock + " in Folge sperren " + this.LongLockMinutes
                + " min. Serie " + this.session.ConsecutiveLosses + ".",
                StrategyLoggingLevel.Info);

            if (lockedNow)
                this.Log("Eine Sperre aus der letzten Sitzung läuft noch bis " + this.FormatLocal(this.session.LockUntilUtc) + ".", StrategyLoggingLevel.Trading);

            this.OpenStatusWindow();
        }

        protected override void OnStop()
        {
            this.Disarm(keepLock: true);
            base.OnStop();
        }

        protected override void OnRemove()
        {
            this.Disarm(keepLock: true);
            base.OnRemove();
        }

        protected override List<StrategyMetric> OnGetMetrics()
        {
            List<StrategyMetric> result = base.OnGetMetrics();
            StatusSnapshot snapshot;
            string last;
            lock (this.gate)
            {
                snapshot = this.CaptureSnapshot(this.NowUtc());
                this.status = snapshot.CompactStatus;
                last = this.lastRoundTrip;
            }

            result.Add(new StrategyMetric { Name = "Status", FormattedValue = snapshot.CompactStatus });
            result.Add(new StrategyMetric { Name = "Verlustserie", FormattedValue = snapshot.Streak });
            result.Add(new StrategyMetric { Name = "Nächster Verlust", FormattedValue = snapshot.NextLoss });
            result.Add(new StrategyMetric { Name = "Restzeit", FormattedValue = snapshot.Remaining });
            result.Add(new StrategyMetric { Name = "Gesperrt bis", FormattedValue = snapshot.Until });
            result.Add(new StrategyMetric { Name = "Letzter Abschluss", FormattedValue = last });
            return result;
        }

        private void Disarm(bool keepLock)
        {
            this.CloseStatusWindow();

            bool wasArmed;
            DateTime? until;
            lock (this.gate)
            {
                wasArmed = this.armed;
                this.armed = false;
                until = this.session.LockUntilUtc;
            }

            if (this.timer != null)
            {
                this.timer.Dispose();
                this.timer = null;
            }

            Core.Instance.PositionAdded -= this.OnPositionAdded;
            Core.Instance.PositionRemoved -= this.OnPositionRemoved;
            Core.Instance.TradeAdded -= this.OnTradeAdded;
            Core.Instance.OrderAdded -= this.OnOrderAdded;

            if (!wasArmed)
                return;

            this.SaveState();
            if (keepLock && until.HasValue && this.NowUtc() < until.Value)
            {
                this.Log(
                    "Strategie gestoppt. Die Kontosperre bleibt bis " + this.FormatLocal(until)
                    + ". Neu starten, damit sie pünktlich aufgehoben wird. "
                    + "Manuelles Entsperren im Account-Info-Panel gilt nur, solange diese Strategie nicht läuft.",
                    StrategyLoggingLevel.Trading);
            }
        }

        private void OnPositionAdded(Position position)
        {
            try
            {
                if (!this.IsWatched(position?.Account, position?.Symbol) || string.IsNullOrEmpty(position.Id))
                    return;

                bool releaseProvisional = false;
                lock (this.gate)
                {
                    if (this.pending.Remove(position.Id))
                    {
                        this.Log("Positions-Update ignoriert, dieselbe Position ist sofort wieder da: " + position.Id, StrategyLoggingLevel.Info);
                        releaseProvisional = !this.ProtectionActive(this.NowUtc());
                    }

                    this.trackedPositions.Add(position.Id);
                }

                if (releaseProvisional && this.UseAccountLock && this.CurrentAccount != null)
                {
                    Core.Instance.UnLockAccount(this.CurrentAccount);
                    lock (this.gate)
                        this.provisionalHeld = false;
                }
            }
            catch (Exception exception)
            {
                this.Log("PositionAdded: " + exception.Message, StrategyLoggingLevel.Error);
            }
        }

        private void OnPositionRemoved(Position position)
        {
            try
            {
                if (position == null || string.IsNullOrEmpty(position.Id))
                    return;
                if (!this.IsWatched(position.Account, position.Symbol))
                    return;

                if (!this.ConnectionIsUp(position.ConnectionId))
                {
                    lock (this.gate)
                    {
                        this.trackedPositions.Remove(position.Id);
                        this.grossByPosition.Remove(position.Id);
                        this.netByPosition.Remove(position.Id);
                        this.pending.Remove(position.Id);
                    }
                    return;
                }

                bool protectNow = false;
                lock (this.gate)
                {
                    this.trackedPositions.Remove(position.Id);

                    double gross;
                    double net;
                    bool hasGross = this.grossByPosition.TryGetValue(position.Id, out gross);
                    bool hasNet = this.netByPosition.TryGetValue(position.Id, out net);
                    this.grossByPosition.Remove(position.Id);
                    this.netByPosition.Remove(position.Id);

                    PendingClose close = new PendingClose
                    {
                        Id = position.Id,
                        SeenUtc = this.NowUtc(),
                        PositionPnl = ReadPnl(this.CountFees ? position.NetPnL : position.GrossPnL),
                        TradePnl = this.CountFees
                            ? (hasNet ? (double?)net : null)
                            : (hasGross ? (double?)gross : null),
                        SymbolName = position.Symbol != null ? position.Symbol.Name : ""
                    };
                    close.Protective = this.LooksLikeLoss(close);
                    this.pending[position.Id] = close;
                    protectNow = close.Protective;
                }

                if (protectNow && this.UseAccountLock && this.CurrentAccount != null)
                {
                    this.Lock(this.CurrentAccount);
                    lock (this.gate)
                        this.provisionalHeld = true;
                }
            }
            catch (Exception exception)
            {
                this.Log("PositionRemoved: " + exception.Message, StrategyLoggingLevel.Error);
            }
        }

        private void OnTradeAdded(Trade trade)
        {
            try
            {
                if (trade == null || string.IsNullOrEmpty(trade.PositionId))
                    return;
                if (!this.IsWatched(trade.Account, trade.Symbol))
                    return;

                bool protectNow = false;
                lock (this.gate)
                {

                    if (trade.GrossPnl != null)
                    {
                        double current;
                        this.grossByPosition.TryGetValue(trade.PositionId, out current);
                        this.grossByPosition[trade.PositionId] = current + trade.GrossPnl.Value;
                    }

                    if (trade.NetPnl != null)
                    {
                        double current;
                        this.netByPosition.TryGetValue(trade.PositionId, out current);
                        this.netByPosition[trade.PositionId] = current + trade.NetPnl.Value;
                    }

                    PendingClose close;
                    if (this.pending.TryGetValue(trade.PositionId, out close))
                    {
                        double sum;
                        if (this.CountFees)
                        {
                            if (this.netByPosition.TryGetValue(trade.PositionId, out sum))
                                close.TradePnl = sum;
                        }
                        else if (this.grossByPosition.TryGetValue(trade.PositionId, out sum))
                        {
                            close.TradePnl = sum;
                        }

                        close.Protective = this.LooksLikeLoss(close);
                        protectNow = close.Protective;
                    }
                }

                if (protectNow && this.UseAccountLock && this.CurrentAccount != null)
                {
                    this.Lock(this.CurrentAccount);
                    lock (this.gate)
                        this.provisionalHeld = true;
                }
            }
            catch (Exception exception)
            {
                this.Log("TradeAdded: " + exception.Message, StrategyLoggingLevel.Error);
            }
        }

        private void OnOrderAdded(Order order)
        {
            try
            {
                if (!this.CancelOpeningOrders || order == null)
                    return;
                if (!this.IsWatched(order.Account, order.Symbol))
                    return;

                bool locked;
                lock (this.gate)
                    locked = this.ProtectionActive(this.NowUtc());

                if (!locked || this.IsReducingOrder(order))
                    return;

                TradingOperationResult result = order.Cancel("LossLock");
                this.Log(
                    "Einstieg während der Sperre abgebrochen: " + order.Id
                    + " (" + (result != null ? result.Status.ToString() : "ohne Ergebnis") + ").",
                    StrategyLoggingLevel.Trading);

                if (this.UseAccountLock && this.CurrentAccount != null)
                    this.Lock(this.CurrentAccount);
            }
            catch (Exception exception)
            {
                this.Log("OrderAdded: " + exception.Message, StrategyLoggingLevel.Error);
            }
        }

        private void Tick()
        {
            try
            {
                List<PendingClose> ready = new List<PendingClose>();
                List<string> alerts = new List<string>();
                bool doLock = false;
                bool doUnlock = false;
                bool dropProvisional = false;
                Account account = this.CurrentAccount;
                DateTime now = this.NowUtc();
                StatusSnapshot snapshot = default(StatusSnapshot);
                string roundTrip = null;
                bool showWindow = false;

                lock (this.gate)
                {
                    if (!this.armed)
                        return;

                    List<string> due = new List<string>();
                    foreach (KeyValuePair<string, PendingClose> pair in this.pending)
                    {
                        if ((now - pair.Value.SeenUtc).TotalMilliseconds >= ConfirmMilliseconds)
                            due.Add(pair.Key);
                    }

                    foreach (string id in due)
                    {
                        ready.Add(this.pending[id]);
                        this.pending.Remove(id);
                        this.grossByPosition.Remove(id);
                        this.netByPosition.Remove(id);
                    }

                    foreach (PendingClose close in ready)
                        this.ApplyClose(close, now, alerts);

                    bool pendingLoss = this.HasProtectivePending();
                    bool sessionLock = LossLockRules.ShouldBeLocked(this.session.LockUntilUtc, this.session.HoldsLock, now);
                    doUnlock = LossLockRules.ShouldRelease(this.session.LockUntilUtc, this.session.HoldsLock, now) && !pendingLoss;
                    if (doUnlock)
                    {
                        this.session = new SessionState(this.session.ConsecutiveLosses, null, false, this.session.LastLocalDate);
                        this.provisionalHeld = false;
                    }

                    doLock = sessionLock || pendingLoss;
                    dropProvisional = this.provisionalHeld && !doLock && !doUnlock;
                    if (doLock && sessionLock)
                        this.provisionalHeld = false;
                    else if (doLock)
                        this.provisionalHeld = true;
                    else if (dropProvisional)
                        this.provisionalHeld = false;

                    snapshot = this.CaptureSnapshot(now);
                    this.status = snapshot.CompactStatus;
                    roundTrip = this.lastRoundTrip;
                    showWindow = true;
                }

                if (doUnlock || dropProvisional)
                {
                    if (this.UseAccountLock && account != null)
                        Core.Instance.UnLockAccount(account);
                    this.SaveState();
                    if (doUnlock)
                        this.Log("Sperre aufgehoben.", StrategyLoggingLevel.Trading);
                }
                else if (doLock && this.UseAccountLock && account != null)
                {
                    this.Lock(account);
                }

                if (ready.Count > 0)
                    this.SaveState();

                foreach (string alert in alerts)
                {
                    this.Log(alert, StrategyLoggingLevel.Trading);
                    Core.Instance.Alert(alert, "", account != null ? account.ConnectionId : "");
                }

                if (showWindow)
                    this.PublishStatusWindow(snapshot, roundTrip);
            }
            catch (Exception exception)
            {
                this.Log("Tick: " + exception.Message, StrategyLoggingLevel.Error);
            }
        }

        private void ApplyClose(PendingClose close, DateTime now, List<string> alerts)
        {
            double? pnl = LossLockRules.ChoosePnl(close.PositionPnl, close.TradePnl);
            RoundTripOutcome outcome = LossLockRules.Classify(pnl, this.MinimumLoss);
            string today = this.LocalDate(now);
            (SessionState next, LockSnapshot snapshot) = LossLockRules.OnRoundTrip(this.session, outcome, now, today, this.SettingsFromInputs());
            this.session = next;

            string pnlText = pnl.HasValue ? pnl.Value.ToString("0.##", CultureInfo.InvariantCulture) : "nicht lesbar";
            this.lastRoundTrip = outcome + " " + pnlText + (string.IsNullOrEmpty(close.SymbolName) ? "" : " " + close.SymbolName);

            if (outcome == RoundTripOutcome.Unknown)
            {
                alerts.Add("Abschluss ohne lesbares P&L (" + close.SymbolName + "). Serie unverändert. Im Log prüfen, ob die Verbindung GrossPnL liefert.");
                return;
            }

            if (!snapshot.Changed || string.IsNullOrEmpty(snapshot.Reason))
                return;

            string text = snapshot.Reason;
            if (snapshot.HoldsLock && snapshot.LockUntilUtc.HasValue)
                text += " Bis " + this.FormatLocal(snapshot.LockUntilUtc) + ".";
            alerts.Add(text);
        }

        private bool IsReducingOrder(Order order)
        {
            if (order.Symbol == null)
                return false;

            double quantity = order.TotalQuantity > 0 ? order.TotalQuantity : order.RemainingQuantity;
            foreach (Position position in Core.Instance.Positions)
            {
                if (!this.IsWatched(position?.Account, position?.Symbol))
                    continue;
                if (position.Symbol == null || position.Symbol.Id != order.Symbol.Id)
                    continue;
                if (order.Side != position.Side && quantity <= position.Quantity + LossLockRules.Dust)
                    return true;
            }

            return false;
        }

        private bool LooksLikeLoss(PendingClose close)
        {
            return LossLockRules.Classify(LossLockRules.ChoosePnl(close.PositionPnl, close.TradePnl), this.MinimumLoss) == RoundTripOutcome.Loss;
        }

        private bool ProtectionActive(DateTime nowUtc)
        {
            if (LossLockRules.ShouldBeLocked(this.session.LockUntilUtc, this.session.HoldsLock, nowUtc))
                return true;
            return this.HasProtectivePending();
        }

        private bool HasProtectivePending()
        {
            foreach (PendingClose close in this.pending.Values)
            {
                if (close.Protective)
                    return true;
            }

            return false;
        }

        private void ApplyDayRoll()
        {
            string today = this.LocalDate(this.NowUtc());
            int streak = LossLockRules.StreakForNewDay(
                this.session.ConsecutiveLosses,
                this.session.LastLocalDate,
                today,
                this.ResetStreakEachLocalDay);
            if (streak != this.session.ConsecutiveLosses)
                this.session = new SessionState(streak, this.session.LockUntilUtc, this.session.HoldsLock, today);
        }

        private bool IsWatched(Account account, Symbol symbol)
        {
            if (!this.SameAccount(account))
                return false;
            if (!this.OnlySelectedSymbol)
                return true;
            if (this.CurrentSymbol == null || symbol == null)
                return false;
            return symbol.Id == this.CurrentSymbol.Id && symbol.ConnectionId == this.CurrentSymbol.ConnectionId;
        }

        private bool SameAccount(Account account)
        {
            Account current = this.CurrentAccount;
            if (account == null || current == null)
                return false;
            if (account == current)
                return true;
            return account.Id == current.Id && account.ConnectionId == current.ConnectionId;
        }

        private bool ConnectionIsUp(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId))
                return false;

            foreach (Connection connection in Core.Instance.Connections.Connected)
            {
                if (connection != null && connection.Id == connectionId)
                    return true;
            }

            return false;
        }

        private void Lock(Account account)
        {
            Core.Instance.LockAccount(account);
        }

        private void RestoreAccount()
        {
            if (this.CurrentAccount != null && this.CurrentAccount.State == BusinessObjectState.Fake)
                this.CurrentAccount = Core.Instance.GetAccount(this.CurrentAccount.CreateInfo());
        }

        private void RestoreSymbol()
        {
            if (this.CurrentSymbol != null && this.CurrentSymbol.State == BusinessObjectState.Fake)
                this.CurrentSymbol = Core.Instance.GetSymbol(this.CurrentSymbol.CreateInfo());
        }

        private LockSettings SettingsFromInputs()
        {
            return new LockSettings(
                this.LossLockMinutes,
                this.LossesForLongLock,
                this.LongLockMinutes,
                this.MinimumLoss,
                this.ResetStreakEachLocalDay);
        }

        private void OpenStatusWindow()
        {
            StatusWindow opened = null;
            try
            {
                string error;
                opened = StatusWindow.TryOpen(out error);
                if (opened == null)
                {
                    this.Log(
                        "Statusfenster nicht geöffnet. " + (string.IsNullOrEmpty(error) ? "" : error + " ")
                        + "Die Kennzahlen im Strategies Manager bleiben gültig.",
                        StrategyLoggingLevel.Error);
                    return;
                }

                bool keep;
                StatusSnapshot snapshot;
                string roundTrip;
                lock (this.gate)
                {
                    keep = this.armed;
                    if (keep)
                    {
                        this.statusWindow = opened;
                        this.statusWindowClosedLogged = false;
                    }

                    snapshot = this.CaptureSnapshot(this.NowUtc());
                    roundTrip = this.lastRoundTrip;
                }

                if (!keep)
                {
                    opened.Dispose();
                    opened = null;
                    return;
                }

                string account = this.CurrentAccount != null ? this.CurrentAccount.Name : "";
                opened.Publish(snapshot, account, roundTrip);
                opened = null;
            }
            catch (Exception exception)
            {
                if (opened != null)
                {
                    lock (this.gate)
                    {
                        if (object.ReferenceEquals(this.statusWindow, opened))
                            this.statusWindow = null;
                    }

                    try
                    {
                        opened.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }

                this.Log("Statusfenster nicht geöffnet. " + exception.Message, StrategyLoggingLevel.Error);
            }
        }

        private void PublishStatusWindow(StatusSnapshot snapshot, string roundTrip)
        {
            try
            {
                StatusWindow current;
                lock (this.gate)
                    current = this.statusWindow;

                if (current == null)
                    return;

                if (!current.IsOpen)
                {
                    bool tellUser = false;
                    lock (this.gate)
                    {
                        if (object.ReferenceEquals(this.statusWindow, current))
                            this.statusWindow = null;
                        if (!this.statusWindowClosedLogged)
                        {
                            this.statusWindowClosedLogged = true;
                            tellUser = true;
                        }
                    }

                    if (tellUser)
                    {
                        this.Log(
                            "Statusfenster geschlossen. Die Strategie läuft weiter und hält die Sperre.",
                            StrategyLoggingLevel.Info);
                    }

                    return;
                }

                string account = this.CurrentAccount != null ? this.CurrentAccount.Name : "";
                current.Publish(snapshot, account, roundTrip);
            }
            catch (Exception exception)
            {
                this.Log("Statusfenster: " + exception.Message, StrategyLoggingLevel.Error);
            }
        }

        private void CloseStatusWindow()
        {
            StatusWindow current;
            lock (this.gate)
            {
                current = this.statusWindow;
                this.statusWindow = null;
                this.statusWindowClosedLogged = true;
            }

            if (current == null)
                return;

            try
            {
                current.Dispose();
            }
            catch (Exception exception)
            {
                this.Log("Statusfenster: " + exception.Message, StrategyLoggingLevel.Error);
            }
        }

        private StatusSnapshot CaptureSnapshot(DateTime nowUtc)
        {
            string untilLocal = this.session.LockUntilUtc.HasValue
                ? this.FormatLocal(this.session.LockUntilUtc)
                : "";
            return LossLockRules.Describe(
                this.session,
                this.SettingsFromInputs(),
                nowUtc,
                this.HasProtectivePending(),
                untilLocal);
        }

        private DateTime NowUtc()
        {
            return Core.Instance.TimeUtils.DateTimeUtcNow;
        }

        private string LocalDate(DateTime utc)
        {
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);
            return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private string FormatLocal(DateTime? utc)
        {
            if (!utc.HasValue)
                return "-";
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), TimeZoneInfo.Local);
            return local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static double? ReadPnl(PnLItem item)
        {
            if (item == null)
                return null;
            return item.Value;
        }

        private void SaveState()
        {
            try
            {
                if (string.IsNullOrEmpty(this.statePath))
                    return;

                SessionState copy;
                lock (this.gate)
                    copy = this.session;

                string directory = Path.GetDirectoryName(this.statePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string until = copy.LockUntilUtc.HasValue
                    ? copy.LockUntilUtc.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                    : "";

                string[] lines =
                {
                    "version=1",
                    "consecutiveLosses=" + copy.ConsecutiveLosses.ToString(CultureInfo.InvariantCulture),
                    "lockUntilUtc=" + until,
                    "holdsLock=" + (copy.HoldsLock ? "true" : "false"),
                    "lastLocalDate=" + (copy.LastLocalDate ?? "")
                };

                File.WriteAllLines(this.statePath, lines);
            }
            catch (Exception exception)
            {
                this.Log("Zustand konnte nicht gespeichert werden: " + exception.Message, StrategyLoggingLevel.Error);
            }
        }

        private static SessionState LoadState(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return new SessionState(0, null, false, null);

                int streak = 0;
                DateTime? until = null;
                bool holds = false;
                string lastDate = null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    int split = raw.IndexOf('=');
                    if (split <= 0)
                        continue;
                    string key = raw.Substring(0, split);
                    string value = raw.Substring(split + 1);
                    if (key == "consecutiveLosses")
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out streak);
                    else if (key == "lockUntilUtc" && value.Length > 0)
                    {
                        DateTime parsed;
                        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed))
                            until = parsed.ToUniversalTime();
                    }
                    else if (key == "holdsLock")
                        holds = value == "true";
                    else if (key == "lastLocalDate" && value.Length > 0)
                        lastDate = value;
                }

                if (streak < 0)
                    streak = 0;
                return new SessionState(streak, until, holds, lastDate);
            }
            catch
            {
                return new SessionState(0, null, false, null);
            }
        }

        private static string StateFilePath(Account account)
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LossLock");
            string name = Sanitize(account.ConnectionId) + "_" + Sanitize(account.Id) + ".state";
            return Path.Combine(folder, name);
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "unknown";
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            }
            return new string(chars);
        }

        private sealed class PendingClose
        {
            public string Id;
            public DateTime SeenUtc;
            public double? PositionPnl;
            public double? TradePnl;
            public string SymbolName;
            public bool Protective;
        }
    }
}
