// =============================================================================
//  SignalBridge - external signal routing into NinjaTrader 8
// =============================================================================
//
//  WHAT IT DOES
//  Hosts a TCP listener inside NinjaTrader, accepts orders from an external
//  research engine over a JSON protocol, places and manages the resulting
//  brackets on the connected account, and streams fills and live P&L back.
//  The wire protocol is specified in the block comment below this one.
//
//  ARCHITECTURAL DECISIONS AND WHY
//
//  * Implemented as an Indicator rather than a Strategy. A Strategy owns its own
//    position and is torn down and rebuilt by NinjaTrader on parameter changes,
//    data reloads and connection events, which would drop a live listener socket
//    and orphan working orders. An Indicator has a stable lifetime on the chart
//    and reaches the account directly, so the transport survives events that
//    would recycle a Strategy.
//
//  * Loopback-only TCP, not an HTTP webhook. Binding to IPAddress.Loopback means
//    the listener is unreachable from off the machine by construction, rather
//    than by firewall configuration that can be changed by accident. The
//    research engine runs on the same host; nothing about this design requires
//    exposing an order entry endpoint to a network.
//
//  * The trailing stop state machine runs HERE, in C#, not in the Python engine
//    that sends the order. The stop has to react to price at the speed the
//    platform sees it. Routing every trail decision back over the socket would
//    add a round trip to each adjustment, and — more seriously — a Python
//    process that crashed mid-trade would leave a live position with a stop that
//    had stopped moving. Placing the state machine next to the order means the
//    position is still managed correctly if the far end disappears.
//
//  * The same state machine is mirrored in the Python execution handler used for
//    backtesting. Two implementations is a real cost and is accepted on purpose:
//    a backtest whose exit logic differs from live execution measures a strategy
//    nobody will trade. Keeping them in step is what makes backtest results
//    transferable to the live account.
//
//  * InternalTraceId is generated for correlating fills back to the originating
//    Python request and is deliberately never sent to the broker. Order tags
//    that reach the exchange become part of the audit record and are subject to
//    broker-side length and character rules; an internal correlation id has no
//    business in that namespace.
//
//  * Two separate locks - ocoLock for bracket state, sendLock for socket writes.
//    The listener thread and NinjaTrader order callbacks run concurrently, so
//    bracket bookkeeping and wire writes are genuinely contended. They are split
//    rather than sharing one lock because a slow socket write would otherwise
//    block order state updates, which is the opposite of the priority order that
//    matters when a position is open.
//
//  * The listener thread is marked IsBackground and joined with a bounded
//    timeout on teardown. A foreground thread would keep the NinjaTrader process
//    alive after the user closed it; an unbounded join would hang shutdown if
//    the socket were blocked.
//
//  * Telemetry is throttled to one message per second rather than sent per tick.
//    The far end needs position and P&L state, not every price change, and an
//    unthrottled stream would spend the socket budget on data nobody reads.
//
// =============================================================================

/*
 * SignalBridge Indicator for NinjaTrader 8
 * 
 * Receives trading signals from Python engine via TCP socket
 * and executes orders on the connected account.
 * 
 * INSTALLATION:
 * 1. In NinjaTrader 8, go to Tools > Edit NinjaScript > Indicator
 * 2. Create new indicator named "SignalBridge"
 * 3. Replace all code with this file's contents
 * 4. Click Compile (F5)
 * 5. Add indicator to any chart
 * 
 * JSON Protocol:
 * - Signal: {"type":"SIGNAL","action":"BUY","qty":1,"symbol":"MES","order_type":"MARKET"}
 * - ACK:    {"type":"ACK","id":"...","nt8_order_id":"...","status":"ACCEPTED"}
 * - Fill:   {"type":"FILL","id":"...","fill_price":5425.25,"fill_qty":1}
 *
 * =========================================================================
 * EXIT GEOMETRY PROTOCOL (Python Master Controller):
 * =========================================================================
 * Python sends bracket parameters in ORDER payload:
 *   - sl_ticks:                 Initial static SL distance from entry
 *   - tp_ticks:                 TP distance from entry
 *   - exit_mode:                "Static_Only" | "Static_BE_Jump" | "Indicator_Ratchet"
 *   - be_trigger_pct:           0.75 = jump to BE at 75% of TP reached
 *   - be_offset_ticks:          1 = BE SL placed 1 tick above entry (LONG)
 *   - brick_size_ticks:         Renko brick size in ticks
 *   - trailing_stop_bricks:     2.0 (reversal distance from HWM, Indicator_Ratchet only)
 *
 * C# implements the state machine:
 *
 *   Static_Only mode: SL and TP never move. Pure static bracket. No phases.
 *
 *   Phase 1 (Initial):
 *     SL = entry ± sl_ticks × tickSize (static, as placed)
 *     Active while MFE < be_trigger_pct × tp_ticks × tickSize
 *
 *   Phase 2 (Breakeven Jump):
 *     Triggered when HWM reaches entry + (be_trigger_pct × tp_distance)
 *     SL moved to entry ± be_offset_ticks × tickSize
 *     If exit_mode == "Static_BE_Jump": STOP HERE. SL stays at BE (stagnant).
 *
 *   Phase 3 (Trailing — only if exit_mode != "Static_BE_Jump"):
 *     SL = max(breakeven_sl, HWM - trailing_stop_bricks × brick_value) [LONG]
 *     SL = min(breakeven_sl, HWM + trailing_stop_bricks × brick_value) [SHORT]
 *     Re-evaluated on every tick. Modify SL order when sl_price changes.
 *
 *   TP:
 *     Static limit at entry ± tp_ticks × tickSize (ceiling — never moves)
 *
 *   Whichever fires first (TP or dynamic SL) closes the position.
 * =========================================================================
 */

