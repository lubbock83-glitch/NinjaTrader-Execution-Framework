// =============================================================================
//  PrecisionWickRenko — custom BarsType for NinjaTrader 8
// =============================================================================
//
//  PROBLEM
//  Standard Renko reports only the brick box: open and close sit exactly one
//  brick apart and the high/low are synthesised from them. The price actually
//  traded inside that brick is discarded. Any strategy reasoning about stop
//  placement, excursion, or wick rejection is therefore reasoning about a
//  price series that never existed.
//
//  APPROACH
//  Build from raw ticks and carry the true intra-brick extremes (the "shadow")
//  onto each emitted brick, so the bar reports the box AND the real excursion.
//
//  DESIGN DECISIONS AND WHY
//
//  * BuiltFrom = Tick, not Minute. Wick fidelity is the entire point of this
//    bar type; a one-minute source would pre-aggregate away the extremes we
//    exist to preserve.
//
//  * RoundToTickSize on every emitted price. Brick boundaries are computed by
//    repeated addition, so without rounding the series accumulates sub-tick
//    floating point drift. Over a session of thousands of bricks that drift
//    becomes visible as levels that no longer align to the instrument grid.
//
//  * Brick emission is a while loop, not an if. A single tick can gap through
//    several brick levels at once — news prints, open auctions, halts. Emitting
//    only one brick per data point would leave the series discontinuous and
//    silently mis-state how far price travelled.
//
//  * BarsPeriodType 108 is deliberately outside NinjaTrader's reserved range to
//    avoid colliding with stock or third-party bar types registered on the
//    same install.
//
// =============================================================================

using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;

namespace NinjaTrader.NinjaScript.BarsTypes
{
    public class PrecisionWickRenko : BarsType
    {
        private double  lastClose;
        private double  sHigh;
        private double  sLow;
        private bool    stateInitialized;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                = "Precision Wick Renko";
                BarsPeriod          = new BarsPeriod
                {
                    BarsPeriodType  = (BarsPeriodType)108,
                    Value           = 10
                };
                BuiltFrom           = BarsPeriodType.Tick;
                IsIntraday          = true;
                DaysToLoad          = 5;
                DefaultChartStyle   = ChartStyleType.CandleStick;
            }
            else if (State == State.Configure)
            {
                lastClose           = 0;
                sHigh               = double.MinValue;
                sLow                = double.MaxValue;
                stateInitialized    = false;
            }
        }

        protected override void OnDataPoint(Bars bars, double open, double high, double low, double close,
            DateTime time, long volume, bool isBar, double bid, double ask)
        {
            // ─────────────────────────────────────────────────────────────
            // STATE RECOVERY: When NT8 reloads chart data (reconnect,
            // instrument switch, workspace load), our instance fields
            // reset but bars.Count > 0. Without this guard, lastClose = 0
            // causes thousands of phantom bricks from 0 → actual price,
            // producing the staircase / vertical wick artifact.
            // ─────────────────────────────────────────────────────────────
            if (!stateInitialized && bars.Count > 0)
            {
                lastClose   = bars.GetClose(bars.Count - 1);
                sHigh       = bars.GetHigh(bars.Count - 1);
                sLow        = bars.GetLow(bars.Count - 1);
                stateInitialized = true;
            }

            // First bar ever — cold start
            if (bars.Count == 0)
            {
                lastClose = bars.Instrument.MasterInstrument.RoundToTickSize(close);
                AddBar(bars, lastClose, lastClose, lastClose, lastClose, time, volume);
                sHigh            = lastClose;
                sLow             = lastClose;
                stateInitialized = true;
                return;
            }

            // Track shadow (wick) extremes while brick is forming
            sHigh = Math.Max(sHigh, high);
            sLow  = Math.Min(sLow, low);

            double brickSize = bars.BarsPeriod.Value * bars.Instrument.MasterInstrument.TickSize;

            // ── Bullish bricks ──
            while (close >= lastClose + brickSize + 0.0000001)
            {
                double nO = lastClose;
                double nC = bars.Instrument.MasterInstrument.RoundToTickSize(lastClose + brickSize);

                AddBar(bars, nO, Math.Max(nC, sHigh), Math.Min(nO, sLow), nC, time, volume);

                lastClose = nC;
                sHigh     = nC;
                sLow      = nC;
            }

            // ── Bearish bricks ──
            while (close <= lastClose - brickSize - 0.0000001)
            {
                double nO = lastClose;
                double nC = bars.Instrument.MasterInstrument.RoundToTickSize(lastClose - brickSize);

                AddBar(bars, nO, Math.Max(nO, sHigh), Math.Min(nC, sLow), nC, time, volume);

                lastClose = nC;
                sHigh     = nC;
                sLow      = nC;
            }

            // ── Update the in-progress bar with latest wick / volume ──
            UpdateBar(bars,
                Math.Max(bars.GetHigh(bars.Count - 1), high),
                Math.Min(bars.GetLow(bars.Count - 1), low),
                close,
                time,
                volume);
        }

        // ── Required abstract implementations ──

        public override void ApplyDefaultBasePeriodValue(BarsPeriod period)
        {
            period.BarsPeriodTypeName   = "PrecisionWickRenko";
            period.BarsPeriodType       = (BarsPeriodType)108;
            period.Value                = 10;
        }

        public override void ApplyDefaultValue(BarsPeriod period)
        {
            period.BarsPeriodTypeName   = "PrecisionWickRenko";
            period.BarsPeriodType       = (BarsPeriodType)108;
            period.Value                = 10;
        }

        public override string ChartLabel(DateTime time)
        {
            return time.ToString("T", System.Globalization.CultureInfo.CurrentCulture);
        }

        public override int GetInitialLookBackDays(BarsPeriod period, TradingHours tradingHours, int barsBack)
        {
            return 5;
        }

        public override double GetPercentComplete(Bars bars, DateTime now)
        {
            return 0;
        }
    }
}