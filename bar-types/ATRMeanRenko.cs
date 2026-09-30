// =============================================================================
//  ATRMeanRenko — volatility-adaptive Renko BarsType for NinjaTrader 8
// =============================================================================
//
//  PROBLEM WITH A FIXED BRICK SIZE
//  A brick measured in ticks is a different instrument in every regime. Twenty
//  ticks is a meaningful move in an overnight session and noise at the cash
//  open. The consequence is that signal density — and therefore trade frequency
//  and every statistic derived from it — drifts with volatility rather than with
//  the strategy. Backtest results taken from a quiet sample do not transfer.
//
//  APPROACH
//  Brick size is set to a percentage of ATR, sampled on its own timeframe, so a
//  brick represents a constant fraction of prevailing volatility instead of a
//  constant number of ticks.
//
//  DESIGN DECISIONS AND WHY
//
//  * ATR is computed from a separate minute series rather than from the Renko
//    bars themselves. Deriving the brick size from bars whose size it controls
//    is a feedback loop: bricks widen, ATR rises, bricks widen further.
//
//  * The multiplier is expressed as a percentage (default 30% of ATR(14))
//    rather than an absolute multiple, so the same configuration is portable
//    across instruments with different tick values.
//
//  * Brick size is recomputed on new data but applied only at brick boundaries,
//    so a brick never changes size while it is forming — otherwise the series
//    could not be reproduced deterministically from the same tick stream.
//
// =============================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.BarsTypes
{
	public class ATRMeanRenko : BarsType
	{
		#region Variables
		// Bar tracking
		private int barDirection;  // 1 = bullish, -1 = bearish, 0 = undetermined
		private double barMax;     // Upper threshold for new bullish bar
		private double barMin;     // Lower threshold for new bearish/reversal bar
		private double barOpen;    // Current bar's open
		private double barHigh;    // Current bar's high (wick tracking)
		private double barLow;     // Current bar's low (wick tracking)
		private double fakeOpen;   // Mean Renko synthetic open
		private long barVolume;    // Accumulated volume
		
		// Configuration
		private double tickSize;
		private double trendOffset;    // Distance for trend continuation
		private double openOffset;     // Mean Renko open offset (50%)
		private double reversalOffset; // Distance for reversal (150%)
		private double previousBrickSize; // For smooth Mean Renko connections
		
		// ATR calculation
		private List<double> minuteHighs;
		private List<double> minuteLows;
		private List<double> minuteCloses;
		private DateTime lastMinuteTime;
		private double currentMinuteHigh;
		private double currentMinuteLow;
		private double currentMinuteOpen;
		private double currentATR;
		private bool atrInitialized;
		
		// Session handling
		private SessionIterator sessionIterator;
		private bool isNewSession;
		#endregion
		
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "ATRMeanRenko";
				BarsPeriod = new BarsPeriod { BarsPeriodType = (BarsPeriodType)1001 };
				BuiltFrom = BarsPeriodType.Tick;
				DaysToLoad = 5;
				IsTimeBased = false;
				IsIntraday = true;
				
				// Defaults
				ATRPeriod = 14;
				ATRMultiplierPct = 30;
				ATRTimeframe = 1;
				OpenOffsetPct = 50;
				ReversalPct = 150;
				MinBrickTicks = 4;
				MaxBrickTicks = 100;
			}
			else if (State == State.Configure)
			{
				Name = string.Format("ATRMeanRenko({0},{1}%)", ATRPeriod, ATRMultiplierPct);
				
				Properties.Remove(Properties.Find("BaseBarsPeriodType", true));
				Properties.Remove(Properties.Find("BaseBarsPeriodValue", true));
				Properties.Remove(Properties.Find("PointAndFigurePriceType", true));
				Properties.Remove(Properties.Find("ReversalType", true));
				SetPropertyName("Value2", "ATR Multiplier %");
				
				SetPropertyName("Value", "ATR Period");
			}
		}
		
		public override void ApplyDefaultBasePeriodValue(BarsPeriod period) { }
		public override void ApplyDefaultValue(BarsPeriod period)
		{
			period.Value = ATRPeriod;
			period.Value2 = ATRMultiplierPct;
		}
		
		public override string ChartLabel(DateTime dateTime)
		{
			return dateTime.ToString("HH:mm:ss");
		}
		
		public override int GetInitialLookBackDays(BarsPeriod barsPeriod, TradingHours tradingHours, int barsBack)
		{
			return DaysToLoad;
		}
		
		public override double GetPercentComplete(Bars bars, DateTime now)
		{
			return 0;
		}
		
		protected override void OnDataPoint(Bars bars, double open, double high, double low, double close, DateTime time, long volume, bool isBar, double bid, double ask)
		{
			// Initialize session iterator
			if (sessionIterator == null)
			{
				sessionIterator = new SessionIterator(bars);
				isNewSession = true;
			}
			
			// Get tick size
			if (tickSize == 0 && bars.Instrument.MasterInstrument.TickSize > 0)
			{
				tickSize = bars.Instrument.MasterInstrument.TickSize;
				InitializeATRTracking();
			}
			
			if (tickSize == 0)
				return;
			
			// Round close to tick size
			double thisClose = bars.Instrument.MasterInstrument.RoundToTickSize(close);
			
			// Check for new session
			if (sessionIterator.IsNewSession(time, isBar))
			{
				sessionIterator.GetNextSession(time, isBar);
				isNewSession = true;
			}
			
			// Update ATR calculation from minute data
			UpdateATR(thisClose, time);
			
			// Calculate current brick size based on ATR
			double brickSize = CalculateBrickSize(bars);
			
			// Set offsets based on current brick size
			trendOffset = brickSize;
			openOffset = brickSize * (OpenOffsetPct / 100.0);
			reversalOffset = brickSize * (ReversalPct / 100.0);
			
			// === FIRST BAR ===
			if (bars.Count == 0)
			{
				barDirection = 0;
				barOpen = thisClose;
				barHigh = thisClose;
				barLow = thisClose;
				barVolume = volume;
				barMax = thisClose + trendOffset;
				barMin = thisClose - trendOffset;
				previousBrickSize = brickSize;
				isNewSession = false;
				
				AddBar(bars, thisClose, thisClose, thisClose, thisClose, time, volume);
				return;
			}
			
			// === NEW SESSION - Reset ===
			if (isNewSession)
			{
				barDirection = 0;
				barOpen = thisClose;
				barHigh = thisClose;
				barLow = thisClose;
				barVolume = volume;
				barMax = thisClose + trendOffset;
				barMin = thisClose - trendOffset;
				previousBrickSize = brickSize;
				isNewSession = false;
				
				AddBar(bars, thisClose, thisClose, thisClose, thisClose, time, volume);
				return;
			}
			
			// === SUBSEQUENT BARS ===
			barVolume += volume;
			
			// Track high/low for wicks
			barHigh = Math.Max(barHigh, thisClose);
			barLow = Math.Min(barLow, thisClose);
			
			// Check if thresholds exceeded
			bool maxExceeded = thisClose >= barMax;
			bool minExceeded = thisClose <= barMin;
			
			// === WITHIN RANGE - Just update current bar ===
			if (!maxExceeded && !minExceeded)
			{
				UpdateBar(bars, barHigh, barLow, thisClose, time, barVolume);
				return;
			}
			
			// === THRESHOLD EXCEEDED - Create new bar(s) ===
			// Use while loop to handle gaps (critical fix from PJSUniRenko)
			while (maxExceeded || minExceeded)
			{
				// Determine new bar direction
				int newDirection = maxExceeded ? 1 : -1;
				
				// Calculate the close for this completed bar
				double completedClose = maxExceeded ? barMax : barMin;
				completedClose = bars.Instrument.MasterInstrument.RoundToTickSize(completedClose);
				
				// Calculate high/low for the completed bar (with wicks)
				double completedHigh, completedLow;
				if (barDirection == 1 || (barDirection == 0 && newDirection == 1))
				{
					completedHigh = Math.Max(completedClose, barHigh);
					completedLow = Math.Min(barOpen, barLow);
				}
				else
				{
					completedHigh = Math.Max(barOpen, barHigh);
					completedLow = Math.Min(completedClose, barLow);
				}
				completedHigh = bars.Instrument.MasterInstrument.RoundToTickSize(completedHigh);
				completedLow = bars.Instrument.MasterInstrument.RoundToTickSize(completedLow);
				
				// Update the current bar to its final completed state
				UpdateBar(bars, completedHigh, completedLow, completedClose, time, barVolume);
				
				// Calculate fake open for the NEW bar using PREVIOUS brick size for smooth connection
				double offsetForNewBar = previousBrickSize * (OpenOffsetPct / 100.0);
				if (barDirection == 0)
				{
					// First directional bar - open at the completed close (no offset)
					fakeOpen = completedClose;
				}
				else if (newDirection == barDirection)
				{
					// Trend continuation - open offset back into previous bar
					fakeOpen = completedClose - (offsetForNewBar * newDirection);
				}
				else
				{
					// Reversal - open at completed close (reversal starts fresh)
					fakeOpen = completedClose;
				}
				fakeOpen = bars.Instrument.MasterInstrument.RoundToTickSize(fakeOpen);
				
				// Store current brick size as previous for next bar
				previousBrickSize = brickSize;
				
				// Add the new bar (starts as a forming bar)
				AddBar(bars, fakeOpen, fakeOpen, fakeOpen, fakeOpen, time, 0);
				
				// Update state for the new bar
				barDirection = newDirection;
				barOpen = fakeOpen;
				barHigh = fakeOpen;
				barLow = fakeOpen;
				barVolume = 0;
				
				// Calculate new thresholds based on CURRENT brick size
				if (newDirection == 1)
				{
					barMax = fakeOpen + trendOffset;
					barMin = fakeOpen - reversalOffset;
				}
				else
				{
					barMax = fakeOpen + reversalOffset;
					barMin = fakeOpen - trendOffset;
				}
				
				// Check if we need another bar (handling gaps)
				maxExceeded = thisClose >= barMax;
				minExceeded = thisClose <= barMin;
			}
			
			// Final update with actual close
			barHigh = Math.Max(barHigh, thisClose);
			barLow = Math.Min(barLow, thisClose);
			barVolume += volume;
			UpdateBar(bars, barHigh, barLow, thisClose, time, barVolume);
		}
		
		#region ATR Calculation
		private void InitializeATRTracking()
		{
			minuteHighs = new List<double>();
			minuteLows = new List<double>();
			minuteCloses = new List<double>();
			lastMinuteTime = DateTime.MinValue;
			currentMinuteHigh = 0;
			currentMinuteLow = double.MaxValue;
			currentMinuteOpen = 0;
			currentATR = 0;
			atrInitialized = false;
		}
		
		private void UpdateATR(double price, DateTime time)
		{
			// Get minute boundary
			int minuteInterval = Math.Max(1, ATRTimeframe);
			DateTime minuteTime = new DateTime(time.Year, time.Month, time.Day, time.Hour, 
				(time.Minute / minuteInterval) * minuteInterval, 0);
			
			// First tick
			if (lastMinuteTime == DateTime.MinValue)
			{
				lastMinuteTime = minuteTime;
				currentMinuteOpen = price;
				currentMinuteHigh = price;
				currentMinuteLow = price;
				return;
			}
			
			// Same minute - update high/low
			if (minuteTime == lastMinuteTime)
			{
				currentMinuteHigh = Math.Max(currentMinuteHigh, price);
				currentMinuteLow = Math.Min(currentMinuteLow, price);
				return;
			}
			
			// New minute - complete previous bar and calculate ATR
			CompleteMinuteBar(price);
			
			// Start new minute
			lastMinuteTime = minuteTime;
			currentMinuteOpen = price;
			currentMinuteHigh = price;
			currentMinuteLow = price;
		}
		
		private void CompleteMinuteBar(double closePrice)
		{
			// Add completed minute bar data
			minuteHighs.Add(currentMinuteHigh);
			minuteLows.Add(currentMinuteLow);
			minuteCloses.Add(closePrice);
			
			// Keep rolling window
			int maxBars = ATRPeriod + 5;
			while (minuteHighs.Count > maxBars)
			{
				minuteHighs.RemoveAt(0);
				minuteLows.RemoveAt(0);
				minuteCloses.RemoveAt(0);
			}
			
			// Calculate ATR when we have enough data
			if (minuteHighs.Count >= ATRPeriod)
			{
				CalculateATR();
			}
		}
		
		private void CalculateATR()
		{
			if (minuteHighs.Count < ATRPeriod)
				return;
			
			double sumTR = 0;
			int startIdx = minuteHighs.Count - ATRPeriod;
			
			for (int i = startIdx; i < minuteHighs.Count; i++)
			{
				double tr;
				if (i == 0)
				{
					tr = minuteHighs[i] - minuteLows[i];
				}
				else
				{
					double highLow = minuteHighs[i] - minuteLows[i];
					double highPrevClose = Math.Abs(minuteHighs[i] - minuteCloses[i - 1]);
					double lowPrevClose = Math.Abs(minuteLows[i] - minuteCloses[i - 1]);
					tr = Math.Max(highLow, Math.Max(highPrevClose, lowPrevClose));
				}
				sumTR += tr;
			}
			
			currentATR = sumTR / ATRPeriod;
			atrInitialized = true;
		}
		
		private double CalculateBrickSize(Bars bars)
		{
			double brickSize;
			
			if (!atrInitialized || currentATR <= 0)
			{
				// Fallback to minimum brick size during warmup
				brickSize = MinBrickTicks * tickSize;
			}
			else
			{
				// ATR * multiplier percentage
				brickSize = currentATR * (bars.BarsPeriod.Value2 / 100.0);
			}
			
			// Clamp to min/max
			double minSize = MinBrickTicks * tickSize;
			double maxSize = MaxBrickTicks * tickSize;
			brickSize = Math.Max(minSize, Math.Min(maxSize, brickSize));
			
			// Round to tick size
			brickSize = bars.Instrument.MasterInstrument.RoundToTickSize(brickSize);
			
			// Ensure at least one tick
			if (brickSize < tickSize)
				brickSize = tickSize;
			
			return brickSize;
		}
		#endregion
		
		public override bool IsRemoveLastBarSupported { get { return true; } }
		
		#region Properties
		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "ATR Period", Description = "Period for ATR calculation", Order = 1, GroupName = "ATR Settings")]
		public int ATRPeriod { get; set; }
		
		[NinjaScriptProperty]
		[Range(1, 500)]
		[Display(Name = "ATR Multiplier %", Description = "Percentage of ATR to use as brick size", Order = 2, GroupName = "ATR Settings")]
		public int ATRMultiplierPct { get; set; }
		
		[NinjaScriptProperty]
		[Range(1, 60)]
		[Display(Name = "ATR Timeframe (min)", Description = "Minute timeframe for ATR calculation", Order = 3, GroupName = "ATR Settings")]
		public int ATRTimeframe { get; set; }
		
		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Open Offset %", Description = "Mean Renko open offset percentage (50 = standard Mean Renko)", Order = 4, GroupName = "Mean Renko")]
		public int OpenOffsetPct { get; set; }
		
		[NinjaScriptProperty]
		[Range(100, 300)]
		[Display(Name = "Reversal %", Description = "Reversal threshold as percentage of brick size (150 = standard Mean Renko)", Order = 5, GroupName = "Mean Renko")]
		public int ReversalPct { get; set; }
		
		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Min Brick Ticks", Description = "Minimum brick size in ticks", Order = 6, GroupName = "Brick Limits")]
		public int MinBrickTicks { get; set; }
		
		[NinjaScriptProperty]
		[Range(10, 500)]
		[Display(Name = "Max Brick Ticks", Description = "Maximum brick size in ticks", Order = 7, GroupName = "Brick Limits")]
		public int MaxBrickTicks { get; set; }
		#endregion
	}
}