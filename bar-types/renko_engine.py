# =============================================================================
#  renko_engine - the Python counterpart to the C# BarsType implementations
# =============================================================================
#
#  WHY IT IS HERE
#  The four BarsType files in this folder build Renko bricks inside NinjaTrader,
#  one tick at a time, as the market prints them. This file builds the same
#  bricks offline over historical arrays. If the two disagree about where a
#  brick closed, then every signal generated in research refers to a bar that
#  the live chart never produced, and no backtest result transfers.
#
#  That requirement is the reason LevelRenko anchors bricks to an absolute price
#  grid rather than to wherever the chart happened to start: a grid-anchored
#  brick is reproducible from price alone, which is the only way an offline
#  consolidator and a live chart can be made to agree.
#
#  DESIGN DECISIONS AND WHY
#
#  * Numba JIT on the hot loop. Brick consolidation is a sequential scan over
#    millions of ticks that cannot be vectorised, because each brick depends on
#    the running state left by the previous one. Interpreted Python makes a
#    parameter sweep across several instruments impractical; compiled kernels
#    make the same sweep routine.
#
#  * Brick building is separated from the statistics computed over it. The
#    consolidator answers only "where did bricks close", and rolling window
#    statistics are a separate pass. Mixing them would mean re-running the
#    expensive consolidation every time a derived measure changed.
#
#  * The engine returns full OHLC per brick, not just open and close. That is
#    the same decision the wick-preserving bar types make on the C# side, and
#    for the same reason: excursion inside a brick is what stop and target
#    analysis depends on, and a synthetic bar that discards it produces
#    backtests biased in the strategy's favour.
#
# =============================================================================

"""
Phase 3 Numba-Optimized Renko Brick Consolidator
C-level JIT compilation for high-performance tick-to-Renko conversion
Maintains rolling 20-brick window of Highest High and Lowest Low for LBR strategy
"""

import logging
from collections import deque
from datetime import datetime
from typing import Any, Dict, List, Optional, Tuple

import numpy as np
import pandas as pd
import polars as pl
from numba import njit

from config import get_instrument_spec

logger = logging.getLogger("RenkoEngine")
from tick_filter import BrickRateMonitor

# Maximum history length to prevent OOM during multi-week live sessions
# Must be larger than any lookback window (rolling_window_size=20)
BRICK_HISTORY_MAXLEN = 5000


