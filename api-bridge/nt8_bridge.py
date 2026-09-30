# =============================================================================
#  nt8_bridge - Python side of the NinjaTrader 8 execution bridge
# =============================================================================
#
#  WHAT IT DOES
#  The counterpart to SignalBridge.cs. Connects to the listener NinjaTrader is
#  hosting on loopback, sends orders with their full exit geometry, and consumes
#  the acknowledgement, fill and telemetry stream coming back.
#
#  WHY THE BOUNDARY IS DRAWN HERE
#  Research and execution have different requirements and are separated
#  deliberately. The Python side owns everything that benefits from the
#  scientific stack - signal generation, parameter search, statistical analysis.
#  The C# side owns everything that must be close to the order: bracket
#  placement, the trailing stop state machine, and reaction to fills.
#
#  The interface between them is an order plus its exit geometry, not a stream of
#  instructions. Python says what trade to take and under what rules to exit it;
#  NinjaTrader executes and manages it. That division is what allows the Python
#  process to be restarted, re-deployed or to crash outright without stranding a
#  live position, because the rules travelled with the order.
#
#  PARITY IS THE POINT
#  The exit geometry sent on the wire is the same structure the backtester
#  applies offline, and the state machine consuming it in C# mirrors the Python
#  execution handler used in research. A backtest that models exits differently
#  from the live path produces numbers that cannot be traded, so the duplication
#  is accepted as the cost of making research results meaningful.
#
# =============================================================================

"""
NinjaTrader 8 Local Bridge - TCP Socket Communication
Routes signals from Python engine to NinjaTrader 8 for order execution.

Architecture:
- Python acts as TCP CLIENT (connects to NT8)
- NT8 acts as TCP SERVER (listens on port 5580)
- JSON protocol for signal/ACK/fill messages
"""

import json
import logging
import socket
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from datetime import datetime
from queue import Empty, Queue
from typing import TYPE_CHECKING, Any, Callable, Dict, Optional

from config import COMMISSION_PER_CONTRACT, get_front_month_contract
from events import FillEvent, OrderEvent, OrderStatus, SignalEvent, SignalType
from log_config import log_event
from tick_filter import TickAggregator

if TYPE_CHECKING:
    from portfolio import Portfolio

# Configure logger for standardized terminal output
logger = logging.getLogger("NT8Bridge")
if not logger.handlers:
    handler = logging.StreamHandler()
    handler.setFormatter(logging.Formatter("%(message)s"))
    logger.addHandler(handler)
    logger.setLevel(logging.INFO)


@dataclass
class BridgeStatus:
    """Status information for dashboard display."""

    connected: bool = False
    last_signal_time: Optional[datetime] = None
    last_signal_desc: str = ""
    last_ack_time: Optional[datetime] = None
    last_ack_desc: str = ""
    pending_signals: int = 0
    heartbeat_ms: float = 0.0
    error: Optional[str] = None

    # PnL Telemetry from NT8 (for risk management)
    daily_realized_pnl: float = 0.0
    open_floating_pnl: float = 0.0
    net_daily_pnl: float = 0.0
    account_value: float = 0.0
    current_position: int = 0  # Signed: +long, -short (from NT8)
    position_symbol: str = ""  # Symbol for current_position
    last_telemetry_time: Optional[datetime] = None


