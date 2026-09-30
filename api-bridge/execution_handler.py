# =============================================================================
#  execution_handler - the reference the C# state machine is held against
# =============================================================================
#
#  WHY A PYTHON FILE SITS IN A NINJATRADER REPOSITORY
#  SignalBridge.cs runs a 3-phase trailing stop inside NinjaTrader. This file
#  runs the same three phases offline, in the backtester. They are two
#  implementations of one specification, and they are published together on
#  purpose: the parity claim in this repository is checkable rather than
#  asserted. Read the exit geometry protocol at the top of SignalBridge.cs, then
#  read the mode handling here, and the two should describe the same behaviour.
#
#  THE THREE MODES, IDENTICAL ON BOTH SIDES
#    Static_Only         stop and target never move
#    Static_BE_Jump      stop jumps to breakeven at a configured fraction of TP
#    Indicator_Ratchet   stop trails a fixed brick distance behind the high water mark
#
#  WHY DUPLICATE THE LOGIC AT ALL
#  The alternative is to have the backtester ask the live engine how it would
#  exit, which is impossible offline, or to accept that research and live use
#  different exits, which makes the research worthless. A backtest whose exit
#  logic differs from the live path measures a strategy nobody will trade.
#  Two implementations is the cost of that guarantee, and it is only safe
#  because the specification is written down and both sides are readable.
#
#  WHAT THIS FILE IS NOT
#  It is not the live order path. Nothing here touches a broker. It is a
#  simulated exchange used to evaluate a strategy before it is allowed near the
#  bridge, which is why it models fills rather than requesting them.
#
# =============================================================================

"""
Phase 2 Execution Handler - Simulated Exchange with Institutional Fill Logic
Processes OrderEvents and emits FillEvents with Volume-Pro-Rata and dynamic slippage
"""

import math
from collections import defaultdict, deque
from datetime import datetime, timedelta
from typing import Deque, Dict, List, Optional, Tuple

from config import get_instrument_spec
from events import FillEvent, OrderEvent, OrderStatus, OrderType