@njit
def build_renko_bricks(
    timestamps: np.ndarray,
    prices: np.ndarray,
    volumes: np.ndarray,
    aggressors: np.ndarray,
    brick_size: float,
    max_bricks: int = 10000,
) -> Tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray, int]:
    """
    Numba JIT-compiled tick-to-Renko brick consolidation.
    Processes NumPy arrays of tick data at C-level speed.

    Args:
        timestamps: Unix timestamps in nanoseconds
        prices: Tick prices
        volumes: Tick volumes
        aggressors: Aggressor flags (+1 buy, -1 sell, 0 unknown)
        brick_size: Size of each Renko brick in price units
        max_bricks: Maximum number of bricks to store

    Returns:
        Tuple of (brick_timestamps, opens, highs, lows, closes, volumes,
                  aggressor_deltas, directions, num_bricks)
        - brick_timestamps: Completion timestamp of each brick
        - opens: Open price of each brick
        - highs: High price of each brick
        - lows: Low price of each brick
        - closes: Close price of each brick
        - volumes: Total volume in each brick
        - aggressor_deltas: Net aggressor volume (buy - sell) in each brick
        - directions: 1 for up brick, -1 for down brick
        - num_bricks: Actual number of bricks created
    """
    n_ticks = len(prices)

    # Preallocate brick arrays
    brick_timestamps = np.zeros(max_bricks, dtype=np.int64)
    brick_opens = np.zeros(max_bricks, dtype=np.float64)
    brick_highs = np.zeros(max_bricks, dtype=np.float64)
    brick_lows = np.zeros(max_bricks, dtype=np.float64)
    brick_closes = np.zeros(max_bricks, dtype=np.float64)
    brick_volumes = np.zeros(max_bricks, dtype=np.float64)
    brick_aggressor_deltas = np.zeros(max_bricks, dtype=np.float64)
    brick_directions = np.zeros(max_bricks, dtype=np.int32)

    if n_ticks == 0:
        return (
            brick_timestamps,
            brick_opens,
            brick_highs,
            brick_lows,
            brick_closes,
            brick_volumes,
            brick_aggressor_deltas,
            brick_directions,
            0,
        )

    brick_count = 0
    current_brick_open = prices[0]
    current_brick_high = prices[0]
    current_brick_low = prices[0]
    brick_start_time = timestamps[0]
    current_volume = 0.0
    current_aggressor_delta = 0.0

    # Iterate through ticks and build bricks
    for i in range(n_ticks):
        price = prices[i]
        timestamp = timestamps[i]
        volume = volumes[i]
        aggressor = aggressors[i]

        # Accumulate volume and aggressor delta for current brick
        current_volume += volume
        current_aggressor_delta += aggressor * volume

        # Update high/low for current brick
        current_brick_high = max(current_brick_high, price)
        current_brick_low = min(current_brick_low, price)

        # Calculate number of bricks to create
        num_up_bricks = int((price - current_brick_open) / brick_size)
        num_down_bricks = int((current_brick_open - price) / brick_size)

        # Create up bricks
        if num_up_bricks > 0:
            for _ in range(num_up_bricks):
                if brick_count >= max_bricks:
                    break

                brick_timestamps[brick_count] = timestamp
                brick_opens[brick_count] = current_brick_open
                brick_highs[brick_count] = current_brick_open + brick_size
                brick_lows[brick_count] = current_brick_open
                brick_closes[brick_count] = current_brick_open + brick_size
                brick_volumes[brick_count] = current_volume
                brick_aggressor_deltas[brick_count] = current_aggressor_delta
                brick_directions[brick_count] = 1

                current_brick_open += brick_size
                brick_count += 1

                # Reset accumulators for next brick
                current_volume = 0.0
                current_aggressor_delta = 0.0

            current_brick_high = price
            current_brick_low = current_brick_open
            brick_start_time = timestamp

        # Create down bricks
        elif num_down_bricks > 0:
            for _ in range(num_down_bricks):
                if brick_count >= max_bricks:
                    break

                brick_timestamps[brick_count] = timestamp
                brick_opens[brick_count] = current_brick_open
                brick_highs[brick_count] = current_brick_open
                brick_lows[brick_count] = current_brick_open - brick_size
                brick_closes[brick_count] = current_brick_open - brick_size
                brick_volumes[brick_count] = current_volume
                brick_aggressor_deltas[brick_count] = current_aggressor_delta
                brick_directions[brick_count] = -1

                current_brick_open -= brick_size
                brick_count += 1

                # Reset accumulators for next brick
                current_volume = 0.0
                current_aggressor_delta = 0.0

            current_brick_high = current_brick_open
            current_brick_low = price
            brick_start_time = timestamp

    return (
        brick_timestamps,
        brick_opens,
        brick_highs,
        brick_lows,
        brick_closes,
        brick_volumes,
        brick_aggressor_deltas,
        brick_directions,
        brick_count,
    )