class NT8Bridge:
    """
    TCP Bridge Client for NinjaTrader 8 communication.

    Connects to NT8's TCP server and sends JSON-formatted trading signals.
    Receives ACKs and fill confirmations back from NT8.

    Features:
    - Persistent TCP connection with auto-reconnect
    - JSON protocol for cross-platform compatibility
    - Heartbeat monitoring
    - Thread-safe signal queue
    - Standardized terminal logging with [BRIDGE] tags
    """

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 5580,
        reconnect_interval: float = 5.0,
        heartbeat_interval: float = 2.0,
        recv_timeout: float = 0.1,
        tick_queue: Optional[Queue] = None,
    ):
        """
        Initialize NT8 Bridge.

        Args:
            host: NT8 server host (localhost for same machine)
            port: NT8 server port (default 5580)
            reconnect_interval: Seconds between reconnect attempts
            heartbeat_interval: Seconds between heartbeat checks
            recv_timeout: Socket receive timeout in seconds
            tick_queue: Thread-safe queue for tick data (Producer/Consumer pattern)
        """
        self.host = host
        self.port = port
        self.reconnect_interval = reconnect_interval
        self.heartbeat_interval = heartbeat_interval
        self.recv_timeout = recv_timeout

        # Connection state
        self.socket: Optional[socket.socket] = None
        self.is_connected = False
        self.is_authenticated = True  # No auth needed for local TCP

        # Threading
        self._lock = threading.RLock()
        self._recv_thread: Optional[threading.Thread] = None
        self._heartbeat_thread: Optional[threading.Thread] = None
        self._running = False

        # Message tracking
        self._pending_signals: Dict[str, SignalEvent] = {}
        self._pending_orders: Dict[str, OrderEvent] = {}  # Track orders from send_order()
        self._signal_queue: Queue = Queue()
        self._fill_queue: Queue = Queue()

        # Order state machine - track terminal states to filter stale messages
        # States: 'PENDING' -> 'SUBMITTED' -> 'FILLED' or 'REJECTED'
        # Terminal states: FILLED, REJECTED (ignore subsequent messages)
        self._order_states: Dict[str, str] = {}
        self._ORDER_STATES_MAX_SIZE: int = 500

        # Status tracking
        self._status = BridgeStatus()
        self._last_heartbeat_sent: float = 0
        self._last_heartbeat_recv: float = 0

        # Thread-safe tick queue (Producer/Consumer pattern)
        self.tick_queue: Optional[Queue] = tick_queue

        # Legacy callbacks (fill_callback still used for order fills)
        self.fill_callback: Optional[Callable[[FillEvent], None]] = None
        self.disconnect_callback: Optional[Callable] = None
        self.quote_callback: Optional[Callable[[dict], None]] = None  # For bid/ask

        # Tick statistics
        self._tick_count = 0
        self._last_tick_time: Optional[datetime] = None

        # ZERO-DRAG HOT PATH: Portfolio reference for deferred DB writes
        self._portfolio: Optional["Portfolio"] = None

        # Background thread pool for async DB writes (non-blocking)
        self._db_executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="db_writer")

        # Aggressor inference from bid/ask tracking
        self._last_bid: Dict[str, float] = {}  # symbol -> last bid price
        self._last_ask: Dict[str, float] = {}  # symbol -> last ask price

        # PHASE 4: Tick aggregation for live data parity with historical Databento
        # DISABLED: Raw Trades schema no longer needs aggregation heuristic
        self._tick_aggregator = TickAggregator(
            window_ms=1.0,
            enabled=False,  # Disabled: raw Trades schema no longer needs aggregation
            debug=False,
        )

        # For compatibility with existing engine interface
        self.trading_active = True
        self.session_synced = True
        self.halt_reason: Optional[str] = None

        # Tripwire monitor reference (set by engine after init)
        self.tripwire = None

        log_event("BRIDGE", f"NT8 Bridge init: {self.host}:{self.port} heartbeat={self.heartbeat_interval}s")

    def connect(self) -> bool:
        """
        Establish TCP connection to NinjaTrader 8.

        Returns:
            True if connected successfully
        """
        with self._lock:
            if self.is_connected:
                return True

            try:
                logger.info(f"[BRIDGE] Connecting to NT8 at {self.host}:{self.port}...")

                self.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
                self.socket.settimeout(5.0)  # Connection timeout
                self.socket.connect((self.host, self.port))
                self.socket.settimeout(self.recv_timeout)  # Receive timeout

                self.is_connected = True
                self._status.connected = True
                self._status.error = None
                self._running = True

                # Start receiver thread
                self._recv_thread = threading.Thread(target=self._receive_loop, daemon=True)
                self._recv_thread.start()

                # Start heartbeat thread
                self._heartbeat_thread = threading.Thread(target=self._heartbeat_loop, daemon=True)
                self._heartbeat_thread.start()

                logger.info("[BRIDGE] ✓ Connected to NinjaTrader 8")

                # #12: Zombie order cleanup — cancel any stale working orders from prior session
                try:
                    cancel_msg = json.dumps({"type": "CANCEL_ALL", "reason": "reconnect_cleanup"}) + "\n"
                    self.socket.sendall(cancel_msg.encode("utf-8"))
                    logger.info("[BRIDGE] Sent CANCEL_ALL to clear zombie orders from prior session")
                except Exception as e:
                    logger.warning(f"[BRIDGE] CANCEL_ALL failed (non-fatal): {e}")

                return True

            except ConnectionRefusedError:
                self._status.error = "Connection refused - is NT8 SignalBridge running?"
                logger.info(f"[BRIDGE] ✗ Connection refused - NT8 not listening on port {self.port}")
                return False
            except socket.timeout:
                self._status.error = "Connection timeout"
                logger.info("[BRIDGE] ✗ Connection timeout")
                return False
            except Exception as e:
                self._status.error = str(e)
                logger.info(f"[BRIDGE] ✗ Connection error: {e}")
                return False

    def disconnect(self):
        """Close TCP connection gracefully."""
        with self._lock:
            self._running = False
            self.is_connected = False
            self._status.connected = False

            if self.socket:
                try:
                    self.socket.close()
                except:
                    pass
                self.socket = None

            logger.info("[BRIDGE] Disconnected from NT8")

    def send_signal(self, signal: SignalEvent, position_size: int = 1) -> bool:
        """
        Send trading signal to NinjaTrader 8.

        Args:
            signal: SignalEvent from strategy
            position_size: Number of contracts

        Returns:
            True if signal sent successfully
        """
        if not self.is_connected:
            logger.info("[BRIDGE] ✗ Cannot send signal: Not connected to NT8")
            return False

        if not self.trading_active:
            logger.info(f"[BRIDGE] ✗ Cannot send signal: Trading halted - {self.halt_reason}")
            return False

        # Determine action from signal type
        if signal.signal_type == SignalType.LONG:
            action = "BUY"
        elif signal.signal_type == SignalType.SHORT:
            action = "SELL"
        elif signal.signal_type == SignalType.EXIT:
            action = "EXIT"
        else:
            logger.info(f"[BRIDGE] ✗ Unknown signal type: {signal.signal_type}")
            return False

        # Create unique signal ID
        signal_id = f"SIG-{int(time.time() * 1000)}-{signal.symbol}"

        # Extract position_size from signal metadata (GUI-controlled), fallback to parameter
        qty = int(signal.metadata.get("position_size", position_size)) if signal.metadata else position_size

        # Build JSON payload (symbol required by NT8 safety checks)
        payload = {
            "type": "SIGNAL",
            "id": signal_id,
            "action": action,
            "symbol": signal.symbol,
            "qty": qty,
            "order_type": "MARKET",
            "strategy_id": signal.strategy_id,
            "timestamp": datetime.now().isoformat(),
        }

        # Add bracket parameters if present
        if signal.metadata:
            payload["tp_bricks"] = signal.metadata.get("tp_bricks", 3)
            payload["sl_bricks"] = signal.metadata.get("sl_bricks", 1)
            payload["brick_size_ticks"] = signal.metadata.get("brick_size_ticks", 8.0)
            # Bracket mode: use_trailing_breakeven=False → "Static_Only" (no BE, no trail)
            if not signal.metadata.get("use_trailing_breakeven", True):
                payload["exit_mode"] = "Static_Only"

        # Log signal
        logger.info(f"[LIVE] Signal Received: {action} {position_size} {signal.symbol}")

        try:
            # Serialize and send
            json_str = json.dumps(payload) + "\n"  # Newline delimiter

            with self._lock:
                if self.socket:
                    self.socket.sendall(json_str.encode("utf-8"))

            # T8: Record dispatch time under the actual wire signal_id
            if self.tripwire and not self.tripwire.halted:
                self.tripwire.record_order_dispatch(signal_id)

            # Track pending signal
            self._pending_signals[signal_id] = signal
            self._status.pending_signals = len(self._pending_signals)
            self._status.last_signal_time = datetime.now()
            self._status.last_signal_desc = f"{action} {position_size} {signal.symbol}"

            logger.info(f"[BRIDGE] Payload Sent to NT8: {json.dumps(payload)}")
            return True

        except Exception as e:
            logger.info(f"[BRIDGE] ✗ Send error: {e}")
            self._handle_disconnect()
            return False

    def set_portfolio(self, portfolio: "Portfolio"):
        """
        Set portfolio reference for deferred DB writes.

        CRITICAL: Must be called during engine initialization to enable
        zero-drag hot path with async database persistence.

        Args:
            portfolio: Portfolio instance for commit_working_order() calls
        """
        self._portfolio = portfolio

    def send_order(self, order: OrderEvent) -> Optional[FillEvent]:
        """
        ZERO-DRAG HOT PATH: Send order to NT8 with microsecond-optimized operations.

        CRITICAL EXECUTION SEQUENCE:
        1. JSON serialize payload (CPU-bound, unavoidable)
        2. Acquire strict minimum thread lock
        3. socket.sendall(payload) - THE TRIGGER
        4. Release lock
        5. THEN (async): logger.info() + background DB write

        This ensures ZERO blocking I/O between ML prediction and TCP transmission.

        Args:
            order: OrderEvent from portfolio

        Returns:
            None (fills come asynchronously)
        """
        if not self.is_connected:
            logger.info("[BRIDGE] ✗ Cannot send order: Not connected to NT8")
            return None

        # STEP 1: Resolve front-month contract symbol for NT8 execution
        # CRITICAL: Use raw contract symbol (e.g., "ESM6") not base symbol ("ES")
        # to ensure NT8 executes on the same contract month as Databento data feed
        try:
            nt8_symbol = get_front_month_contract(order.symbol)
        except Exception:
            nt8_symbol = order.symbol  # Fallback to base symbol on error

        # STEP 2: JSON serialize (CPU-bound, pre-compute before lock)
        signal_id = f"ORD-{order.order_id}"
        payload = {
            "type": "ORDER",
            "id": signal_id,
            "action": order.direction,
            "symbol": nt8_symbol,  # Use front-month contract symbol
            "qty": order.quantity,
            "order_type": str(order.order_type.value) if hasattr(order.order_type, "value") else str(order.order_type),
            "strategy_id": order.strategy_id,
            "price": order.price,
            "timestamp": datetime.now().isoformat(),
        }

        # V4: Inject bracket params + exit geometry from order metadata.
        # CONTRACT: C# reads sl_ticks, tp_ticks, and exit_mode-specific params.
        if order.metadata:
            sl_ticks = order.metadata.get("sl_ticks")
            tp_ticks = order.metadata.get("tp_ticks")
            if sl_ticks is not None:
                payload["sl_ticks"] = int(sl_ticks)
            if tp_ticks is not None:
                payload["tp_ticks"] = int(tp_ticks)
            # Exit geometry: mode-exclusive payload — no dead fields in either branch.
            # use_trailing_breakeven=False overrides to "Static_Only" (no BE, no trail)
            if not order.metadata.get("use_trailing_breakeven", True):
                _exit_mode = "Static_Only"
            else:
                _exit_mode = order.metadata.get("exit_mode", "Static_BE_Jump")
            payload["exit_mode"] = _exit_mode
            if _exit_mode == "Indicator_Ratchet":
                # C# ratchets SL along the specified indicator level + offset
                payload["trail_indicator_type"] = order.metadata.get("trail_indicator_type", "none")
                payload["trail_indicator_period"] = int(order.metadata.get("trail_indicator_period", 20))
                payload["trail_offset_ticks"] = int(order.metadata.get("trail_offset_ticks", 0))
            else:
                # Static_BE_Jump: C# implements milestone-based stop advancement
                payload["be_trigger_pct"] = float(order.metadata.get("be_trigger_pct", 0.75))
                payload["be_offset_ticks"] = int(order.metadata.get("be_offset_ticks", 1))
        json_bytes = (json.dumps(payload) + "\n").encode("utf-8")

        try:
            # CRITICAL SECTION: Send order AND register pending in same lock
            # to prevent fast ACK/FILL from arriving before registration
            with self._lock:
                if self.socket:
                    self.socket.sendall(json_bytes)  # THE TRIGGER - FIRST!
                self._pending_orders[signal_id] = order
                send_timestamp = datetime.now()

            # T8: Record dispatch time for flight-time monitoring
            if self.tripwire and not self.tripwire.halted:
                self.tripwire.record_order_dispatch(signal_id)

            # Update status (in-memory, fast)
            self._status.last_signal_time = send_timestamp
            self._status.last_signal_desc = f"{order.direction} {order.quantity} {order.symbol}"

            # Async logging (non-blocking)
            self._db_executor.submit(self._log_order_sent, order, payload)

            # SYNCHRONOUS DB write — must complete BEFORE fill can arrive on main thread
            # Previously async via _db_executor, but caused deadlock: background thread
            # holds ExecutionStateDB._lock while main thread's on_fill() blocks on same lock.
            # A single INSERT (~1ms) is not worth a permanent deadlock.
            if self._portfolio is not None:
                self._portfolio.commit_working_order(order, send_timestamp)

            return None  # Fills come asynchronously

        except Exception as e:
            logger.info(f"[BRIDGE] ✗ Send error: {e}")
            self._handle_disconnect()
            return None

    def _log_order_sent(self, order: OrderEvent, payload: dict):
        """
        Background logging task - called AFTER socket.sendall().
        Runs in thread pool to avoid blocking hot path.
        """
        logger.info(f"[LIVE] Signal Sent: {order.direction} {order.quantity} {order.symbol}")
        logger.info(f"[BRIDGE] Payload Sent to NT8: {json.dumps(payload)}")

    def get_status(self) -> Dict[str, Any]:
        """
        Get current bridge status for dashboard display.

        Returns:
            Status dictionary compatible with dashboard
        """
        return {
            "connected": self.is_connected,
            "authenticated": True,  # No auth for local TCP
            "trading_active": self.trading_active,
            "account_id": "NT8-LOCAL",
            "latency_ms": self._status.heartbeat_ms,
            "positions": {},  # NT8 manages positions
            "working_orders": self._status.pending_signals,
            "unrealized_pnl": 0.0,  # NT8 tracks P&L
            "realized_pnl": 0.0,
            "halt_reason": self.halt_reason,
            "session_synced": True,
            "last_signal": self._status.last_signal_desc,
            "last_ack": self._status.last_ack_desc,
            "error": self._status.error,
            "tick_count": self._tick_count,
            "last_tick_time": self._last_tick_time.isoformat() if self._last_tick_time else None,
        }

    def _receive_loop(self):
        """Background thread: receive ACKs and fills from NT8."""
        buffer = ""

        while self._running:
            try:
                if not self.socket:
                    break

                try:
                    data = self.socket.recv(4096)
                except socket.timeout:
                    continue

                if not data:
                    # Connection closed
                    self._handle_disconnect()
                    break

                buffer += data.decode("utf-8")

                # Process complete JSON messages (newline delimited)
                while "\n" in buffer:
                    line, buffer = buffer.split("\n", 1)
                    if line.strip():
                        self._process_message(line.strip())

            except Exception as e:
                if self._running:
                    logger.info(f"[BRIDGE] Receive error: {e}")
                    self._handle_disconnect()
                break

    def _process_message(self, json_str: str):
        """Process incoming JSON message from NT8."""
        try:
            msg = json.loads(json_str)
            msg_type = msg.get("type", "")

            # Debug: Log first few messages to see what we're receiving
            if not hasattr(self, "_msg_count"):
                self._msg_count = 0
            self._msg_count += 1
            if self._msg_count <= 5:
                logger.info(f"[BRIDGE] Message #{self._msg_count}: type={msg_type}")

            if msg_type == "HEARTBEAT":
                # TRIPWIRE: Check heartbeat staleness before updating
                if self.tripwire and not self.tripwire.halted and self._last_heartbeat_recv > 0:
                    self.tripwire.check_heartbeat(self._last_heartbeat_recv)
                self._last_heartbeat_recv = time.time()
                self._status.heartbeat_ms = msg.get("latency_ms", 0)

            elif msg_type == "ACK":
                signal_id = msg.get("id", "")
                nt8_order_id = msg.get("nt8_order_id", "")
                status = msg.get("status", "")

                # Check if order already in terminal state (REJECTED/FILLED)
                current_state = self._order_states.get(signal_id, "PENDING")
                if current_state in ("REJECTED", "FILLED"):
                    logger.warning(f"[BRIDGE] ⚠️ Ignoring stale ACK for {signal_id} (already {current_state})")
                    return

                # Update state machine
                self._order_states[signal_id] = "SUBMITTED"

                self._status.last_ack_time = datetime.now()
                self._status.last_ack_desc = f"order_id={nt8_order_id}, {status}"

                logger.info(f"[BRIDGE] ACK Received from NT8: order_id={nt8_order_id}, status={status}")

                # Don't remove from pending yet - wait for FILL or REJECT

            elif msg_type == "FILL":
                signal_id = msg.get("id", "")
                fill_price = msg.get("fill_price", 0.0)
                fill_qty = msg.get("fill_qty", 0)
                symbol = msg.get("symbol", "")
                is_bracket_exit = msg.get("is_bracket_exit", False)
                exit_type = msg.get("exit_type", "")  # 'TP' or 'SL'

                # Check if order already in terminal state
                current_state = self._order_states.get(signal_id, "PENDING")
                if current_state == "FILLED":
                    logger.warning(f"[BRIDGE] ⚠️ Ignoring duplicate FILL for {signal_id}")
                    return

                # Mark as terminal state
                self._order_states[signal_id] = "FILLED"

                # T8: Check order flight time (dispatch → fill latency)
                if self.tripwire and not self.tripwire.halted:
                    self.tripwire.check_order_flight(signal_id)

                # Prune stale terminal entries to prevent unbounded growth
                if len(self._order_states) > self._ORDER_STATES_MAX_SIZE:
                    terminal = [k for k, v in self._order_states.items() if v in ("FILLED", "REJECTED")]
                    for k in terminal[: len(terminal) // 2]:
                        del self._order_states[k]

                # =========================================================================
                # GHOST POSITION FIX: Handle bracket exit fills (TP/SL)
                # These don't have a pending signal - they're native NT8 bracket orders
                # =========================================================================
                if is_bracket_exit:
                    logger.info(f"[BRACKET] {exit_type} Exit Fill: {fill_qty} {symbol} @ {fill_price}")

                    # Bracket exits close positions - quantity is negative (opposite of entry)
                    # If action is BUY, we're closing a SHORT (quantity becomes positive to close)
                    # If action is SELL, we're closing a LONG (quantity becomes negative to close)
                    signed_qty = fill_qty if msg.get("action") == "BUY" else -fill_qty

                    # Use a generic strategy_id for bracket exits - engine will match by symbol
                    # The portfolio will update position based on fill, and engine will sync strategy
                    fill = FillEvent(
                        timestamp=datetime.now(),
                        symbol=symbol,
                        strategy_id="BRACKET_EXIT",  # Special marker for bracket exits
                        order_id=f"BRACKET_{exit_type}_{signal_id}" if exit_type else signal_id,
                        quantity=signed_qty,
                        fill_price=fill_price,
                        commission=COMMISSION_PER_CONTRACT,
                        slippage=0.0,
                        exchange="CME",
                        fill_qty=fill_qty,
                        remaining_qty=0,
                        order_status=OrderStatus.FILLED,
                    )

                    # Queue for engine processing
                    self._fill_queue.put(fill)

                    # Call callback if registered
                    if self.fill_callback:
                        self.fill_callback(fill)

                    return

                # Standard entry fill handling (existing logic)
                logger.info(f"[BRIDGE] Fill Confirmed: {fill_qty} {symbol} @ {fill_price}")

                # Create FillEvent and notify engine
                # Check _pending_orders FIRST (from send_order()), then _pending_signals (from send_signal())
                if signal_id in self._pending_orders:
                    # Match found in pending orders - this is the primary path for live trading
                    order = self._pending_orders[signal_id]

                    fill = FillEvent(
                        timestamp=datetime.now(),
                        symbol=order.symbol,
                        strategy_id=order.strategy_id,
                        order_id=signal_id,
                        quantity=fill_qty if msg.get("action") == "BUY" else -fill_qty,
                        fill_price=fill_price,
                        commission=COMMISSION_PER_CONTRACT,  # Default commission
                        slippage=0.0,
                        exchange="CME",
                        fill_qty=fill_qty,
                        remaining_qty=0,
                        order_status=OrderStatus.FILLED,
                    )

                    # Queue for engine processing
                    self._fill_queue.put(fill)

                    # Call callback if registered
                    if self.fill_callback:
                        self.fill_callback(fill)

                    del self._pending_orders[signal_id]
                    logger.info(f"[BRIDGE] Fill matched to pending order: {order.strategy_id}")

                elif signal_id in self._pending_signals:
                    # Legacy path for send_signal() usage
                    signal = self._pending_signals[signal_id]

                    fill = FillEvent(
                        timestamp=datetime.now(),
                        symbol=signal.symbol,
                        strategy_id=signal.strategy_id,
                        order_id=signal_id,
                        quantity=fill_qty if msg.get("action") == "BUY" else -fill_qty,
                        fill_price=fill_price,
                        commission=COMMISSION_PER_CONTRACT,  # Default commission
                        slippage=0.0,
                        exchange="CME",
                        fill_qty=fill_qty,
                        remaining_qty=0,
                        order_status=OrderStatus.FILLED,
                    )

                    # Queue for engine processing
                    self._fill_queue.put(fill)

                    # Call callback if registered
                    if self.fill_callback:
                        self.fill_callback(fill)

                    del self._pending_signals[signal_id]
                    self._status.pending_signals = len(self._pending_signals)
                else:
                    # =========================================================================
                    # ORPHAN FILL: Handle fills that don't match any pending order/signal
                    # This should now be rare - only for bracket exits or unexpected fills
                    # =========================================================================
                    logger.warning(f"[BRIDGE] ⚠️ Orphan fill received: {signal_id}")
                    logger.warning(f"[BRIDGE] Pending orders: {list(self._pending_orders.keys())}")
                    logger.warning(f"[BRIDGE] Pending signals: {list(self._pending_signals.keys())}")

                    # Still create FillEvent with generic strategy_id - let engine route by symbol
                    action = msg.get("action", "BUY")
                    signed_qty = fill_qty if action == "BUY" else -fill_qty

                    fill = FillEvent(
                        timestamp=datetime.now(),
                        symbol=symbol,
                        strategy_id="ORPHAN_FILL",  # Special marker for orphan fills
                        order_id=signal_id,
                        quantity=signed_qty,
                        fill_price=fill_price,
                        commission=COMMISSION_PER_CONTRACT,
                        slippage=0.0,
                        exchange="CME",
                        fill_qty=fill_qty,
                        remaining_qty=0,
                        order_status=OrderStatus.FILLED,
                    )

                    self._fill_queue.put(fill)
                    if self.fill_callback:
                        self.fill_callback(fill)

                    logger.info(f"[BRIDGE] Orphan fill routed as ORPHAN_FILL: {signed_qty} {symbol} @ {fill_price}")

            elif msg_type == "REJECT":
                signal_id = msg.get("id", "")
                reason = msg.get("reason", "Unknown")

                # Check if order already in terminal state
                current_state = self._order_states.get(signal_id, "PENDING")
                if current_state in ("REJECTED", "FILLED"):
                    logger.warning(f"[BRIDGE] ⚠️ Ignoring duplicate REJECT for {signal_id} (already {current_state})")
                    return

                # Mark as terminal state
                self._order_states[signal_id] = "REJECTED"

                logger.info(f"[BRIDGE] ✗ Order Rejected by NT8: {reason}")

                if signal_id in self._pending_signals:
                    del self._pending_signals[signal_id]
                    self._status.pending_signals = len(self._pending_signals)
                if signal_id in self._pending_orders:
                    del self._pending_orders[signal_id]

            elif msg_type == "ERROR":
                error_msg = msg.get("message", "Unknown error")
                logger.info(f"[BRIDGE] ✗ NT8 Error: {error_msg}")
                self._status.error = error_msg

            elif msg_type == "TICK":
                # Market data tick from NT8
                self._tick_count += 1
                self._last_tick_time = datetime.now()

                # Log first tick and every 10K ticks for debugging
                if self._tick_count == 1:
                    log_event("BRIDGE", "First tick received")
                if self._tick_count % 10000 == 0:
                    agg_stats = self._tick_aggregator.get_stats()
                    log_event("BRIDGE", f"{self._tick_count:,} ticks (agg_ratio={agg_stats['aggregation_ratio']:.1f}x)")
                    # V5: Volume provenance logging for backtest parity monitoring
                    logger.info(
                        f"[VOLUME] {self._tick_count:,} ticks: source=NT8_live, "
                        f"agg_ratio={agg_stats['aggregation_ratio']:.2f}x, "
                        f"emitted={agg_stats['ticks_emitted']:,}"
                    )

                # Extract and normalize fields
                symbol = msg.get("symbol", "")
                price = float(msg.get("price", 0.0))
                volume = int(msg.get("volume", 1) or 1)  # Default to 1 if 0/None
                timestamp_ms = msg.get("timestamp", 0)

                # Convert timestamp from milliseconds to datetime (UTC)
                from datetime import timezone

                tick_timestamp = datetime.fromtimestamp(timestamp_ms / 1000.0, tz=timezone.utc)

                # =============================================================
                # AGGRESSOR INFERENCE (Tick-Test Method)
                # Compare trade price to last bid/ask to determine aggressor:
                # - Price >= Ask: Buy aggressor (lifted the offer)
                # - Price <= Bid: Sell aggressor (hit the bid)
                # - Otherwise: Unknown (inside spread or stale quotes)
                #
                # NOTE: This differs from Databento's explicit exchange-provided
                # side field. The TickAggregator helps mitigate discrepancies
                # by consolidating micro-bursts where classification may vary.
                # =============================================================
                aggressor = 0
                if symbol in self._last_bid and symbol in self._last_ask:
                    if price >= self._last_ask[symbol]:
                        aggressor = 1  # Buy aggressor (lifted the offer)
                    elif price <= self._last_bid[symbol]:
                        aggressor = -1  # Sell aggressor (hit the bid)
                    # else: price is inside spread - aggressor unknown (0)

                # =============================================================
                # PHASE 4: Micro-Burst Aggregation for Live Data Parity
                # Consolidates sub-millisecond ticks at same price level to
                # match how Databento parquet naturally aggregates high-frequency
                # noise. This eliminates Renko brick inflation in live execution.
                # =============================================================
                aggregated_tick = self._tick_aggregator.process(
                    symbol=symbol, timestamp=tick_timestamp, price=price, volume=volume, aggressor=aggressor
                )

                # Only push to queue when aggregator emits a consolidated tick
                if aggregated_tick is not None:
                    tick_data = {
                        "symbol": aggregated_tick.symbol,
                        "price": aggregated_tick.price,
                        "volume": aggregated_tick.volume,
                        "timestamp": aggregated_tick.timestamp,
                        "aggressor": aggregated_tick.aggressor,
                        "aggressor_source": "tick_test",  # V4: Inferred from bid/ask (vs Databento 'exchange')
                    }

                    # Debug logging (every 10K raw ticks to avoid spam)
                    if self._tick_count % 10000 == 1:
                        logger.debug(
                            f"[TICK_AGG] {symbol} @ {price} vol={aggregated_tick.volume} aggr={aggregated_tick.aggressor}"
                        )

                    # Push to tick queue (Producer pattern - no cross-thread callback)
                    if self.tick_queue is not None:
                        self.tick_queue.put(tick_data)
                        if self._tick_count == 1:
                            log_event("BRIDGE", f"First tick queued (qsize={self.tick_queue.qsize()})")
                    else:
                        if self._tick_count == 1:
                            logger.debug("[BRIDGE] tick_queue is None — NT8 ticks discarded (Databento is data source)")

            elif msg_type == "TELEMETRY":
                # PnL telemetry from NT8 for risk management
                daily_realized = float(msg.get("daily_realized_pnl", 0.0))
                open_floating = float(msg.get("open_floating_pnl", 0.0))
                account_value = float(msg.get("account_value", 0.0))

                # Update status with telemetry data
                self._status.daily_realized_pnl = daily_realized
                self._status.open_floating_pnl = open_floating
                self._status.net_daily_pnl = daily_realized + open_floating
                self._status.account_value = account_value
                self._status.current_position = int(msg.get("current_position", 0))
                self._status.position_symbol = msg.get("position_symbol", "")
                self._status.last_telemetry_time = datetime.now()

                # Log telemetry occasionally (every 300 seconds — quiet mode)
                if (
                    not hasattr(self, "_last_telemetry_log")
                    or (datetime.now() - self._last_telemetry_log).total_seconds() >= 300
                ):
                    logger.debug(
                        f"[TELEMETRY] Net Daily PnL: ${self._status.net_daily_pnl:,.2f} "
                        f"(Realized: ${daily_realized:,.2f}, Floating: ${open_floating:,.2f})"
                    )
                    self._last_telemetry_log = datetime.now()

            elif msg_type == "QUOTE":
                # Bid/Ask quote from NT8 - update tracking for aggressor inference
                symbol = msg.get("symbol", "")
                side = msg.get("side", "")
                price = float(msg.get("price", 0.0))

                # Track last bid/ask for aggressor inference
                if side == "BID":
                    self._last_bid[symbol] = price
                elif side == "ASK":
                    self._last_ask[symbol] = price

                # T2: Negative spread guard — warn if ask < bid beyond tolerance (no kill)
                if self.tripwire and not self.tripwire.halted:
                    _b = self._last_bid.get(symbol, 0)
                    _a = self._last_ask.get(symbol, 0)
                    if _b > 0 and _a > 0:
                        self.tripwire.check_negative_spread(symbol, _b, _a)

                quote_data = {
                    "symbol": symbol,
                    "side": side,
                    "price": price,
                    "size": int(msg.get("size", 0)),
                    "timestamp": msg.get("timestamp", 0),
                }

                # Call quote callback if registered
                if self.quote_callback:
                    self.quote_callback(quote_data)

        except json.JSONDecodeError as e:
            logger.info(f"[BRIDGE] Invalid JSON from NT8: {e}")

    def _heartbeat_loop(self):
        """Background thread: send periodic heartbeats."""
        while self._running:
            time.sleep(self.heartbeat_interval)

            if not self.is_connected or not self.socket:
                continue

            try:
                heartbeat = {"type": "HEARTBEAT", "timestamp": int(time.time() * 1000)}
                json_str = json.dumps(heartbeat) + "\n"

                with self._lock:
                    if self.socket:
                        send_time = time.time()
                        self.socket.sendall(json_str.encode("utf-8"))
                        self._last_heartbeat_sent = send_time

            except Exception as e:
                if self._running:
                    logger.info(f"[BRIDGE] Heartbeat error: {e}")
                    self._handle_disconnect()

    def _handle_disconnect(self):
        """Handle unexpected disconnection."""
        with self._lock:
            if not self.is_connected:
                return

            self.is_connected = False
            self._status.connected = False
            self._status.error = "Disconnected from NT8"

            if self.socket:
                try:
                    self.socket.close()
                except:
                    pass
                self.socket = None

            logger.info("[BRIDGE] ✗ Connection to NT8 lost")

            # Notify engine
            if self.disconnect_callback:
                self.disconnect_callback()

    def get_pending_fills(self) -> list:
        """
        Get any pending fills from the queue.

        Returns:
            List of FillEvent objects
        """
        fills = []
        while True:
            try:
                fill = self._fill_queue.get_nowait()
                fills.append(fill)
            except Empty:
                break
        return fills

    def halt_trading(self, reason: str):
        """Emergency halt all trading."""
        self.trading_active = False
        self.halt_reason = reason
        logger.info(f"[BRIDGE] ⚠️ Trading HALTED: {reason}")

    def send_flatten_command(self) -> bool:
        """
        Send FLATTEN command to NT8 to close all positions and cancel orders.
        Used by Shadow Mode risk manager when daily limits are hit.

        Returns:
            True if command sent successfully
        """
        if not self.is_connected:
            logger.warning("[BRIDGE] Cannot send FLATTEN: Not connected to NT8")
            return False

        try:
            payload = {
                "type": "FLATTEN",
                "reason": "Python Risk Manager - Daily Limit Reached",
                "timestamp": datetime.now().isoformat(),
            }
            json_bytes = (json.dumps(payload) + "\n").encode("utf-8")

            with self._lock:
                if self.socket:
                    self.socket.sendall(json_bytes)

            logger.info("[BRIDGE] 🛑 FLATTEN command sent to NT8")
            return True

        except Exception as e:
            logger.error(f"[BRIDGE] Failed to send FLATTEN: {e}")
            return False

    def send_cancel_all(self, reason: str = "post_exit_safety") -> bool:
        """
        Send CANCEL_ALL to NT8 to clean up any residual working orders.
        Safe to call when already flat — NT8 no-ops the position close.
        Used as a safety net after each bracket exit to prevent orphaned orders.

        Returns:
            True if command sent successfully
        """
        if not self.is_connected:
            return False

        try:
            payload = {
                "type": "CANCEL_ALL",
                "reason": reason,
                "timestamp": datetime.now().isoformat(),
            }
            json_bytes = (json.dumps(payload) + "\n").encode("utf-8")
            with self._lock:
                if self.socket:
                    self.socket.sendall(json_bytes)
            logger.info(f"[BRIDGE] CANCEL_ALL sent ({reason})")
            return True
        except Exception as e:
            logger.warning(f"[BRIDGE] CANCEL_ALL failed (non-fatal): {e}")
            return False

    def get_net_daily_pnl(self) -> float:
        """
        Get current net daily PnL from telemetry.

        Returns:
            Net daily PnL (realized + floating)
        """
        return self._status.net_daily_pnl

    def shutdown(self):
        """
        Graceful shutdown - wait for pending DB writes to complete.
        Call this before disconnecting to ensure all orders are persisted.
        """
        self._db_executor.shutdown(wait=True)
        self.disconnect()

    def set_tick_queue(self, tick_queue: Queue):
        """Set the tick queue for Producer/Consumer pattern."""
        self.tick_queue = tick_queue
        logger.info("[BRIDGE] Tick queue attached")

    def register_quote_callback(self, callback: Callable[[dict], None]):
        """Register callback for quote data from NT8."""
        self.quote_callback = callback

    def get_tick_count(self) -> int:
        """Get total ticks received from NT8."""
        return self._tick_count


# =============================================================================
# Standalone test
# =============================================================================

if __name__ == "__main__":
    print("=" * 60)
    print("NT8 Bridge - Standalone Test")
    print("=" * 60)

    bridge = NT8Bridge()

    print("\nAttempting connection to NT8...")
    if bridge.connect():
        print("✓ Connected!")

        # Wait a moment
        time.sleep(2)

        print(f"\nStatus: {bridge.get_status()}")

        bridge.disconnect()
    else:
        print("✗ Could not connect to NT8")
        print("  Make sure NinjaTrader 8 is running with SignalBridge indicator")

    print("\n" + "=" * 60)