class ExecutionHandler:
    """
    Simulates exchange execution with institutional-grade fill logic.

    Features:
    - Volume-Pro-Rata for limit orders: Requires cumulative tick volume at price
      to exceed simulated queue size AFTER order placement before granting fill
    - Dynamic slippage for market/stop orders: Exponential penalty based on
      tick velocity to simulate thin order book during news events
    - Commission modeling per instrument
    - Accurate Databento tick volume tracking
    """

    def __init__(
        self,
        commission_per_contract: float = 2.50,
        limit_order_queue_size: int = 100,
        base_slippage_ticks: float = 0.5,
        velocity_slippage_exponent: float = 1.5,
        network_latency_ms: int = 25,
    ):
        """
        Initialize execution handler.

        Args:
            commission_per_contract: Commission per contract in dollars
            limit_order_queue_size: Simulated queue size in contracts for limit orders
            base_slippage_ticks: Base slippage in ticks for market orders
            velocity_slippage_exponent: Exponent for velocity-based slippage (exponential penalty)
            network_latency_ms: Network latency in milliseconds (simulates Rithmic prop firm execution delay)
        """
        self.commission_per_contract = commission_per_contract
        self.limit_order_queue_size = limit_order_queue_size
        self.base_slippage_ticks = base_slippage_ticks
        self.velocity_slippage_exponent = velocity_slippage_exponent
        self.network_latency_ms = network_latency_ms

        # Track cumulative volume at each price level per symbol
        self.cumulative_volume_at_price: Dict[str, Dict[float, int]] = defaultdict(lambda: defaultdict(int))

        # Track tick history for velocity calculation (timestamp, price, volume)
        self.tick_history: Dict[str, Deque[tuple]] = defaultdict(lambda: deque(maxlen=100))

        # Pending limit orders with placement timestamp, cumulative volume snapshot, and filled quantity
        # Format: [(order, placement_time, volume_at_placement, filled_qty), ...]
        self.pending_limit_orders: Dict[str, List[Tuple]] = defaultdict(list)

        # Track working orders by order_id for partial fill reconciliation
        self.working_orders: Dict[str, OrderEvent] = {}

        # Track ticks within latency window for realistic queue simulation
        # Format: [(timestamp, price, volume), ...]
        self.latency_window_ticks: Dict[str, Deque[tuple]] = defaultdict(lambda: deque(maxlen=1000))

        # Current bid/ask for proper execution (BUY against ask, SELL against bid)
        self.current_bid: Dict[str, float] = {}
        self.current_ask: Dict[str, float] = {}

        # V2: Bracket exit simulation for backtest parity with live NT8 OCO orders
        # Key: f"{strategy_id}:{symbol}", Value: bracket info dict
        self.active_brackets: Dict[str, Dict] = {}

        # State guard: track filled order IDs to prevent double-fill
        self._filled_orders: set = set()

    def calculate_tick_velocity(self, symbol: str, current_time: datetime) -> float:
        """
        Calculate recent tick velocity (ticks per second) for dynamic slippage.

        Args:
            symbol: Futures symbol
            current_time: Current timestamp

        Returns:
            Ticks per second over recent 10-second window
        """
        if len(self.tick_history[symbol]) < 2:
            return 0.0

        # Filter to last 10 seconds
        recent_ticks = [tick for tick in self.tick_history[symbol] if (current_time - tick[0]).total_seconds() <= 10.0]

        if len(recent_ticks) < 2:
            return 0.0

        time_window = (recent_ticks[-1][0] - recent_ticks[0][0]).total_seconds()

        if time_window <= 0:
            return 0.0

        return len(recent_ticks) / time_window

    def update_tick_data(
        self, symbol: str, timestamp: datetime, price: float, volume: int, bid: float = None, ask: float = None
    ):
        """
        Update tick history and cumulative volume tracking for fill simulation.

        Args:
            symbol: Futures symbol
            timestamp: Tick timestamp
            price: Tick price (mid price)
            volume: Tick volume from Databento
            bid: Best bid price for SELL execution
            ask: Best ask price for BUY execution
        """
        self.tick_history[symbol].append((timestamp, price, volume))

        # Track ticks for latency window simulation
        self.latency_window_ticks[symbol].append((timestamp, price, volume))

        # Track current bid/ask for proper execution
        if bid is not None:
            self.current_bid[symbol] = bid
        else:
            self.current_bid[symbol] = price

        if ask is not None:
            self.current_ask[symbol] = ask
        else:
            self.current_ask[symbol] = price

        # Accumulate volume at this price level
        self.cumulative_volume_at_price[symbol][price] += volume

        # Check pending limit orders for fills
        self._check_limit_order_fills(symbol, price, timestamp)

    def _check_limit_order_fills(self, symbol: str, price: float, timestamp: datetime) -> List[FillEvent]:
        """
        Check if any pending limit orders can be filled using Volume-Pro-Rata logic.
        Supports PARTIAL FILLS - orders may fill incrementally based on available volume.

        Args:
            symbol: Futures symbol
            price: Current tick price
            timestamp: Current timestamp

        Returns:
            List of FillEvents (may be partial fills)
        """
        if symbol not in self.pending_limit_orders:
            return []

        fill_events = []
        completed_orders = []

        for i, (order, placement_time, volume_at_placement, filled_qty) in enumerate(self.pending_limit_orders[symbol]):
            # Calculate latency-adjusted order entry time
            latency_seconds = self.network_latency_ms / 1000.0
            order_entry_time = placement_time + timedelta(seconds=latency_seconds)

            # Order not yet in the book due to network latency
            if timestamp < order_entry_time:
                continue

            # Check if price touches limit price
            price_touched = False
            if order.direction == "BUY" and price <= order.price:
                price_touched = True
            elif order.direction == "SELL" and price >= order.price:
                price_touched = True

            if not price_touched:
                continue

            # Calculate volume available for fill
            current_volume = self.cumulative_volume_at_price[symbol][order.price]
            volume_at_entry = volume_at_placement

            # Add volume from ticks that occurred during latency window
            for tick_ts, tick_price, tick_vol in self.latency_window_ticks[symbol]:
                if placement_time <= tick_ts < order_entry_time and tick_price == order.price:
                    volume_at_entry += tick_vol

            volume_since_entry = current_volume - volume_at_entry

            # PARTIAL FILL LOGIC: Fill proportional to volume available
            if volume_since_entry > 0:
                remaining_qty = order.quantity - filled_qty

                # Calculate fill quantity (partial or complete)
                # Use pro-rata allocation: min(remaining, volume_available * fill_rate)
                fill_rate = min(1.0, volume_since_entry / self.limit_order_queue_size)
                potential_fill_qty = int(remaining_qty * fill_rate)

                # Ensure at least 1 contract fills if volume exists
                if potential_fill_qty == 0 and volume_since_entry >= 1:
                    potential_fill_qty = 1

                # Cap at remaining quantity
                actual_fill_qty = min(potential_fill_qty, remaining_qty)

                if actual_fill_qty > 0:
                    # Update filled quantity
                    new_filled_qty = filled_qty + actual_fill_qty
                    new_remaining_qty = order.quantity - new_filled_qty

                    # Determine order status
                    if new_remaining_qty == 0:
                        order_status = OrderStatus.FILLED
                        completed_orders.append(i)
                    else:
                        order_status = OrderStatus.PARTIAL
                        # Update the pending order with new filled_qty
                        self.pending_limit_orders[symbol][i] = (
                            order,
                            placement_time,
                            volume_at_placement,
                            new_filled_qty,
                        )

                    # Generate FillEvent
                    spec = get_instrument_spec(order.symbol)
                    commission = self.commission_per_contract * actual_fill_qty

                    # Quantity sign based on direction
                    signed_qty = actual_fill_qty if order.direction == "BUY" else -actual_fill_qty

                    fill_event = FillEvent(
                        timestamp=timestamp,
                        symbol=order.symbol,
                        strategy_id=order.strategy_id,
                        order_id=order.order_id,
                        quantity=signed_qty,
                        fill_price=order.price,
                        commission=commission,
                        slippage=0.0,
                        exchange="CME",
                        fill_qty=actual_fill_qty,
                        remaining_qty=new_remaining_qty,
                        order_status=order_status,
                        original_qty=order.quantity,
                    )

                    fill_events.append(fill_event)

        # Remove completed orders (reverse order to maintain indices)
        for i in reversed(completed_orders):
            self.pending_limit_orders[symbol].pop(i)

        return fill_events

    def execute_market_order(self, order: OrderEvent, current_price: float, timestamp: datetime) -> FillEvent:
        """
        Execute market order with exponential dynamic slippage.
        Market orders fill IMMEDIATELY but may experience partial fills in thin markets.

        CRITICAL: BUY orders execute against ASK, SELL orders execute against BID.

        Args:
            order: OrderEvent to execute
            current_price: Current market price (mid, used as fallback)
            timestamp: Execution timestamp

        Returns:
            FillEvent (may be partial in extreme conditions)
        """
        spec = get_instrument_spec(order.symbol)
        tick_size = spec["tick_size"]

        # MATHEMATICAL HONESTY: BUY at ASK, SELL at BID
        if order.direction == "BUY":
            base_price = self.current_ask.get(order.symbol, current_price)
        else:
            base_price = self.current_bid.get(order.symbol, current_price)

        # Calculate tick velocity (ticks/second)
        velocity = self.calculate_tick_velocity(order.symbol, timestamp)

        # Exponential slippage penalty: base + velocity^exponent
        # When velocity spikes (news), slippage increases exponentially
        velocity_penalty = math.pow(velocity, self.velocity_slippage_exponent) if velocity > 0 else 0.0
        slippage_ticks = self.base_slippage_ticks + (velocity_penalty * 0.01)  # Scale factor

        slippage_price = slippage_ticks * tick_size

        # Apply slippage in unfavorable direction (on top of bid/ask spread)
        if order.direction == "BUY":
            fill_price = base_price + slippage_price
        else:
            fill_price = base_price - slippage_price

        commission = self.commission_per_contract * abs(order.quantity)

        # State guard: reject if this order was already filled
        if order.order_id in self._filled_orders:
            return None

        # Market orders fill immediately (full fill)
        signed_qty = order.quantity if order.direction == "BUY" else -order.quantity

        self._filled_orders.add(order.order_id)
        return FillEvent(
            timestamp=timestamp,
            symbol=order.symbol,
            strategy_id=order.strategy_id,
            order_id=order.order_id,
            quantity=signed_qty,
            fill_price=fill_price,
            commission=commission,
            slippage=slippage_price,
            exchange=spec["exchange"],
            fill_qty=abs(order.quantity),
            remaining_qty=0,
            order_status=OrderStatus.FILLED,
            original_qty=abs(order.quantity),
        )

    def execute_limit_order(self, order: OrderEvent, timestamp: datetime) -> Optional[FillEvent]:
        """
        Place limit order with Volume-Pro-Rata logic.

        Order is added to pending queue. Fill occurs when cumulative volume
        at limit price AFTER placement exceeds queue size (checked in update_tick_data).

        Args:
            order: OrderEvent to execute
            timestamp: Order placement timestamp

        Returns:
            None (order added to pending queue)
        """
        if order.price is None:
            raise ValueError("Limit order must have a price")

        # Snapshot current cumulative volume at this price
        volume_at_placement = self.cumulative_volume_at_price[order.symbol][order.price]

        # Add to pending queue with placement metadata and filled_qty=0
        self.pending_limit_orders[order.symbol].append((order, timestamp, volume_at_placement, 0))

        # Track as working order
        self.working_orders[order.order_id] = order

        return None

    def execute_stop_order(self, order: OrderEvent, current_price: float, timestamp: datetime) -> Optional[FillEvent]:
        """
        Execute stop order when price crosses stop level.
        Converts to market order with dynamic slippage.

        Args:
            order: OrderEvent to execute
            current_price: Current market price
            timestamp: Execution timestamp

        Returns:
            FillEvent if stop triggered, None otherwise
        """
        stop_price = order.price

        if stop_price is None:
            raise ValueError("Stop order must have a price")

        triggered = False
        if order.direction == "BUY" and current_price >= stop_price:
            triggered = True
        elif order.direction == "SELL" and current_price <= stop_price:
            triggered = True

        if not triggered:
            return None

        return self.execute_market_order(order, current_price, timestamp)

    def execute_order(self, order: OrderEvent, current_price: float, timestamp: datetime) -> Optional[FillEvent]:
        """
        Route order to appropriate execution method based on order type.

        Args:
            order: OrderEvent to execute
            current_price: Current market price
            timestamp: Execution timestamp

        Returns:
            FillEvent if immediately filled, None if pending (limit orders)
        """
        if order.order_type == OrderType.MARKET:
            fill = self.execute_market_order(order, current_price, timestamp)
        elif order.order_type == OrderType.LIMIT:
            fill = self.execute_limit_order(order, timestamp)
        elif order.order_type in [OrderType.STOP, OrderType.STOP_LIMIT]:
            fill = self.execute_stop_order(order, current_price, timestamp)
        else:
            fill = self.execute_market_order(order, current_price, timestamp)

        # V2: Auto-register bracket after entry fill, or cancel bracket on exit fill
        if fill is not None:
            bracket_key = f"{order.strategy_id}:{order.symbol}"
            if order.metadata and order.metadata.get("sl_ticks") and order.metadata.get("tp_ticks"):
                # Entry order with bracket params - register bracket
                self._register_bracket(order, fill.fill_price)
            elif bracket_key in self.active_brackets:
                # Exit or reversal - cancel existing bracket
                del self.active_brackets[bracket_key]

        return fill

    def get_pending_limit_orders(self, symbol: str) -> List[OrderEvent]:
        """
        Get all pending limit orders for a symbol.

        Args:
            symbol: Futures symbol

        Returns:
            List of pending OrderEvents
        """
        return [order for order, _, _, _ in self.pending_limit_orders.get(symbol, [])]

    def cancel_limit_order(self, symbol: str, order: OrderEvent) -> bool:
        """
        Cancel a pending limit order.

        Args:
            symbol: Futures symbol
            order: OrderEvent to cancel

        Returns:
            True if cancelled, False if not found
        """
        if symbol not in self.pending_limit_orders:
            return False

        for i, (pending_order, _, _, _) in enumerate(self.pending_limit_orders[symbol]):
            if pending_order == order:
                self.pending_limit_orders[symbol].pop(i)
                # Remove from working orders
                if order.order_id in self.working_orders:
                    del self.working_orders[order.order_id]
                return True

        return False

    # =========================================================================
    # V4: BRACKET EXIT SIMULATION — Static TP/SL + Milestone BE-Jump
    # Simulates NT8 OCO bracket orders. Called on every tick.
    #
    # TP: Static limit at entry ± tp_ticks × tick_size
    # SL: Static stop at entry ∓ sl_ticks × tick_size (until BE-jump fires)
    # BE-Jump: When price crosses 75% of TP distance, SL jumps to entry ± 1 tick
    # =========================================================================

    def _register_bracket(self, order: OrderEvent, entry_price: float):
        """
        Register bracket TP/SL orders after an entry fill.

        Args:
            order: The entry OrderEvent (must have metadata with sl_ticks, tp_ticks)
            entry_price: Actual fill price of the entry order
        """
        if not order.metadata:
            return

        sl_ticks = order.metadata.get("sl_ticks")
        tp_ticks = order.metadata.get("tp_ticks")
        if sl_ticks is None or tp_ticks is None:
            return

        spec = get_instrument_spec(order.symbol)
        tick_size = spec["tick_size"]

        # Determine direction: BUY entry = LONG position, SELL entry = SHORT position
        is_long = order.direction == "BUY"

        if is_long:
            tp_price = entry_price + (tp_ticks * tick_size)
            sl_price = entry_price - (sl_ticks * tick_size)
        else:
            tp_price = entry_price - (tp_ticks * tick_size)
            sl_price = entry_price + (sl_ticks * tick_size)

        # Round to tick size
        tp_price = round(tp_price / tick_size) * tick_size
        sl_price = round(sl_price / tick_size) * tick_size

        # BE-Jump: Compute trigger price and BE SL level from signal metadata
        be_trigger_pct = float(order.metadata.get("be_trigger_pct", 0.75)) if order.metadata else 0.75
        be_offset_ticks = int(order.metadata.get("be_offset_ticks", 1)) if order.metadata else 1
        if is_long:
            be_trigger_price = entry_price + be_trigger_pct * (tp_price - entry_price)
            be_sl_price = entry_price + be_offset_ticks * tick_size
        else:
            be_trigger_price = entry_price - be_trigger_pct * (entry_price - tp_price)
            be_sl_price = entry_price - be_offset_ticks * tick_size

        # Temporal decay params from signal metadata
        rolling_median_ibt = order.metadata.get("rolling_median_ibt")
        temporal_timeout_s = 3.0 * rolling_median_ibt if rolling_median_ibt else 60.0

        # Read exit_mode from signal metadata (defaults to Static_BE_Jump for backward compat)
        # use_trailing_breakeven=False overrides to "Static_Only" (no BE, no decay)
        if order.metadata and not order.metadata.get("use_trailing_breakeven", True):
            exit_mode = "Static_Only"
        else:
            exit_mode = order.metadata.get("exit_mode", "Static_BE_Jump") if order.metadata else "Static_BE_Jump"

        bracket_key = f"{order.strategy_id}:{order.symbol}"
        self.active_brackets[bracket_key] = {
            "entry_order_id": order.order_id,
            "strategy_id": order.strategy_id,
            "symbol": order.symbol,
            "is_long": is_long,
            "entry_price": entry_price,
            "tp_price": tp_price,
            "sl_price": sl_price,
            "quantity": order.quantity,
            "tick_size": tick_size,
            "exchange": spec["exchange"],
            # Exit geometry mode (drives BE-jump vs Indicator_Ratchet path)
            "exit_mode": exit_mode,
            # BE-Jump State (Static_BE_Jump only — Phase 5)
            "be_trigger_price": be_trigger_price,
            "be_sl_price": be_sl_price,
            "be_triggered": False,
            # Temporal Decay State (Phase 3)
            "temporal_timeout_s": temporal_timeout_s,
            "last_favorable_brick_time": order.timestamp if hasattr(order, 'timestamp') and order.timestamp else datetime.now(),
            "entry_fill_time": order.timestamp if hasattr(order, 'timestamp') and order.timestamp else datetime.now(),
        }

        print(
            f"[BRACKET] Registered: {order.strategy_id} {order.symbol} "
            f"{'LONG' if is_long else 'SHORT'} entry={entry_price:.2f} "
            f"TP={tp_price:.2f} SL={sl_price:.2f} "
            f"BE-jump@{be_trigger_price:.2f} timeout={temporal_timeout_s:.1f}s"
        )

    def update_bracket_temporal(self, symbol: str, brick_direction: int, brick_timestamp: datetime):
        """
        Update temporal decay state after a new brick forms.

        Called from engine after each Renko brick during an active position.
        If the brick is in the favorable direction, resets the temporal deadline.

        Args:
            symbol: Futures symbol
            brick_direction: +1 for UP brick, -1 for DOWN brick
            brick_timestamp: Timestamp of the brick completion
        """
        for key, bracket in self.active_brackets.items():
            if bracket["symbol"] != symbol:
                continue
            # Favorable = UP brick for LONG, DOWN brick for SHORT
            is_favorable = (bracket["is_long"] and brick_direction > 0) or \
                           (not bracket["is_long"] and brick_direction < 0)
            if is_favorable:
                bracket["last_favorable_brick_time"] = brick_timestamp

    def check_bracket_exits(self, symbol: str, price: float, timestamp: datetime) -> List[FillEvent]:
        """
        Check if any active brackets for this symbol have been triggered.

        Called on every tick in backtest mode. Checks:
        - BE-Jump: When price crosses 75% of TP distance, SL jumps to entry ± 1 tick
        - Temporal Decay: If no favorable brick within 3×median_IBT, exit at market
        - TP: Limit order fills at exact tp_price (no slippage)
        - SL: Stop fills at sl_price

        Args:
            symbol: Futures symbol from tick
            price: Current tick price
            timestamp: Tick timestamp

        Returns:
            List of FillEvents for triggered bracket exits
        """
        fills = []
        keys_to_remove = []

        for key, bracket in self.active_brackets.items():
            if bracket["symbol"] != symbol:
                continue

            tp_price = bracket["tp_price"]
            sl_price = bracket["sl_price"]
            is_long = bracket["is_long"]
            triggered = None
            fill_price = None

            # --- Static_Only: pure static TP/SL — skip BE-jump and temporal decay ---
            _exit_mode = bracket.get("exit_mode", "Static_BE_Jump")

            # --- BE-Jump: Move SL to breakeven+1 when 75% of TP distance crossed ---
            # Only active for Static_BE_Jump mode. Static_Only and Indicator_Ratchet skip.
            if _exit_mode == "Static_BE_Jump" and not bracket["be_triggered"]:
                if (is_long and price >= bracket["be_trigger_price"]) or \
                   (not is_long and price <= bracket["be_trigger_price"]):
                    bracket["be_triggered"] = True
                    bracket["sl_price"] = bracket["be_sl_price"]
                    sl_price = bracket["sl_price"]  # Use updated SL for this tick

            # --- Temporal Decay: Exit if no favorable brick within timeout ---
            # Skipped for Static_Only (pure static TP/SL)
            if _exit_mode != "Static_Only" and bracket.get("last_favorable_brick_time") and bracket.get("temporal_timeout_s"):
                elapsed = (timestamp - bracket["last_favorable_brick_time"]).total_seconds()
                if elapsed > bracket["temporal_timeout_s"]:
                    triggered = "TIMEOUT"
                    fill_price = price  # Market exit at current price

            # --- TP / SL check ---
            if not triggered:
                if is_long:
                    if price >= tp_price:
                        triggered = "TP"
                        fill_price = tp_price  # Limit fill at exact TP price
                    elif price <= sl_price:
                        triggered = "SL"
                        fill_price = sl_price  # Fill at stop level
                else:
                    if price <= tp_price:
                        triggered = "TP"
                        fill_price = tp_price  # Limit fill at exact TP price
                    elif price >= sl_price:
                        triggered = "SL"
                        fill_price = sl_price  # Fill at stop level

            if triggered:
                # Exit direction is opposite of entry
                exit_direction = "SELL" if is_long else "BUY"
                signed_qty = bracket["quantity"] if exit_direction == "BUY" else -bracket["quantity"]
                commission = self.commission_per_contract * bracket["quantity"]

                bracket_order_id = f"BRACKET_{triggered}_{bracket['entry_order_id']}"

                # State guard: skip if this bracket was already filled
                if bracket_order_id in self._filled_orders:
                    continue

                self._filled_orders.add(bracket_order_id)
                fill = FillEvent(
                    timestamp=timestamp,
                    symbol=bracket["symbol"],
                    strategy_id=bracket["strategy_id"],
                    order_id=bracket_order_id,
                    quantity=signed_qty,
                    fill_price=fill_price,
                    commission=commission,
                    slippage=0.0 if triggered == "TP" else abs(price - fill_price),
                    exchange=bracket["exchange"],
                    fill_qty=bracket["quantity"],
                    remaining_qty=0,
                    order_status=OrderStatus.FILLED,
                    original_qty=bracket["quantity"],
                )

                fills.append(fill)
                keys_to_remove.append(key)

                pnl_ticks = (fill_price - bracket["entry_price"]) / bracket["tick_size"]
                if not is_long:
                    pnl_ticks = -pnl_ticks
                be_label = "BE" if bracket["be_triggered"] else "Static"
                print(
                    f"[BRACKET] {triggered} EXIT ({be_label}): {bracket['strategy_id']} {bracket['symbol']} "
                    f"{'LONG' if is_long else 'SHORT'} @ {fill_price:.2f} "
                    f"(entry={bracket['entry_price']:.2f}, P&L={pnl_ticks:.0f} ticks)"
                )

        # Remove triggered brackets
        for key in keys_to_remove:
            del self.active_brackets[key]

        return fills

    def cancel_bracket(self, strategy_id: str, symbol: str) -> bool:
        """
        Cancel an active bracket (e.g., on EOD flatten or manual exit).

        Args:
            strategy_id: Strategy identifier
            symbol: Futures symbol

        Returns:
            True if bracket was cancelled, False if not found
        """
        bracket_key = f"{strategy_id}:{symbol}"
        if bracket_key in self.active_brackets:
            bracket = self.active_brackets[bracket_key]
            print(
                f"[BRACKET] Cancelled: {strategy_id} {symbol} TP={bracket['tp_price']:.2f} SL={bracket['sl_price']:.2f}"
            )
            del self.active_brackets[bracket_key]
            return True
        return False