@njit
def build_renko_bricks_full(
    timestamps: np.ndarray,
    prices: np.ndarray,
    volumes: np.ndarray,
    aggressors: np.ndarray,
    brick_size: float,
    max_bricks: int = 10000,
    max_bricks_per_tick: int = 20,
) -> Tuple[
    np.ndarray, np.ndarray, np.ndarray, np.ndarray, np.ndarray,
    np.ndarray, np.ndarray, np.ndarray,
    np.ndarray, np.ndarray, np.ndarray, np.ndarray,
    int,
]:
    """
    Extended Numba JIT-compiled tick-to-Renko brick consolidation.
    Returns ALL fields needed by ML strategy on_renko(), including tick counts,
    IBT data, VWAP data, and tick indices for bracket-level price scanning.

    Args:
        timestamps: Unix timestamps in nanoseconds (int64)
        prices: Tick prices (float64)
        volumes: Tick volumes (float64)
        aggressors: Aggressor flags (+1 buy, -1 sell, 0 unknown) (float64)
        brick_size: Size of each Renko brick in price units
        max_bricks: Maximum number of bricks to store
        max_bricks_per_tick: Cap on bricks generated from a single tick

    Returns:
        Tuple of 13 arrays:
        - brick_timestamps: Completion timestamp (ns) of each brick
        - brick_opens: Open price
        - brick_highs: High price (intra-brick)
        - brick_lows: Low price (intra-brick)
        - brick_closes: Close price
        - brick_volumes: Total volume accumulated in brick
        - brick_aggressor_deltas: Net aggressor volume (buy - sell)
        - brick_directions: +1 up, -1 down
        - brick_tick_counts: Number of ticks that formed this brick
        - brick_start_timestamps: Timestamp (ns) when brick started forming
        - brick_price_volume_sums: Sum(price * volume) for VWAP calculation
        - brick_end_tick_idx: Index of the tick that completed this brick
        - num_bricks: Actual number of bricks created (int)
    """
    n_ticks = len(prices)

    # Preallocate all brick arrays
    brick_timestamps = np.zeros(max_bricks, dtype=np.int64)
    brick_opens = np.zeros(max_bricks, dtype=np.float64)
    brick_highs = np.zeros(max_bricks, dtype=np.float64)
    brick_lows = np.zeros(max_bricks, dtype=np.float64)
    brick_closes = np.zeros(max_bricks, dtype=np.float64)
    brick_volumes = np.zeros(max_bricks, dtype=np.float64)
    brick_aggressor_deltas = np.zeros(max_bricks, dtype=np.float64)
    brick_directions = np.zeros(max_bricks, dtype=np.int32)
    brick_tick_counts = np.zeros(max_bricks, dtype=np.int32)
    brick_start_timestamps = np.zeros(max_bricks, dtype=np.int64)
    brick_price_volume_sums = np.zeros(max_bricks, dtype=np.float64)
    brick_end_tick_idx = np.zeros(max_bricks, dtype=np.int64)

    if n_ticks == 0:
        return (
            brick_timestamps, brick_opens, brick_highs, brick_lows, brick_closes,
            brick_volumes, brick_aggressor_deltas, brick_directions,
            brick_tick_counts, brick_start_timestamps, brick_price_volume_sums,
            brick_end_tick_idx, 0,
        )

    brick_count = 0
    current_brick_open = prices[0]
    current_brick_high = prices[0]
    current_brick_low = prices[0]
    current_start_time = timestamps[0]
    current_volume = 0.0
    current_aggressor_delta = 0.0
    current_tick_count = 0
    current_pv_sum = 0.0

    for i in range(n_ticks):
        price = prices[i]
        timestamp = timestamps[i]
        volume = volumes[i]
        aggressor = aggressors[i]

        # Accumulate within current forming brick
        current_volume += volume
        current_aggressor_delta += aggressor * volume
        current_tick_count += 1
        current_pv_sum += price * volume

        # Update intra-brick high/low
        if price > current_brick_high:
            current_brick_high = price
        if price < current_brick_low:
            current_brick_low = price

        # Check for brick completions (may generate multiple)
        bricks_this_tick = 0
        while True:
            if bricks_this_tick >= max_bricks_per_tick:
                break
            if brick_count >= max_bricks:
                break

            num_up = int((price - current_brick_open) / brick_size)
            num_down = int((current_brick_open - price) / brick_size)

            if num_up > 0:
                brick_close = current_brick_open + brick_size

                brick_timestamps[brick_count] = timestamp
                brick_opens[brick_count] = current_brick_open
                brick_highs[brick_count] = brick_close         # Theoretical: high = close for UP
                brick_lows[brick_count] = current_brick_open   # Theoretical: low = open for UP
                brick_closes[brick_count] = brick_close
                brick_volumes[brick_count] = current_volume
                brick_aggressor_deltas[brick_count] = current_aggressor_delta
                brick_directions[brick_count] = 1
                brick_tick_counts[brick_count] = current_tick_count
                brick_start_timestamps[brick_count] = current_start_time
                brick_price_volume_sums[brick_count] = current_pv_sum
                brick_end_tick_idx[brick_count] = i

                # Reset for next brick
                current_brick_open = brick_close
                current_brick_high = brick_close
                current_brick_low = brick_close
                current_start_time = timestamp
                current_volume = 0.0
                current_aggressor_delta = 0.0
                current_tick_count = 0
                current_pv_sum = 0.0

                brick_count += 1
                bricks_this_tick += 1

            elif num_down > 0:
                brick_close = current_brick_open - brick_size

                brick_timestamps[brick_count] = timestamp
                brick_opens[brick_count] = current_brick_open
                brick_highs[brick_count] = current_brick_open  # Theoretical: high = open for DOWN
                brick_lows[brick_count] = brick_close          # Theoretical: low = close for DOWN
                brick_closes[brick_count] = brick_close
                brick_volumes[brick_count] = current_volume
                brick_aggressor_deltas[brick_count] = current_aggressor_delta
                brick_directions[brick_count] = -1
                brick_tick_counts[brick_count] = current_tick_count
                brick_start_timestamps[brick_count] = current_start_time
                brick_price_volume_sums[brick_count] = current_pv_sum
                brick_end_tick_idx[brick_count] = i

                # Reset for next brick
                current_brick_open = brick_close
                current_brick_high = brick_close
                current_brick_low = brick_close
                current_start_time = timestamp
                current_volume = 0.0
                current_aggressor_delta = 0.0
                current_tick_count = 0
                current_pv_sum = 0.0

                brick_count += 1
                bricks_this_tick += 1

            else:
                break

    return (
        brick_timestamps, brick_opens, brick_highs, brick_lows, brick_closes,
        brick_volumes, brick_aggressor_deltas, brick_directions,
        brick_tick_counts, brick_start_timestamps, brick_price_volume_sums,
        brick_end_tick_idx, brick_count,
    )