#region Using declarations
using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SignalBridge : Indicator
    {
        #region Variables
        private TcpListener tcpListener;
        private TcpClient connectedClient;
        private NetworkStream networkStream;
        private Thread listenerThread;
        private bool isRunning = false;
        private JavaScriptSerializer jsonSerializer;
        private Account tradingAccount;
        private string lastSignalId = "";
        private DateTime lastHeartbeat = DateTime.Now;
        
        // Position state tracking
        private enum PositionState { Flat, Managed, Orphaned }
        private class BracketPair
        {
            public Order TpOrder;
            public Order SlOrder;
            public string InternalTraceId;  // For Python fill matching only, never touches broker
            
            // ═══════════════════════════════════════════════════════════════
            // 3-PHASE TRAILING STOP STATE MACHINE
            // Mirrors Python execution_handler.py for backtest/live parity
            // ═══════════════════════════════════════════════════════════════
            public int Phase = 1;               // 1=Static, 2=Breakeven (Static_BE_Jump stops here)
            public bool IsLong;                 // Position direction
            public double EntryPrice;           // Fill price of entry order
            public double HighWaterMark;        // Best price achieved (for trailing calc)
            public double BreakevenSlPrice;     // Entry ± be_offset_ticks (Phase 2 SL target)
            public double AbsoluteBrickValue;   // brick_size_ticks × tick_size (in points)
            public double BreakevenTriggerBricks; // be_trigger_pct × tp_ticks / brick_size_ticks
            public double TrailingStopBricks;   // 2.0 — reversal distance from HWM in bricks
            public double CurrentSlPrice;       // Tracks last SL price sent to exchange
            public double TickSize;             // Instrument tick size for rounding
            public string ExitMode = "Static_BE_Jump"; // "Static_BE_Jump" = stop at BE; else = trail
        }
        private Dictionary<string, BracketPair> activeBrackets = new Dictionary<string, BracketPair>();
        private object ocoLock = new object();
        private object sendLock = new object();
        
        // PnL Telemetry tracking
        private DateTime lastTelemetrySend = DateTime.MinValue;
        private const double TELEMETRY_INTERVAL_SECONDS = 1.0;  // Send every 1 second
        
        // TRIPWIRE: C# defense-in-depth constants
        // NOTE: TP is now DYNAMIC (MFE regression × brick_size_ticks), typical range 24-240+ ticks
        // NQ uses 40-tick bricks → tp_bricks=6 = 240 ticks (60 pts). ES uses 8-tick bricks → smaller.
        private const int TRIPWIRE_MIN_TP_TICKS = 4;
        private const int TRIPWIRE_MAX_TP_TICKS = 500;
        private const int TRIPWIRE_MIN_SL_TICKS = 4;
        private const int TRIPWIRE_MAX_SL_TICKS = 500;
        private const int TRIPWIRE_MAX_ORDERS_PER_MINUTE = 10;
        private int tripwireOrdersThisMinute = 0;
        private DateTime tripwireMinuteStart = DateTime.MinValue;
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Range(1024, 65535)]
        [Display(Name = "Port", Order = 1, GroupName = "Connection")]
        public int Port { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Account Name", Order = 2, GroupName = "Connection")]
        public string AccountName { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trading", Order = 3, GroupName = "Connection")]
        public bool EnableTrading { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Stream Market Data", Order = 4, GroupName = "Connection")]
        public bool StreamMarketData { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Contract Month (MM-YY)", Description = "e.g. 03-26 for March 2026", Order = 5, GroupName = "Connection")]
        public string ContractMonth { get; set; }
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Bridges Python engine with NT8 - streams market data and executes orders";
                Name = "SignalBridge";
                Calculate = Calculate.OnEachTick;  // Need tick-by-tick for streaming
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                
                // Default settings
                Port = 5580;
                AccountName = "";
                EnableTrading = false;
                StreamMarketData = true;  // Stream ticks to Python
                ContractMonth = "06-26";  // Default to June 2026 (current front month)
            }
            else if (State == State.Configure)
            {
                jsonSerializer = new JavaScriptSerializer();
                
                // Subscribe to tick data stream - ensures OnMarketData fires for every tick
                AddDataSeries(BarsPeriodType.Tick, 1);  // 1-tick series for maximum granularity
            }
            else if (State == State.DataLoaded)
            {
                // =========================================================================
                // ACCOUNT LOOKUP - Log all available accounts for debugging
                // =========================================================================
                Print("[BRIDGE] === ACCOUNT LOOKUP ===");
                Print("[BRIDGE] Configured AccountName: '" + AccountName + "'");
                
                lock (Account.All)
                {
                    // Log ALL available accounts
                    Print(string.Format("[BRIDGE] Available accounts ({0} total):", Account.All.Count));
                    foreach (Account acct in Account.All)
                    {
                        Print(string.Format("[BRIDGE]   - '{0}' (Connection: {1})", 
                            acct.Name, 
                            acct.Connection != null ? acct.Connection.Status.ToString() : "None"));
                    }
                    
                    // Try to find the configured account
                    if (!string.IsNullOrEmpty(AccountName))
                    {
                        // First try: exact match
                        foreach (Account acct in Account.All)
                        {
                            if (acct.Name == AccountName)
                            {
                                tradingAccount = acct;
                                Print("[BRIDGE] ✓ Exact match found: " + acct.Name);
                                break;
                            }
                        }
                        
                        // Second try: case-insensitive match
                        if (tradingAccount == null)
                        {
                            foreach (Account acct in Account.All)
                            {
                                if (acct.Name.Equals(AccountName, StringComparison.OrdinalIgnoreCase))
                                {
                                    tradingAccount = acct;
                                    Print("[BRIDGE] ✓ Case-insensitive match found: " + acct.Name);
                                    break;
                                }
                            }
                        }
                        
                        // Third try: contains match (for partial names)
                        if (tradingAccount == null)
                        {
                            foreach (Account acct in Account.All)
                            {
                                if (acct.Name.IndexOf(AccountName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    AccountName.IndexOf(acct.Name, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    tradingAccount = acct;
                                    Print("[BRIDGE] ✓ Partial match found: " + acct.Name);
                                    break;
                                }
                            }
                        }
                    }
                }
                
                // FAIL LOUDLY if configured account not found - do NOT silently fall back
                if (tradingAccount == null)
                {
                    if (!string.IsNullOrEmpty(AccountName))
                    {
                        Print("[BRIDGE] ✗ ERROR: Account '" + AccountName + "' NOT FOUND!");
                        Print("[BRIDGE] ✗ Please check: 1) Account is connected 2) Name matches exactly");
                        Print("[BRIDGE] ✗ Trading will be DISABLED until correct account is configured");
                    }
                    else
                    {
                        Print("[BRIDGE] ✗ WARNING: No AccountName configured - trading disabled");
                    }
                }
                else
                {
                    Print("[BRIDGE] ✓ Trading account ready: " + tradingAccount.Name);
                    
                    // =========================================================================
                    // GHOST POSITION FIX: Subscribe to ExecutionUpdate for bracket fill detection
                    // This handler fires for ALL executions including TP/SL bracket orders
                    // =========================================================================
                    tradingAccount.ExecutionUpdate += OnExecutionUpdate;
                    Print("[BRIDGE] ✓ ExecutionUpdate handler registered for bracket fill detection");
                }
                
                // Start TCP listener
                StartListener();
            }
            else if (State == State.Terminated)
            {
                // Unsubscribe from ExecutionUpdate to prevent memory leaks
                if (tradingAccount != null)
                {
                    tradingAccount.ExecutionUpdate -= OnExecutionUpdate;
                }
                StopListener();
            }
        }

        protected override void OnBarUpdate()
        {
            // Check heartbeat timeout (10 seconds)
            if ((DateTime.Now - lastHeartbeat).TotalSeconds > 10 && connectedClient != null)
            {
                Print("[BRIDGE] WARNING: No heartbeat received for 10+ seconds");
            }
        }

        private int tickCounter = 0;
        private DateTime lastTickLog = DateTime.MinValue;
        
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            // Debug: Log every 1000 ticks to show OnMarketData is firing
            tickCounter++;
            if ((DateTime.Now - lastTickLog).TotalSeconds >= 5)
            {
                Print(string.Format("[BRIDGE] OnMarketData fired {0} times, StreamMarketData={1}, ClientConnected={2}", 
                    tickCounter, StreamMarketData, connectedClient != null && connectedClient.Connected));
                lastTickLog = DateTime.Now;
            }
            
            // PnL telemetry fires ALWAYS (independent of StreamMarketData)
            // Python needs daily PnL for Shadow Mode risk limits even when using Databento for ticks
            if (connectedClient != null && connectedClient.Connected &&
                (DateTime.Now - lastTelemetrySend).TotalSeconds >= TELEMETRY_INTERVAL_SECONDS)
            {
                SendPnlTelemetry();
                lastTelemetrySend = DateTime.Now;
            }
            
            // ═══════════════════════════════════════════════════════════════════════
            // 3-PHASE TRAILING STOP: Update HWM + modify SL on every trade tick
            // Must run BEFORE StreamMarketData gate — trailing is independent of tick relay
            // ═══════════════════════════════════════════════════════════════════════
            if (e.MarketDataType == MarketDataType.Last)
            {
                UpdateTrailingStops(e.Price);
            }
            
            // Stream tick data to Python - fires on EVERY market data event
            if (!StreamMarketData)
            {
                return;
            }
            
            if (connectedClient == null || !connectedClient.Connected)
            {
                return;
            }

            // Last = trade tick (most important for Renko)
            if (e.MarketDataType == MarketDataType.Last)
            {
                var tickData = new Dictionary<string, object>
                {
                    { "type", "TICK" },
                    { "symbol", e.Instrument.MasterInstrument.Name },
                    { "price", e.Price },
                    { "volume", e.Volume },
                    { "time", e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff") },  // Exchange timestamp
                    { "timestamp", new DateTimeOffset(e.Time).ToUnixTimeMilliseconds() }
                };
                SendMessage(jsonSerializer.Serialize(tickData));
            }
            // Bid/Ask updates - L1 quote data
            else if (e.MarketDataType == MarketDataType.Bid || e.MarketDataType == MarketDataType.Ask)
            {
                var quoteData = new Dictionary<string, object>
                {
                    { "type", "QUOTE" },
                    { "symbol", e.Instrument.MasterInstrument.Name },
                    { "side", e.MarketDataType == MarketDataType.Bid ? "BID" : "ASK" },
                    { "price", e.Price },
                    { "size", e.Volume },
                    { "time", e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff") },
                    { "timestamp", new DateTimeOffset(e.Time).ToUnixTimeMilliseconds() }
                };
                SendMessage(jsonSerializer.Serialize(quoteData));
            }
        }
        
        /// <summary>
        /// Send daily PnL telemetry to Python for risk management.
        /// Includes realized PnL for the day and floating (unrealized) PnL.
        /// </summary>
        private void SendPnlTelemetry()
        {
            if (connectedClient == null || !connectedClient.Connected || tradingAccount == null)
                return;
            
            try
            {
                // Get daily realized PnL from account
                double dailyRealizedPnl = tradingAccount.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                
                // Calculate floating PnL from all open positions
                double openFloatingPnl = 0.0;
                if (tradingAccount.Positions != null)
                {
                    foreach (Position pos in tradingAccount.Positions)
                    {
                        if (pos.Quantity != 0)
                        {
                            openFloatingPnl += pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency);
                        }
                    }
                }
                
                // Get account value for reference
                double accountValue = tradingAccount.Get(AccountItem.CashValue, Currency.UsDollar);
                
                // Get current position for T3 position desync guard
                int currentPosition = 0;
                string positionSymbol = "";
                if (tradingAccount.Positions != null)
                {
                    foreach (Position pos in tradingAccount.Positions)
                    {
                        if (pos.Quantity != 0)
                        {
                            // Signed: +long, -short
                            currentPosition = pos.MarketPosition == MarketPosition.Long
                                ? pos.Quantity : -pos.Quantity;
                            positionSymbol = pos.Instrument.MasterInstrument.Name;
                            break;  // Single-instrument system
                        }
                    }
                }
                
                var telemetry = new Dictionary<string, object>
                {
                    { "type", "TELEMETRY" },
                    { "daily_realized_pnl", dailyRealizedPnl },
                    { "open_floating_pnl", openFloatingPnl },
                    { "account_value", accountValue },
                    { "current_position", currentPosition },
                    { "position_symbol", positionSymbol },
                    { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                };
                
                SendMessage(jsonSerializer.Serialize(telemetry));
            }
            catch (Exception ex)
            {
                // Non-critical - don't spam logs, just skip this telemetry update
                if (tickCounter % 10000 == 0)
                    Print("[TELEMETRY] Error getting PnL: " + ex.Message);
            }
        }

        #region TCP Listener
        private void StartListener()
        {
            try
            {
                isRunning = true;
                listenerThread = new Thread(ListenerLoop);
                listenerThread.IsBackground = true;
                listenerThread.Start();
                Print("[BRIDGE] TCP Server started on port " + Port);
            }
            catch (Exception ex)
            {
                Print("[BRIDGE] ERROR starting listener: " + ex.Message);
            }
        }

        private void StopListener()
        {
            isRunning = false;
            
            try
            {
                if (networkStream != null) networkStream.Close();
                if (connectedClient != null) connectedClient.Close();
                if (tcpListener != null) tcpListener.Stop();
                if (listenerThread != null && listenerThread.IsAlive)
                    listenerThread.Join(1000);
            }
            catch { }
            
            Print("[BRIDGE] TCP Server stopped");
        }

        private void ListenerLoop()
        {
            try
            {
                tcpListener = new TcpListener(IPAddress.Loopback, Port);
                tcpListener.Start();
                Print("[BRIDGE] Listening for Python connection on 127.0.0.1:" + Port);

                while (isRunning)
                {
                    // Accept connection
                    if (tcpListener.Pending())
                    {
                        connectedClient = tcpListener.AcceptTcpClient();
                        networkStream = connectedClient.GetStream();
                        Print("[BRIDGE] Python client connected");
                        
                        // Handle messages
                        HandleClientMessages();
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                if (isRunning)
                    Print("[BRIDGE] Listener error: " + ex.Message);
            }
        }

        private void HandleClientMessages()
        {
            byte[] buffer = new byte[4096];
            StringBuilder messageBuffer = new StringBuilder();

            try
            {
                while (isRunning && connectedClient != null && connectedClient.Connected)
                {
                    if (networkStream.DataAvailable)
                    {
                        int bytesRead = networkStream.Read(buffer, 0, buffer.Length);
                        if (bytesRead == 0)
                        {
                            Print("[BRIDGE] Client disconnected");
                            break;
                        }

                        messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                        // Process complete JSON messages (newline delimited)
                        string data = messageBuffer.ToString();
                        while (data.Contains("\n"))
                        {
                            int idx = data.IndexOf('\n');
                            string jsonLine = data.Substring(0, idx).Trim();
                            data = data.Substring(idx + 1);
                            messageBuffer.Clear();
                            messageBuffer.Append(data);

                            if (!string.IsNullOrEmpty(jsonLine))
                            {
                                ProcessMessage(jsonLine);
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                }
            }
            catch (Exception ex)
            {
                Print("[BRIDGE] Message handling error: " + ex.Message);
            }
            finally
            {
                if (connectedClient != null)
                {
                    connectedClient.Close();
                    connectedClient = null;
                }
                networkStream = null;
            }
        }
        #endregion

        #region Message Processing
        private void ProcessMessage(string jsonStr)
        {
            try
            {
                var msg = jsonSerializer.Deserialize<Dictionary<string, object>>(jsonStr);
                string msgType = msg.ContainsKey("type") ? msg["type"].ToString() : "";

                switch (msgType)
                {
                    case "HEARTBEAT":
                        lastHeartbeat = DateTime.Now;
                        SendHeartbeatResponse();
                        break;

                    case "SIGNAL":
                    case "ORDER":
                        ProcessSignal(msg);
                        break;
                    
                    case "FLATTEN":
                        // Emergency flatten from Python Risk Manager (Shadow Mode)
                        string reason = msg.ContainsKey("reason") ? msg["reason"].ToString() : "Python Risk Manager";
                        ProcessFlattenCommand(reason);
                        break;
                    
                    case "CANCEL_ALL":
                        // #12: Zombie order cleanup on reconnect — cancel all working orders
                        string cancelReason = msg.ContainsKey("reason") ? msg["reason"].ToString() : "cancel_all";
                        Print(string.Format("[BRIDGE] CANCEL_ALL received: {0}", cancelReason));
                        ProcessFlattenCommand("CANCEL_ALL: " + cancelReason);
                        break;

                    default:
                        Print("[BRIDGE] Unknown message type: " + msgType);
                        break;
                }
            }
            catch (Exception ex)
            {
                Print("[BRIDGE] JSON parse error: " + ex.Message);
                SendError("JSON parse error: " + ex.Message);
            }
        }

        private void ProcessSignal(Dictionary<string, object> msg)
        {
            string signalId = msg.ContainsKey("id") ? msg["id"].ToString() : "";
            string action = msg.ContainsKey("action") ? msg["action"].ToString().ToUpper() : "";
            string symbol = msg.ContainsKey("symbol") ? msg["symbol"].ToString() : "";
            int qty = msg.ContainsKey("qty") ? Convert.ToInt32(msg["qty"]) : 1;
            string orderType = msg.ContainsKey("order_type") ? msg["order_type"].ToString().ToUpper() : "MARKET";
            
            // Extract bracket parameters: prefer pre-computed ticks from Python, fallback to brick-based
            int tpTicks, slTicks;
            if (msg.ContainsKey("tp_ticks") && msg.ContainsKey("sl_ticks"))
            {
                // V3: Python Master Controller sends pre-computed tick values
                tpTicks = Convert.ToInt32(msg["tp_ticks"]);
                slTicks = Convert.ToInt32(msg["sl_ticks"]);
            }
            else
            {
                // Fallback: legacy brick-based calculation (backward compat with send_signal())
                int tpBricks = msg.ContainsKey("tp_bricks") ? Convert.ToInt32(msg["tp_bricks"]) : 3;
                int slBricks = msg.ContainsKey("sl_bricks") ? Convert.ToInt32(msg["sl_bricks"]) : 1;
                double brickSizeTicks = msg.ContainsKey("brick_size_ticks") ? Convert.ToDouble(msg["brick_size_ticks"]) : 8.0;
                tpTicks = (int)(tpBricks * brickSizeTicks);
                slTicks = (int)(slBricks * brickSizeTicks);
            }

            // Extract trailing stop protocol params (sent by Python nt8_bridge.py)
            string exitMode = msg.ContainsKey("exit_mode") ? Convert.ToString(msg["exit_mode"]) : "Static_BE_Jump";
            double beTriggerPct = msg.ContainsKey("be_trigger_pct") ? Convert.ToDouble(msg["be_trigger_pct"]) : 0.75;
            int beOffsetTicks = msg.ContainsKey("be_offset_ticks") ? Convert.ToInt32(msg["be_offset_ticks"]) : 1;
            double brickSizeTicksParam = msg.ContainsKey("brick_size_ticks") ? Convert.ToDouble(msg["brick_size_ticks"]) : 8.0;
            double trailingStopBricks = msg.ContainsKey("trailing_stop_bricks") ? Convert.ToDouble(msg["trailing_stop_bricks"]) : 2.0;
            // Compute BE trigger in bricks from be_trigger_pct: e.g. 0.75 × 240 / 40 = 4.5 bricks
            double breakevenTriggerBricks = (beTriggerPct * tpTicks) / brickSizeTicksParam;

            Print(string.Format("[BRIDGE] Signal: {0} {1} {2} @ {3} (TP:{4} SL:{5} ticks, trail={6}brk, BE={7}brk)", 
                action, qty, symbol, orderType, tpTicks, slTicks, trailingStopBricks, breakevenTriggerBricks));

            // TRIPWIRE: Bracket parameter validation (C# defense-in-depth)
            if (tpTicks < TRIPWIRE_MIN_TP_TICKS || tpTicks > TRIPWIRE_MAX_TP_TICKS)
            {
                Print(string.Format("[TRIPWIRE] TP ticks {0} outside [{1}, {2}] — REJECTED", tpTicks, TRIPWIRE_MIN_TP_TICKS, TRIPWIRE_MAX_TP_TICKS));
                SendReject(signalId, string.Format("TRIPWIRE: TP ticks {0} out of bounds", tpTicks));
                return;
            }
            if (slTicks < TRIPWIRE_MIN_SL_TICKS || slTicks > TRIPWIRE_MAX_SL_TICKS)
            {
                Print(string.Format("[TRIPWIRE] SL ticks {0} outside [{1}, {2}] — REJECTED", slTicks, TRIPWIRE_MIN_SL_TICKS, TRIPWIRE_MAX_SL_TICKS));
                SendReject(signalId, string.Format("TRIPWIRE: SL ticks {0} out of bounds", slTicks));
                return;
            }

            // TRIPWIRE: Order rate limiting (defense against runaway signal loop)
            DateTime now = DateTime.Now;
            if ((now - tripwireMinuteStart).TotalSeconds >= 60)
            {
                tripwireOrdersThisMinute = 0;
                tripwireMinuteStart = now;
            }
            tripwireOrdersThisMinute++;
            if (tripwireOrdersThisMinute > TRIPWIRE_MAX_ORDERS_PER_MINUTE)
            {
                Print(string.Format("[TRIPWIRE] {0} orders/min exceeds {1} — REJECTED", tripwireOrdersThisMinute, TRIPWIRE_MAX_ORDERS_PER_MINUTE));
                SendReject(signalId, string.Format("TRIPWIRE: rate limit {0}/min exceeded", tripwireOrdersThisMinute));
                return;
            }

            // Validate
            if (string.IsNullOrEmpty(action) || string.IsNullOrEmpty(symbol))
            {
                SendReject(signalId, "Missing action or symbol");
                return;
            }

            if (!EnableTrading)
            {
                Print("[BRIDGE] Trading disabled - signal ignored");
                SendReject(signalId, "Trading disabled in indicator settings");
                return;
            }

            if (tradingAccount == null)
            {
                SendReject(signalId, "No trading account connected");
                return;
            }

            // Prevent duplicate signals
            if (signalId == lastSignalId)
            {
                Print("[BRIDGE] Duplicate signal ignored: " + signalId);
                return;
            }
            lastSignalId = signalId;

            // Execute order with bracket parameters + trailing stop protocol
            try
            {
                ExecuteOrder(signalId, action, symbol, qty, orderType, tpTicks, slTicks, brickSizeTicksParam, trailingStopBricks, breakevenTriggerBricks, exitMode, beOffsetTicks);
            }
            catch (Exception ex)
            {
                Print("[BRIDGE] Order execution error: " + ex.Message);
                SendReject(signalId, ex.Message);
            }
        }

        private void ExecuteOrder(string signalId, string action, string symbol, int qty, string orderType, 
            int tpTicks = 24, int slTicks = 8, double brickSizeTicks = 8.0, double trailingStopBricks = 2.0, 
            double breakevenTriggerBricks = 4.5, string exitMode = "Static_BE_Jump", int beOffsetTicks = 1)
        {
            // =========================================================================
            // DIAGNOSTIC: Ultra-defensive logging to find null reference
            // =========================================================================
            try { Print(string.Format("[DIAG] === ORDER EXECUTION START === SignalId={0}", signalId)); } catch (Exception ex) { Print("[DIAG] ERROR at line 1: " + ex.Message); }
            try { Print(string.Format("[DIAG] Account is null: {0}", tradingAccount == null)); } catch (Exception ex) { Print("[DIAG] ERROR at line 2: " + ex.Message); }
            try { if (tradingAccount != null) Print(string.Format("[DIAG] Account name: {0}", tradingAccount.Name)); } catch (Exception ex) { Print("[DIAG] ERROR at line 3: " + ex.Message); }
            try { Print(string.Format("[DIAG] Request: {0} {1} {2} @ {3}", action, qty, symbol, orderType)); } catch (Exception ex) { Print("[DIAG] ERROR at line 4: " + ex.Message); }
            
            // =================================================================
            // USE CHART INSTRUMENT: Ensures market data subscription exists
            // Signal symbol (ES) may differ from chart instrument (MES) but
            // the SIM account only has market data for the chart's instrument.
            // This fixes SIM101 "no market data available" errors.
            // =================================================================
            Instrument instrument = Instrument;  // Chart's primary instrument
            Print(string.Format("[DIAG] Using chart instrument: {0} (signal requested: {1})", 
                instrument.FullName, symbol));
            
            if (instrument == null)
            {
                Print("[DIAG] ERROR: Chart instrument is null");
                SendReject(signalId, "Chart instrument not available");
                return;
            }

            // =========================================================================
            // POSITION SAFETY CHECK: Verify flat or handle orphaned positions
            // =========================================================================
            if (action == "BUY" || action == "SELL")
            {
                PositionState posState = GetPositionState(instrument);
                
                if (posState == PositionState.Managed)
                {
                    // Position exists with valid OCO bracket - reject new entry
                    Print("[SAFETY] Rejecting signal - managed position already exists");
                    SendReject(signalId, "Position already exists with active bracket orders");
                    return;
                }
                else if (posState == PositionState.Orphaned)
                {
                    // Orphaned position - flatten first, then continue with new entry
                    Print("[SAFETY] Orphaned position detected - flattening before new entry");
                    FlattenAndCancelAll(instrument, "Orphaned position before " + action);
                    // Note: FlattenAndCancelAll is fire-and-forget for the close order
                    // The new entry will be submitted immediately after
                    // In production, you might want to wait for flatten confirmation
                }
                // PositionState.Flat - proceed normally
            }

            // Determine order action
            Print("[DIAG] Determining order action for: " + action);
            OrderAction orderAction;
            try
            {
                if (action == "BUY")
                    orderAction = OrderAction.Buy;
                else if (action == "SELL")
                    orderAction = OrderAction.Sell;
                else if (action == "EXIT")
                {
                    // Exit = close position - find position by iterating
                    Print("[DIAG] EXIT: Checking tradingAccount.Positions...");
                    Position pos = null;
                    if (tradingAccount.Positions != null)
                    {
                        foreach (Position p in tradingAccount.Positions)
                        {
                            if (p.Instrument == instrument)
                            {
                                pos = p;
                                break;
                            }
                        }
                    }
                    if (pos == null || pos.Quantity == 0)
                    {
                        Print("[DIAG] EXIT requested but no position found");
                        SendAck(signalId, "NO_POSITION", "No position to exit");
                        return;
                    }
                    orderAction = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                    qty = Math.Abs(pos.Quantity);
                    Print(string.Format("[DIAG] EXIT: Closing {0} position, qty={1}", pos.MarketPosition, qty));
                    
                    // Cancel any working bracket orders before closing position
                    string instrumentKey = instrument.FullName;
                    lock (ocoLock)
                    {
                        if (activeBrackets.ContainsKey(instrumentKey))
                        {
                            var pair = activeBrackets[instrumentKey];
                            Print(string.Format("[SAFETY] EXIT: Cancelling bracket orders for {0}", instrumentKey));
                            
                            if (pair.TpOrder != null && (pair.TpOrder.OrderState == OrderState.Working || pair.TpOrder.OrderState == OrderState.Accepted))
                                try { tradingAccount.Cancel(new[] { pair.TpOrder }); } catch { }
                            if (pair.SlOrder != null && (pair.SlOrder.OrderState == OrderState.Working || pair.SlOrder.OrderState == OrderState.Accepted))
                                try { tradingAccount.Cancel(new[] { pair.SlOrder }); } catch { }
                            
                            activeBrackets.Remove(instrumentKey);
                        }
                    }
                }
                else
                {
                    Print("[DIAG] ERROR: Unknown action type: " + action);
                    SendReject(signalId, "Unknown action: " + action);
                    return;
                }
            }
            catch (Exception actionEx)
            {
                Print("[DIAG] ERROR determining order action: " + actionEx.Message);
                SendReject(signalId, "Order action error: " + actionEx.Message);
                return;
            }

            // =========================================================================
            // DIAGNOSTIC: Log order creation parameters
            // =========================================================================
            Print(string.Format("[DIAG] CreateOrder params: Instrument={0}, Action={1}, Type=Market, Entry=Manual, TIF=Day, Qty={2}",
                instrument.FullName, orderAction, qty));
            
            // Submit order - use DateTime.MaxValue instead of Core.Globals.MaxDate for GTD
            Order order = null;
            try
            {
                order = tradingAccount.CreateOrder(
                    instrument,
                    orderAction,
                    OrderType.Market,
                    OrderEntry.Manual,
                    TimeInForce.Day,
                    qty,
                    0,  // limitPrice
                    0,  // stopPrice
                    "",  // oco
                    "",  // Sanitized: no algo fingerprint on broker-facing name
                    DateTime.MaxValue,  // gtd - FIXED: was Core.Globals.MaxDate which can be null
                    null
                );
            }
            catch (Exception createEx)
            {
                Print(string.Format("[DIAG] ERROR: CreateOrder threw exception: {0}", createEx.Message));
                Print(string.Format("[DIAG] Stack trace: {0}", createEx.StackTrace));
                SendReject(signalId, "CreateOrder failed: " + createEx.Message);
                return;
            }
            
            // =========================================================================
            // DIAGNOSTIC: Verify order was created
            // =========================================================================
            if (order == null)
            {
                Print("[DIAG] ERROR: CreateOrder returned NULL - order object not created");
                Print(string.Format("[DIAG] Account state: Name={0}", tradingAccount.Name));
                SendReject(signalId, "Order creation returned null - check account permissions and instrument status");
                return;
            }
            
            Print(string.Format("[DIAG] Order created: Id={0}, State={1}", order.Id, order.OrderState));

            // Subscribe to account order updates BEFORE submitting
            // IMPORTANT: All ACK/REJECT/FILL messages are sent from the event handler
            // to avoid race conditions where rejection fires before SendAck()
            string capturedSignalId = signalId;
            string capturedAction = action;
            int capturedTpTicks = tpTicks;
            int capturedSlTicks = slTicks;
            Instrument capturedInstrument = instrument;
            double capturedBrickSizeTicks = brickSizeTicks;
            double capturedTrailingStopBricks = trailingStopBricks;
            double capturedBreakevenTriggerBricks = breakevenTriggerBricks;
            string capturedExitMode = exitMode;
            int capturedBeOffsetTicks = beOffsetTicks;
            bool ackSent = false;  // Prevent duplicate ACKs
            EventHandler<OrderEventArgs> orderHandler = null;
            orderHandler = (sender, e) =>
            {
                if (e.Order == order)
                {
                    // DIAGNOSTIC: Log every order state change
                    Print(string.Format("[DIAG] OrderState changed: {0} -> Id={1}", e.OrderState, e.Order.Id));
                    
                    // Handle order state transitions
                    if (e.OrderState == OrderState.Submitted || e.OrderState == OrderState.Accepted)
                    {
                        Print(string.Format("[DIAG] Order {0}: State={1}", e.Order.Id, e.OrderState));
                        // Only send ACK once for submitted/accepted
                        if (!ackSent)
                        {
                            ackSent = true;
                            SendAck(capturedSignalId, e.Order.Id.ToString(), "SUBMITTED");
                        }
                    }
                    else if (e.OrderState == OrderState.Filled)
                    {
                        Print(string.Format("[DIAG] FILL: Id={0}, Price={1}, Qty={2}", 
                            e.Order.Id, e.Order.AverageFillPrice, e.Order.Quantity));
                        SendFill(capturedSignalId, e.Order.Instrument.MasterInstrument.Name, 
                                 e.Order.AverageFillPrice, e.Order.Quantity, capturedAction);
                        
                        // =========================================================================
                        // BRACKET ORDERS: Submit TP and SL after entry fill
                        // =========================================================================
                        try
                        {
                            double fillPrice = e.Order.AverageFillPrice;
                            int fillQty = e.Order.Quantity;
                            double tickSize = capturedInstrument.MasterInstrument.TickSize;
                            
                            // V3: Calculate TP and SL prices using pre-computed tick values
                            double tpOffset = capturedTpTicks * tickSize;
                            double slOffset = capturedSlTicks * tickSize;
                            
                            double tpPrice, slPrice;
                            OrderAction tpAction, slAction;
                            
                            if (capturedAction == "BUY")
                            {
                                // Long position: TP above, SL below
                                tpPrice = fillPrice + tpOffset;
                                slPrice = fillPrice - slOffset;
                                tpAction = OrderAction.Sell;
                                slAction = OrderAction.Sell;
                            }
                            else
                            {
                                // Short position: TP below, SL above
                                tpPrice = fillPrice - tpOffset;
                                slPrice = fillPrice + slOffset;
                                tpAction = OrderAction.Buy;
                                slAction = OrderAction.Buy;
                            }
                            
                            // Round to tick size
                            tpPrice = Math.Round(tpPrice / tickSize) * tickSize;
                            slPrice = Math.Round(slPrice / tickSize) * tickSize;
                            
                            Print(string.Format("[BRACKET] Entry filled @ {0}, placing TP @ {1}, SL @ {2}", fillPrice, tpPrice, slPrice));
                            
                            // Create Take Profit order (LIMIT) — no OCO string, software-managed cross-cancel
                            Order tpOrder = tradingAccount.CreateOrder(
                                capturedInstrument,
                                tpAction,
                                OrderType.Limit,
                                OrderEntry.Manual,
                                TimeInForce.Gtc,
                                fillQty,
                                tpPrice,  // limitPrice
                                0,        // stopPrice
                                "",       // oco - empty: no algo fingerprint in execution CSV
                                "Target 1",  // Mimics manual bracket name
                                DateTime.MaxValue,
                                null
                            );
                            
                            // Create Stop Loss order (STOP MARKET) — no OCO string, software-managed cross-cancel
                            Order slOrder = tradingAccount.CreateOrder(
                                capturedInstrument,
                                slAction,
                                OrderType.StopMarket,
                                OrderEntry.Manual,
                                TimeInForce.Gtc,
                                fillQty,
                                0,        // limitPrice
                                slPrice,  // stopPrice
                                "",       // oco - empty: no algo fingerprint in execution CSV
                                "Stop 1",  // Mimics manual bracket name
                                DateTime.MaxValue,
                                null
                            );
                            
                            if (tpOrder != null && slOrder != null)
                            {
                                tradingAccount.Submit(new[] { tpOrder, slOrder });
                                Print(string.Format("[BRACKET] Submitted: TP={0} @ {1}, SL={2} @ {3}", tpOrder.Id, tpPrice, slOrder.Id, slPrice));
                                
                                // Track bracket pair with full trailing stop state machine
                                string instrumentKey = capturedInstrument.FullName;
                                double absoluteBrickValue = capturedBrickSizeTicks * tickSize;
                                bool isLong = (capturedAction == "BUY");
                                
                                // Breakeven SL: entry ± be_offset_ticks (e.g. 1 tick above entry for LONG)
                                double breakevenSl = isLong 
                                    ? (fillPrice + capturedBeOffsetTicks * tickSize) 
                                    : (fillPrice - capturedBeOffsetTicks * tickSize);
                                
                                lock (ocoLock)
                                {
                                    activeBrackets[instrumentKey] = new BracketPair
                                    {
                                        TpOrder = tpOrder,
                                        SlOrder = slOrder,
                                        InternalTraceId = capturedSignalId,
                                        // Trailing Stop State Machine — Phase 1 (Static SL)
                                        Phase = 1,
                                        IsLong = isLong,
                                        EntryPrice = fillPrice,
                                        HighWaterMark = fillPrice,
                                        BreakevenSlPrice = breakevenSl,
                                        AbsoluteBrickValue = absoluteBrickValue,
                                        BreakevenTriggerBricks = capturedBreakevenTriggerBricks,
                                        TrailingStopBricks = capturedTrailingStopBricks,
                                        CurrentSlPrice = slPrice,
                                        TickSize = tickSize,
                                        ExitMode = capturedExitMode
                                    };
                                    Print(string.Format("[TRAILING] Initialized: {0} {1} entry={2:F2} BE_trigger={3:F1}brk trail={4:F1}brk brick_val={5:F4} mode={6}",
                                        instrumentKey, isLong ? "LONG" : "SHORT", fillPrice, 
                                        capturedBreakevenTriggerBricks, capturedTrailingStopBricks, absoluteBrickValue, capturedExitMode));
                                }
                            }
                            else
                            {
                                Print("[BRACKET] ERROR: Failed to create TP/SL orders");
                            }
                        }
                        catch (Exception bracketEx)
                        {
                            Print(string.Format("[BRACKET] ERROR submitting TP/SL: {0}", bracketEx.Message));
                        }
                        
                        tradingAccount.OrderUpdate -= orderHandler;
                        Print("[DIAG] === ORDER EXECUTION COMPLETE (FILLED) ===");
                    }
                    else if (e.OrderState == OrderState.Rejected)
                    {
                        // Note: NT8 Order object doesn't have ErrorMessage - use generic message
                        Print(string.Format("[DIAG] REJECTED: Id={0}, OrderState={1}", 
                            e.Order.Id, e.OrderState));
                        // Only send REJECT if we haven't sent an ACK yet
                        // (prevents confusing REJECT after SUBMITTED)
                        if (!ackSent)
                        {
                            SendReject(capturedSignalId, "Order rejected by exchange");
                        }
                        else
                        {
                            // Order was accepted then rejected - send as REJECT
                            SendReject(capturedSignalId, "Order rejected after submission");
                        }
                        tradingAccount.OrderUpdate -= orderHandler;
                        Print("[DIAG] === ORDER EXECUTION COMPLETE (REJECTED) ===");
                    }
                    else if (e.OrderState == OrderState.Cancelled)
                    {
                        Print(string.Format("[DIAG] CANCELLED: Id={0}", e.Order.Id));
                        SendReject(capturedSignalId, "Order cancelled");
                        tradingAccount.OrderUpdate -= orderHandler;
                        Print("[DIAG] === ORDER EXECUTION COMPLETE (CANCELLED) ===");
                    }
                }
            };
            tradingAccount.OrderUpdate += orderHandler;

            // DIAGNOSTIC: Log submission attempt
            Print(string.Format("[DIAG] Submitting order to account: {0}", tradingAccount.Name));
            
            try
            {
                tradingAccount.Submit(new[] { order });
                Print(string.Format("[BRIDGE] Order submitted: {0} {1} {2}", orderAction, qty, symbol));
                Print(string.Format("[DIAG] Submit() returned successfully, awaiting state updates..."));
            }
            catch (Exception submitEx)
            {
                Print(string.Format("[DIAG] ERROR: Submit() threw exception: {0}", submitEx.Message));
                Print(string.Format("[DIAG] Stack trace: {0}", submitEx.StackTrace));
                tradingAccount.OrderUpdate -= orderHandler;
                SendReject(signalId, "Submit failed: " + submitEx.Message);
            }
            // NOTE: ACK is sent from event handler, not here, to avoid race condition
        }
        #endregion

        #region Trailing Stop State Machine
        /// <summary>
        /// 3-Phase Trailing Stop — called on every trade tick from OnMarketData.
        /// 
        /// Phase 1 (Static):    SL stays at initial placement (entry ± sl_ticks)
        /// Phase 2 (Breakeven): SL moves to entry ± 1 tick when MFE ≥ breakeven_trigger_bricks
        /// Phase 3 (Trailing):  SL = max(breakeven, HWM − trailing_stop_bricks × brick_value)
        /// 
        /// Mirrors Python execution_handler.py exactly for backtest/live parity.
        /// Uses tradingAccount.Change() to modify the live SL order on the exchange.
        /// </summary>
        private void UpdateTrailingStops(double price)
        {
            lock (ocoLock)
            {
                foreach (var kvp in activeBrackets)
                {
                    BracketPair pair = kvp.Value;
                    
                    // Skip if SL order is no longer working (already filled/cancelled)
                    if (pair.SlOrder == null || 
                        (pair.SlOrder.OrderState != OrderState.Working && pair.SlOrder.OrderState != OrderState.Accepted))
                        continue;
                    
                    // Static_Only: pure static TP/SL — no HWM, no breakeven, no trailing
                    if (pair.ExitMode == "Static_Only")
                        continue;
                    
                    // ─────────────────────────────────────────────────────────
                    // STEP 1: Update High-Water Mark
                    // ─────────────────────────────────────────────────────────
                    if (pair.IsLong)
                    {
                        if (price > pair.HighWaterMark)
                            pair.HighWaterMark = price;
                    }
                    else
                    {
                        if (price < pair.HighWaterMark)
                            pair.HighWaterMark = price;
                    }
                    
                    double hwm = pair.HighWaterMark;
                    double newSlPrice = pair.CurrentSlPrice;
                    int oldPhase = pair.Phase;
                    
                    // ─────────────────────────────────────────────────────────
                    // STEP 2: Phase 1 → 2 (Breakeven trigger)
                    // When MFE reaches breakeven_trigger_bricks × brick_value
                    // ─────────────────────────────────────────────────────────
                    if (pair.Phase == 1)
                    {
                        double breakevenDistance = pair.BreakevenTriggerBricks * pair.AbsoluteBrickValue;
                        
                        if (pair.IsLong && hwm >= pair.EntryPrice + breakevenDistance)
                        {
                            pair.Phase = 2;
                            newSlPrice = pair.BreakevenSlPrice;
                        }
                        else if (!pair.IsLong && hwm <= pair.EntryPrice - breakevenDistance)
                        {
                            pair.Phase = 2;
                            newSlPrice = pair.BreakevenSlPrice;
                        }
                    }
                    
                    // ─────────────────────────────────────────────────────────
                    // STEP 3: Phase 2+ → Trailing calculation
                    // Static_BE_Jump: STOP at Phase 2 (SL stays at breakeven)
                    // Other modes: trail HWM by trailing_stop_bricks distance
                    // ─────────────────────────────────────────────────────────
                    if (pair.Phase >= 2 && pair.ExitMode != "Static_BE_Jump")
                    {
                        double trailDistance = pair.TrailingStopBricks * pair.AbsoluteBrickValue;
                        
                        if (pair.IsLong)
                        {
                            double trailingSl = hwm - trailDistance;
                            newSlPrice = Math.Max(pair.BreakevenSlPrice, trailingSl);
                            if (trailingSl > pair.BreakevenSlPrice)
                                pair.Phase = 3;
                        }
                        else
                        {
                            double trailingSl = hwm + trailDistance;
                            newSlPrice = Math.Min(pair.BreakevenSlPrice, trailingSl);
                            if (trailingSl < pair.BreakevenSlPrice)
                                pair.Phase = 3;
                        }
                    }
                    
                    // ─────────────────────────────────────────────────────────
                    // STEP 4: Round to tick size and modify SL if changed
                    // Only send Change() when price actually moves (avoids spam)
                    // ─────────────────────────────────────────────────────────
                    newSlPrice = Math.Round(newSlPrice / pair.TickSize) * pair.TickSize;
                    
                    if (Math.Abs(newSlPrice - pair.CurrentSlPrice) >= pair.TickSize)
                    {
                        // SL needs to move — modify the live order on the exchange
                        // NT8 Account.Change() API: set StopPriceChanged first, then call Change(Order[])
                        try
                        {
                            pair.SlOrder.StopPriceChanged = newSlPrice;
                            tradingAccount.Change(new[] { pair.SlOrder });
                            
                            if (pair.Phase != oldPhase)
                            {
                                Print(string.Format("[TRAILING] Phase {0}→{1}: {2} SL {3:F2}→{4:F2} (HWM={5:F2}, entry={6:F2})",
                                    oldPhase, pair.Phase, kvp.Key, pair.CurrentSlPrice, newSlPrice, hwm, pair.EntryPrice));
                            }
                            
                            pair.CurrentSlPrice = newSlPrice;
                        }
                        catch (Exception ex)
                        {
                            Print(string.Format("[TRAILING] ERROR modifying SL: {0}", ex.Message));
                        }
                    }
                }
            }
        }
        #endregion

        #region Position Safety Methods
        /// <summary>
        /// Check if account is flat, has a managed position (with OCO), or has an orphaned position (no OCO).
        /// </summary>
        private PositionState GetPositionState(Instrument instrument)
        {
            try
            {
                // Check for existing position
                Position pos = null;
                if (tradingAccount.Positions != null)
                {
                    foreach (Position p in tradingAccount.Positions)
                    {
                        if (p.Instrument == instrument && p.Quantity != 0)
                        {
                            pos = p;
                            break;
                        }
                    }
                }
                
                // Get instrument key for bracket tracking
                string instrumentKey = instrument.FullName;
                
                if (pos == null || pos.Quantity == 0)
                {
                    // Clean up any stale bracket tracking if position is now flat
                    lock (ocoLock)
                    {
                        if (activeBrackets.ContainsKey(instrumentKey))
                        {
                            Print(string.Format("[SAFETY] Cleaning up stale bracket tracking for {0}", instrumentKey));
                            activeBrackets.Remove(instrumentKey);
                        }
                    }
                    Print("[SAFETY] Position state: FLAT");
                    return PositionState.Flat;
                }
                
                // Position exists - check if we have tracked bracket orders for it
                BracketPair trackedPair = null;
                lock (ocoLock)
                {
                    if (activeBrackets.ContainsKey(instrumentKey))
                        trackedPair = activeBrackets[instrumentKey];
                }
                
                if (trackedPair == null)
                {
                    Print(string.Format("[SAFETY] Position state: ORPHANED (no tracked bracket for {0}, qty={1})", instrumentKey, pos.Quantity));
                    return PositionState.Orphaned;
                }
                
                // Verify the bracket orders still exist and are working
                bool foundTp = trackedPair.TpOrder != null && 
                    (trackedPair.TpOrder.OrderState == OrderState.Working || trackedPair.TpOrder.OrderState == OrderState.Accepted);
                bool foundSl = trackedPair.SlOrder != null && 
                    (trackedPair.SlOrder.OrderState == OrderState.Working || trackedPair.SlOrder.OrderState == OrderState.Accepted);
                
                if (foundTp && foundSl)
                {
                    Print(string.Format("[SAFETY] Position state: MANAGED (TP={0}, SL={1})", foundTp, foundSl));
                    return PositionState.Managed;
                }
                else
                {
                    Print(string.Format("[SAFETY] Position state: ORPHANED (bracket missing orders: TP={0}, SL={1})", foundTp, foundSl));
                    return PositionState.Orphaned;
                }
            }
            catch (Exception ex)
            {
                Print("[SAFETY] ERROR checking position state: " + ex.Message);
                return PositionState.Orphaned;  // Assume orphaned on error - safer to flatten
            }
        }
        
        /// <summary>
        /// Cancel all working orders and flatten any position for the given instrument.
        /// </summary>
        private void FlattenAndCancelAll(Instrument instrument, string reason)
        {
            Print(string.Format("[SAFETY] === FLATTEN AND CANCEL ALL === Reason: {0}", reason));
            
            try
            {
                // Step 1: Cancel all working orders for this instrument
                List<Order> ordersToCancel = new List<Order>();
                if (tradingAccount.Orders != null)
                {
                    foreach (Order o in tradingAccount.Orders.ToList())
                    {
                        if (o.Instrument == instrument && 
                            (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted))
                        {
                            ordersToCancel.Add(o);
                        }
                    }
                }
                
                foreach (Order o in ordersToCancel)
                {
                    Print(string.Format("[SAFETY] Cancelling order: {0} {1} @ {2}", o.OrderType, o.OrderAction, o.LimitPrice > 0 ? o.LimitPrice : o.StopPrice));
                    tradingAccount.Cancel(new[] { o });
                }
                
                // Step 2: Close any position
                Position pos = null;
                if (tradingAccount.Positions != null)
                {
                    foreach (Position p in tradingAccount.Positions.ToList())
                    {
                        if (p.Instrument == instrument && p.Quantity != 0)
                        {
                            pos = p;
                            break;
                        }
                    }
                }
                
                if (pos != null && pos.Quantity != 0)
                {
                    OrderAction closeAction = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                    int closeQty = Math.Abs(pos.Quantity);
                    
                    Print(string.Format("[SAFETY] Closing position: {0} {1} @ MARKET", closeAction, closeQty));
                    
                    Order closeOrder = tradingAccount.CreateOrder(
                        instrument,
                        closeAction,
                        OrderType.Market,
                        OrderEntry.Manual,
                        TimeInForce.Day,
                        closeQty,
                        0, 0, "", "",  // Sanitized: no algo fingerprint
                        DateTime.MaxValue, null
                    );
                    
                    if (closeOrder != null)
                    {
                        tradingAccount.Submit(new[] { closeOrder });
                    }
                }
                
                // Step 3: Clear tracked brackets for this instrument
                string instrumentKey = instrument.FullName;
                lock (ocoLock)
                {
                    if (activeBrackets.ContainsKey(instrumentKey))
                    {
                        Print(string.Format("[SAFETY] Clearing tracked brackets for {0}", instrumentKey));
                        activeBrackets.Remove(instrumentKey);
                    }
                }
                
                Print("[SAFETY] === FLATTEN COMPLETE ===");
            }
            catch (Exception ex)
            {
                Print("[SAFETY] ERROR during flatten: " + ex.Message);
            }
        }
        
        /// <summary>
        /// Process FLATTEN command from Python Risk Manager (Shadow Mode).
        /// Closes all positions and cancels all working orders across all instruments.
        /// </summary>
        private void ProcessFlattenCommand(string reason)
        {
            Print("\n" + new string('=', 60));
            Print("[RISK MANAGER] FLATTEN COMMAND RECEIVED FROM PYTHON");
            Print(new string('=', 60));
            Print("Reason: " + reason);
            Print("Closing all positions and cancelling all orders...");
            Print(new string('=', 60));
            
            if (tradingAccount == null)
            {
                Print("[FLATTEN] ERROR: No trading account connected");
                SendError("FLATTEN failed: No trading account");
                return;
            }
            
            try
            {
                int positionsClosed = 0;
                int ordersCancelled = 0;
                
                // Step 1: Cancel ALL working orders (isolated — failure here must not block Step 2)
                try
                {
                    if (tradingAccount.Orders != null)
                    {
                        List<Order> ordersToCancel = new List<Order>();
                        foreach (Order o in tradingAccount.Orders.ToList())
                        {
                            if (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted)
                            {
                                ordersToCancel.Add(o);
                            }
                        }
                        
                        foreach (Order o in ordersToCancel)
                        {
                            try
                            {
                                tradingAccount.Cancel(new[] { o });
                                ordersCancelled++;
                                Print(string.Format("[FLATTEN] Cancelled: {0} {1} {2}", o.OrderAction, o.Quantity, o.Instrument.FullName));
                            }
                            catch (Exception cancelEx)
                            {
                                Print("[FLATTEN] Cancel error: " + cancelEx.Message);
                            }
                        }
                    }
                }
                catch (Exception step1Ex)
                {
                    Print("[FLATTEN] Step 1 error (continuing to position close): " + step1Ex.Message);
                }
                
                // Step 2: Close ALL positions with market orders
                if (tradingAccount.Positions != null)
                {
                    foreach (Position pos in tradingAccount.Positions.ToList())
                    {
                        if (pos.Quantity != 0)
                        {
                            try
                            {
                                OrderAction closeAction = pos.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                                int closeQty = Math.Abs(pos.Quantity);
                                
                                Order closeOrder = tradingAccount.CreateOrder(
                                    pos.Instrument,
                                    closeAction,
                                    OrderType.Market,
                                    OrderEntry.Manual,
                                    TimeInForce.Day,
                                    closeQty,
                                    0, 0, "", "",  // Sanitized: no algo fingerprint
                                    DateTime.MaxValue, null
                                );
                                
                                if (closeOrder != null)
                                {
                                    tradingAccount.Submit(new[] { closeOrder });
                                    positionsClosed++;
                                    Print(string.Format("[FLATTEN] Closing: {0} {1} {2} @ MARKET", closeAction, closeQty, pos.Instrument.FullName));
                                }
                            }
                            catch (Exception closeEx)
                            {
                                Print("[FLATTEN] Close error: " + closeEx.Message);
                            }
                        }
                    }
                }
                
                // Step 3: Clear all tracked bracket pairs
                lock (ocoLock)
                {
                    activeBrackets.Clear();
                }
                
                Print(new string('=', 60));
                Print(string.Format("[FLATTEN] Complete: {0} positions closed, {1} orders cancelled", positionsClosed, ordersCancelled));
                Print(new string('=', 60) + "\n");
                
                // Send confirmation to Python
                var response = new Dictionary<string, object>
                {
                    { "type", "FLATTEN_ACK" },
                    { "positions_closed", positionsClosed },
                    { "orders_cancelled", ordersCancelled },
                    { "reason", reason },
                    { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }
                };
                SendMessage(jsonSerializer.Serialize(response));
            }
            catch (Exception ex)
            {
                Print("[FLATTEN] ERROR: " + ex.Message);
                SendError("FLATTEN failed: " + ex.Message);
            }
        }
        #endregion

        #region Response Methods
        private int sendCounter = 0;
        private DateTime lastSendLog = DateTime.MinValue;
        
        private void SendMessage(string json)
        {
            try
            {
                if (networkStream != null && connectedClient != null && connectedClient.Connected)
                {
                    byte[] data = Encoding.UTF8.GetBytes(json + "\n");
                    lock (sendLock)
                    {
                        networkStream.Write(data, 0, data.Length);
                        networkStream.Flush();
                    }
                    
                    sendCounter++;
                    if ((DateTime.Now - lastSendLog).TotalSeconds >= 5)
                    {
                        Print(string.Format("[BRIDGE] Sent {0} messages to Python", sendCounter));
                        lastSendLog = DateTime.Now;
                    }
                }
            }
            catch (Exception ex)
            {
                Print("[BRIDGE] Send error: " + ex.Message);
            }
        }

        private void SendHeartbeatResponse()
        {
            var response = new Dictionary<string, object>
            {
                { "type", "HEARTBEAT" },
                { "timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
                { "latency_ms", (DateTime.Now - lastHeartbeat).TotalMilliseconds }
            };
            SendMessage(jsonSerializer.Serialize(response));
        }

        private void SendAck(string signalId, string orderId, string status)
        {
            var response = new Dictionary<string, object>
            {
                { "type", "ACK" },
                { "id", signalId },
                { "nt8_order_id", orderId },
                { "status", status }
            };
            SendMessage(jsonSerializer.Serialize(response));
            Print("[BRIDGE] ACK sent: " + signalId + " -> " + status);
        }

        private void SendFill(string signalId, string symbol, double fillPrice, int fillQty, string action)
        {
            var response = new Dictionary<string, object>
            {
                { "type", "FILL" },
                { "id", signalId },
                { "symbol", symbol },
                { "fill_price", fillPrice },
                { "fill_qty", fillQty },
                { "action", action },
                { "status", "FILLED" }
            };
            SendMessage(jsonSerializer.Serialize(response));
            Print(string.Format("[BRIDGE] Fill sent: {0} {1} @ {2}", fillQty, symbol, fillPrice));
        }

        private void SendReject(string signalId, string reason)
        {
            var response = new Dictionary<string, object>
            {
                { "type", "REJECT" },
                { "id", signalId },
                { "reason", reason }
            };
            SendMessage(jsonSerializer.Serialize(response));
            Print("[BRIDGE] Reject sent: " + reason);
        }

        private void SendError(string message)
        {
            var response = new Dictionary<string, object>
            {
                { "type", "ERROR" },
                { "message", message }
            };
            SendMessage(jsonSerializer.Serialize(response));
        }
        
        /// <summary>
        /// Send bracket exit fill (TP or SL) to Python for position sync.
        /// </summary>
        private void SendBracketFill(string symbol, double fillPrice, int fillQty, string exitType, string action)
        {
            // Generate unique ID for bracket exit
            string bracketId = string.Format("BRACKET_{0}_{1}", exitType, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            
            var response = new Dictionary<string, object>
            {
                { "type", "FILL" },
                { "id", bracketId },
                { "symbol", symbol },
                { "fill_price", fillPrice },
                { "fill_qty", fillQty },
                { "action", action },
                { "exit_type", exitType },
                { "is_bracket_exit", true },
                { "status", "FILLED" }
            };
            SendMessage(jsonSerializer.Serialize(response));
            Print(string.Format("[BRACKET] Exit fill sent to Python: {0} {1} {2} @ {3}", exitType, fillQty, symbol, fillPrice));
        }
        
        /// <summary>
        /// GHOST POSITION FIX: Handle all execution updates to detect bracket TP/SL fills.
        /// This fires for ALL fills including native NT8 bracket orders.
        /// </summary>
        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (e.Execution == null || e.Execution.Order == null)
                return;
            
            Order order = e.Execution.Order;
            
            // Only process fills (not other execution states)
            if (e.Execution.Order.OrderState != OrderState.Filled && 
                e.Execution.Order.OrderState != OrderState.PartFilled)
                return;
            
            // Detect bracket fills by sanitized order name (reliable across Order lifecycle)
            // Names "Target 1" / "Stop 1" mimic manual ATM brackets — no algo fingerprint
            string orderName = order.Name ?? "";
            string exitType = null;
            
            if (orderName == "Target 1")
                exitType = "TP";
            else if (orderName == "Stop 1")
                exitType = "SL";
            
            if (exitType == null)
                return;  // Not a bracket order - ignore
            string symbol = order.Instrument.MasterInstrument.Name;
            double fillPrice = e.Execution.Price;
            int fillQty = e.Execution.Quantity;
            
            // Determine action based on order direction (exit is opposite of entry)
            string action = order.OrderAction == OrderAction.Buy ? "BUY" : "SELL";
            
            // Enrich exit type with trailing stop phase info
            string instrumentKey = order.Instrument.FullName;
            int exitPhase = 1;
            lock (ocoLock)
            {
                if (activeBrackets.ContainsKey(instrumentKey))
                    exitPhase = activeBrackets[instrumentKey].Phase;
            }
            
            // Reclassify SL exits: Phase 2+ means breakeven or trailing (not a loss)
            if (exitType == "SL" && exitPhase >= 2)
                exitType = "TRAILING";
            
            Print(string.Format("[BRACKET] {0} (Ph{1}) order filled: {2} {3} @ {4}", exitType, exitPhase, fillQty, symbol, fillPrice));
            
            // Send bracket fill to Python with phase info
            SendBracketFill(symbol, fillPrice, fillQty, exitType, action);
            
            // SOFTWARE OCO: Cross-cancel the counterpart bracket order
            lock (ocoLock)
            {
                if (activeBrackets.ContainsKey(instrumentKey))
                {
                    var pair = activeBrackets[instrumentKey];
                    
                    if ((exitType == "TP") && pair.SlOrder != null)
                    {
                        // TP filled → cancel SL
                        if (pair.SlOrder.OrderState == OrderState.Working || pair.SlOrder.OrderState == OrderState.Accepted)
                        {
                            try { tradingAccount.Cancel(new[] { pair.SlOrder }); } catch { }
                            Print(string.Format("[BRACKET] Software OCO: TP filled, cancelled SL for {0}", instrumentKey));
                        }
                    }
                    else if ((exitType == "SL" || exitType == "TRAILING") && pair.TpOrder != null)
                    {
                        // SL/TRAILING filled → cancel TP
                        if (pair.TpOrder.OrderState == OrderState.Working || pair.TpOrder.OrderState == OrderState.Accepted)
                        {
                            try { tradingAccount.Cancel(new[] { pair.TpOrder }); } catch { }
                            Print(string.Format("[BRACKET] Software OCO: {0} filled, cancelled TP for {1}", exitType, instrumentKey));
                        }
                    }
                    
                    activeBrackets.Remove(instrumentKey);
                    Print(string.Format("[BRACKET] Bracket pair cleared for {0} after {1} fill (Phase {2})", instrumentKey, exitType, exitPhase));
                }
            }
        }
        
        /// <summary>
        /// Auto-detect current front-month contract based on CME quarterly cycle.
        /// ES/MES/NQ/MNQ use March (H), June (M), September (U), December (Z).
        /// </summary>
        private string GetCurrentContractMonth()
        {
            DateTime now = DateTime.Now;
            int month = now.Month;
            int year = now.Year % 100;  // 2-digit year
            
            // CME quarterly months: 3 (Mar), 6 (Jun), 9 (Sep), 12 (Dec)
            // Roll to next quarter if within 2 weeks of expiration (3rd Friday)
            int[] quarters = { 3, 6, 9, 12 };
            int nextQuarter = 3;
            
            foreach (int q in quarters)
            {
                if (month < q || (month == q && now.Day < 14))
                {
                    nextQuarter = q;
                    break;
                }
            }
            
            // If past December, roll to March next year
            if (month >= 12 && now.Day >= 14)
            {
                nextQuarter = 3;
                year = (year + 1) % 100;
            }
            
            return string.Format("{0:D2}-{1:D2}", nextQuarter, year);
        }
        #endregion
    }
}


#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private SignalBridge[] cacheSignalBridge;
		public SignalBridge SignalBridge(int port, string accountName, bool enableTrading, bool streamMarketData, string contractMonth)
		{
			return SignalBridge(Input, port, accountName, enableTrading, streamMarketData, contractMonth);
		}

		public SignalBridge SignalBridge(ISeries<double> input, int port, string accountName, bool enableTrading, bool streamMarketData, string contractMonth)
		{
			if (cacheSignalBridge != null)
				for (int idx = 0; idx < cacheSignalBridge.Length; idx++)
					if (cacheSignalBridge[idx] != null && cacheSignalBridge[idx].Port == port && cacheSignalBridge[idx].AccountName == accountName && cacheSignalBridge[idx].EnableTrading == enableTrading && cacheSignalBridge[idx].StreamMarketData == streamMarketData && cacheSignalBridge[idx].ContractMonth == contractMonth && cacheSignalBridge[idx].EqualsInput(input))
						return cacheSignalBridge[idx];
			return CacheIndicator<SignalBridge>(new SignalBridge(){ Port = port, AccountName = accountName, EnableTrading = enableTrading, StreamMarketData = streamMarketData, ContractMonth = contractMonth }, input, ref cacheSignalBridge);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.SignalBridge SignalBridge(int port, string accountName, bool enableTrading, bool streamMarketData, string contractMonth)
		{
			return indicator.SignalBridge(Input, port, accountName, enableTrading, streamMarketData, contractMonth);
		}

		public Indicators.SignalBridge SignalBridge(ISeries<double> input , int port, string accountName, bool enableTrading, bool streamMarketData, string contractMonth)
		{
			return indicator.SignalBridge(input, port, accountName, enableTrading, streamMarketData, contractMonth);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.SignalBridge SignalBridge(int port, string accountName, bool enableTrading, bool streamMarketData, string contractMonth)
		{
			return indicator.SignalBridge(Input, port, accountName, enableTrading, streamMarketData, contractMonth);
		}

		public Indicators.SignalBridge SignalBridge(ISeries<double> input , int port, string accountName, bool enableTrading, bool streamMarketData, string contractMonth)
		{
			return indicator.SignalBridge(input, port, accountName, enableTrading, streamMarketData, contractMonth);
		}
	}
}

#endregion