if __name__ == "__main__":
    print("Phase 2 ExecutionHandler - Test Run")
    print("=" * 60)

    from datetime import datetime

    from events import OrderEvent, OrderType

    eh = ExecutionHandler(
        commission_per_contract=2.50,
        limit_order_queue_size=100,
        base_slippage_ticks=0.5,
        velocity_slippage_exponent=1.5,
        network_latency_ms=25,
    )

    print("\nConfiguration:")
    print(f"  Commission: ${eh.commission_per_contract}/contract")
    print(f"  Limit queue size: {eh.limit_order_queue_size} contracts")
    print(f"  Base slippage: {eh.base_slippage_ticks} ticks")
    print(f"  Velocity exponent: {eh.velocity_slippage_exponent}")
    print(f"  Network latency: {eh.network_latency_ms}ms")

    # Simulate some tick data
    symbol = "ES"
    timestamp = datetime.now()

    for i in range(10):
        eh.update_tick_data(symbol, timestamp, 4800.0 + i * 0.25, 50)

    velocity = eh.calculate_tick_velocity(symbol, timestamp)
    print(f"\nTick velocity: {velocity:.2f} ticks/second")

    # Test market order
    market_order = OrderEvent(
        timestamp=timestamp,
        symbol=symbol,
        strategy_id="test_strategy",
        order_type=OrderType.MARKET,
        quantity=1,
        direction="BUY",
    )

    fill = eh.execute_order(market_order, 4800.0, timestamp)
    print("\nMarket Order Fill:")
    print(f"  Fill price: ${fill.fill_price:.2f}")
    print(f"  Slippage: ${fill.slippage:.4f}")
    print(f"  Commission: ${fill.commission:.2f}")

    # Test limit order
    limit_order = OrderEvent(
        timestamp=timestamp,
        symbol=symbol,
        strategy_id="test_strategy",
        order_type=OrderType.LIMIT,
        quantity=1,
        direction="BUY",
        price=4799.75,
    )

    result = eh.execute_order(limit_order, 4800.0, timestamp)
    print(f"\nLimit Order: {result}")
    print(f"  Pending orders: {len(eh.get_pending_limit_orders(symbol))}")

    print("\n✓ ExecutionHandler tests complete")