@njit
def calculate_rolling_window_stats(
    highs: np.ndarray, lows: np.ndarray, num_bricks: int, window_size: int = 20
) -> Tuple[np.ndarray, np.ndarray]:
    """
    Calculate rolling window of Highest High and Lowest Low.
    CRUCIAL FOR LBR STRATEGY: Provides boundary detection for Replica Symmetry Breaking.

    Args:
        highs: High prices of all bricks
        lows: Low prices of all bricks
        num_bricks: Actual number of bricks
        window_size: Rolling window size (default: 20 bricks)

    Returns:
        Tuple of (highest_highs, lowest_lows) arrays
        - highest_highs[i]: Highest high of last 20 bricks ending at brick i
        - lowest_lows[i]: Lowest low of last 20 bricks ending at brick i
    """
    highest_highs = np.zeros(num_bricks, dtype=np.float64)
    lowest_lows = np.zeros(num_bricks, dtype=np.float64)

    for i in range(num_bricks):
        start_idx = max(0, i - window_size + 1)
        end_idx = i + 1

        window_highs = highs[start_idx:end_idx]
        window_lows = lows[start_idx:end_idx]

        highest_highs[i] = np.max(window_highs)
        lowest_lows[i] = np.min(window_lows)

    return highest_highs, lowest_lows


class RenkoEngine:
    """
    High-level Renko brick engine with Polars integration.
    Uses Numba JIT-compiled core for C-level performance.
    Maintains rolling 20-brick window for LBR strategy boundary detection.

    ASSET-AGNOSTIC: brick_size is specified in TICKS, not raw points.
    The engine converts to absolute price units using the instrument's tick_size.

    Supports two modes:
    - Batch: process_ticks() for processing full DataFrame
    - Incremental: process_ticks_incremental() for streaming tick-by-tick
    """

    def __init__(self, brick_size_ticks: float, symbol: str = "ES", rolling_window_size: int = 20):
        """
        Initialize Renko engine with asset-agnostic brick sizing.

        Args:
            brick_size_ticks: Size of each Renko brick in TICKS (not raw points)
            symbol: Instrument symbol for tick_size lookup (e.g., 'ES', 'NQ', 'CL')
            rolling_window_size: Window size for highest high/lowest low (default: 20)
        """
        self.symbol = symbol
        self.brick_size_ticks = brick_size_ticks
        # TYPE SAFETY: Force-cast to int for slicing operations
        self.rolling_window_size = int(rolling_window_size)

        # Get instrument-specific tick size from config
        spec = get_instrument_spec(symbol)
        self.tick_size = spec["tick_size"]
        self.point_value = spec["point_value"]

        # CRITICAL: Convert tick-based brick size to absolute price units
        # ES: brick_size_ticks=8 * tick_size=0.25 = 2.0 points
        # NQ: brick_size_ticks=8 * tick_size=0.25 = 2.0 points
        # CL: brick_size_ticks=8 * tick_size=0.01 = 0.08 points
        self.absolute_brick_value = self.brick_size_ticks * self.tick_size

        # Legacy compatibility: brick_size now refers to absolute value
        self.brick_size = self.absolute_brick_value

        self.bricks_df: Optional[pl.DataFrame] = None

        # Incremental state tracking
        self._initialized = False
        self._current_brick_open = 0.0
        self._current_brick_high = 0.0
        self._current_brick_low = 0.0
        self._current_volume = 0.0
        self._current_aggressor_delta = 0.0
        self._brick_start_time: Optional[datetime] = None
        self._current_tick_count = 0  # Track ticks per brick for ATS calculation
        self._current_price_volume_sum = 0.0  # For intra-brick VWAP calculation
        self._last_brick_time: Optional[datetime] = None  # For IBT calculation

        # Brick history for rolling window calculations (bounded deque prevents OOM)
        self._brick_highs: deque = deque(maxlen=BRICK_HISTORY_MAXLEN)
        self._brick_lows: deque = deque(maxlen=BRICK_HISTORY_MAXLEN)
        self._brick_count = 0

        # PHASE 4: Brick rate monitoring (DISABLED)
        # Was logging false positives - legitimate volatility (FOMC, CPI) regularly
        # exceeds any reasonable threshold. Monitor only logged warnings without
        # taking protective action. Re-enable if feed-level anomaly detection is needed.
        self._brick_rate_monitor = BrickRateMonitor(threshold_per_second=5, window_seconds=1.0, enabled=False)

    def process_ticks(self, tick_data: pl.DataFrame) -> pl.DataFrame:
        """
        Convert tick data to Renko bricks with rolling window statistics.

        Args:
            tick_data: Polars DataFrame with columns: Timestamp, Price, Volume, Aggressor

        Returns:
            Polars DataFrame with Renko bricks including:
            - OHLC (Open, High, Low, Close)
            - Completion timestamp
            - Volume and aggressor delta
            - Rolling 20-brick Highest High and Lowest Low
        """
        # Extract NumPy arrays for Numba processing
        timestamps = tick_data["Timestamp"].to_numpy().astype(np.int64)
        prices = tick_data["Price"].to_numpy()
        volumes = tick_data["Volume"].to_numpy()
        aggressors = tick_data["Aggressor"].to_numpy()

        # Build bricks with Numba JIT compilation
        (
            brick_timestamps,
            brick_opens,
            brick_highs,
            brick_lows,
            brick_closes,
            brick_volumes,
            brick_aggressor_deltas,
            brick_directions,
            num_bricks,
        ) = build_renko_bricks(timestamps, prices, volumes, aggressors, self.brick_size)

        if num_bricks == 0:
            return pl.DataFrame()

        # Calculate rolling window stats for LBR strategy
        highest_highs, lowest_lows = calculate_rolling_window_stats(
            brick_highs, brick_lows, num_bricks, self.rolling_window_size
        )

        # Build Polars DataFrame with all brick data
        self.bricks_df = pl.DataFrame(
            {
                "timestamp": brick_timestamps[:num_bricks],
                "open": brick_opens[:num_bricks],
                "high": brick_highs[:num_bricks],
                "low": brick_lows[:num_bricks],
                "close": brick_closes[:num_bricks],
                "volume": brick_volumes[:num_bricks],
                "aggressor_delta": brick_aggressor_deltas[:num_bricks],
                "direction": brick_directions[:num_bricks],
                "highest_high_20": highest_highs,
                "lowest_low_20": lowest_lows,
            }
        )

        return self.bricks_df

    def get_bricks_dataframe(self) -> pl.DataFrame:
        """
        Get all generated bricks as Polars DataFrame.

        Returns:
            Polars DataFrame with all brick data
        """
        if self.bricks_df is None:
            return pl.DataFrame()

        return self.bricks_df

    def get_last_n_bricks(self, n: int = 20) -> pl.DataFrame:
        """
        Get last N bricks.

        Args:
            n: Number of recent bricks to retrieve

        Returns:
            Polars DataFrame with last N bricks
        """
        df = self.get_bricks_dataframe()

        if df.height == 0:
            return df

        return df.tail(n)

    def process_ticks_incremental(
        self, timestamp: datetime, price: float, volume: int = 1, aggressor: int = 0
    ) -> List[Dict[str, Any]]:
        """
        Process a single tick incrementally for streaming use.

        This is the primary interface for live/streaming tick processing.
        Call this for each tick and it will return a list of completed bricks.
        When price jumps multiple brick widths in a single tick, ALL bricks
        are generated (first brick gets accumulated tick data; subsequent
        gap bricks get zero volume/delta/ticks with tick_density=0).

        Args:
            timestamp: Tick timestamp
            price: Tick price
            volume: Tick volume (default: 1)
            aggressor: Aggressor flag (+1 buy, -1 sell, 0 unknown)

        Returns:
            List of brick dicts (empty if no bricks completed).
            Each brick dict contains:
            - timestamp: Completion timestamp
            - open, high, low, close: OHLC prices
            - volume: Total volume in brick (0 for gap bricks)
            - aggressor_delta: Net aggressor volume
            - direction: +1 for up brick, -1 for down brick
            - highest_high_20: Rolling 20-brick highest high
            - lowest_low_20: Rolling 20-brick lowest low
        """
        # Initialize on first tick
        if not self._initialized:
            self._current_brick_open = price
            self._current_brick_high = price
            self._current_brick_low = price
            self._brick_start_time = timestamp
            self._current_volume = 0.0
            self._current_aggressor_delta = 0.0
            self._current_tick_count = 0
            self._current_price_volume_sum = 0.0
            self._initialized = True

        # Accumulate volume, aggressor delta, tick count, and price*volume
        self._current_volume += volume
        self._current_aggressor_delta += aggressor * volume
        self._current_tick_count += 1
        self._current_price_volume_sum += price * volume

        # Update high/low for current forming brick
        self._current_brick_high = max(self._current_brick_high, price)
        self._current_brick_low = min(self._current_brick_low, price)

        completed_bricks = []

        # Loop: generate ALL bricks when price spans multiple brick widths.
        # First brick gets accumulated tick data; subsequent gap bricks get
        # zero volume/delta/ticks (they are synthetic — price jumped past them).
        # CAP: Prevent corrupted exchange tick (+500pts) from generating hundreds
        # of synthetic bricks. 20 bricks ≈ 80 ticks on 4-tick ES brick — well
        # beyond any legitimate single-tick move. Tripwire detects post-hoc.
        _MAX_BRICKS_PER_TICK = 20
        while True:
            if len(completed_bricks) >= _MAX_BRICKS_PER_TICK:
                logger.debug(
                    f"[RENKO] Capped at {_MAX_BRICKS_PER_TICK} bricks from single tick "
                    f"(price={price}, open={self._current_brick_open})"
                )
                break
            num_up_bricks = int((price - self._current_brick_open) / self.absolute_brick_value)
            num_down_bricks = int((self._current_brick_open - price) / self.absolute_brick_value)

            if num_up_bricks > 0:
                brick_close = self._current_brick_open + self.absolute_brick_value

                completed_brick = self._create_brick(
                    timestamp=timestamp,
                    open_price=self._current_brick_open,
                    high_price=brick_close,
                    low_price=self._current_brick_open,
                    close_price=brick_close,
                    direction=1,
                )
                completed_bricks.append(completed_brick)

                # Update state for next brick
                # Gap bricks get zero volume/delta/ticks (no real ticks formed within them)
                self._current_brick_open = brick_close
                self._current_brick_high = brick_close  # Clamp to new open (not gap price)
                self._current_brick_low = brick_close  # Clamp to new open (not gap price)
                self._brick_start_time = timestamp
                self._current_volume = 0.0
                self._current_aggressor_delta = 0.0
                self._current_tick_count = 0
                self._current_price_volume_sum = 0.0

            elif num_down_bricks > 0:
                brick_close = self._current_brick_open - self.absolute_brick_value

                completed_brick = self._create_brick(
                    timestamp=timestamp,
                    open_price=self._current_brick_open,
                    high_price=self._current_brick_open,
                    low_price=brick_close,
                    close_price=brick_close,
                    direction=-1,
                )
                completed_bricks.append(completed_brick)

                # Update state for next brick
                # Gap bricks get zero volume/delta/ticks (no real ticks formed within them)
                self._current_brick_open = brick_close
                self._current_brick_high = brick_close  # Clamp to new open (not gap price)
                self._current_brick_low = brick_close  # Clamp to new open (not gap price)
                self._brick_start_time = timestamp
                self._current_volume = 0.0
                self._current_aggressor_delta = 0.0
                self._current_tick_count = 0
                self._current_price_volume_sum = 0.0

            else:
                break

        return completed_bricks

    def _create_brick(
        self,
        timestamp: datetime,
        open_price: float,
        high_price: float,
        low_price: float,
        close_price: float,
        direction: int,
    ) -> Dict[str, Any]:
        """
        Create a brick dict and update rolling window stats.

        Args:
            timestamp: Brick completion timestamp
            open_price: Brick open price
            high_price: Brick high price
            low_price: Brick low price
            close_price: Brick close price
            direction: +1 for up, -1 for down

        Returns:
            Dict with complete brick data including rolling window stats
        """
        # Store for rolling window calculations
        self._brick_highs.append(high_price)
        self._brick_lows.append(low_price)
        self._brick_count += 1

        # Calculate rolling window stats (cast deque to list for slicing)
        window_start = max(0, len(self._brick_highs) - self.rolling_window_size)
        window_highs = list(self._brick_highs)[window_start:]
        window_lows = list(self._brick_lows)[window_start:]

        highest_high_20 = max(window_highs)
        lowest_low_20 = min(window_lows)

        # Calculate inter-brick time (IBT) in seconds
        # Use pd.Timestamp to handle datetime, numpy.datetime64, and pd.Timestamp uniformly
        ibt = 0.0
        if self._last_brick_time is not None and self._brick_start_time is not None:
            ibt = (pd.Timestamp(timestamp) - pd.Timestamp(self._last_brick_time)).total_seconds()

        # IBT CLAMP: Cap at 120s to prevent exchange halt / circuit breaker IBT
        # from poisoning Pacing_Volatility rolling window (normal brick IBT is 5-60s)
        IBT_CLAMP_SECONDS = 120.0
        if ibt > IBT_CLAMP_SECONDS:
            ibt = IBT_CLAMP_SECONDS

        # Update last brick time for next IBT calculation
        self._last_brick_time = timestamp

        # Compute intra-brick VWAP for Adverse Selection feature
        brick_vwap = self._current_price_volume_sum / max(self._current_volume, 1e-9)

        # PASSIVE METRIC: Max ticks price moved against the brick's final direction
        if direction == 1:  # UP brick — adverse = how far price dipped below open
            intra_brick_excursion = int(round((self._current_brick_open - self._current_brick_low) / self.tick_size))
        else:  # DOWN brick — adverse = how far price rose above open
            intra_brick_excursion = int(round((self._current_brick_high - self._current_brick_open) / self.tick_size))

        brick = {
            "timestamp": timestamp,
            "open_timestamp": self._brick_start_time,  # CIRCUIT BREAKER: Time-to-form calculation
            "open": open_price,
            "high": high_price,
            "low": low_price,
            "close": close_price,
            "volume": self._current_volume,
            "delta": self._current_aggressor_delta,  # Strategy expects 'delta', not 'aggressor_delta'
            "ticks": self._current_tick_count,  # Tick count for ATS_Normalized calculation
            "ibt": ibt,  # Inter-brick time for Pacing_Volatility / Time_Acceleration
            "brick_vwap": brick_vwap,  # Intra-brick VWAP (retained for diagnostics)
            "direction": direction,
            "highest_high_20": highest_high_20,
            "lowest_low_20": lowest_low_20,
            "brick_number": self._brick_count,
            "intra_brick_excursion": intra_brick_excursion,  # Passive: max adverse ticks during formation
            "tick_density": self._current_tick_count,  # Passive: raw tick count to form this brick
        }

        # PHASE 4: Monitor brick generation rate for live data quality
        # Warns if brick rate exceeds threshold (indicates noisy feed)
        self._brick_rate_monitor.record_brick(timestamp, brick)

        return brick

    def apply_gap_splice(self, offset: float):
        """
        Apply mathematical splice to neutralize an RTH gap.

        Shifts the RenkoEngine's price-based state by the gap offset so that
        the engine continues building bricks from the new session's price level.
        The partial-brick accumulator is reset because it belongs to the old session.

        Args:
            offset: Signed price offset (new_session_open - last_brick_close)
        """
        old_open = self._current_brick_open

        # Splice the brick open price to the new session level
        self._current_brick_open += offset

        # Splice rolling window stats (used for highest_high_20 / lowest_low_20)
        for i in range(len(self._brick_highs)):
            self._brick_highs[i] += offset
        for i in range(len(self._brick_lows)):
            self._brick_lows[i] += offset

        # Reset partial-brick accumulator (stale data from old session)
        self._current_brick_high = self._current_brick_open
        self._current_brick_low = self._current_brick_open
        self._current_volume = 0.0
        self._current_aggressor_delta = 0.0
        self._current_tick_count = 0
        self._current_price_volume_sum = 0.0

        print(
            f"[SPLICE] RenkoEngine: brick_open {old_open:.2f} → {self._current_brick_open:.2f} "
            f"(offset={offset:+.2f}), {len(self._brick_highs)} rolling stats spliced, accumulator reset"
        )

    def reset_incremental(self):
        """Reset incremental state for fresh processing."""
        self._initialized = False
        self._current_brick_open = 0.0
        self._current_brick_high = 0.0
        self._current_brick_low = 0.0
        self._current_volume = 0.0
        self._current_aggressor_delta = 0.0
        self._brick_start_time = None
        self._current_tick_count = 0
        self._current_price_volume_sum = 0.0
        self._last_brick_time = None
        self._brick_highs = deque(maxlen=BRICK_HISTORY_MAXLEN)
        self._brick_lows = deque(maxlen=BRICK_HISTORY_MAXLEN)
        self._brick_count = 0

    def get_incremental_state(self) -> Dict[str, Any]:
        """Get current incremental processing state."""
        return {
            "initialized": self._initialized,
            "current_brick_open": self._current_brick_open,
            "current_brick_high": self._current_brick_high,
            "current_brick_low": self._current_brick_low,
            "current_volume": self._current_volume,
            "current_aggressor_delta": self._current_aggressor_delta,
            "current_price_volume_sum": self._current_price_volume_sum,
            "brick_count": self._brick_count,
        }


if __name__ == "__main__":
    import time

    print("Phase 3 Renko Engine - Numba JIT Performance Test")
    print("=" * 60)

    n_ticks = 100000
    print(f"Generating {n_ticks:,} synthetic ticks with aggressor data...")

    timestamps = np.arange(n_ticks, dtype=np.int64) * 1_000_000_000
    base_price = 4800.0
    prices = base_price + np.cumsum(np.random.randn(n_ticks) * 0.25)
    volumes = np.random.randint(1, 100, n_ticks)
    aggressors = np.random.choice([-1, 1], n_ticks)

    tick_df = pl.DataFrame({"Timestamp": timestamps, "Price": prices, "Volume": volumes, "Aggressor": aggressors})

    brick_size_ticks = 4  # 4 ticks = 1.0 points for ES (0.25 tick size)
    engine = RenkoEngine(brick_size_ticks, symbol="ES", rolling_window_size=20)

    print(
        f"\nBuilding Renko bricks (brick_size_ticks={brick_size_ticks}, absolute={engine.absolute_brick_value}, window=20)..."
    )
    start = time.time()
    bricks = engine.process_ticks(tick_df)
    elapsed = time.time() - start

    print("\nResults:")
    print(f"  Ticks processed: {n_ticks:,}")
    print(f"  Bricks created: {len(bricks):,}")
    print(f"  Processing time: {elapsed:.4f} seconds")
    print(f"  Throughput: {n_ticks / elapsed:,.0f} ticks/second")

    print("\nSample bricks with rolling window stats:")
    print(
        bricks.select(
            [
                "timestamp",
                "open",
                "high",
                "low",
                "close",
                "volume",
                "aggressor_delta",
                "direction",
                "highest_high_20",
                "lowest_low_20",
            ]
        ).head(10)
    )

    print("\nBrick statistics:")
    print(f"  Up bricks: {(bricks['direction'] == 1).sum()}")
    print(f"  Down bricks: {(bricks['direction'] == -1).sum()}")
    print(f"  Total volume: {bricks['volume'].sum():,.0f}")
    print(f"  Net aggressor delta: {bricks['aggressor_delta'].sum():,.0f}")

    print("\nRolling window verification (last 5 bricks):")
    tail = bricks.tail(5)
    print(tail.select(["close", "highest_high_20", "lowest_low_20"]))

    print("\n✓ Numba JIT compilation successful - C-level performance achieved")
